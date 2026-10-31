using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using DramaBoard.CharacterGym;

namespace DramaBoard.Microtown.Cli.Gym;

/// <summary>Explicit developer management. Reads never open a working copy, call a controller or repair a journal.</summary>
public static class TownGymCommands {
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void ListFeedbacks(string store, string? branch, string? actor, TextWriter writer) {
        ArgumentNullException.ThrowIfNull(writer);
        IReadOnlyList<TownBranchInfo> branches = TownDurableHistory.ListBranches(store);
        string? selected = branch is null ? null : SelectBranch(branches, branch);
        GymJournalSnapshot snapshot = ReadJournal(TownGymSession.JournalPath(store));
        writer.WriteLine($"StoreId: {snapshot.StoreId}; feedback is an experiment report, not verified world truth.");
        foreach (GymRecord record in snapshot.Records.Where(record => record.Kind == "feedback" &&
            (selected is null || record.BranchId == selected) && (actor is null || record.ActorId == actor))) {
            writer.WriteLine(JsonSerializer.Serialize(record, Json));
        }
        if (snapshot.DiscardedTailBytes != 0) { writer.WriteLine($"Incomplete tail: {snapshot.DiscardedTailBytes} bytes ignored without modifying the source."); }
    }

    public static void Export(string store, string? branch, string actor, string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string selected = SelectBranch(TownDurableHistory.ListBranches(store), branch);
        string rendered = GymTraceExporter.Render(ReadJournal(TownGymSession.JournalPath(store)), selected, actor);
        // Preserve existing artifacts. Reading/rendering first also avoids an empty success artifact on source errors.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var output = new StreamWriter(stream, new UTF8Encoding(false));
        output.Write(rendered);
    }

    /// <summary>Forks an exact request prestate, not the latest State. Feedback merely locates its recorded request.</summary>
    public static TownBranchInfo Fork(string store, string sourceId, bool isFeedback, string label) {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        string path = TownGymSession.JournalPath(store);
        GymJournalSnapshot snapshot = ReadJournal(path);
        GymRecord request;
        string? feedbackId = null;
        if (isFeedback) {
            GymRecord feedback = Find(snapshot, "feedback", "FeedbackId", sourceId);
            feedbackId = sourceId;
            request = Find(snapshot, "request", "RequestId", RequiredString(feedback.Payload, "RequestId"));
            if (feedback.ActorId != request.ActorId || feedback.BranchId != request.BranchId ||
                !feedback.Payload.TryGetProperty("RequestSequence", out JsonElement sequence) ||
                !sequence.TryGetInt64(out long recordedSequence) || recordedSequence != request.Sequence ||
                feedback.Sequence <= request.Sequence || Boundary(feedback) != Boundary(request)) {
                throw new InvalidDataException("Feedback and request identities or source boundaries disagree; no branch created.");
            }
        } else { request = Find(snapshot, "request", "RequestId", sourceId); }
        TownGymBoundary boundary = Boundary(request);
        if (request.ActorId != boundary.ActorId || request.BranchId != boundary.BranchId) {
            throw new InvalidDataException("Request identity disagrees with its decision boundary.");
        }
        BranchId sourceBranch = TownDurableHistory.ParseBranchId(boundary.BranchId);
        CommitId sourceState = TownDurableHistory.ParseCommitId(boundary.StateCommitId);
        TownGymBoundary actual = TownGymBoundary.From(TownDurableHistory.InspectDecisionBoundary(store,
            sourceBranch, sourceState, boundary.ActorId));
        if (actual != boundary) { throw new InvalidDataException("Recorded boundary does not match the exact saved decision State."); }
        TownBranchInfo child;
        try { child = TownDurableHistory.Fork(store, sourceBranch, sourceState, label); }
        catch (RepositoryPublicationException error) {
            string branch = error.TargetBranchId is { } candidate ? TownDurableHistory.FormatBranchId(candidate) : "unknown";
            throw new IOException($"Fork registration outcome is {error.Outcome}; candidate branch {branch}. Inspect branches after reopening; do not blindly retry.", error);
        }
        // Branch registration is durable before host evidence. A logging error must never cause implicit re-registration.
        try {
            using GymJournal journal = GymJournal.Open(path);
            if (journal.StoreId != snapshot.StoreId) { throw new InvalidDataException("Gym journal was replaced during branch registration."); }
            journal.Append("branch-fork", null, TownDurableHistory.FormatBranchId(child.Id), null, new {
                boundary.GameId, boundary.GameRevision, SourceBranchId = boundary.BranchId,
                SourceStateCommitId = boundary.StateCommitId, SourceThroughSequence = request.Sequence - 1,
                SourceRequestId = RequiredString(request.Payload, "RequestId"), SourceFeedbackId = feedbackId
            });
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) {
            throw new IOException($"Branch {TownDurableHistory.FormatBranchId(child.Id)} was registered, but its source log could not be confirmed. No branch was opened or run; do not blindly repeat the fork.", error);
        }
        return child;
    }

    private static string SelectBranch(IReadOnlyList<TownBranchInfo> branches, string? requested) {
        if (requested is null) {
            return branches.Count == 1 ? TownDurableHistory.FormatBranchId(branches[0].Id)
                : throw new ArgumentException("Multiple branches require an explicit branch ID.");
        }
        BranchId id = TownDurableHistory.ParseBranchId(requested);
        if (!branches.Any(branch => branch.Id == id)) { throw new ArgumentException("The selected branch does not exist."); }
        return TownDurableHistory.FormatBranchId(id);
    }

    private static GymRecord Find(GymJournalSnapshot snapshot, string kind, string field, string id) {
        GymRecord[] matches = snapshot.Records.Where(record => record.Kind == kind &&
            record.Payload.ValueKind == JsonValueKind.Object && record.Payload.TryGetProperty(field, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String && value.GetString() == id).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"Expected one {kind} record for {id}, found {matches.Length}.");
    }

    private static GymJournalSnapshot ReadJournal(string path) {
        GymJournalSnapshot snapshot = GymJournal.Read(path);
        TownGymSession.ValidateGameIdentity(snapshot.Records);
        return snapshot;
    }

    private static string RequiredString(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out JsonElement text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())
            ? text.GetString()! : throw new InvalidDataException($"Missing Gym field {property}.");

    private static TownGymBoundary Boundary(GymRecord record) {
        try {
            if (record.Payload.ValueKind != JsonValueKind.Object || !record.Payload.TryGetProperty("Boundary", out JsonElement boundary)) {
                throw new InvalidDataException("Missing exact Gym decision boundary.");
            }
            foreach (string field in new[] { "GameId", "BranchId", "StateCommitId", "ActorId" }) { _ = RequiredString(boundary, field); }
            foreach (string field in new[] { "GameRevision", "DecisionSequence", "OperationSequence", "LineageId", "TransitionCount", "TimeTicks" }) {
                if (!boundary.TryGetProperty(field, out JsonElement value) || !value.TryGetInt64(out _)) {
                    throw new InvalidDataException($"Missing numeric decision boundary field {field}.");
                }
            }
            return boundary.Deserialize<TownGymBoundary>() ?? throw new InvalidDataException("Empty Gym decision boundary.");
        } catch (Exception error) when (error is JsonException or InvalidOperationException) {
            throw new InvalidDataException("Invalid Gym decision boundary encoding.", error);
        }
    }
}
