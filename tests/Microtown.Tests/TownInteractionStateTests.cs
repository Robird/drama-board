using DramaBoard.Kernel.Time;

namespace DramaBoard.Microtown.Tests;

public sealed class TownInteractionStateTests {
    [Fact]
    public void OpeningDeliversAllThirtyTwoEntriesWithoutLosingTheNewOpeningMaterial() {
        TownInteractionState pending = Fill(TownInteractionState.Empty, 32);
        Assert.Equal(32, pending.PendingCount);
        Assert.False(pending.CanAppend);
        TownInteractionState opened = pending.OpenDecision();

        opened.Validate("b", ModelTime.Zero);
        Assert.Equal(32, opened.Entries.Count);
        Assert.Equal(pending.Entries, opened.Entries);
        Assert.Equal(0, opened.OpeningAfter);
        Assert.Equal(32, opened.DeliveredThrough);
        Assert.Equal(32, opened.LastSequence);
        Assert.Equal(0, opened.PendingCount);
        Assert.True(opened.CanAppend);
        Assert.Equal(32, pending.PendingCount);
        Assert.Equal(0, pending.DeliveredThrough);
    }

    [Fact]
    public void OnlyOpeningTrimsOldHistoryToSixteenAndRetainsAllNewAndPendingEntries() {
        TownInteractionState first = Fill(TownInteractionState.Empty, 32).OpenDecision();
        TownInteractionState secondPending = Fill(first, 32);
        Assert.Equal(64, secondPending.Entries.Count);
        TownInteractionState second = secondPending.OpenDecision();
        second.Validate("b", ModelTime.Zero);
        Assert.Equal(48, second.Entries.Count);
        Assert.Equal(17, second.Entries[0].Sequence);
        Assert.Equal(32, second.OpeningAfter);
        Assert.Equal(64, second.DeliveredThrough);
        Assert.Equal(32, second.Entries.Count(entry => entry.Sequence > second.OpeningAfter));

        TownInteractionState thirdPending = Fill(second, 32);
        thirdPending.Validate("b", ModelTime.Zero);
        Assert.Equal(80, thirdPending.Entries.Count);
        Assert.Equal(32, thirdPending.PendingCount);
        Assert.Equal(17, thirdPending.Entries[0].Sequence);
        TownInteractionState third = thirdPending.OpenDecision();
        third.Validate("b", ModelTime.Zero);
        Assert.Equal(48, third.Entries.Count);
        Assert.Equal(49, third.Entries[0].Sequence);
        Assert.Equal(64, third.OpeningAfter);
        Assert.Equal(96, third.DeliveredThrough);
        Assert.Equal(64, secondPending.Entries.Count);
    }

    [Fact]
    public void FullInboxRejectsAppendWithoutChangingAnyPriorState() {
        TownInteractionState full = Fill(TownInteractionState.Empty, 32);
        TownInteractionEntry[] before = full.Entries.ToArray();
        Assert.Throws<InvalidOperationException>(() => Append(full));
        Assert.Equal(before, full.Entries);
        Assert.Equal(32, full.LastSequence);
        Assert.Equal(0, full.DeliveredThrough);
        Assert.Equal(0, full.OpeningAfter);
        Assert.Empty(TownInteractionState.Empty.Entries);
    }

    [Fact]
    public void ResponseFlagIsStickyUntilExplicitlyClearedWithoutTouchingCursorsOrEntries() {
        TownInteractionState response = Append(TownInteractionState.Empty, needsResponse: true);
        TownInteractionState opened = Append(response).OpenDecision();
        Assert.True(opened.NeedsResponse);
        TownInteractionState cleared = opened.ClearResponse();
        cleared.Validate("b", ModelTime.Zero);
        Assert.False(cleared.NeedsResponse);
        Assert.True(opened.NeedsResponse);
        Assert.Equal(opened.Entries, cleared.Entries);
        Assert.Equal(opened.LastSequence, cleared.LastSequence);
        Assert.Equal(opened.DeliveredThrough, cleared.DeliveredThrough);
        Assert.Equal(opened.OpeningAfter, cleared.OpeningAfter);
        Assert.Equal(opened.PendingCount, cleared.PendingCount);
    }

    [Fact]
    public void EntriesAreCopiedOnConstructionAndExposedThroughAReadOnlyCollection() {
        var original = new List<TownInteractionEntry> { Speech(1) };
        var state = new TownInteractionState(original, lastSequence: 1);
        original.Clear();
        Assert.Single(state.Entries);
        ICollection<TownInteractionEntry> exposed = Assert.IsAssignableFrom<ICollection<TownInteractionEntry>>(state.Entries);
        Assert.True(exposed.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => exposed.Clear());
        state.Validate("a", ModelTime.Zero);
        state.Validate("b", ModelTime.Zero);
        Assert.Throws<InvalidOperationException>(() => state.Validate("c", ModelTime.Zero));
    }

    [Fact]
    public void SpeechLimitCountsUnicodeScalarsAndRejectsMalformedUtf16() {
        string maximum = string.Concat(Enumerable.Repeat("🌱", TownInteractionState.MaximumTextScalars));
        var valid = TownInteractionState.Empty.Append("s1", "a", "b", ModelTime.Zero, TownInteractionKind.Speech, maximum);
        valid.Validate("b", ModelTime.Zero);
        Assert.Throws<InvalidOperationException>(() => TownInteractionState.Empty.Append("s2", "a", "b", ModelTime.Zero,
            TownInteractionKind.Speech, maximum + "x"));
        Assert.Throws<InvalidOperationException>(() => TownInteractionState.Empty.Append("s3", "a", "b", ModelTime.Zero,
            TownInteractionKind.Speech, "invalid\ud800"));
    }

    [Fact]
    public void GiftsRequireConsistentItemAndQuantityAndSharedIdentityCannotBeRepeated() {
        var item = TownInteractionState.Empty.Append("gift1", "a", "b", ModelTime.Zero, TownInteractionKind.ItemGift, "A meal.", "meal", 1);
        item.Validate("b", ModelTime.Zero);
        var money = item.Append("gift2", "b", "a", ModelTime.Zero, TownInteractionKind.MoneyGift, "Some money.", quantity: long.MaxValue);
        money.Validate("b", ModelTime.Zero);
        Assert.Throws<InvalidOperationException>(() => money.Append("gift1", "a", "b", ModelTime.Zero, TownInteractionKind.Speech, "Duplicate."));
        Assert.Throws<InvalidOperationException>(() => TownInteractionState.Empty.Append("x", "a", "b", ModelTime.Zero,
            TownInteractionKind.ItemGift, "Unknown item.", "unknown", 1));
        Assert.Throws<InvalidOperationException>(() => TownInteractionState.Empty.Append("x", "a", "b", ModelTime.Zero,
            TownInteractionKind.ItemGift, "Too many.", "meal", 1001));
        Assert.Throws<InvalidOperationException>(() => TownInteractionState.Empty.Append("x", "a", "b", ModelTime.Zero,
            TownInteractionKind.MoneyGift, "No money.", quantity: 0));
        Assert.Throws<InvalidOperationException>(() => TownInteractionState.Empty.Append("x", "a", "b", ModelTime.Zero,
            TownInteractionKind.Speech, "Speech is not a gift.", "meal", 1));
    }

    [Fact]
    public void ValidationRejectsMissingOpeningOrPendingEntriesAndDiscontinuousOrDuplicateHistory() {
        AssertInvalid(new([Speech(2)], lastSequence: 2));
        AssertInvalid(new([Speech(2)], lastSequence: 2, deliveredThrough: 2));
        AssertInvalid(new([Speech(1), Speech(3)], lastSequence: 3));
        AssertInvalid(new([Speech(1), Speech(2) with { Id = "s1" }], lastSequence: 2));
        AssertInvalid(new(lastSequence: 4, deliveredThrough: 4, openingAfter: 4));
        AssertInvalid(new([Speech(1)], lastSequence: 1, deliveredThrough: 1, openingAfter: 2));
        AssertInvalid(new([Speech(1) with { At = new ModelTime(1) }], lastSequence: 1));
        AssertInvalid(new(Enumerable.Range(1, 17).Select(number => Speech(number)), 17, 17, 17));
        AssertInvalid(new(Enumerable.Range(1, 33).Select(number => Speech(number)), 33, 33));
    }

    private static void AssertInvalid(TownInteractionState state) => Assert.Throws<InvalidOperationException>(() => state.Validate("b", ModelTime.Zero));
    private static TownInteractionEntry Speech(long sequence) => new(sequence, $"s{sequence}", "a", "b", ModelTime.Zero, TownInteractionKind.Speech, "Hello.");
    private static TownInteractionState Append(TownInteractionState state, bool needsResponse = false) => state.Append(
        $"s{state.LastSequence + 1}", "a", "b", ModelTime.Zero, TownInteractionKind.Speech, "Hello.", needsResponse: needsResponse);
    private static TownInteractionState Fill(TownInteractionState state, int count) {
        for (int i = 0; i < count; i++) { state = Append(state); }
        return state;
    }
}
