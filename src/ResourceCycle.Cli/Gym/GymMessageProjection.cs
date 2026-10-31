using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;

/// <summary>Host-only audit projection of Completion content, not a character-visible view or replay codec.</summary>
/// <remarks>Expand polymorphic collections explicitly. Serializing only a message's runtime type still
/// loses derived fields inside its abstract block lists. Provider-native replay fields remain raw audit
/// evidence; consumers must not render them as dialogue. Do not pass clients or credential configuration.</remarks>
internal static class GymMessageProjection {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        Converters = { new JsonStringEnumConverter() }
    };

    public static object Message(IHistoryMessage message) {
        ArgumentNullException.ThrowIfNull(message);
        return message switch {
            ActionMessage action => new {
                Kind = action.Kind.ToString(),
                Blocks = action.Blocks.Select(Runtime).ToArray()
            },
            ToolResultsMessage results => new {
                Kind = results.Kind.ToString(), results.Content,
                Results = results.Results.Select(Runtime).ToArray()
            },
            ObservationMessage observation when observation.GetType() == typeof(ObservationMessage) => new {
                Kind = observation.Kind.ToString(), observation.Content
            },
            _ => throw new NotSupportedException($"Unsupported audit message type '{message.GetType().FullName}'.")
        };
    }

    /// <summary>Snapshots content with its concrete fields, including native reasoning/replay payloads.</summary>
    public static JsonElement Runtime<T>(T value) where T : notnull {
        ArgumentNullException.ThrowIfNull(value);
        object projection = value switch {
            IHistoryMessage message => Message(message),
            ActionBlock.ToolCall block => new { block.Kind, Call = Runtime(block.Call) },
            RawToolCall call => Call(call),
            ToolResult result => new {
                result.ToolName, result.ToolCallId, result.Status,
                Blocks = result.Blocks.Select(Runtime).ToArray()
            },
            CompletionOutputContract contract => Contract(contract),
            _ => value
        };
        return JsonSerializer.SerializeToElement(projection, projection.GetType(), JsonOptions);
    }

    public static object Contract(CompletionOutputContract contract) {
        ArgumentNullException.ThrowIfNull(contract);
        return new {
            ToolChoice = Runtime(contract.ToolChoice), contract.AllowParallelToolCalls,
            contract.SemanticFingerprint, contract.IsProviderDefault,
            Tools = contract.Tools.Select(Tool).ToArray()
        };
    }

    private static object Call(RawToolCall call) => call.InputKind switch {
        ToolInputKind.JsonObject => new {
            call.ToolName, call.ToolCallId, InputKind = call.InputKind.ToString(),
            call.RawInput, call.RawArgumentsJson
        },
        ToolInputKind.Text => new {
            call.ToolName, call.ToolCallId, InputKind = call.InputKind.ToString(), call.RawInput
        },
        _ => throw new NotSupportedException($"Unsupported audit tool input kind '{call.InputKind}'.")
    };

    private static object Tool(ToolDefinition tool) {
        if (tool.InputKind == ToolInputKind.Text) {
            return new {
                tool.Name, tool.Description, InputKind = tool.InputKind.ToString(), tool.TextFormat
            };
        }
        // Current game tools have object roots and scalar properties. Fail rather than silently
        // truncate a future nested schema; extend this projection when a consumer needs that shape.
        var schema = tool.InputSchema as ToolSchema.Object
            ?? throw new NotSupportedException("Expected an object audit tool schema.");
        return new {
            tool.Name, tool.Description, InputKind = tool.InputKind.ToString(),
            InputSchema = new {
                Type = "object", schema.Description, schema.Example,
                schema.AdditionalProperties, schema.IsNullable,
                Properties = schema.Properties.Select(property => new {
                    property.Name, property.IsRequired,
                    Schema = Runtime(property.Schema as ToolSchema.Value
                        ?? throw new NotSupportedException("Expected a scalar audit tool parameter."))
                }).ToArray()
            }
        };
    }
}
