using DramaBoard.Player.Llm;

namespace DramaBoard.FirstBoard.Demo;

internal sealed class DemoBackend : IAsyncDisposable
{
    private readonly HttpClient? _httpClient;
    private readonly CodexAppServerBackend? _codexBackend;

    private DemoBackend(
        ILlmChatBackend client,
        HttpClient? httpClient,
        CodexAppServerBackend? codexBackend)
    {
        Client = client;
        _httpClient = httpClient;
        _codexBackend = codexBackend;
    }

    public ILlmChatBackend Client { get; }

    public static DemoBackend Create(
        DemoOptions options,
        DemoBackendOptions backend)
    {
        if (backend.Backend == "codex")
        {
            var codex = new CodexAppServerBackend(new CodexAppServerOptions(
                options.CodexCommand,
                backend.Model,
                WorkingDirectory: Path.GetTempPath(),
                options.ReasoningEffort,
                options.RequestTimeout));
            return new DemoBackend(codex, httpClient: null, codexBackend: codex);
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException(
                "OpenAI-compatible mode requires --base-url or DEEPSEEK_BASE_URL/BASE_URL.");
        }

        string? apiKey = Environment.GetEnvironmentVariable(options.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"OpenAI-compatible mode cannot read environment variable " +
                $"'{options.ApiKeyEnvironmentVariable}'.");
        }

        var httpClient = new HttpClient { Timeout = options.RequestTimeout };
        var client = new OpenAiCompatBackend(
            httpClient,
            new Uri(options.BaseUrl),
            apiKey,
            backend.Model);
        return new DemoBackend(client, httpClient, codexBackend: null);
    }

    public async ValueTask DisposeAsync()
    {
        _httpClient?.Dispose();
        if (_codexBackend is not null)
        {
            await _codexBackend.DisposeAsync();
        }
    }
}
