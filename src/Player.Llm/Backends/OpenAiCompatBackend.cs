using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DramaBoard.Player.Llm;

/// <summary>Calls an OpenAI-compatible chat completions endpoint over HTTP.</summary>
public sealed class OpenAiCompatBackend : ILlmChatBackend
{
    private readonly HttpClient _httpClient;
    private readonly Uri _completionUri;
    private readonly string? _apiKey;
    private readonly string _model;

    /// <summary>Initializes a backend with runtime endpoint, credential, and model configuration.</summary>
    public OpenAiCompatBackend(
        HttpClient httpClient,
        Uri baseUrl,
        string? apiKey,
        string model)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (!baseUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("The base URL must be absolute.", nameof(baseUrl));
        }

        _httpClient = httpClient;
        _completionUri = new Uri($"{baseUrl.AbsoluteUri.TrimEnd('/')}/chat/completions");
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _model = model;
    }

    /// <inheritdoc />
    public async Task<LlmChatResponse> CompleteAsync(
        LlmChatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        long started = Stopwatch.GetTimestamp();

        using var message = new HttpRequestMessage(HttpMethod.Post, _completionUri);
        if (_apiKey is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        var payload = new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = request.System },
                new { role = "user", content = request.User },
            },
        };
        message.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The OpenAI-compatible endpoint returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                inner: null,
                response.StatusCode);
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(
            body,
            cancellationToken: cancellationToken);
        if (!TryReadAssistantText(document.RootElement, out string? text))
        {
            throw new InvalidDataException(
                "The OpenAI-compatible response did not contain choices[0].message.content text.");
        }

        return new LlmChatResponse(
            text,
            TryReadUsage(document.RootElement),
            QueueDuration: TimeSpan.Zero,
            ServiceDuration: Stopwatch.GetElapsedTime(started));
    }

    private static LlmTokenUsage? TryReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        long? cached = ReadInt64(usage, "prompt_cache_hit_tokens") ??
            ReadNestedInt64(usage, "prompt_tokens_details", "cached_tokens");
        return new LlmTokenUsage(
            ReadInt64(usage, "prompt_tokens"),
            ReadInt64(usage, "completion_tokens"),
            ReadInt64(usage, "total_tokens"),
            ReadNestedInt64(usage, "completion_tokens_details", "reasoning_tokens"),
            cached,
            ReadInt64(usage, "prompt_cache_miss_tokens"));
    }

    private static long? ReadInt64(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long parsed)
            ? parsed
            : null;

    private static long? ReadNestedInt64(JsonElement parent, string name, string child) =>
        parent.TryGetProperty(name, out JsonElement nested) &&
        nested.ValueKind == JsonValueKind.Object
            ? ReadInt64(nested, child)
            : null;

    private static bool TryReadAssistantText(JsonElement root, out string text)
    {
        text = string.Empty;
        if (!root.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return false;
        }

        JsonElement firstChoice = choices[0];
        if (!firstChoice.TryGetProperty("message", out JsonElement message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = content.GetString()!;
        return true;
    }
}
