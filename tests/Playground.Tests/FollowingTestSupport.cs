using DramaBoard.Kernel.Journal;
using DramaBoard.Kernel.Simulation;
using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;

namespace DramaBoard.Playground.Tests;

internal static class FollowingTestSupport {
    public static PlaygroundWorld World(int count = 3) => PlaygroundGameFactory.CreateSurvival(Hour(8),
        enableNeeds: false, enableWeather: false, characterCount: count).World;

    public static Script TwoLegScript() => new(
        [PlaygroundCommand.Handoff("集合后出发"), PlaygroundCommand.Go("e-c7"), PlaygroundCommand.Go("e-h5"),
            PlaygroundCommand.DismissFollowers(), PlaygroundCommand.Wait(1)],
        [PlaygroundCommand.Follow("a"), PlaygroundCommand.Wait(1)],
        [PlaygroundCommand.Follow("a"), PlaygroundCommand.Wait(1)]);

    public static CharacterInteractionState Interaction(PlaygroundWorld world, string actor) => world.Business.InteractionStates[actor];
    public static PlaygroundFact Last(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) => Assert.Single(kernel.LastCompletion!.Event.Facts);
    public static bool AllAt(PlaygroundWorld world, string place) => world.Expedition!.Spatial.Entities.All(entity =>
        entity.Location is AtPlaceLocation at && at.PlaceId.Value == place);

    public static void ValidateRuntime(PlaygroundWorld world) {
        world.Validate();
        PlaygroundWorld rebuilt = PlaygroundEngine.Rebuild(world.Business, world.Time);
        Assert.Equal(rebuilt.Forecasts.ToString(), world.Forecasts.ToString());
        Assert.Equal(rebuilt.Spatial!.Contacts, world.Spatial!.Contacts);
    }

    public static async Task Advance(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel) {
        Assert.Equal(StepStatus.Committed, await kernel.StepAsync(Hour(24)));
        ValidateRuntime(kernel.World);
    }

    public static async Task UntilFact(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel,
        Func<PlaygroundFact, bool> matches) {
        for (int step = 0; step < 100; step++) {
            await Advance(kernel);
            if (matches(Last(kernel))) { return; }
        }
        Assert.Fail("The following scenario did not reach the requested fact within 100 commits.");
    }

    public static async Task UntilState(SimulationKernel<PlaygroundWorld, PlaygroundPrediction, PlaygroundFact> kernel,
        Func<PlaygroundWorld, bool> matches) {
        for (int step = 0; step < 100; step++) {
            if (matches(kernel.World)) { return; }
            await Advance(kernel);
        }
        Assert.Fail("The following scenario did not reach the requested state within 100 commits.");
    }

    public static void AssertSameWorld(PlaygroundWorld expected, PlaygroundWorld actual) {
        Assert.Equal(expected.Time, actual.Time);
        Assert.Equal(MaterialSnapshot(expected.Business), MaterialSnapshot(actual.Business));
        Assert.Equal(expected.Forecasts.ToString(), actual.Forecasts.ToString());
        Assert.Equal(expected.Spatial!.Contacts, actual.Spatial!.Contacts);
        ValidateRuntime(actual);
    }

    public sealed class Script(PlaygroundCommand[] a, PlaygroundCommand[] b, PlaygroundCommand[]? c = null) : IPlaygroundCharacterController {
        private readonly Dictionary<string, Queue<PlaygroundCommand>> _commands = new(StringComparer.Ordinal) {
            ["a"] = new(a), ["b"] = new(b), ["c"] = new(c ?? [])
        };
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
        public int Calls(string actor) => _calls.GetValueOrDefault(actor);
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            string actor = context.Observation.Actor.Id;
            Assert.True(_commands[actor].Count > 0, $"Unexpected additional decision for {actor} at {context.Observation.Time}.");
            PlaygroundCommand command = _commands[actor].Dequeue();
            Assert.Null(context.Check(command));
            int call = _calls[actor] = Calls(actor) + 1;
            string id = $"following/{actor}/{call}";
            return ValueTask.FromResult(new PlaygroundControllerOperation(id, PlaygroundCommandParser.Format(command), id));
        }
    }

    public sealed class ForbiddenController : IPlaygroundCharacterController {
        public int Calls { get; private set; }
        public ValueTask<PlaygroundControllerOperation> ChooseCommandAsync(PlaygroundDecisionContext context, CancellationToken cancellationToken) {
            Calls++;
            throw new InvalidOperationException("Restoration and automatic arrivals must not request a controller decision.");
        }
    }

    public sealed class InjectedCrash : IOException { }
    public sealed class StopAfterEvent(IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> inner, Func<PlaygroundFact, bool> matches)
        : IOccurrenceHistory<PlaygroundWorld, PlaygroundFact> {
        public PlaygroundWorld State => inner.State;
        public KernelCursor Cursor => inner.Cursor;
        public OccurrenceEvent<PlaygroundFact>? PendingEvent => inner.PendingEvent;
        public void CommitEvent(OccurrenceEvent<PlaygroundFact> occurrence) {
            inner.CommitEvent(occurrence);
            if (occurrence.Facts.Any(matches)) { throw new InjectedCrash(); }
        }
        public void CommitState(PlaygroundWorld state, KernelCursor cursor) => inner.CommitState(state, cursor);
    }
}
