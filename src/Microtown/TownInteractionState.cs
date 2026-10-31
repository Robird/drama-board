using System.Buffers;
using System.Text;
using Atelia.DurableGraph;
using DramaBoard.Kernel.Time;

namespace DramaBoard.Microtown;

[DurableType("DramaBoard.Microtown.InteractionKind", 1)]
public enum TownInteractionKind { Speech, ItemGift, MoneyGift }

/// <summary>An immutable exchange record. Its identity is shared by sender and recipient; Sequence belongs to one resident's history.</summary>
[DurableType("DramaBoard.Microtown.InteractionEntry", 1)]
public sealed partial record TownInteractionEntry(
    [field: DurableField(1)] long Sequence,
    [field: DurableField(2)] string Id,
    [field: DurableField(3)] string SenderId,
    [field: DurableField(4)] string RecipientId,
    [field: DurableField(5)] ModelTime At,
    [field: DurableField(6)] TownInteractionKind Kind,
    [field: DurableField(7)] string Text,
    [field: DurableField(8)] string? ItemId = null,
    [field: DurableField(9)] long Quantity = 0) : IDurableObject {
    public void Validate(string ownerId, ModelTime now) {
        if (Sequence <= 0 || string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(SenderId) ||
            string.IsNullOrWhiteSpace(RecipientId) || SenderId == RecipientId ||
            string.IsNullOrWhiteSpace(ownerId) || ownerId != SenderId && ownerId != RecipientId ||
            At < ModelTime.Zero || At > now || !Enum.IsDefined(Kind) ||
            !(Kind == TownInteractionKind.Speech ? TownInteractionState.IsValidSpeech(Text) : ValidText(Text, int.MaxValue))) {
            throw new InvalidOperationException("Invalid resident interaction record.");
        }
        bool validPayload = Kind switch {
            TownInteractionKind.Speech => ItemId is null && Quantity == 0,
            TownInteractionKind.ItemGift => ItemId is not null && TownContent.Items.Contains(ItemId) && Quantity is >= 1 and <= 1000,
            TownInteractionKind.MoneyGift => ItemId is null && Quantity > 0,
            _ => false
        };
        if (!validPayload) { throw new InvalidOperationException("Interaction kind and transferred quantity disagree."); }
    }

    internal static bool ValidText(string? text, int maximumScalars) {
        if (string.IsNullOrWhiteSpace(text)) { return false; }
        ReadOnlySpan<char> remaining = text.AsSpan();
        int count = 0;
        while (!remaining.IsEmpty) {
            if (Rune.DecodeFromUtf16(remaining, out _, out int consumed) != OperationStatus.Done || ++count > maximumScalars) { return false; }
            remaining = remaining[consumed..];
        }
        return true;
    }
}

/// <summary>Bounded resident-local exchange history. Opening a decision delivers pending entries without discarding that opening's new material.</summary>
[DurableType("DramaBoard.Microtown.InteractionState", 1)]
public sealed partial class TownInteractionState : IDurableObject {
    public const int MaximumPending = 32, RetainedHistory = 16, MaximumTextScalars = 512;
    public static TownInteractionState Empty { get; } = new();

    /// <summary>The command check and stored speech validation share one Unicode-aware text limit.</summary>
    public static bool IsValidSpeech(string? text) => TownInteractionEntry.ValidText(text, MaximumTextScalars);

    [DurableField(1)] private readonly List<TownInteractionEntry> _entries;
    [DurableField(2)] private readonly long _lastSequence;
    [DurableField(3)] private readonly long _deliveredThrough;
    [DurableField(4)] private readonly long _openingAfter;
    [DurableField(5)] private readonly bool _needsResponse;

    public IReadOnlyList<TownInteractionEntry> Entries => _entries.AsReadOnly();
    public long LastSequence => _lastSequence;
    public long DeliveredThrough => _deliveredThrough;
    public long OpeningAfter => _openingAfter;
    public bool NeedsResponse => _needsResponse;
    public int PendingCount => checked((int)(LastSequence - DeliveredThrough));
    public bool CanAppend => PendingCount < MaximumPending && LastSequence < long.MaxValue;

    /// <summary>Restores representation fields. The owning resident validates them with its identity and current world time.</summary>
    public TownInteractionState(IEnumerable<TownInteractionEntry>? entries = null, long lastSequence = 0,
        long deliveredThrough = 0, long openingAfter = 0, bool needsResponse = false) {
        _entries = entries?.ToList() ?? [];
        _lastSequence = lastSequence;
        _deliveredThrough = deliveredThrough;
        _openingAfter = openingAfter;
        _needsResponse = needsResponse;
    }

    public TownInteractionState Append(string id, string senderId, string recipientId, ModelTime at,
        TownInteractionKind kind, string text, string? itemId = null, long quantity = 0, bool needsResponse = false) {
        if (!CanAppend) { throw new InvalidOperationException("Interaction inbox is full or its sequence is exhausted."); }
        if (_entries.Any(entry => entry.Id == id)) { throw new InvalidOperationException("Interaction identity already exists in this history."); }
        long next = checked(LastSequence + 1);
        var entry = new TownInteractionEntry(next, id, senderId, recipientId, at, kind, text, itemId, quantity);
        entry.Validate(senderId, at);
        return new(_entries.Append(entry), next, DeliveredThrough, OpeningAfter, NeedsResponse || needsResponse);
    }

    public TownInteractionState OpenDecision() => new(
        _entries.Where(entry => entry.Sequence <= DeliveredThrough).TakeLast(RetainedHistory)
            .Concat(_entries.Where(entry => entry.Sequence > DeliveredThrough)),
        LastSequence, LastSequence, DeliveredThrough, NeedsResponse);

    public TownInteractionState ClearResponse() => new(_entries, LastSequence, DeliveredThrough, OpeningAfter, false);

    public void Validate(string ownerId, ModelTime now) {
        if (string.IsNullOrWhiteSpace(ownerId) || now < ModelTime.Zero || _entries is null ||
            OpeningAfter < 0 || DeliveredThrough < OpeningAfter || LastSequence < DeliveredThrough ||
            LastSequence - DeliveredThrough > MaximumPending || DeliveredThrough - OpeningAfter > MaximumPending ||
            _entries.Count > RetainedHistory + 2 * MaximumPending) {
            throw new InvalidOperationException("Invalid resident interaction cursors or capacity.");
        }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        long previous = LastSequence - _entries.Count;
        int history = 0, opening = 0, pending = 0;
        foreach (TownInteractionEntry entry in _entries) {
            if (entry is null || previous == long.MaxValue || entry.Sequence != previous + 1) {
                throw new InvalidOperationException("Interaction entries must be a contiguous suffix ending at the last sequence.");
            }
            entry.Validate(ownerId, now);
            if (!identities.Add(entry.Id)) { throw new InvalidOperationException("Interaction identities must be unique within a resident's retained history."); }
            if (entry.Sequence <= OpeningAfter) { history++; }
            else if (entry.Sequence <= DeliveredThrough) { opening++; }
            else { pending++; }
            previous = entry.Sequence;
        }
        if ((_entries.Count == 0 && LastSequence != 0) || history > RetainedHistory ||
            opening != DeliveredThrough - OpeningAfter || pending != LastSequence - DeliveredThrough) {
            throw new InvalidOperationException("Interaction history must retain the complete opening and all pending entries.");
        }
    }
}
