using DramaBoard.Host;
using DramaBoard.Protocol;

namespace DramaBoard.Player.Llm;

/// <summary>Controls whether memory maintenance blocks the current or the actor's next turn.</summary>
public enum MemoryMaintenanceMode
{
    Blocking,
    Pipelined,
}

/// <summary>Runs one self-contained LLM cognitive loop for a single actor.</summary>
public sealed class LlmPlayerDriver : IPlayerDriver, IAsyncDisposable
{
    private const string RetryInstruction =
        "[格式纠正]\n上次回复无法解析，请严格按格式重新回复，并确保【行动】包含单个合法 JSON 对象。";

    private readonly CharacterCard _characterCard;
    private readonly ILlmChatBackend _backend;
    private readonly Action<LlmTurnTrace>? _turnTraceSink;
    private readonly IReadOnlyList<ReferenceMaterial> _referenceMaterials;
    private readonly IReadOnlyList<IMemoryShardMaintainer> _memoryMaintainers;
    private readonly MemoryMaintenanceMode _memoryMaintenanceMode;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private IReadOnlyList<KnownFact> _previousKnownFacts = [];
    private MemoryBank _currentMemory;
    private PendingMemoryMaintenance? _pendingMemoryMaintenance;
    private bool _disposed;

    /// <summary>Initializes one actor's driver and private memory document.</summary>
    public LlmPlayerDriver(
        CharacterCard characterCard,
        MemoryBank initialMemory,
        ILlmChatBackend backend,
        IReadOnlyList<IMemoryShardMaintainer> memoryMaintainers,
        Action<LlmTurnTrace>? turnTraceSink = null,
        IReadOnlyList<ReferenceMaterial>? referenceMaterials = null,
        MemoryMaintenanceMode memoryMaintenanceMode = MemoryMaintenanceMode.Blocking)
    {
        ArgumentNullException.ThrowIfNull(characterCard);
        ArgumentNullException.ThrowIfNull(initialMemory);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(memoryMaintainers);

        _characterCard = characterCard;
        _currentMemory = initialMemory;
        _backend = backend;
        _turnTraceSink = turnTraceSink;
        _referenceMaterials = referenceMaterials is null ? [] : [.. referenceMaterials];
        _memoryMaintainers = OrderAndValidateMaintainers(initialMemory, memoryMaintainers);
        _memoryMaintenanceMode = memoryMaintenanceMode;
    }

    /// <summary>Gets the actor's latest private memory snapshot.</summary>
    public MemoryBank CurrentMemoryBank => _currentMemory;

    /// <summary>Gets the actor's latest private memory rendered as one document.</summary>
    public string CurrentMemory => _currentMemory.Render();

    /// <inheritdoc />
    public async ValueTask<PlayerDecision> DecideAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        await FlushMemoryAsync(cancellationToken);

        LlmChatRequest prompt = PromptRenderer.Render(
            _characterCard,
            _currentMemory,
            request,
            _previousKnownFacts,
            _referenceMaterials);
        string response = (await _backend.CompleteAsync(prompt, cancellationToken)).Content;
        LlmOutputParseResult parsed = LlmOutputParser.Parse(response);
        int attemptCount = 1;
        if (!parsed.IsSuccess)
        {
            attemptCount = 2;
            var retry = prompt with { User = $"{prompt.User}\n\n{RetryInstruction}" };
            response = (await _backend.CompleteAsync(retry, cancellationToken)).Content;
            parsed = LlmOutputParser.Parse(response);
        }

        if (!parsed.IsSuccess)
        {
            return CreateDecision(request, new Intent(ActionKinds.Wait));
        }

        PlayerDecision decision = CreateDecision(request, parsed.Intent!);
        var maintenanceContext = new MemoryMaintenanceContext(
            _characterCard,
            _referenceMaterials,
            _currentMemory,
            request,
            parsed.Monologue,
            parsed.Intent!,
            parsed.Dialogue,
            parsed.Memory);
        if (_memoryMaintenanceMode == MemoryMaintenanceMode.Pipelined)
        {
            _pendingMemoryMaintenance = new PendingMemoryMaintenance(
                request,
                decision,
                parsed.Monologue,
                parsed.Dialogue,
                parsed.Memory,
                attemptCount,
                MaintainMemoryAsync(maintenanceContext, _lifetimeCancellation.Token));
            return decision;
        }

        (MemoryBank updatedMemory, IReadOnlyList<MemoryShardMaintenanceTrace> maintenanceTraces) =
            await MaintainMemoryAsync(maintenanceContext, cancellationToken);
        CommitMaintenance(
            new PendingMemoryMaintenance(
                request,
                decision,
                parsed.Monologue,
                parsed.Dialogue,
                parsed.Memory,
                attemptCount,
                Task.FromResult((updatedMemory, maintenanceTraces))),
            updatedMemory,
            maintenanceTraces);

        return decision;
    }

    /// <summary>Waits for and commits this actor's previously pipelined memory maintenance.</summary>
    public async Task FlushMemoryAsync(CancellationToken cancellationToken = default)
    {
        PendingMemoryMaintenance? pending = _pendingMemoryMaintenance;
        if (pending is null)
        {
            return;
        }

        (MemoryBank memory, IReadOnlyList<MemoryShardMaintenanceTrace> traces) =
            await pending.Task.WaitAsync(cancellationToken);
        _pendingMemoryMaintenance = null;
        CommitMaintenance(pending, memory, traces);
    }

    /// <summary>Cancels unfinished private maintenance without disposing shared LLM backends.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifetimeCancellation.CancelAsync();
        try
        {
            await FlushMemoryAsync();
        }
        catch (OperationCanceledException)
        {
            _pendingMemoryMaintenance = null;
        }
        finally
        {
            _lifetimeCancellation.Dispose();
        }
    }

    private void CommitMaintenance(
        PendingMemoryMaintenance pending,
        MemoryBank updatedMemory,
        IReadOnlyList<MemoryShardMaintenanceTrace> maintenanceTraces)
    {
        MemoryBank previousMemory = _currentMemory;
        IReadOnlyList<KnownFact> previousKnownFacts = _previousKnownFacts;
        _currentMemory = updatedMemory;
        _previousKnownFacts = [.. pending.Request.Observation.KnownFacts];
        try
        {
            _turnTraceSink?.Invoke(new LlmTurnTrace(
                pending.Request,
                pending.Decision,
                pending.Monologue,
                pending.Dialogue,
                pending.MemoryProposal,
                updatedMemory.Render(),
                maintenanceTraces,
                pending.AttemptCount));
        }
        catch
        {
            _currentMemory = previousMemory;
            _previousKnownFacts = previousKnownFacts;
            throw;
        }
    }

    private async Task<(MemoryBank Memory, IReadOnlyList<MemoryShardMaintenanceTrace> Traces)>
        MaintainMemoryAsync(
            MemoryMaintenanceContext context,
            CancellationToken cancellationToken)
    {
        Task<MaintenanceOutcome>[] tasks = _memoryMaintainers
            .Select(maintainer => MaintainOneAsync(maintainer, context, cancellationToken))
            .ToArray();
        MaintenanceOutcome[] outcomes = await Task.WhenAll(tasks);
        MemoryBank updated = context.PreviousMemory;
        foreach (MaintenanceOutcome outcome in outcomes)
        {
            if (outcome.Update is { IsReplacement: true, Content: not null } update)
            {
                updated = updated.Replace(update.ShardKey, update.Content);
            }
        }

        return (updated, outcomes.Select(outcome => outcome.Trace).ToArray());
    }

    private static async Task<MaintenanceOutcome> MaintainOneAsync(
        IMemoryShardMaintainer maintainer,
        MemoryMaintenanceContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            MemoryShardUpdate update = await maintainer.MaintainAsync(context, cancellationToken);
            if (!string.Equals(update.ShardKey, maintainer.ShardKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Maintainer '{maintainer.ShardKey}' returned update for '{update.ShardKey}'.");
            }

            if (update.IsReplacement && string.IsNullOrWhiteSpace(update.Content))
            {
                throw new InvalidOperationException(
                    $"Maintainer '{maintainer.ShardKey}' returned a blank replacement.");
            }

            return new MaintenanceOutcome(
                update,
                new MemoryShardMaintenanceTrace(
                    maintainer.ShardKey,
                    update.IsReplacement
                        ? MemoryMaintenanceOperation.Replace
                        : MemoryMaintenanceOperation.Keep,
                    Error: null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new MaintenanceOutcome(
                Update: null,
                new MemoryShardMaintenanceTrace(
                    maintainer.ShardKey,
                    MemoryMaintenanceOperation.FallbackKeep,
                    $"{exception.GetType().Name}: {exception.Message}"));
        }
    }

    private static IReadOnlyList<IMemoryShardMaintainer> OrderAndValidateMaintainers(
        MemoryBank initialMemory,
        IReadOnlyList<IMemoryShardMaintainer> maintainers)
    {
        if (maintainers.Count != initialMemory.Shards.Count)
        {
            throw new ArgumentException(
                "There must be exactly one maintainer for every memory shard.",
                nameof(maintainers));
        }

        var byKey = new Dictionary<string, IMemoryShardMaintainer>(StringComparer.Ordinal);
        foreach (IMemoryShardMaintainer maintainer in maintainers)
        {
            ArgumentNullException.ThrowIfNull(maintainer);
            if (!byKey.TryAdd(maintainer.ShardKey, maintainer))
            {
                throw new ArgumentException(
                    $"Duplicate memory maintainer key '{maintainer.ShardKey}'.",
                    nameof(maintainers));
            }
        }

        return initialMemory.Shards.Select(shard =>
            byKey.TryGetValue(shard.Key, out IMemoryShardMaintainer? maintainer)
                ? maintainer
                : throw new ArgumentException(
                    $"Memory shard '{shard.Key}' has no maintainer.",
                    nameof(maintainers))).ToArray();
    }

    private static PlayerDecision CreateDecision(DecisionRequest request, Intent intent) =>
        new(
            request.DecisionId,
            request.BasedOnWorldVersion,
            request.LineageId,
            intent);

    private sealed record MaintenanceOutcome(
        MemoryShardUpdate? Update,
        MemoryShardMaintenanceTrace Trace);

    private sealed record PendingMemoryMaintenance(
        DecisionRequest Request,
        PlayerDecision Decision,
        string Monologue,
        string? Dialogue,
        string MemoryProposal,
        int AttemptCount,
        Task<(MemoryBank Memory, IReadOnlyList<MemoryShardMaintenanceTrace> Traces)> Task);
}
