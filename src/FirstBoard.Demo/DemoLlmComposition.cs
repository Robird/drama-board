using System.Collections.ObjectModel;
using DramaBoard.Player;
using DramaBoard.Player.Llm;

namespace DramaBoard.FirstBoard.Demo;

internal sealed record DemoAiActorPlan(string ActorId, DemoBackendOptions DecisionBackend);

/// <summary>Pure resource plan for the AI side of one Demo session.</summary>
internal sealed class DemoLlmRosterPlan
{
    private DemoLlmRosterPlan(
        IReadOnlyList<DemoAiActorPlan> aiActors,
        IReadOnlyList<DemoBackendOptions> requiredBackends)
    {
        AiActors = aiActors;
        RequiredBackends = requiredBackends;
    }

    public IReadOnlyList<DemoAiActorPlan> AiActors { get; }

    public IReadOnlyList<DemoBackendOptions> RequiredBackends { get; }

    public static DemoLlmRosterPlan Create(DemoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        DemoAiActorPlan[] actors =
        [
            .. CandidateActors(options)
                .Where(actor => actor.ActorId != options.HumanActorId),
        ];
        var required = new List<DemoBackendOptions>();
        foreach (DemoAiActorPlan actor in actors)
        {
            AddDistinct(required, actor.DecisionBackend);
            AddDistinct(required, options.MemoryBackend);
        }

        return new DemoLlmRosterPlan(
            Array.AsReadOnly(actors),
            required.AsReadOnly());
    }

    private static IEnumerable<DemoAiActorPlan> CandidateActors(DemoOptions options)
    {
        yield return new DemoAiActorPlan(BoardIds.Alice, options.AliceBackend);
        yield return new DemoAiActorPlan(BoardIds.Bob, options.BobBackend);
    }

    private static void AddDistinct(
        ICollection<DemoBackendOptions> required,
        DemoBackendOptions backend)
    {
        if (!required.Contains(backend))
        {
            required.Add(backend);
        }
    }
}

/// <summary>Owns only the LLM drivers and shared backends required by actual AI actors.</summary>
internal sealed class DemoLlmComposition : IAsyncDisposable
{
    private readonly DemoOptions _options;
    private readonly ScenarioInstance _scenario;
    private readonly DemoLlmProfiler _profiler;
    private readonly DemoTraceSink _traceSink;
    private readonly Dictionary<DemoBackendOptions, DemoBackend> _backends = [];
    private readonly List<DemoBackend> _backendCreationOrder = [];
    private readonly List<LlmPlayerDriver> _llmDrivers = [];
    private readonly Dictionary<string, DecisionBudgetPlayerDriver> _budgetDrivers =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPlayerDriver> _aiDrivers =
        new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, IPlayerDriver> _aiDriversView;
    private bool _disposed;

    private DemoLlmComposition(
        DemoOptions options,
        ScenarioInstance scenario,
        DemoLlmProfiler profiler,
        DemoTraceSink traceSink)
    {
        _options = options;
        _scenario = scenario;
        _profiler = profiler;
        _traceSink = traceSink;
        _aiDriversView = new ReadOnlyDictionary<string, IPlayerDriver>(_aiDrivers);
        Plan = DemoLlmRosterPlan.Create(options);
    }

    public DemoLlmRosterPlan Plan { get; }

    public IReadOnlyDictionary<string, IPlayerDriver> AiDrivers => _aiDriversView;

    public int ForcedSceneEndCount => checked(
        _budgetDrivers.Values.Sum(driver => driver.ForcedSceneEndCount));

    public static async ValueTask<DemoLlmComposition> CreateAsync(
        DemoOptions options,
        ScenarioInstance scenario,
        DemoLlmProfiler profiler,
        DemoTraceSink traceSink)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(profiler);
        ArgumentNullException.ThrowIfNull(traceSink);
        var composition = new DemoLlmComposition(options, scenario, profiler, traceSink);
        try
        {
            composition.Initialize();
            return composition;
        }
        catch (Exception creationError)
        {
            try
            {
                await composition.DisposeAsync();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(creationError, cleanupError);
            }

            throw;
        }
    }

    public async Task FlushMemoryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Task.WhenAll(_llmDrivers.Select(driver =>
            driver.FlushMemoryAsync(cancellationToken)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Exception>? errors = null;
        for (int index = _llmDrivers.Count - 1; index >= 0; index--)
        {
            try
            {
                await _llmDrivers[index].DisposeAsync();
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }

        for (int index = _backendCreationOrder.Count - 1; index >= 0; index--)
        {
            try
            {
                await _backendCreationOrder[index].DisposeAsync();
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }

        if (errors is not null)
        {
            throw new AggregateException("One or more Demo LLM resources failed to dispose.", errors);
        }
    }

    private void Initialize()
    {
        foreach (DemoAiActorPlan actor in Plan.AiActors)
        {
            ScenarioActorDefinition definition = _scenario.Definition.Actor(actor.ActorId);
            MemoryBank memory = DemoMemoryProfile.Create(definition);
            ILlmChatBackend decisionBackend = _profiler.Wrap(
                Backend(actor.DecisionBackend),
                Descriptor(actor.ActorId, "role-decision", shardKey: null, actor.DecisionBackend));
            ILlmChatBackend memoryBackend = Backend(_options.MemoryBackend);
            var llm = new LlmPlayerDriver(
                new CharacterCard(
                    definition.Role.Name,
                    definition.Role.Traits,
                    definition.Role.Goal,
                    definition.Role.Voice),
                memory,
                decisionBackend,
                DemoMemoryProfile.Maintainers(
                    memory,
                    shardKey => _profiler.Wrap(
                        memoryBackend,
                        Descriptor(
                            actor.ActorId,
                            "memory-maintenance",
                            shardKey,
                            _options.MemoryBackend))),
                _traceSink.Record,
                Materials(definition),
                _options.MemoryMaintenanceMode);
            var budget = new DecisionBudgetPlayerDriver(llm, _options.MaxTurnsPerActor);
            _llmDrivers.Add(llm);
            _budgetDrivers.Add(actor.ActorId, budget);
            _aiDrivers.Add(actor.ActorId, budget);
        }
    }

    private ILlmChatBackend Backend(DemoBackendOptions backendOptions)
    {
        if (_backends.TryGetValue(backendOptions, out DemoBackend? existing))
        {
            return existing.Client;
        }

        DemoBackend created = DemoBackend.Create(_options, backendOptions);
        _backends.Add(backendOptions, created);
        _backendCreationOrder.Add(created);
        return created.Client;
    }

    private DemoLlmCallDescriptor Descriptor(
        string actorId,
        string purpose,
        string? shardKey,
        DemoBackendOptions backend) =>
        new(
            actorId,
            purpose,
            shardKey,
            backend.Backend,
            backend.Model,
            backend.Backend == "codex"
                ? _options.ReasoningEffort ?? "provider-default"
                : "provider-default");

    private static IReadOnlyList<ReferenceMaterial> Materials(ScenarioActorDefinition actor) =>
        [
            .. actor.Role.ReferenceMaterials.Select(material => new ReferenceMaterial(
                material.Id,
                material.Source,
                material.Content)),
        ];
}
