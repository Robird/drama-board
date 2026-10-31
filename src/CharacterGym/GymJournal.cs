using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DramaBoard.CharacterGym;

public sealed record GymRecord(int FormatVersion, long Sequence, string Kind, string? RunId,
    string? BranchId, string? ActorId, JsonElement Payload);

public sealed record GymJournalSnapshot(string? StoreId, IReadOnlyList<GymRecord> Records, long DiscardedTailBytes);

/// <summary>Serial host evidence shared by all runs and branches. Never authorizes game recovery.</summary>
/// <remarks>Only newline-terminated, chained records are committed. Read never repairs the file. A writer
/// may remove an incomplete last line; corruption in any complete line fails closed. Append flushes to
/// disk before installing a record in memory. A write failure retires the instance until reopen.</remarks>
public sealed class GymJournal : IDisposable {
    public const int CurrentFormatVersion = 1;
    private const string MetadataKind = "metadata";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Stream _stream;
    private readonly List<GymRecord> _records = [];
    private readonly IReadOnlyList<GymRecord> _view;
    private string _lastHash = "";
    private string? _storeId;
    private bool _disposed;
    private bool _faulted;

    private GymJournal(Stream stream) {
        _stream = stream;
        _view = _records.AsReadOnly();
        ReadExisting(stream);
        if (DiscardedTailBytes != 0) {
            stream.SetLength(stream.Length - DiscardedTailBytes);
            FlushDurable(stream);
        }
        stream.Position = stream.Length;
        if (_records.Count == 0) {
            AppendCore(MetadataKind, null, null, null, new { StoreId = Guid.NewGuid().ToString("N") });
        }
    }

    public static GymJournal Open(string path) {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var stream = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        try { return new(stream); }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>Returns complete evidence without creating, truncating, reconciling or otherwise writing it.</summary>
    public static GymJournalSnapshot Read(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var reader = new Reader();
        ReadLines(stream, reader.Accept, out long tail);
        return new(reader.StoreId, reader.Records.AsReadOnly(), tail);
    }

    internal static GymJournal CreateForTesting(Stream stream) => new(stream);

    public string StoreId { get { RequireUsable(); return _storeId!; } }
    public IReadOnlyList<GymRecord> Records { get { RequireUsable(); return _view; } }
    public bool IsFaulted => _faulted;
    public long DiscardedTailBytes { get; private set; }

    public GymRecord Append(string kind, string? runId, string? branchId, string? actorId, object payload) {
        RequireUsable();
        if (kind == MetadataKind) { throw new ArgumentException("The store metadata is written only once.", nameof(kind)); }
        return AppendCore(kind, runId, branchId, actorId, payload);
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        _stream.Dispose();
    }

    private GymRecord AppendCore(string kind, string? runId, string? branchId, string? actorId, object payload) {
        ArgumentNullException.ThrowIfNull(payload);
        long sequence = checked((long)_records.Count + 1);
        var record = new GymRecord(CurrentFormatVersion, sequence, kind, runId, branchId, actorId,
            JsonSerializer.SerializeToElement(payload, payload.GetType()));
        Validate(record);
        string serialized = JsonSerializer.Serialize(record);
        string hash = Hash(sequence, _lastHash, serialized);
        byte[] bytes = Utf8.GetBytes(JsonSerializer.Serialize(new JournalLine(sequence, _lastHash, serialized, hash)) + "\n");
        try {
            _stream.Write(bytes);
            FlushDurable(_stream);
        } catch {
            _faulted = true;
            throw;
        }
        _records.Add(record);
        _lastHash = hash;
        if (record.Kind == MetadataKind) { _storeId = ReadStoreId(record); }
        return record;
    }

    private void ReadExisting(Stream stream) {
        var reader = new Reader();
        ReadLines(stream, reader.Accept, out long tail);
        _records.AddRange(reader.Records);
        _storeId = reader.StoreId;
        _lastHash = reader.LastHash;
        DiscardedTailBytes = tail;
    }

    private static void ReadLines(Stream stream, Action<byte[]> accept, out long tail) {
        stream.Position = 0;
        // Freeze the read boundary, so a live writer cannot keep extending this diagnostic read.
        long remaining = stream.Length;
        using var line = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (remaining > 0) {
            int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0) { throw new IOException("The Gym journal changed length while being read."); }
            remaining -= count;
            for (int index = 0; index < count; index++) {
                if (buffer[index] == (byte)'\n') {
                    accept(line.ToArray());
                    line.SetLength(0);
                } else {
                    line.WriteByte(buffer[index]);
                }
            }
        }
        tail = line.Length;
    }

    private sealed class Reader {
        public List<GymRecord> Records { get; } = [];
        public string? StoreId { get; private set; }
        public string LastHash { get; private set; } = "";

        public void Accept(byte[] bytes) {
            try {
                var line = JsonSerializer.Deserialize<JournalLine>(Utf8.GetString(bytes))
                    ?? throw new InvalidDataException("A Gym journal line is null.");
                if (line.Sequence != (long)Records.Count + 1 || line.PreviousHash != LastHash ||
                    line.Payload is null || line.Hash != Hash(line.Sequence, LastHash, line.Payload)) {
                    throw new InvalidDataException("A Gym journal line has an invalid sequence or checksum.");
                }
                var record = JsonSerializer.Deserialize<GymRecord>(line.Payload)
                    ?? throw new InvalidDataException("A Gym journal record is null.");
                Validate(record);
                if (record.Sequence != line.Sequence || (Records.Count == 0) != (record.Kind == MetadataKind)) {
                    throw new InvalidDataException("The Gym journal metadata or record sequence is invalid.");
                }
                if (record.Kind == MetadataKind) { StoreId = ReadStoreId(record); }
                Records.Add(record);
                LastHash = line.Hash;
            } catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException or InvalidOperationException) {
                throw new InvalidDataException("A completed Gym journal line is corrupt.", error);
            }
        }
    }

    private static void Validate(GymRecord record) {
        if (record.FormatVersion != CurrentFormatVersion) {
            throw new InvalidDataException($"Unsupported Gym journal format version: {record.FormatVersion}.");
        }
        if (record.Sequence <= 0) { throw new InvalidDataException("A Gym journal sequence must be positive."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Kind);
        foreach (string? identity in new[] { record.RunId, record.BranchId, record.ActorId }) {
            if (identity is not null && string.IsNullOrWhiteSpace(identity)) {
                throw new ArgumentException("Optional Gym identities must be nonempty when supplied.");
            }
        }
        if (record.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) {
            throw new InvalidDataException("A Gym journal payload is required.");
        }
    }

    private static string ReadStoreId(GymRecord record) {
        if (record.RunId is not null || record.BranchId is not null || record.ActorId is not null ||
            record.Payload.ValueKind != JsonValueKind.Object ||
            !record.Payload.TryGetProperty("StoreId", out var value) || value.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(value.GetString(), "N", out _)) {
            throw new InvalidDataException("The Gym journal store identity is invalid.");
        }
        return value.GetString()!;
    }

    private static string Hash(long sequence, string previousHash, string payload) => Convert.ToHexString(
        SHA256.HashData(Utf8.GetBytes(sequence.ToString(CultureInfo.InvariantCulture) + "\n" + previousHash + "\n" + payload)));

    private static void FlushDurable(Stream stream) {
        if (stream is FileStream file) { file.Flush(flushToDisk: true); }
        else { stream.Flush(); }
    }

    private void RequireUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) { throw new InvalidOperationException("Gym journal is faulted; dispose and reopen it."); }
    }

    private sealed record JournalLine(long Sequence, string PreviousHash, string Payload, string Hash);
}
