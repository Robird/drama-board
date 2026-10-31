using System.Buffers;
using System.Globalization;
using System.Text;
using Atelia.DurableGraph;

namespace DramaBoard.ResourceCycle;

public static class CycleGoalLimits {
    public const int MaximumUnfinished = 12, MaximumGoals = 32, MaximumLinks = 48, MaximumRevisions = 3;
    public const int RecentFinishedCount = 3, RecentReceiptCount = 3;
    public const int TitleScalars = 80, CriteriaScalars = 240, ProgressScalars = 320, NextStepScalars = 160,
        RevisitScalars = 160, StateNoteScalars = 240, ReasonScalars = 160, MaximumCommandCodeUnits = 8192;

    public static bool ValidText(string? text, int maximum, bool required = false) {
        if (string.IsNullOrWhiteSpace(text)) { return !required && text is null; }
        ReadOnlySpan<char> remaining = text.AsSpan();
        int count = 0;
        while (!remaining.IsEmpty) {
            if (Rune.DecodeFromUtf16(remaining, out _, out int consumed) != OperationStatus.Done || ++count > maximum) { return false; }
            remaining = remaining[consumed..];
        }
        return true;
    }

    internal static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    internal static long Identity(string id, char prefix) => id.Length > 1 && id[0] == prefix &&
        long.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out long number) && number > 0 &&
        id == prefix + number.ToString(CultureInfo.InvariantCulture) ? number : 0;
}

[DurableType("DramaBoard.ResourceCycle.GoalStatus", 1)]
public enum CycleGoalStatus { Open, Paused, Completed, Abandoned }

[DurableType("DramaBoard.ResourceCycle.GoalOperationKind", 1)]
public enum CycleGoalOperationKind { Create, Update, Link, UpdateLink, Unlink, Focus, Pause, Resume, Finish, Reopen, Forget, Show }

[DurableType("DramaBoard.ResourceCycle.GoalRevision", 1)]
public sealed partial record CycleGoalRevision(
    [field: DurableField(1)] CycleGoalStatus OldStatus,
    [field: DurableField(2)] string OldTitle,
    [field: DurableField(3)] string OldCriteria,
    [field: DurableField(4)] string? OldEndNote,
    [field: DurableField(5)] string Reason,
    [field: DurableField(6)] long ContextSequence,
    [field: DurableField(7)] long OperationSequence) : IDurableObject;

/// <summary>An immutable character-authored intention, not a world achievement or an activity.</summary>
[DurableType("DramaBoard.ResourceCycle.Goal", 1)]
public sealed partial class CycleGoal : IDurableObject {
    [DurableField(1)] private string _id;
    [DurableField(2)] private string _title;
    [DurableField(3)] private string _criteria;
    [DurableField(4)] private CycleGoalStatus _status;
    [DurableField(5)] private string? _progress;
    [DurableField(6)] private string? _nextStep;
    [DurableField(7)] private string? _revisit;
    [DurableField(8)] private string? _pauseReason;
    [DurableField(9)] private string? _endNote;
    [DurableField(10)] private List<CycleGoalRevision> _revisions;
    [DurableField(11)] private long _finishedContextSequence;
    [DurableField(12)] private long _finishedOperationSequence;
    public string Id => _id;
    public string Title => _title;
    public string Criteria => _criteria;
    public CycleGoalStatus Status => _status;
    public string? Progress => _progress;
    public string? NextStep => _nextStep;
    public string? Revisit => _revisit;
    public string? PauseReason => _pauseReason;
    public string? EndNote => _endNote;
    public IReadOnlyList<CycleGoalRevision> Revisions => _revisions.AsReadOnly();
    public long FinishedContextSequence => _finishedContextSequence;
    public long FinishedOperationSequence => _finishedOperationSequence;
    public bool IsUnfinished => Status is CycleGoalStatus.Open or CycleGoalStatus.Paused;

    public CycleGoal(string id, string title, string criteria, CycleGoalStatus status = CycleGoalStatus.Open,
        string? progress = null, string? nextStep = null, string? revisit = null, string? pauseReason = null,
        string? endNote = null, IEnumerable<CycleGoalRevision>? revisions = null,
        long finishedContextSequence = 0, long finishedOperationSequence = 0) {
        _id = id; _title = title; _criteria = criteria; _status = status;
        _progress = progress; _nextStep = nextStep; _revisit = revisit; _pauseReason = pauseReason; _endNote = endNote;
        _revisions = revisions?.ToList() ?? []; _finishedContextSequence = finishedContextSequence;
        _finishedOperationSequence = finishedOperationSequence;
    }

    public void Validate() {
        if (Id is null || CycleGoalLimits.Identity(Id, 'G') == 0 || !Enum.IsDefined(Status) ||
            !CycleGoalLimits.ValidText(Title, CycleGoalLimits.TitleScalars, true) ||
            !CycleGoalLimits.ValidText(Criteria, CycleGoalLimits.CriteriaScalars, true) ||
            !CycleGoalLimits.ValidText(Progress, CycleGoalLimits.ProgressScalars) ||
            !CycleGoalLimits.ValidText(NextStep, CycleGoalLimits.NextStepScalars) ||
            !CycleGoalLimits.ValidText(Revisit, CycleGoalLimits.RevisitScalars) ||
            (Status == CycleGoalStatus.Paused ? !CycleGoalLimits.ValidText(PauseReason, CycleGoalLimits.StateNoteScalars, true) : PauseReason is not null) ||
            (IsUnfinished ? EndNote is not null || FinishedContextSequence != 0 || FinishedOperationSequence != 0 :
                !CycleGoalLimits.ValidText(EndNote, CycleGoalLimits.StateNoteScalars, true) || FinishedContextSequence <= 0 || FinishedOperationSequence <= 0) ||
            _revisions is null || Revisions.Count > CycleGoalLimits.MaximumRevisions) {
            throw new InvalidOperationException("Invalid goal state or text.");
        }
        (long, long) previous = (0, 0);
        foreach (CycleGoalRevision revision in Revisions) {
            if (revision is null || !Enum.IsDefined(revision.OldStatus) ||
                !CycleGoalLimits.ValidText(revision.OldTitle, CycleGoalLimits.TitleScalars, true) ||
                !CycleGoalLimits.ValidText(revision.OldCriteria, CycleGoalLimits.CriteriaScalars, true) ||
                !CycleGoalLimits.ValidText(revision.Reason, CycleGoalLimits.ReasonScalars, true) ||
                (revision.OldStatus is CycleGoalStatus.Completed or CycleGoalStatus.Abandoned ?
                    !CycleGoalLimits.ValidText(revision.OldEndNote, CycleGoalLimits.StateNoteScalars, true) : revision.OldEndNote is not null) ||
                revision.ContextSequence <= 0 || revision.OperationSequence <= 0 ||
                (revision.ContextSequence, revision.OperationSequence).CompareTo(previous) <= 0) {
                throw new InvalidOperationException("Invalid goal revision.");
            }
            previous = (revision.ContextSequence, revision.OperationSequence);
        }
    }

    public bool SameAs(CycleGoal? other) => other is not null && Id == other.Id && Title == other.Title && Criteria == other.Criteria &&
        Status == other.Status && Progress == other.Progress && NextStep == other.NextStep && Revisit == other.Revisit &&
        PauseReason == other.PauseReason && EndNote == other.EndNote && FinishedContextSequence == other.FinishedContextSequence &&
        FinishedOperationSequence == other.FinishedOperationSequence && Revisions.SequenceEqual(other.Revisions);
}

[DurableType("DramaBoard.ResourceCycle.GoalLink", 1)]
public sealed partial record CycleGoalLink(
    [field: DurableField(1)] string Id,
    [field: DurableField(2)] string ParentId,
    [field: DurableField(3)] string ChildId,
    [field: DurableField(4)] string Reason) : IDurableObject;

/// <summary>Immutable actor-local graph. Its owning interaction state supplies the actor identity.</summary>
[DurableType("DramaBoard.ResourceCycle.GoalSet", 1)]
public sealed partial class CycleGoalSet : IDurableObject {
    [DurableField(1)] private List<CycleGoal> _goals;
    [DurableField(2)] private List<CycleGoalLink> _links;
    [DurableField(3)] private List<string> _focus;
    [DurableField(4)] private long _nextGoalId;
    [DurableField(5)] private long _nextLinkId;
    public IReadOnlyList<CycleGoal> Goals => _goals.AsReadOnly();
    public IReadOnlyList<CycleGoalLink> Links => _links.AsReadOnly();
    public IReadOnlyList<string> Focus => _focus.AsReadOnly();
    public long NextGoalId => _nextGoalId;
    public long NextLinkId => _nextLinkId;
    public int UnfinishedCount => Goals.Count(goal => goal.IsUnfinished);
    public static CycleGoalSet Empty { get; } = new();

    public CycleGoalSet(IEnumerable<CycleGoal>? goals = null, IEnumerable<CycleGoalLink>? links = null,
        IEnumerable<string>? focus = null, long nextGoalId = 1, long nextLinkId = 1) {
        _goals = goals?.ToList() ?? []; _links = links?.ToList() ?? []; _focus = focus?.ToList() ?? [];
        _nextGoalId = nextGoalId; _nextLinkId = nextLinkId;
    }

    public void Validate() {
        if (_goals is null || _links is null || _focus is null || NextGoalId <= 0 || NextLinkId <= 0 ||
            Goals.Count > CycleGoalLimits.MaximumGoals || Links.Count > CycleGoalLimits.MaximumLinks) {
            throw new InvalidOperationException("Invalid goal collection size or identity counters.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long previousGoalId = 0;
        foreach (CycleGoal goal in Goals) {
            if (goal is null) { throw new InvalidOperationException("Null goal."); }
            goal.Validate();
            long number = CycleGoalLimits.Identity(goal.Id, 'G');
            if (!ids.Add(goal.Id) || number <= previousGoalId || number >= NextGoalId) { throw new InvalidOperationException("Invalid goal identity order."); }
            previousGoalId = number;
        }
        if (UnfinishedCount > CycleGoalLimits.MaximumUnfinished) { throw new InvalidOperationException("Too many unfinished goals."); }
        var pairs = new HashSet<(string, string)>();
        long previousLinkId = 0;
        foreach (CycleGoalLink link in Links) {
            long number = link?.Id is { } linkId ? CycleGoalLimits.Identity(linkId, 'L') : 0;
            if (link is null || number <= previousLinkId || number >= NextLinkId ||
                !ids.Contains(link.ParentId) || !ids.Contains(link.ChildId) || link.ParentId == link.ChildId ||
                !pairs.Add((link.ParentId, link.ChildId)) || !CycleGoalLimits.ValidText(link.Reason, CycleGoalLimits.ReasonScalars, true)) {
                throw new InvalidOperationException("Invalid goal association.");
            }
            previousLinkId = number;
        }
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        bool Visit(string id) {
            if (visited.Contains(id)) { return true; }
            if (!visiting.Add(id)) { return false; }
            foreach (CycleGoalLink link in Links.Where(link => link.ParentId == id)) { if (!Visit(link.ChildId)) { return false; } }
            visiting.Remove(id); visited.Add(id); return true;
        }
        if (ids.Any(id => !Visit(id))) { throw new InvalidOperationException("Goal associations must be acyclic."); }
        if (Focus.Count > Goals.Count || Focus.Distinct(StringComparer.Ordinal).Count() != Focus.Count || Focus.Any(id => !ids.Contains(id)) ||
            Focus.Zip(Focus.Skip(1)).Any(pair => !pairs.Contains((pair.First, pair.Second))) ||
            Focus.Count > 0 && Goals.Single(goal => goal.Id == Focus[^1]).Status != CycleGoalStatus.Open) {
            throw new InvalidOperationException("Invalid goal focus path.");
        }
    }

    public bool SameAs(CycleGoalSet? other) => other is not null && NextGoalId == other.NextGoalId && NextLinkId == other.NextLinkId &&
        Goals.Count == other.Goals.Count && Goals.Zip(other.Goals).All(pair => pair.First.SameAs(pair.Second)) &&
        Links.SequenceEqual(other.Links) && Focus.SequenceEqual(other.Focus);

    /// <summary>Checks that a frozen snapshot contains no finish or revision from beyond its Context boundary.</summary>
    /// <remarks>Operation zero denotes a Context opening; (0, 0) permits only records without historical markers.</remarks>
    public void ValidateThrough(long contextSequence, long operationSequence) {
        Validate();
        if (contextSequence < 0 || operationSequence < 0 || contextSequence == 0 && operationSequence != 0) {
            throw new InvalidOperationException("Invalid goal snapshot boundary.");
        }
        (long, long) boundary = (contextSequence, operationSequence);
        foreach (CycleGoal goal in Goals) {
            if (goal.FinishedContextSequence > 0 && (goal.FinishedContextSequence, goal.FinishedOperationSequence).CompareTo(boundary) > 0 ||
                goal.Revisions.Any(revision => (revision.ContextSequence, revision.OperationSequence).CompareTo(boundary) > 0)) {
                throw new InvalidOperationException("Goal snapshot contains future finish or revision records.");
            }
        }
    }
}
