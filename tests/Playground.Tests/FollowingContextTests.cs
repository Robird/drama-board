using DramaBoard.Spatial;
using static DramaBoard.Playground.Tests.PlaygroundTestSupport;
using static DramaBoard.Playground.Tests.FollowingTestSupport;

namespace DramaBoard.Playground.Tests;

public sealed class FollowingContextTests {
    [Fact]
    public async Task Three_characters_travel_two_legs_without_reasking_followers_then_dismissal_delivers_original_follow_advance() {
        var script = TwoLegScript();
        var kernel = PlaygroundEngine.CreateKernel(Memory(World()), characterController: script);
        await UntilFact(kernel, fact => fact.Kind == PlaygroundEventKind.FollowStarted && fact.ActorId == "c");
        PendingAdvance original = Interaction(kernel.World, "b").PendingAdvance!;
        Assert.Equal(PlaygroundCommand.Follow("a"), original.Command);
        Assert.Null(original.ActivityId);
        Assert.Null(original.MovementGeneration);
        Assert.Equal("following/b/1", original.OperationRecordId);
        Assert.Equal(CharacterContextStatus.Closed, Interaction(kernel.World, "b").CurrentContext!.Status);
        Assert.False(PlaygroundActions.NeedsDecision(kernel.World, "b"));
        Assert.Equal(new[] { "b", "c" }, PlaygroundDecisionContext.Observe(kernel.World, "a").Followers.Keys.Order());

        for (int leg = 0; leg < 2; leg++) {
            await UntilFact(kernel, fact => fact.Kind == PlaygroundEventKind.MoveStarted);
            PlaygroundFact departure = Last(kernel);
            Assert.Equal("a", departure.ActorId);
            Assert.StartsWith("following/a/", departure.OperationRecordId);
            Assert.Equal(3, departure.SpatialFacts.Count);
            Assert.All(kernel.World.Expedition!.Spatial.Entities, entity => Assert.IsType<TraversingLocation>(entity.Location));
            foreach (string follower in new[] { "b", "c" }) {
                var sleeping = Interaction(kernel.World, follower);
                Assert.Equal(CharacterContextStatus.Closed, sleeping.CurrentContext!.Status);
                Assert.Equal(1, sleeping.CurrentContext.ContextSequence);
                Assert.Equal(PlaygroundCommandKind.Follow, sleeping.PendingAdvance!.Command.Kind);
                Assert.Null(sleeping.PendingAdvance.MovementGeneration);
                Assert.Contains(sleeping.UndeliveredExperiences, experience => experience.Event.Kind == PlaygroundObservedEventKind.MoveStarted);
                Assert.Equal(1, script.Calls(follower));
            }
            await UntilState(kernel, world => AllAt(world, leg == 0 ? "stream" : "ravine"));
            Assert.Equal(MaterialSnapshot(original), MaterialSnapshot(Interaction(kernel.World, "b").PendingAdvance));
            Assert.Equal(1, script.Calls("b"));
            Assert.Equal(1, script.Calls("c"));
        }

        await UntilFact(kernel, fact => fact.Kind == PlaygroundEventKind.FollowersDismissed);
        Assert.All(kernel.World.Characters, actor => Assert.Null(actor.FollowingLeaderId));
        Assert.Equal(CharacterContextStatus.Closed, Interaction(kernel.World, "b").CurrentContext!.Status);
        Assert.Equal(MaterialSnapshot(original), MaterialSnapshot(Interaction(kernel.World, "b").PendingAdvance));
        await UntilFact(kernel, fact => fact.Kind == PlaygroundEventKind.DecisionContextOpened && fact.ActorId == "b");
        CharacterInteractionState resumed = Interaction(kernel.World, "b");
        Assert.Null(resumed.PendingAdvance);
        Assert.Equal(2, resumed.CurrentContext!.ContextSequence);
        Assert.Equal(MaterialSnapshot(original), MaterialSnapshot(resumed.CurrentContext.Opening.PreviousAdvance));
        Assert.Null(resumed.CurrentContext.LatestObservation.FollowingLeaderId);
        Assert.Equal("ravine", resumed.CurrentContext.LatestObservation.PlaceId);
        Assert.Equal(2, resumed.CurrentContext.Opening.Experiences.Count(e => e.Event.Kind == PlaygroundObservedEventKind.MoveStarted));
        Assert.Equal(2, resumed.CurrentContext.Opening.Experiences.Count(e => e.Event.Kind == PlaygroundObservedEventKind.MoveArrived));
        Assert.All(resumed.CurrentContext.Opening.Experiences, e => Assert.Equal("b", e.Event.ObserverId));
        Assert.Equal(1, script.Calls("b"));
    }

    [Fact]
    public async Task Follow_acknowledges_received_speech_and_continue_can_resume_stationary_following_after_another_message() {
        var script = new Script([PlaygroundCommand.Say("b", "准备一起走"), PlaygroundCommand.Say("b", "再确认一下")],
            [PlaygroundCommand.Follow("a"), PlaygroundCommand.Continue()]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(World(2)), characterController: script);
        await UntilFact(kernel, fact => fact.Kind == PlaygroundEventKind.FollowStarted);
        Assert.False(kernel.World.GetCharacter("b").Communication.AttentionPending);
        Assert.False(PlaygroundActions.NeedsDecision(kernel.World, "b"));
        Assert.Equal(CharacterContextStatus.Closed, Interaction(kernel.World, "b").CurrentContext!.Status);
        Assert.Contains(Interaction(kernel.World, "b").CurrentContext!.Opening.Experiences,
            experience => experience.Event.Communication?.SpeechText == "准备一起走");

        await UntilFact(kernel, fact => fact.Kind == PlaygroundEventKind.SpeechSent);
        Assert.True(kernel.World.GetCharacter("b").Communication.AttentionPending);
        await UntilFact(kernel, fact => fact.Kind == PlaygroundEventKind.DecisionContextOpened && fact.ActorId == "b");
        CharacterContext opened = Interaction(kernel.World, "b").CurrentContext!;
        Assert.Equal(PlaygroundOutcomeActivityState.Ongoing, opened.Opening.ActivityState);
        Assert.Equal(PlaygroundCommandKind.Follow, opened.Opening.PreviousAdvance!.Command.Kind);
        Assert.Equal("a", opened.LatestObservation.FollowingLeaderId);
        Assert.Contains(opened.Opening.Experiences, experience => experience.Event.Communication?.SpeechText == "再确认一下");
        await Advance(kernel);
        var resumed = Interaction(kernel.World, "b");
        Assert.Equal(CharacterContextStatus.Closed, resumed.CurrentContext!.Status);
        Assert.Equal(PlaygroundCommand.Continue(), resumed.PendingAdvance!.Command);
        Assert.Null(resumed.PendingAdvance.ActivityId);
        Assert.Null(resumed.PendingAdvance.MovementGeneration);
        Assert.False(kernel.World.GetCharacter("b").Communication.AttentionPending);
        Assert.False(PlaygroundActions.NeedsDecision(kernel.World, "b"));
        Assert.Equal(2, script.Calls("b"));
    }

    [Fact]
    public async Task Leader_departure_updates_an_open_follower_context_without_consuming_its_draft_or_inventing_an_operation() {
        var script = new Script([PlaygroundCommand.Handoff("领队准备"), PlaygroundCommand.Say("b", "路上再回应"), PlaygroundCommand.Go("e-c7")],
            [PlaygroundCommand.Follow("a"), PlaygroundCommand.Handoff("队员自己的计划"), PlaygroundCommand.Continue()]);
        var kernel = PlaygroundEngine.CreateKernel(Memory(World(2)), characterController: script);
        await UntilFact(kernel, fact => fact.Kind == PlaygroundEventKind.HandoffEdited && fact.ActorId == "b");
        CharacterContext before = Interaction(kernel.World, "b").CurrentContext!;
        Assert.Equal(CharacterContextStatus.Open, before.Status);
        Assert.True(kernel.World.GetCharacter("b").Communication.AttentionPending);

        await UntilFact(kernel, fact => fact.Kind == PlaygroundEventKind.MoveStarted);
        CharacterInteractionState carried = Interaction(kernel.World, "b");
        Assert.Equal(CharacterContextStatus.Open, carried.CurrentContext!.Status);
        Assert.Equal(before.ContextSequence, carried.CurrentContext.ContextSequence);
        Assert.Equal(before.LastOperationSequence, carried.CurrentContext.LastOperationSequence);
        Assert.Equal("队员自己的计划", carried.DraftHandoff);
        Assert.Null(carried.PendingAdvance);
        Assert.True(kernel.World.GetCharacter("b").Communication.AttentionPending);
        CharacterContextItem movement = Assert.Single(carried.CurrentContext.Items.Skip(before.Items.Count));
        Assert.Null(movement.OperationSequence);
        Assert.Null(movement.ToolCallId);
        Assert.Null(movement.OperationRecordId);
        Assert.Null(movement.Result);
        Assert.Contains(movement.Experiences, experience => experience.Event.Kind == PlaygroundObservedEventKind.MoveStarted);
        Assert.NotNull(carried.CurrentContext.LatestObservation.Journey);
        Assert.Null(carried.CurrentContext.LatestObservation.PlaceId);

        await Advance(kernel);
        var released = Interaction(kernel.World, "b");
        Assert.Equal(CharacterContextStatus.Closed, released.CurrentContext!.Status);
        Assert.Equal(PlaygroundCommand.Continue(), released.PendingAdvance!.Command);
        Assert.Equal("following/b/3", released.PendingAdvance.OperationRecordId);
        Assert.Equal("队员自己的计划", released.PendingAdvance.Handoff);
        Assert.NotNull(released.PendingAdvance.MovementGeneration);
        Assert.Null(released.PendingAdvance.ActivityId);
        Assert.Null(released.DraftHandoff);
        Assert.False(kernel.World.GetCharacter("b").Communication.AttentionPending);
    }
}
