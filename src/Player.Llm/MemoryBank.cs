using System.Text;

namespace DramaBoard.Player.Llm;

/// <summary>Stores one independently maintained category of an actor's mutable cognition.</summary>
public sealed record MemoryShard(
    string Key,
    string Title,
    string MaintenanceInstructions,
    string Content);

/// <summary>Contains an ordered immutable snapshot of independently maintained memory shards.</summary>
public sealed class MemoryBank
{
    private readonly MemoryShard[] _shards;
    private readonly IReadOnlyList<MemoryShard> _shardView;

    /// <summary>Creates a memory snapshot with stable shard order and unique keys.</summary>
    public MemoryBank(IEnumerable<MemoryShard> shards)
    {
        ArgumentNullException.ThrowIfNull(shards);
        _shards = [.. shards];
        _shardView = Array.AsReadOnly(_shards);
        if (_shards.Length == 0)
        {
            throw new ArgumentException("A memory bank must contain at least one shard.", nameof(shards));
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (MemoryShard shard in _shards)
        {
            ArgumentNullException.ThrowIfNull(shard);
            if (string.IsNullOrWhiteSpace(shard.Key) ||
                string.IsNullOrWhiteSpace(shard.Title) ||
                string.IsNullOrWhiteSpace(shard.MaintenanceInstructions))
            {
                throw new ArgumentException(
                    "Memory shard key, title, and maintenance instructions must not be blank.",
                    nameof(shards));
            }

            if (!keys.Add(shard.Key))
            {
                throw new ArgumentException($"Duplicate memory shard key '{shard.Key}'.", nameof(shards));
            }
        }
    }

    /// <summary>Gets the shards in deterministic rendering order.</summary>
    public IReadOnlyList<MemoryShard> Shards => _shardView;

    /// <summary>Gets a shard by its stable key.</summary>
    public MemoryShard this[string key] =>
        _shards.FirstOrDefault(shard => string.Equals(shard.Key, key, StringComparison.Ordinal)) ??
        throw new KeyNotFoundException($"Memory shard '{key}' does not exist.");

    /// <summary>Returns a new snapshot with one shard's content replaced.</summary>
    public MemoryBank Replace(string key, string content)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        bool found = false;
        MemoryShard[] updated = _shards.Select(shard =>
        {
            if (!string.Equals(shard.Key, key, StringComparison.Ordinal))
            {
                return shard;
            }

            found = true;
            return shard with { Content = content };
        }).ToArray();
        if (!found)
        {
            throw new KeyNotFoundException($"Memory shard '{key}' does not exist.");
        }

        return new MemoryBank(updated);
    }

    /// <summary>Renders all shards as one actor-facing memory document.</summary>
    public string Render()
    {
        var text = new StringBuilder();
        foreach (MemoryShard shard in _shards)
        {
            text.Append("## ").Append(shard.Title).Append(" [").Append(shard.Key).AppendLine("]")
                .AppendLine(string.IsNullOrWhiteSpace(shard.Content) ? "（暂无）" : shard.Content)
                .AppendLine();
        }

        return text.ToString().TrimEnd();
    }

    /// <inheritdoc />
    public override string ToString() => Render();
}
