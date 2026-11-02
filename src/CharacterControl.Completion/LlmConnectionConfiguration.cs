using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Atelia.Completion;

namespace DramaBoard.CharacterControl.Completion;

/// <summary>Frozen connection catalog. Credentials are resolved only by the connection pool on use.</summary>
public sealed class LlmConnectionConfiguration {
    private readonly FrozenDictionary<string, CompletionConnectionConfig> _connections;
    public string DefaultConnectionId { get; }
    public IReadOnlyList<string> ConnectionIds { get; }

    private LlmConnectionConfiguration(CompletionConnectionCatalogConfig catalog, string defaultId) {
        _connections = catalog.Connections.ToFrozenDictionary(x => x.Id, StringComparer.Ordinal);
        if (!_connections.ContainsKey(defaultId)) { throw new InvalidDataException("Default connection does not exist in the catalog."); }
        DefaultConnectionId = defaultId;
        ConnectionIds = Array.AsReadOnly(catalog.Connections.Select(x => x.Id).ToArray());
    }

    internal CompletionConnectionConfig GetConnection(string connectionId) =>
        _connections.TryGetValue(connectionId, out var connection) ? connection
            : throw new ArgumentException("Unknown LLM connection ID.", nameof(connectionId));

    public static LlmConnectionConfiguration Load(string path) {
        using JsonDocument document = Read(path);
        JsonElement root = document.RootElement;
        RequireProperties(root, "version", "defaultConnectionId", "catalog");
        RequireVersion(root);
        string defaultId = Identifier(root.GetProperty("defaultConnectionId"));
        var catalog = CompletionConnectionConfigLoader.DecodeCatalogWithoutEnvironment(
            Encoding.UTF8.GetBytes(root.GetProperty("catalog").GetRawText()));
        return new(catalog, defaultId);
    }

    private static JsonDocument Read(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > CompletionConnectionConfigLoader.MaximumInputUtf8Bytes) {
            throw new InvalidDataException("LLM configuration must contain between 1 byte and 1 MiB.");
        }
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || stream.Length != bytes.Length) { throw new InvalidDataException("LLM configuration changed during read."); }
        try {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        } catch (Exception error) when (error is JsonException or DecoderFallbackException) {
            throw new InvalidDataException("LLM configuration is not a strict UTF-8 JSON document.", error);
        }
    }

    private static void RequireVersion(JsonElement root) {
        JsonElement version = root.GetProperty("version");
        if (version.ValueKind != JsonValueKind.Number || version.GetRawText() != "1") {
            throw new InvalidDataException("LLM configuration requires exact integer version 1.");
        }
    }

    private static string Identifier(JsonElement element) {
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()) ||
            Encoding.UTF8.GetByteCount(element.GetString()!) > 256) {
            throw new InvalidDataException("LLM configuration IDs must be nonblank strings of at most 256 UTF-8 bytes.");
        }
        return element.GetString()!;
    }

    private static void RequireProperties(JsonElement root, params string[] required) {
        if (root.ValueKind != JsonValueKind.Object) { throw new InvalidDataException("LLM configuration must be an object."); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject()) {
            if (!required.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) {
                throw new InvalidDataException("LLM configuration contains an unknown or duplicate field.");
            }
        }
        if (seen.Count != required.Length) { throw new InvalidDataException("LLM configuration is missing a required field."); }
    }
}
