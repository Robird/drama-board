using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using DramaBoard.CharacterControl.Completion;

namespace DramaBoard.Playground.Cli;

internal abstract record CharacterControlBinding {
    private protected CharacterControlBinding() { }
    internal sealed record HumanConsole : CharacterControlBinding;
    internal sealed record Llm(string ConnectionId) : CharacterControlBinding;
}

/// <summary>Frozen runtime overrides. Actor membership is checked against the loaded world.</summary>
internal sealed class CharacterControlConfiguration {
    private readonly FrozenDictionary<string, CharacterControlBinding> _overrides;
    internal static CharacterControlConfiguration Empty { get; } = new(new(StringComparer.Ordinal));

    private CharacterControlConfiguration(Dictionary<string, CharacterControlBinding> overrides) =>
        _overrides = overrides.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Creates programmatic overrides; the host validates LLM references against its selected catalog.</summary>
    internal static CharacterControlConfiguration Create(IReadOnlyDictionary<string, CharacterControlBinding> overrides) {
        ArgumentNullException.ThrowIfNull(overrides);
        var snapshot = new Dictionary<string, CharacterControlBinding>(StringComparer.Ordinal);
        foreach (var (actorId, binding) in overrides) {
            RequireIdentifier(actorId);
            switch (binding) {
                case CharacterControlBinding.HumanConsole: break;
                case CharacterControlBinding.Llm llm: RequireIdentifier(llm.ConnectionId); break;
                default: throw new InvalidDataException("Invalid control binding.");
            }
            if (!snapshot.TryAdd(actorId, binding)) { throw new InvalidDataException("Duplicate control actor ID."); }
        }
        return new(snapshot);
    }

    internal IReadOnlyDictionary<string, CharacterControlBinding> Resolve(IEnumerable<string> actorIds, string defaultConnectionId) {
        ArgumentNullException.ThrowIfNull(actorIds);
        RequireIdentifier(defaultConnectionId);
        var bindings = new Dictionary<string, CharacterControlBinding>(StringComparer.Ordinal);
        foreach (string actorId in actorIds) {
            RequireIdentifier(actorId);
            if (!bindings.TryAdd(actorId, _overrides.GetValueOrDefault(actorId) ?? new CharacterControlBinding.Llm(defaultConnectionId))) {
                throw new InvalidDataException("The world contains duplicate actor IDs.");
            }
        }
        foreach (string actorId in _overrides.Keys) {
            if (!bindings.ContainsKey(actorId)) { throw new InvalidDataException($"Control override references unknown actor '{actorId}'."); }
        }
        return bindings.ToFrozenDictionary(StringComparer.Ordinal);
    }

    internal static CharacterControlConfiguration Load(string path, LlmConnectionConfiguration connections) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(connections);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > CompletionConnectionConfigLoader.MaximumInputUtf8Bytes) {
            throw new InvalidDataException("Character controls must contain between 1 byte and 1 MiB.");
        }
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || stream.Length != bytes.Length) { throw new InvalidDataException("Character controls changed during read."); }
        try {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            JsonElement root = document.RootElement;
            RequireProperties(root, "version", "overrides");
            JsonElement version = root.GetProperty("version");
            if (version.ValueKind != JsonValueKind.Number || version.GetRawText() != "1") {
                throw new InvalidDataException("Character controls require exact integer version 1.");
            }
            JsonElement values = root.GetProperty("overrides");
            if (values.ValueKind != JsonValueKind.Object) { throw new InvalidDataException("Control overrides must be an object."); }
            var overrides = new Dictionary<string, CharacterControlBinding>(StringComparer.Ordinal);
            foreach (JsonProperty item in values.EnumerateObject()) {
                RequireIdentifier(item.Name);
                JsonElement value = item.Value;
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String) {
                    throw new InvalidDataException("Each control binding requires a kind string.");
                }
                CharacterControlBinding binding;
                switch (kind.GetString()) {
                    case "human-console":
                        RequireProperties(value, "kind");
                        binding = new CharacterControlBinding.HumanConsole();
                        break;
                    case "llm":
                        RequireProperties(value, "kind", "connectionId");
                        JsonElement id = value.GetProperty("connectionId");
                        if (id.ValueKind != JsonValueKind.String) { throw new InvalidDataException("Connection ID must be a string."); }
                        string connectionId = id.GetString()!;
                        RequireIdentifier(connectionId);
                        if (!connections.ConnectionIds.Contains(connectionId, StringComparer.Ordinal)) {
                            throw new InvalidDataException("Control binding references an unknown connection.");
                        }
                        binding = new CharacterControlBinding.Llm(connectionId);
                        break;
                    default: throw new InvalidDataException("Unknown control binding kind.");
                }
                if (!overrides.TryAdd(item.Name, binding)) { throw new InvalidDataException("Duplicate control actor ID."); }
            }
            return new(overrides);
        } catch (Exception error) when (error is JsonException or DecoderFallbackException) {
            throw new InvalidDataException("Character controls are not a strict UTF-8 JSON document.", error);
        }
    }

    private static void RequireIdentifier(string value) {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > 256) {
            throw new InvalidDataException("Control IDs must be nonblank strings of at most 256 UTF-8 bytes.");
        }
    }

    private static void RequireProperties(JsonElement value, params string[] required) {
        if (value.ValueKind != JsonValueKind.Object) { throw new InvalidDataException("Control configuration must be an object."); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject()) {
            if (!required.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) {
                throw new InvalidDataException("Control configuration contains an unknown or duplicate field.");
            }
        }
        if (seen.Count != required.Length) { throw new InvalidDataException("Control configuration is missing a required field."); }
    }
}
