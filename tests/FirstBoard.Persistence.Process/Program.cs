using System.Collections;
using System.Reflection;
using System.Text.Json;
using DramaBoard.FirstBoard;
using DramaBoard.FirstBoard.Persistence;
using DramaBoard.FirstBoard.Tests;
using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Scheduling;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Kernel.Time;
using DramaBoard.Player;
using DramaBoard.Protocol;

namespace DramaBoard.FirstBoard.Persistence.Process;

/// <summary>Locates the separately launched, real-package persistence consumer.</summary>
public static class ProcessWitness
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "inspect")
        {
            Console.WriteLine(JsonSerializer.Serialize(PersistenceMetrics.Inspect(args[1])));
            return;
        }
        if (args.Length != 4)
        {
            throw new ArgumentException("Expected create|open|pending, save directory, Continue|Reverse, step count; or inspect, closed save directory.");
        }
        string mode = args[0];
        string path = args[1];
        PassageEncounterResolution response = args[2] == "Continue"
            ? PassageEncounterResolution.Continued : PassageEncounterResolution.Reversed;
        int steps = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        var binding = new FirstBoardDriverBinding([BoardIds.Alice], $"encounter-pure-{args[2]}-v1");
        using FirstBoardOccurrenceHistory history = mode == "open"
            ? FirstBoardOccurrenceHistory.Open(path, "main", binding)
            : Create(path, binding);
        var measured = new MeasuredHistory(history);

        var completions = new List<object?>();
        int folds = 0;
        var forbidden = new ForbiddenRecoveryRule();
        var reducer = new FirstBoardReducer(history.Scenario.Graph);
        int pendingFacts = history.PendingEvent?.Facts.Count ?? 0;
        var recovery = new SimulationKernel<FirstBoardWorld, BoardCandidate, FirstBoardFact>(
            measured, history.Rules, [forbidden],
            (world, instant, fact) => { folds++; return reducer.Apply(world, instant, fact); },
            reducer.Validate);
        bool recovered = recovery.RecoverPending();
        if (recovered) { completions.Add(Canonical(recovery.LastCompletion!.Event)); }

        IOccurrenceHistory<FirstBoardWorld, FirstBoardFact> active = mode == "pending"
            ? new InterruptBeforeState(measured, steps) : measured;
        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = new EncounterPersistenceOracle.FixedEncounterResponseDriver(response),
        };
        var kernel = FirstBoardScenario.CreateKernel(drivers, history.Scenario, active);
        for (int index = 0; index < steps + (mode == "pending" ? 1 : 0); index++)
        {
            try
            {
                StepStatus status = await kernel.StepAsync(EncounterPersistenceOracle.ContactDue);
                Assert.Equal(StepStatus.Committed, status);
                completions.Add(Canonical(kernel.LastCompletion!.Event));
            }
            catch (InvalidOperationException error) when (error.InnerException is StopBeforeStateException)
            {
                Assert.Equal("pending", mode);
                Assert.NotNull(history.PendingEvent);
                break;
            }
        }

        object? nextRequest = history.PendingEvent is null
            ? await FindNextRequest(history) : null;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            World = Canonical(history.State),
            Cursor = Canonical(history.Cursor),
            NextRequest = nextRequest,
            Completions = completions,
            Pending = Canonical(history.PendingEvent),
            Recovered = recovered,
            RecoveryFolds = folds,
            PendingFactCount = pendingFacts,
            RecoveryForecastCalls = forbidden.ForecastCalls,
            RecoveryPlanCalls = forbidden.PlanCalls,
            CommitMetricsScope = "Synchronous adapter calls only; excludes S0 creation, planning, scratch fold, diagnostics, and other threads; no performance threshold.",
            CommitMetrics = measured.Measurements,
        }));
    }

    private static FirstBoardOccurrenceHistory Create(string path, FirstBoardDriverBinding binding)
    {
        var s0 = EncounterPersistenceOracle.CreateTravelingS0();
        EncounterPersistenceOracle.AssertS0(s0);
        return FirstBoardOccurrenceHistory.Create(path, "main", s0.Instance, s0.World,
            s0.History.Cursor, new SimulationRules(s0.Instance.WorldSeed, 10_000), binding);
    }

    private static async Task<object?> FindNextRequest(FirstBoardOccurrenceHistory saved)
    {
        // Pure fold keeps this diagnostic look-ahead off the authoritative save.
        var memory = new InMemoryOccurrenceHistory<FirstBoardWorld, FirstBoardFact>(saved.State, saved.Cursor);
        var drivers = new Dictionary<string, IPlayerDriver>(StringComparer.Ordinal)
        {
            [BoardIds.Alice] = new CaptureRequestDriver(),
        };
        var kernel = FirstBoardScenario.CreateKernel(drivers, saved.Scenario, memory);
        for (int index = 0; index < 32; index++)
        {
            try
            {
                if (await kernel.StepAsync(new ModelTime(4_200_000)) != StepStatus.Committed)
                {
                    return null;
                }
            }
            catch (CapturedRequestException captured) { return Canonical(captured.Request); }
        }
        throw new InvalidOperationException("The encounter witness did not reach its next Player request.");
    }

    /// <summary>Diagnostic JSON only. Never read to reconstruct a world or feed a reducer.
    /// Walks every declared durable field (including private and inherited fields), so the
    /// oracle cannot silently omit a newly saved member or a polymorphic location/fact case.</summary>
    private static object? Canonical(object? value)
    {
        if (value is null || value is string || value is bool || value is char || value is decimal) { return value; }
        Type type = value.GetType();
        if (type.IsPrimitive) { return value; }
        if (type.IsEnum) { return $"{type.FullName}:{value}"; }
        if (value is byte[] bytes) { return Convert.ToHexString(bytes); }
        if (value is IEnumerable sequence) { return sequence.Cast<object?>().Select(Canonical).ToArray(); }
        var fields = new List<FieldInfo>();
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            fields.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(field => field.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "DurableFieldAttribute")));
        }
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["$type"] = type.FullName };
        if (fields.Count != 0)
        {
            foreach (FieldInfo field in fields) { result[field.Name] = Canonical(field.GetValue(value)); }
        }
        else
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(property => property.CanRead && property.GetIndexParameters().Length == 0))
            {
                result[property.Name] = Canonical(property.GetValue(value));
            }
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                result[field.Name] = Canonical(field.GetValue(value));
            }
        }
        return result;
    }

    private sealed class CapturedRequestException(DecisionRequest request) : Exception
    {
        public DecisionRequest Request { get; } = request;
    }
    private sealed class CaptureRequestDriver : IPlayerDriver
    {
        public ValueTask<PlayerDecision> DecideAsync(DecisionRequest request, CancellationToken cancellationToken) =>
            throw new CapturedRequestException(request);
    }
    private sealed class StopBeforeStateException : Exception;
    private sealed class InterruptBeforeState(IOccurrenceHistory<FirstBoardWorld, FirstBoardFact> inner, int completeFirst) : IOccurrenceHistory<FirstBoardWorld, FirstBoardFact>
    {
        private int _states;
        public FirstBoardWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<FirstBoardFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<FirstBoardFact> occurrence) => inner.CommitEvent(occurrence);
        public void CommitState(FirstBoardWorld nextState, KernelCursor nextCursor)
        {
            if (_states++ == completeFirst) { throw new StopBeforeStateException(); }
            inner.CommitState(nextState, nextCursor);
        }
    }
    private sealed class ForbiddenRecoveryRule : IOccurrenceRule<FirstBoardWorld, BoardCandidate, FirstBoardFact>
    {
        public int ForecastCalls { get; private set; }
        public int PlanCalls { get; private set; }
        public IReadOnlyList<OccurrenceCandidate<BoardCandidate>> Forecast(FirstBoardWorld world, SimulationRules rules)
        {
            ForecastCalls++;
            throw new InvalidOperationException("Recovery must not forecast.");
        }
        public ValueTask<TransitionDraft<FirstBoardFact>> PlanSelectedAsync(FirstBoardWorld world,
            OccurrenceCandidate<BoardCandidate> winner, CancellationToken cancellationToken)
        {
            PlanCalls++;
            throw new InvalidOperationException("Recovery must not plan or invoke a Player.");
        }
    }
}
