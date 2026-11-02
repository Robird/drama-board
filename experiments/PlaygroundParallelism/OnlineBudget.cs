using System.Text;
using System.Text.Json;

namespace DramaBoard.PlaygroundParallelism;

public sealed record OnlineGrant(string ResearchId, string BatchId, string LedgerPath, string Strategy,
    int MaxCalls, int MaxInFlight, int TimeoutSeconds);

/// <summary>Consumes one coordinator-reserved batch. Existing or crashed runs cannot reuse a batch.
/// The ledger is spending evidence, never a world recovery authority.</summary>
internal sealed class OnlineBudget : IDisposable {
    private readonly FileStream _ledger;
    private readonly object _gate = new();
    private readonly Action<string>? _beforeFlush;
    private bool _faulted;
    private int _calls;
    public OnlineGrant Grant { get; }
    public int Calls { get { lock (_gate) { return _calls; } } }
    public bool IsFaulted { get { lock (_gate) { return _faulted; } } }

    public OnlineBudget(string grantPath, string strategy, Action<string>? beforeFlush = null) {
        _beforeFlush = beforeFlush;
        Grant = JsonSerializer.Deserialize<OnlineGrant>(File.ReadAllText(grantPath))
            ?? throw new InvalidDataException("Missing online budget grant.");
        if (Grant.ResearchId != "predictive-time-parallelism" || string.IsNullOrWhiteSpace(Grant.BatchId) ||
            Grant.Strategy != strategy || !Path.IsPathFullyQualified(Grant.LedgerPath) ||
            Grant.MaxCalls <= 0 || Grant.MaxInFlight is < 1 or > 2 || Grant.TimeoutSeconds <= 0) {
            throw new InvalidDataException("Invalid online budget grant.");
        }
        _ledger = new(Grant.LedgerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        try {
            if (_ledger.Length == 0) { throw new InvalidDataException("Online ledger is empty."); }
            _ledger.Seek(-1, SeekOrigin.End);
            if (_ledger.ReadByte() != '\n') { throw new InvalidDataException("Online ledger has an unterminated tail; reconcile before further calls."); }
            _ledger.Position = 0;
            using var reader = new StreamReader(_ledger, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            bool reserved = false;
            while (reader.ReadLine() is { } line) {
                using JsonDocument record = JsonDocument.Parse(line);
                JsonElement root = record.RootElement;
                if (!root.TryGetProperty("BatchId", out JsonElement batch) || batch.GetString() != Grant.BatchId) { continue; }
                string? kind = root.GetProperty("Kind").GetString();
                if (kind != "batch-reserved" || reserved) {
                    throw new InvalidDataException("Batch already used or reservation is ambiguous; reconcile externally and reserve a new batch.");
                }
                OnlineGrant reservation = root.GetProperty("Grant").Deserialize<OnlineGrant>()
                    ?? throw new InvalidDataException("Invalid reservation.");
                if (reservation != Grant) { throw new InvalidDataException("Grant does not match persisted reservation."); }
                reserved = true;
            }
            if (!reserved) { throw new InvalidDataException("No matching coordinator reservation in existing ledger."); }
            _ledger.Seek(0, SeekOrigin.End);
            Append("run-started", new { Grant.Strategy, Grant.MaxCalls, Grant.MaxInFlight });
        } catch { _ledger.Dispose(); throw; }
    }

    public string BeforeInvoke(string actor, string inputHash) {
        lock (_gate) {
            if (_faulted) { throw new InvalidOperationException("Budget writer is retired after an uncertain append outcome."); }
            if (_calls >= Grant.MaxCalls) { throw new InvalidOperationException("Online batch call budget exhausted; no provider request issued."); }
            string requestId = $"{Grant.BatchId}-request-{++_calls}";
            Append("request-invoked", new { RequestId = requestId, ActorId = actor, InputHash = inputHash });
            return requestId;
        }
    }
    public void Finished(string requestId, string outcome, object? usage) {
        lock (_gate) { Append("request-finished", new { RequestId = requestId, Outcome = outcome, Usage = usage }); }
    }
    public void FinishRun(string outcome) {
        lock (_gate) { Append("run-finished", new { Outcome = outcome, Calls = _calls, UnusedReservationsRemainReserved = true }); }
    }
    private void Append(string kind, object detail) {
        if (_faulted) { throw new InvalidOperationException("Budget writer is retired after an uncertain append outcome."); }
        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            Kind = kind, Grant.ResearchId, Grant.BatchId, Utc = DateTimeOffset.UtcNow, Detail = detail
        }) + "\n");
        try {
            _ledger.Write(line);
            _beforeFlush?.Invoke(kind);
            _ledger.Flush(flushToDisk: true);
        } catch { _faulted = true; throw; }
    }
    public void Dispose() => _ledger.Dispose();
}
