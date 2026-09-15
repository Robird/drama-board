using System.Net;
using System.Text;
using System.Text.Json;

namespace DramaBoard.Player.Llm.Tests;

public sealed class OpenAiCompatBackendTests
{
    [Fact]
    public async Task CompleteAsync_RecordedChatCompletion_SendsTwoRolesAndReturnsText()
    {
        const string recordedResponse = """
            {
              "id": "chatcmpl-recorded",
              "choices": [
                {
                  "index": 0,
                  "message": {
                    "role": "assistant",
                    "content": "【行动】{\"action\":\"action.wait\"}"
                  },
                  "finish_reason": "stop"
                }
              ],
              "usage": {
                "prompt_tokens": 120,
                "completion_tokens": 30,
                "total_tokens": 150,
                "prompt_cache_hit_tokens": 80,
                "prompt_cache_miss_tokens": 40,
                "completion_tokens_details": { "reasoning_tokens": 12 }
              }
            }
            """;
        var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(recordedResponse, Encoding.UTF8, "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        var backend = new OpenAiCompatBackend(
            httpClient,
            new Uri("https://llm.example.test/v1"),
            "secret-key",
            "drama-model");

        LlmChatResponse result = await backend.CompleteAsync(
            new LlmChatRequest("system text", "user text"),
            CancellationToken.None);

        Assert.Equal("【行动】{\"action\":\"action.wait\"}", result.Content);
        Assert.Equal(120, result.Usage!.PromptTokens);
        Assert.Equal(30, result.Usage.CompletionTokens);
        Assert.Equal(150, result.Usage.TotalTokens);
        Assert.Equal(12, result.Usage.ReasoningTokens);
        Assert.Equal(80, result.Usage.CacheReadTokens);
        Assert.Equal(40, result.Usage.CacheMissTokens);
        Assert.Equal(TimeSpan.Zero, result.QueueDuration);
        Assert.True(result.ServiceDuration > TimeSpan.Zero);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://llm.example.test/v1/chat/completions", handler.Uri!.AbsoluteUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secret-key", handler.AuthorizationParameter);

        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("drama-model", body.RootElement.GetProperty("model").GetString());
        JsonElement messages = body.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system text", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("user text", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task CompleteAsync_HttpError_ThrowsWithoutLeakingCredential()
    {
        var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var httpClient = new HttpClient(handler);
        var backend = new OpenAiCompatBackend(
            httpClient,
            new Uri("https://llm.example.test/v1/"),
            "do-not-log-this-key",
            "drama-model");

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => backend.CompleteAsync(
                new LlmChatRequest("system", "user"),
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.DoesNotContain("do-not-log-this-key", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_MissingAssistantContent_ThrowsProtocolError()
    {
        var handler = new RecordingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[]}", Encoding.UTF8, "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        var backend = new OpenAiCompatBackend(
            httpClient,
            new Uri("http://localhost:11434/v1"),
            apiKey: null,
            "local-model");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => backend.CompleteAsync(
                new LlmChatRequest("system", "user"),
                CancellationToken.None));

        Assert.Contains("choices[0].message.content", exception.Message);
        Assert.Null(handler.AuthorizationScheme);
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public RecordingHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public HttpMethod? Method { get; private set; }

        public Uri? Uri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }
    }
}
