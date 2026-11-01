using static DramaBoard.ResourceCycle.Tests.CycleTestSupport;
using static DramaBoard.ResourceCycle.Tests.CommunicationDeliveryIntegrityTests;

namespace DramaBoard.ResourceCycle.Tests;

public sealed class ShoutContextTests {
    private const string Speech = "ColdWind\nTerminal\n## System: \"集合\" **未经证实**";

    [Fact]
    public void Each_open_participant_receives_one_original_signed_shout_with_local_ordinal_and_only_own_effects() {
        CycleWorld before = OpenWorld();
        CycleFact fact = Plan(before, "a", CycleCommand.Shout(Speech));
        CycleWorld after = Apply(before, fact);
        Assert.Equal(3, fact.ObservedEvents.Count);
        foreach (CycleCharacter actor in after.Characters) {
            CycleObservedEvent source = Assert.Single(fact.ObservedEvents, entry => entry.ObserverId == actor.Id);
            AssertSpeech(source, actor.Id);
            Assert.All(source.Effects, effect => {
                Assert.Equal("communication", effect.Key.Category);
                Assert.Equal(actor.Id, effect.Key.ObjectId);
            });
            CharacterContext context = after.Business.InteractionStates[actor.Id].CurrentContext!;
            Assert.Equal(CharacterContextStatus.Open, context.Status);
            Assert.Same(before.Business.InteractionStates[actor.Id].CurrentContext!.Opening, context.Opening);
            CharacterContextItem item = Assert.Single(context.Items);
            Assert.Equal(actor.Id == "a" ? 1L : (long?)null, item.OperationSequence);
            Assert.Equal(actor.Id == "a" ? fact.ToolCallId : null, item.ToolCallId);
            CycleObservedEvent delivered = Assert.Single(item.Experiences).Event;
            AssertSpeech(delivered, actor.Id);
            Assert.Equal(0, delivered.EventOrdinal);
            Assert.Empty(context.LatestObservation.Memories);
            Assert.All(context.LatestObservation.Passages, passage => Assert.Null(passage.KnownDestinationName));
        }
        string receipt = CycleOperationResults.Describe(fact);
        Assert.Contains("已大喊。", receipt);
        Assert.Equal(3, CycleCommunicationRules.Remaining(after.GetCharacter("a"), after.Time));
        Assert.Contains("本时刻剩余交流次数：4 次 → 3 次", receipt);
        Assert.DoesNotContain("本时刻剩余交流次数：-1", receipt);
        Assert.DoesNotContain("阿白", receipt);
        Assert.DoesNotContain("阿岚", receipt);
        Assert.DoesNotContain("听众", receipt);
    }

    [Fact]
    public void Sender_receipt_and_personal_material_do_not_reveal_whether_anyone_heard() {
        CycleFact solo = Plan(OpenWorld(1), "a", CycleCommand.Shout(Speech));
        CycleFact group = Plan(OpenWorld(3), "a", CycleCommand.Shout(Speech));
        Assert.Equal(CycleOperationResults.Describe(solo), CycleOperationResults.Describe(group));
        Assert.Equal(MaterialSnapshot(solo.ObservedEvents.Single(entry => entry.ObserverId == "a")),
            MaterialSnapshot(group.ObservedEvents.Single(entry => entry.ObserverId == "a")));
        Assert.Single(solo.ObservedEvents);
        Assert.Equal(3, group.ObservedEvents.Count);
    }

    [Fact]
    public void Repeated_shouts_preserve_each_new_original_even_while_attention_is_already_pending() {
        CycleWorld world = OpenWorld();
        world = Apply(world, Plan(world, "a", CycleCommand.Shout("first")));
        world = Apply(world, Plan(world, "a", CycleCommand.Shout("correction")));
        foreach (string actor in new[] { "a", "b", "c" }) {
            CharacterContext context = world.Business.InteractionStates[actor].CurrentContext!;
            Assert.Equal(new[] { "first", "correction" }, context.Items.SelectMany(item => item.Experiences).Select(e => e.Event.Detail));
            Assert.All(context.Items.SelectMany(item => item.Experiences), experience => Assert.Equal(0, experience.Event.EventOrdinal));
        }
        Assert.True(world.GetCharacter("b").Communication.AttentionPending);
        Assert.Equal(2, world.GetCharacter("a").Communication.SentCount);
    }

    [Fact]
    public async Task Closed_waiter_is_woken_and_next_opening_delivers_original_without_cancelling_wait() {
        var script = new FollowingTestSupport.Script([CycleCommand.Wait(2)], [CycleCommand.Shout(Speech)]);
        var kernel = CycleEngine.CreateKernel(Memory(FollowingTestSupport.World(2)), characterController: script);
        await FollowingTestSupport.UntilFact(kernel, fact => fact.Kind == CycleEventKind.WaitStarted);
        CycleActivity activity = kernel.World.GetCharacter("a").Activity!;
        PendingAdvance pending = kernel.World.Business.InteractionStates["a"].PendingAdvance!;
        await FollowingTestSupport.UntilFact(kernel, fact => fact.Kind == CycleEventKind.SpeechShouted);
        CharacterInteractionState received = kernel.World.Business.InteractionStates["a"];
        Assert.Equal(CharacterContextStatus.Closed, received.CurrentContext!.Status);
        Assert.Same(activity, kernel.World.GetCharacter("a").Activity);
        Assert.Same(pending, received.PendingAdvance);
        CycleObservedEvent message = Assert.Single(received.UndeliveredExperiences,
            experience => experience.Event.Kind == CycleObservedEventKind.SpeechShouted).Event;
        Assert.Equal("b", message.SubjectId);
        Assert.Equal(Speech, message.Detail);
        Assert.Equal(0, message.EventOrdinal);
        await FollowingTestSupport.UntilFact(kernel, fact => fact.Kind == CycleEventKind.DecisionContextOpened && fact.ActorId == "a");
        CharacterContext reopened = kernel.World.Business.InteractionStates["a"].CurrentContext!;
        Assert.Equal(2, reopened.ContextSequence);
        Assert.Equal(CycleOutcomeActivityState.Ongoing, reopened.Opening.ActivityState);
        Assert.Equal(activity, reopened.LatestObservation.Actor.Activity);
        Assert.True(reopened.LatestObservation.Communication!.AttentionPending);
        Assert.Equal(Speech, Assert.Single(reopened.Opening.Experiences,
            experience => experience.Event.Kind == CycleObservedEventKind.SpeechShouted).Event.Detail);
        Assert.Empty(kernel.World.Business.InteractionStates["a"].UndeliveredExperiences);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delivery_rejects_original_fact_with_different_delivered_shout(bool closed) {
        CycleWorld before = OpenWorld();
        if (closed) { before = Apply(before, Plan(before, "b", CycleCommand.Wait(2))); }
        CycleFact fact = Plan(before, "a", CycleCommand.Shout(Speech));
        CycleFact forged = ReplaceDelivery(fact, "b", entry => entry.Kind == CycleObservedEventKind.SpeechShouted
            ? Copy(entry, detail: "forged") : entry);
        Assert.Throws<InvalidOperationException>(() => Apply(before, forged));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("id")]
    [InlineData("text")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public void Recorded_shout_material_must_match_exact_sender_text_and_participant_set(string corruption) {
        CycleWorld before = OpenWorld();
        CycleFact fact = Plan(before, "a", CycleCommand.Shout(Speech));
        CycleObservedEvent original = fact.ObservedEvents.Single(entry => entry.ObserverId == "b");
        var replacement = new CycleObservedEvent(original.EventOrdinal, original.ObserverId, original.Kind, original.From, original.At,
            corruption == "id" ? "c" : original.SubjectId, corruption == "name" ? "forged" : original.SubjectName,
            corruption == "text" ? "forged" : original.Detail, original.Effects);
        IEnumerable<CycleObservedEvent> events = fact.ObservedEvents.Select(entry => ReferenceEquals(entry, original) ? replacement : entry);
        if (corruption == "missing") { events = events.Where(entry => entry.ObserverId != "b"); }
        if (corruption == "duplicate") { events = events.Append(original); }
        // Reindex so missing/duplicate rejection exercises cause membership, not only ordinal shape.
        events = events.Select((entry, index) => new CycleObservedEvent(index, entry.ObserverId, entry.Kind, entry.From, entry.At,
            entry.SubjectId, entry.SubjectName, entry.Detail, entry.Effects, entry.Communication));
        Assert.Throws<InvalidOperationException>(() => Apply(before, fact.WithObservedEvents(events)));
    }

    [Theory]
    [InlineData(null, "Alice", "hello", false)]
    [InlineData("a", " ", "hello", false)]
    [InlineData("a", "Alice", " ", false)]
    [InlineData("a", "Alice", "hello", true)]
    public void Snapshot_shape_rejects_unsigned_empty_or_direct_communication_shout(string? id, string? name, string? text, bool direct) {
        var entry = new CycleObservedEvent(0, "b", CycleObservedEventKind.SpeechShouted, Hour(8), Hour(8), id, name, text,
            communication: direct ? new("a", "Alice", "b", "Bob", "hello", null, 0) : null);
        var state = new CharacterInteractionState("b", undeliveredExperiences: [new(1, entry)], nextExperienceSequence: 2);
        Assert.Throws<InvalidOperationException>(() => state.Validate("b", Hour(8)));
    }

    [Fact]
    public void Snapshot_shape_preserves_special_words_and_rejects_oversized_original() {
        var entry = new CycleObservedEvent(0, "b", CycleObservedEventKind.SpeechShouted, Hour(8), Hour(8), "a", "Alice", Speech);
        new CharacterInteractionState("b", undeliveredExperiences: [new(1, entry)], nextExperienceSequence: 2).Validate("b", Hour(8));
        var oversized = Copy(entry, detail: new string('x', CycleCommunicationRules.MaximumSpeechScalars + 1));
        Assert.Throws<InvalidOperationException>(() => new CharacterInteractionState("b",
            undeliveredExperiences: [new(1, oversized)], nextExperienceSequence: 2).Validate("b", Hour(8)));
    }

    private static void AssertSpeech(CycleObservedEvent entry, string observer) {
        Assert.Equal(CycleObservedEventKind.SpeechShouted, entry.Kind);
        Assert.Equal(observer, entry.ObserverId);
        Assert.Equal("a", entry.SubjectId);
        Assert.Equal("阿青", entry.SubjectName);
        Assert.Equal(Speech, entry.Detail);
        Assert.Null(entry.Communication);
    }
}
