using Atelia.DurableGraph.Persistence;
using DramaBoard.Kernel;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;

namespace DramaBoard.FirstBoard.Persistence;

/// <summary>Owns one FirstBoard EventHistory repository and its single active branch session.
/// Open restores the exact saved world and pending Event without processing it or calling a Player.
/// Any commit exception stops this adapter; dispose and reopen to determine the published result.</summary>
public sealed class FirstBoardOccurrenceHistory : IOccurrenceHistory<FirstBoardWorld, FirstBoardFact>, IDisposable
{
    private readonly EventHistoryRepository _repository;
    private readonly EventHistorySession<FirstBoardCommittedState> _session;
    private readonly FirstBoardReducer _validator;
    private bool _faulted;
    private bool _disposed;

    private FirstBoardOccurrenceHistory(EventHistoryRepository repository,
        EventHistorySession<FirstBoardCommittedState> session, ScenarioInstance scenario,
        SimulationRules rules, FirstBoardDriverBinding drivers)
    {
        _repository = repository;
        _session = session;
        Scenario = scenario;
        Rules = rules;
        DriverBinding = drivers;
        _validator = new FirstBoardReducer(scenario.Graph);
    }

    public ScenarioInstance Scenario { get; }
    public SimulationRules Rules { get; }
    public FirstBoardDriverBinding DriverBinding { get; }
    public bool IsFaulted => _faulted || _repository.IsFaulted;
    public FirstBoardWorld State { get { RequireUsable(); return _session.State.World; } }
    public KernelCursor Cursor { get { RequireUsable(); return _session.State.Cursor; } }
    public OccurrenceEvent<FirstBoardFact>? PendingEvent
    {
        get
        {
            RequireUsable();
            return _session.PendingEvent is null ? null : _session.GetPendingEvent<OccurrenceEvent<FirstBoardFact>>();
        }
    }

    /// <summary>Creates a new repository whose first State is the supplied complete boundary.
    /// A hand-built test prefix should supply a zero-count cursor with GenesisTime equal to world.Now.</summary>
    public static FirstBoardOccurrenceHistory Create(string path, string branchName, ScenarioInstance scenario,
        FirstBoardWorld initialWorld, KernelCursor cursor, SimulationRules rules, FirstBoardDriverBinding driverBinding)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(initialWorld);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(driverBinding);
        var binding = new FirstBoardRunBinding(scenario, rules, driverBinding);
        ValidateBoundary(initialWorld, cursor, scenario, rules, driverBinding, new FirstBoardReducer(scenario.Graph));
        var state = new FirstBoardCommittedState(initialWorld, cursor, binding);
        EventHistoryRepository repository = EventHistoryRepository.CreateNew(path);
        try
        {
            EventHistorySession<FirstBoardCommittedState> session = repository.CreateBranch(branchName, state, CreateModels());
            return new FirstBoardOccurrenceHistory(repository, session, scenario, rules, driverBinding);
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }

    /// <summary>Restores saved content and rules, requiring the supplied strategy binding to match.
    /// Optional expected scenario/rules add strict caller checks; omission uses the saved configuration.
    /// PendingEvent is returned untouched and must be completed through the Kernel's recovery entry.</summary>
    public static FirstBoardOccurrenceHistory Open(string path, string branchName,
        FirstBoardDriverBinding expectedDrivers, ScenarioInstance? expectedScenario = null,
        SimulationRules? expectedRules = null)
    {
        ArgumentNullException.ThrowIfNull(expectedDrivers);
        EventHistoryRepository repository = EventHistoryRepository.OpenExisting(path);
        EventHistorySession<FirstBoardCommittedState>? session = null;
        try
        {
            session = repository.Resume<FirstBoardCommittedState>(branchName, CreateModels());
            FirstBoardCommittedState saved = session.State;
            if (saved.World is null || saved.Binding is null)
            {
                throw new InvalidDataException("The saved FirstBoard State is incomplete.");
            }
            FirstBoardStoredGraphValidation.State(saved.World);
            ScenarioInstance scenario = saved.Binding.RestoreScenario(saved.World.WorldSeed);
            SimulationRules rules = saved.Binding.RestoreRules(saved.World.WorldSeed);
            FirstBoardDriverBinding drivers = saved.Binding.DriverBinding;
            if (!drivers.Matches(expectedDrivers))
            {
                throw new InvalidDataException("The supplied driver actors or strategy policy do not match the saved run.");
            }
            if (expectedScenario is not null &&
                (expectedScenario.WorldSeed != scenario.WorldSeed ||
                 !expectedScenario.Definition.ToCanonicalJsonUtf8().AsSpan().SequenceEqual(saved.Binding.CopyDefinitionContent())))
            {
                throw new InvalidDataException("The supplied scenario does not match the exact saved scenario and seed.");
            }
            if (expectedRules is not null &&
                (expectedRules.WorldSeed != rules.WorldSeed ||
                 expectedRules.MaxTransitionsPerModelTime != rules.MaxTransitionsPerModelTime))
            {
                throw new InvalidDataException("The supplied scheduling rules do not match the saved configuration.");
            }
            ValidateBoundary(saved.World, saved.Cursor, scenario, rules, drivers, new FirstBoardReducer(scenario.Graph));
            if (session.PendingEvent is not null)
            {
                // Target-instant/cause compatibility is checked by Kernel recovery before any fold.
                FirstBoardStoredGraphValidation.Event(session.GetPendingEvent<OccurrenceEvent<FirstBoardFact>>());
            }
            return new FirstBoardOccurrenceHistory(repository, session, scenario, rules, drivers);
        }
        catch
        {
            session?.Dispose();
            repository.Dispose();
            throw;
        }
    }

    public void CommitEvent(OccurrenceEvent<FirstBoardFact> occurrence)
    {
        RequireUsable();
        try
        {
            ArgumentNullException.ThrowIfNull(occurrence);
            FirstBoardStoredGraphValidation.Event(occurrence);
            _session.CommitDomainEvent(occurrence);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    public void CommitState(FirstBoardWorld nextState, KernelCursor nextCursor)
    {
        RequireUsable();
        try
        {
            ArgumentNullException.ThrowIfNull(nextState);
            OccurrenceEvent<FirstBoardFact> pending = PendingEvent
                ?? throw new InvalidOperationException("A State must complete a pending occurrence.");
            if (nextCursor != Cursor.Advance(pending.CauseKey, pending.TargetInstant))
            {
                throw new ArgumentException("The next cursor must complete exactly the pending occurrence.", nameof(nextCursor));
            }
            ValidateBoundary(nextState, nextCursor, Scenario, Rules, DriverBinding, _validator);
            var next = new FirstBoardCommittedState(nextState, nextCursor, _session.State.Binding);
            _session.CommitDomainState(next);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        try { _session.Dispose(); }
        finally { _repository.Dispose(); }
    }

    private static StateModelRegistry CreateModels()
    {
        var models = new StateModelRegistry();
        KernelDurableModels.Register(models);
        SpatialDurableModels.Register(models);
        FirstBoardDurableModels.Register(models);
        return models;
    }

    private static void ValidateBoundary(FirstBoardWorld world, KernelCursor cursor, ScenarioInstance scenario,
        SimulationRules rules, FirstBoardDriverBinding drivers, FirstBoardReducer validator)
    {
        FirstBoardStoredGraphValidation.State(world);
        cursor.Validate();
        if (world.WorldSeed != scenario.WorldSeed || world.WorldSeed != rules.WorldSeed ||
            world.Now != cursor.CurrentModelTime || rules.MaxTransitionsPerModelTime <= 0 ||
            cursor.LastInstant is { } last && last.CausalOrdinal >= rules.MaxTransitionsPerModelTime)
        {
            throw new InvalidDataException("The world, scheduling cursor and run configuration do not describe one complete boundary.");
        }
        validator.Validate(world);
        long[] ids = world.Actors.Select(actor => actor.Id).Concat(world.Objects.Select(item => item.Id)).ToArray();
        if (ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length ||
            world.Game.NextPersistentId <= 0 || ids.Any(id => id >= world.Game.NextPersistentId))
        {
            throw new InvalidDataException("NextPersistentId must follow all existing actor and object identities.");
        }
        HashSet<string> actors = world.Actors.Select(actor => actor.Key).ToHashSet(StringComparer.Ordinal);
        HashSet<string> definedActors = scenario.Definition.Actors.Select(actor => actor.Id).ToHashSet(StringComparer.Ordinal);
        if (drivers.ActorIds.Any(id => !actors.Contains(id) || !definedActors.Contains(id)))
        {
            throw new InvalidDataException("Every driver must be bound to an actor in the saved world and scenario.");
        }
    }

    private void RequireUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsFaulted)
        {
            throw new InvalidOperationException("This history attempt has stopped; dispose and reopen before further use.");
        }
    }
}
