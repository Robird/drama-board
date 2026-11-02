using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;

namespace DramaBoard.CharacterControl.Completion;

/// <summary>Owns lazily created clients for one frozen host configuration. Returned bindings borrow them.</summary>
public sealed class LlmConnectionPool : IAsyncDisposable {
    private readonly LlmConnectionConfiguration _configuration;
    private readonly ICompletionClientFactory? _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, LlmDecisionBinding> _bindings = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <param name="factory">Optional factory whose returned clients become owned by this pool.</param>
    public LlmConnectionPool(LlmConnectionConfiguration configuration, ICompletionClientFactory? factory = null) {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _factory = factory;
    }

    public async Task<LlmDecisionBinding> GetAsync(string connectionId, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_bindings.TryGetValue(connectionId, out var existing)) { return existing; }
            CompletionConnectionConfig definition = _configuration.GetConnection(connectionId);
            CompletionConnectionCatalogConfig resolved = CompletionConnectionConfigLoader.NormalizeAndValidateCatalog(new([definition]));
            ICompletionClientFactory factory = _factory ?? new DefaultCompletionClientFactory();
            if (_factory is null && definition.Kind == CodexSubscriptionCompletionClientFactory.ConnectionKind) {
                var credentials = new CodexCliAuthFileCredentialProvider();
                CodexSubscriptionCredential credential = await credentials.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
                factory = new CodexSubscriptionCompletionClientFactory(credentials, credential.AccountFingerprint,
                    productName: "DramaBoard", fallback: factory);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ICompletionClient client = factory.Create(resolved.Connections.Single())
                ?? throw new InvalidOperationException("LLM client factory returned null.");
            var binding = new LlmDecisionBinding(client, definition.ModelId, connectionId);
            _bindings.Add(connectionId, binding);
            return binding;
        } finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync() {
        await _gate.WaitAsync().ConfigureAwait(false);
        try {
            if (_disposed) { return; }
            _disposed = true;
            var failures = new List<Exception>();
            foreach (ICompletionClient client in _bindings.Values.Select(x => x.Client)
                .Distinct<ICompletionClient>(ReferenceEqualityComparer.Instance)) {
                try {
                    if (client is IAsyncDisposable asyncDisposable) { await asyncDisposable.DisposeAsync().ConfigureAwait(false); }
                    else if (client is IDisposable disposable) { disposable.Dispose(); }
                } catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException) {
                    failures.Add(error);
                }
            }
            _bindings.Clear();
            if (failures.Count != 0) { throw new AggregateException("LLM clients failed during disposal.", failures); }
        } finally { _gate.Release(); }
    }
}
