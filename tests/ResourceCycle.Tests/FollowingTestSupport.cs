using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;

namespace DramaBoard.ResourceCycle.Tests;

internal static class FollowingTestSupport {
    public static CycleWorld World(int count = 3) => CycleGameFactory.CreateSurvival(Hour(8),
        enableNeeds: false, enableWeather: false, characterCount: count).World;

    public static Script TwoLegScript() => new(
        [CycleCommand.Handoff("集合后出发"), CycleCommand.Go("trail-1"), CycleCommand.Go("trail-3"),
            CycleCommand.DismissFollowers(), CycleCommand.Wait(1)],
        [CycleCommand.Follow("a"), CycleCommand.Wait(1)],
        [CycleCommand.Follow("a"), CycleCommand.Wait(1)]);

    public static CharacterInteractionState Interaction(CycleWorld world, string actor) => world.Business.InteractionStates[actor];
    public static CycleFact Last(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) => Assert.Single(kernel.LastCompletion!.Event.Facts);
    public static bool AllAt(CycleWorld world, string place) => world.Expedition!.Spatial.Entities.All(entity =>
        entity.Location is AtPlaceLocation at && at.PlaceId.Value == place);

    public static void ValidateRuntime(CycleWorld world) {
        world.Validate();
        CycleWorld rebuilt = CycleEngine.Rebuild(world.Business, world.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), world.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts, world.Spatial!.Contacts);
    }

    public static async Task Advance(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
        ValidateRuntime(kernel.World);
    }

    public static async Task UntilFact(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel,
        Func<CycleFact, bool> matches) {
        for (int step = 0; step < 100; step++) {
            await Advance(kernel);
            if (matches(Last(kernel))) { return; }
        }
        Assert.Fail("The following scenario did not reach the requested fact within 100 commits.");
    }

    public static async Task UntilState(SimulationKernel<CycleWorld, CyclePrediction, CycleFact> kernel,
        Func<CycleWorld, bool> matches) {
        for (int step = 0; step < 100; step++) {
            if (matches(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("The following scenario did not reach the requested state within 100 commits.");
    }

    public static void AssertSameWorld(CycleWorld expected, CycleWorld actual) {
        Assert.Equal(expected.Time, actual.Time);
        Assert.Equal(MaterialSnapshot(expected.Business), MaterialSnapshot(actual.Business));
        Assert.Equal(expected.Forecasts.ToString(), actual.Forecasts.ToString());
        Assert.Equal(expected.Spatial!.Contacts, actual.Spatial!.Contacts);
        ValidateRuntime(actual);
    }

    public sealed class Script(CycleCommand[] a, CycleCommand[] b, CycleCommand[]? c = null) : ICycleCharacterController {
        private readonly Dictionary<string, Queue<CycleCommand>> _commands = new(StringComparer.Ordinal) {
            ["a"] = new(a), ["b"] = new(b), ["c"] = new(c ?? [])
        };
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
        public int Calls(string actor) => _calls.GetValueOrDefault(actor);
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            string actor = context.Observation.Actor.Id;
            Assert.True(_commands[actor].Count > 0, $"Unexpected additional decision for {actor} at {context.Observation.Time}.");
            CycleCommand command = _commands[actor].Dequeue();
            Assert.Null(context.Check(command));
            int call = _calls[actor] = Calls(actor) + 1;
            string id = $"following/{actor}/{call}";
            return ValueTask.FromResult(new CycleControllerOperation(id, CycleCommandParser.Format(command), id));
        }
    }

    public sealed class ForbiddenController : ICycleCharacterController {
        public int Calls { get; private set; }
        public ValueTask<CycleControllerOperation> ChooseCommandAsync(CycleDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            throw new InvalidOperationException("Restoration and automatic arrivals must not request a controller decision.");
        }
    }

    public sealed class InjectedCrash : IOException { }
    public sealed class StopAfterEvent(IOccurrenceHistory<CycleWorld, CycleFact> inner, Func<CycleFact, bool> matches)
        : IOccurrenceHistory<CycleWorld, CycleFact> {
        public CycleWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<CycleFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<CycleFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(matches)) { throw new InjectedCrash(); }
        }
        public void CommitState(CycleWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}
