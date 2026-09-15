using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DramaBoard.Player.Llm;

/// <summary>Completes prompts through one reusable local Codex app-server process.</summary>
public sealed class CodexAppServerBackend : ILlmChatBackend, IAsyncDisposable
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(2);

    private readonly CodexAppServerOptions _options;
    private readonly Func<ICodexAppServerConnection> _connectionFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ICodexAppServerConnection? _connection;
    private CodexAppServerProtocolClient? _client;
    private bool _disposed;

    /// <summary>Initializes a backend that starts Codex on first use.</summary>
    public CodexAppServerBackend(CodexAppServerOptions? options = null)
    {
        _options = Validate(options ?? new CodexAppServerOptions());
        _connectionFactory = () => ProcessCodexAppServerConnection.Start(_options);
    }

    internal CodexAppServerBackend(
        CodexAppServerOptions options,
        Func<ICodexAppServerConnection> connectionFactory)
    {
        _options = Validate(options);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    /// <inheritdoc />
    public async Task<LlmChatResponse> CompleteAsync(
        LlmChatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        long queuedAt = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(cancellationToken);
        TimeSpan queueDuration = Stopwatch.GetElapsedTime(queuedAt);
        long serviceStarted = Stopwatch.GetTimestamp();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await EnsureClientAsync();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout ?? DefaultRequestTimeout);
            try
            {
                string content = await _client!.CompleteAsync(request, timeout.Token);
                return new LlmChatResponse(
                    content,
                    Usage: null,
                    queueDuration,
                    Stopwatch.GetElapsedTime(serviceStarted));
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                await ResetConnectionAsync();
                throw new TimeoutException(
                    $"Codex app-server did not complete within " +
                    $"{(_options.RequestTimeout ?? DefaultRequestTimeout).TotalSeconds:0.###} seconds.",
                    exception);
            }
            catch
            {
                await ResetConnectionAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the owned app-server process.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await ResetConnectionAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static CodexAppServerOptions Validate(CodexAppServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CommandPath);
        if (options.RequestTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.RequestTimeout),
                "The request timeout must be positive.");
        }

        string workingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? Path.GetTempPath()
            : Path.GetFullPath(options.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The Codex app-server working directory does not exist: {workingDirectory}");
        }

        return options with { WorkingDirectory = workingDirectory };
    }

    private async ValueTask EnsureClientAsync()
    {
        if (_connection is { IsAlive: true } && _client is not null)
        {
            return;
        }

        await ResetConnectionAsync();
        _connection = _connectionFactory();
        _client = new CodexAppServerProtocolClient(_connection, _options);
    }

    private async ValueTask ResetConnectionAsync()
    {
        _client = null;
        ICodexAppServerConnection? connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }
}

internal sealed class CodexAppServerProtocolClient
{
    private const string ClientName = "dramaboard";
    private const string ClientTitle = "DramaBoard LLM Player";
    private const string ClientVersion = "0.1.0";

    private readonly ICodexAppServerConnection _connection;
    private readonly CodexAppServerOptions _options;
    private long _nextRequestId;
    private bool _initialized;

    public CodexAppServerProtocolClient(
        ICodexAppServerConnection connection,
        CodexAppServerOptions options)
    {
        _connection = connection;
        _options = options;
    }

    public async Task<string> CompleteAsync(
        LlmChatRequest request,
        CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken);
        }

        var threadParameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["approvalPolicy"] = "never",
            ["cwd"] = _options.WorkingDirectory,
            ["ephemeral"] = true,
            ["serviceName"] = ClientName,
        };
        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            threadParameters["model"] = _options.Model;
        }

        JsonElement threadResult = await SendRequestAsync(
            "thread/start",
            threadParameters,
            notificationHandler: null,
            cancellationToken);
        string threadId = ReadRequiredString(threadResult, "thread", "id");

        try
        {
            return await RunTurnAsync(threadId, request, cancellationToken);
        }
        finally
        {
            await SendRequestAsync(
                "thread/unsubscribe",
                new { threadId },
                notificationHandler: null,
                cancellationToken);
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await SendRequestAsync(
            "initialize",
            new
            {
                clientInfo = new
                {
                    name = ClientName,
                    title = ClientTitle,
                    version = ClientVersion,
                },
            },
            notificationHandler: null,
            cancellationToken);
        await WriteMessageAsync(
            new { method = "initialized", @params = new { } },
            cancellationToken);
        _initialized = true;
    }

    private async Task<string> RunTurnAsync(
        string threadId,
        LlmChatRequest request,
        CancellationToken cancellationToken)
    {
        var turnParameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["threadId"] = threadId,
            ["input"] = new[]
            {
                new { type = "text", text = ComposePrompt(request) },
            },
            ["approvalPolicy"] = "never",
            ["sandboxPolicy"] = new { type = "readOnly", networkAccess = false },
        };
        if (!string.IsNullOrWhiteSpace(_options.ReasoningEffort))
        {
            turnParameters["effort"] = _options.ReasoningEffort;
        }

        string? lastAgentMessage = null;
        string? finalAgentMessage = null;
        string? reportedError = null;
        JsonElement? completedTurnNotification = null;

        void HandleNotification(JsonElement notification)
        {
            string? method = ReadOptionalString(notification, "method");
            if (method == "item/completed" &&
                notification.TryGetProperty("params", out JsonElement itemParameters) &&
                HasStringValue(itemParameters, "threadId", threadId) &&
                itemParameters.TryGetProperty("item", out JsonElement item))
            {
                CaptureAgentMessage(item, ref lastAgentMessage, ref finalAgentMessage);
            }
            else if (method == "error" &&
                notification.TryGetProperty("params", out JsonElement errorParameters))
            {
                reportedError = ReadNestedOptionalString(errorParameters, "error", "message");
            }
            else if (method == "turn/completed" &&
                notification.TryGetProperty("params", out JsonElement completedParameters) &&
                HasStringValue(completedParameters, "threadId", threadId))
            {
                completedTurnNotification = notification.Clone();
            }
        }

        JsonElement turnResult = await SendRequestAsync(
            "turn/start",
            turnParameters,
            HandleNotification,
            cancellationToken);
        string turnId = ReadRequiredString(turnResult, "turn", "id");

        while (completedTurnNotification is null)
        {
            JsonElement message = await ReadNextMessageAsync(cancellationToken);
            if (message.TryGetProperty("id", out _))
            {
                throw new InvalidDataException(
                    "Codex app-server returned an unexpected response while a turn was active.");
            }

            HandleNotification(message);
        }

        JsonElement completed = completedTurnNotification.Value;
        if (!completed.TryGetProperty("params", out JsonElement parameters) ||
            !parameters.TryGetProperty("turn", out JsonElement turn) ||
            !HasStringValue(turn, "id", turnId))
        {
            throw new InvalidDataException(
                "Codex app-server completed a different turn than the one it started.");
        }

        if (turn.TryGetProperty("items", out JsonElement items) &&
            items.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                CaptureAgentMessage(item, ref lastAgentMessage, ref finalAgentMessage);
            }
        }

        string status = ReadRequiredString(turn, "status");
        if (!string.Equals(status, "completed", StringComparison.Ordinal))
        {
            string? turnError = ReadNestedOptionalString(turn, "error", "message");
            throw new InvalidOperationException(
                $"Codex app-server turn ended with status '{status}': " +
                $"{turnError ?? reportedError ?? "no error detail"}");
        }

        string? response = finalAgentMessage ?? lastAgentMessage;
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new InvalidDataException(
                "Codex app-server completed the turn without an agent message.");
        }

        return response;
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        object parameters,
        Action<JsonElement>? notificationHandler,
        CancellationToken cancellationToken)
    {
        long id = checked(++_nextRequestId);
        await WriteMessageAsync(new { method, id, @params = parameters }, cancellationToken);

        while (true)
        {
            JsonElement message = await ReadNextMessageAsync(cancellationToken);
            if (!message.TryGetProperty("id", out JsonElement responseId))
            {
                notificationHandler?.Invoke(message);
                continue;
            }

            if (!responseId.TryGetInt64(out long actualId) || actualId != id)
            {
                throw new InvalidDataException(
                    $"Codex app-server returned response id '{responseId}' while waiting for '{id}'.");
            }

            if (message.TryGetProperty("error", out JsonElement error))
            {
                string? code = error.TryGetProperty("code", out JsonElement codeElement)
                    ? codeElement.ToString()
                    : null;
                string? errorMessage = ReadOptionalString(error, "message");
                throw new InvalidOperationException(
                    $"Codex app-server request '{method}' failed" +
                    $"{(code is null ? string.Empty : $" ({code})")}: " +
                    $"{errorMessage ?? "no error detail"}");
            }

            if (!message.TryGetProperty("result", out JsonElement result))
            {
                throw new InvalidDataException(
                    $"Codex app-server response to '{method}' had neither result nor error.");
            }

            return result.Clone();
        }
    }

    private async Task<JsonElement> ReadNextMessageAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await _connection.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new EndOfStreamException("Codex app-server closed stdout unexpectedly.");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "Codex app-server wrote malformed JSON to stdout.",
                    exception);
            }

            using (document)
            {
                JsonElement message = document.RootElement.Clone();
                if (message.TryGetProperty("method", out _) &&
                    message.TryGetProperty("id", out JsonElement serverRequestId))
                {
                    await RejectServerRequestAsync(message, serverRequestId, cancellationToken);
                    continue;
                }

                return message;
            }
        }
    }

    private async Task RejectServerRequestAsync(
        JsonElement request,
        JsonElement requestId,
        CancellationToken cancellationToken)
    {
        string? method = ReadOptionalString(request, "method");
        object response = method switch
        {
            "item/commandExecution/requestApproval" or
            "item/fileChange/requestApproval" => new
            {
                id = requestId,
                result = new { decision = "decline" },
            },
            "item/permissions/requestApproval" => new
            {
                id = requestId,
                result = new { permissions = new { } },
            },
            "mcpServer/elicitation/request" => new
            {
                id = requestId,
                result = new { action = "decline", content = (object?)null },
            },
            _ => new
            {
                id = requestId,
                error = new { code = -32601, message = "DramaBoard does not support server requests." },
            },
        };
        await WriteMessageAsync(response, cancellationToken);
    }

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        string line = JsonSerializer.Serialize(message);
        await _connection.WriteLineAsync(line, cancellationToken);
    }

    private static string ComposePrompt(LlmChatRequest request) =>
        $"""
        [DramaBoard system prompt]
        {request.System}

        [DramaBoard current turn]
        {request.User}

        This is a text-only role-playing turn. Do not inspect files, run commands, or call tools.
        Return only the response requested by the DramaBoard system prompt.
        """;

    private static void CaptureAgentMessage(
        JsonElement item,
        ref string? lastAgentMessage,
        ref string? finalAgentMessage)
    {
        if (!HasStringValue(item, "type", "agentMessage") ||
            !item.TryGetProperty("text", out JsonElement textElement) ||
            textElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string text = textElement.GetString()!;
        lastAgentMessage = text;
        if (HasStringValue(item, "phase", "final_answer"))
        {
            finalAgentMessage = text;
        }
    }

    private static bool HasStringValue(JsonElement element, string name, string expected) =>
        element.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String &&
        string.Equals(property.GetString(), expected, StringComparison.Ordinal);

    private static string ReadRequiredString(JsonElement element, params string[] path)
    {
        JsonElement current = element;
        foreach (string segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                throw new InvalidDataException(
                    $"Codex app-server response was missing '{string.Join('.', path)}'.");
            }
        }

        if (current.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"Codex app-server response field '{string.Join('.', path)}' was not text.");
        }

        return current.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? ReadNestedOptionalString(
        JsonElement element,
        string parent,
        string child) =>
        element.TryGetProperty(parent, out JsonElement parentElement) &&
        parentElement.ValueKind == JsonValueKind.Object
            ? ReadOptionalString(parentElement, child)
            : null;
}

internal interface ICodexAppServerConnection : IAsyncDisposable
{
    bool IsAlive { get; }

    ValueTask WriteLineAsync(string line, CancellationToken cancellationToken);

    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken);
}

internal sealed class ProcessCodexAppServerConnection : ICodexAppServerConnection
{
    internal static Encoding JsonLineEncoding { get; } = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false);

    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly Task<string> _stderrDrain;

    private ProcessCodexAppServerConnection(Process process)
    {
        _process = process;
        _writer = process.StandardInput;
        _reader = process.StandardOutput;
        _stderrDrain = process.StandardError.ReadToEndAsync();
    }

    public bool IsAlive => !_process.HasExited;

    public static ProcessCodexAppServerConnection Start(CodexAppServerOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.CommandPath,
            WorkingDirectory = options.WorkingDirectory!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = JsonLineEncoding,
            StandardOutputEncoding = JsonLineEncoding,
            StandardErrorEncoding = JsonLineEncoding,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Failed to start the Codex app-server process.");
        return new ProcessCodexAppServerConnection(process);
    }

    public async ValueTask WriteLineAsync(
        string line,
        CancellationToken cancellationToken)
    {
        await _writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        await _writer.FlushAsync(cancellationToken);
    }

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        _reader.ReadLineAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            _writer.Close();
        }
        catch (IOException)
        {
            // The child may have already closed stdin while failing.
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // A concurrently exiting child is already stopped.
        }

        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await _stderrDrain.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException)
        {
            // Process disposal remains best-effort after the owned process has been killed.
        }
        finally
        {
            _reader.Dispose();
            _writer.Dispose();
            _process.Dispose();
        }
    }
}
