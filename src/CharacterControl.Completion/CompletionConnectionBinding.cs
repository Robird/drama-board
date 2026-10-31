using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;

namespace DramaBoard.CharacterControl.Completion;

/// <summary>Host-only connection selection; never persist this configuration or credential contents as character state.</summary>
public sealed record CompletionConnectionOptions(string? Model = null, string? AuthFile = null,
    string? Catalog = null, string? Connection = null);

/// <summary>Owns a selected Completion registry. Create lazily at the first actual model decision, after recovery.</summary>
public sealed class CompletionConnectionBinding : IAsyncDisposable {
    private readonly CompletionConnectionRegistry _registry;
    public string ConnectionId { get; }
    public string Model { get; }
    public ICompletionClient Client => _registry.GetClient(ConnectionId);

    private CompletionConnectionBinding(CompletionConnectionRegistry registry, string connectionId, string model) {
        _registry = registry; ConnectionId = connectionId; Model = model;
    }

    public static async Task<CompletionConnectionBinding> ConnectAsync(CompletionConnectionOptions options,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Catalog is null && options.Connection is not null || options.Catalog is not null && options.AuthFile is not null) {
            throw new ArgumentException("A connection requires a catalog; an explicit auth file cannot override a catalog connection.");
        }
        CompletionConnectionCatalogConfig catalog;
        CompletionConnectionConfig selected;
        ICompletionClientFactory factory = new DefaultCompletionClientFactory();
        if (options.Catalog is not null) {
            selected = CompletionConnectionConfigLoader.LoadSelectedConnectionFile(options.Catalog,
                options.Connection ?? throw new ArgumentException("A catalog requires an explicit connection ID."));
            if (options.Model is not null) { selected = selected with { ModelId = options.Model }; }
            // Unselected providers need not resolve secrets or initialize clients.
            catalog = CompletionConnectionConfigLoader.NormalizeAndValidateCatalog(new([selected]));
            selected = catalog.Connections.Single();
            if (selected.Kind == CodexSubscriptionCompletionClientFactory.ConnectionKind) {
                factory = CodexSubscriptionCompletionClientFactory.CreateFromEnvironment(factory, productName: "DramaBoard");
            }
        } else {
            selected = new("drama-codex", CodexSubscriptionCompletionClientFactory.ConnectionKind,
                options.Model ?? "gpt-6.1-sol", CodexSubscriptionCompletionClientFactory.CompletionSurfaceId,
                CodexSubscriptionCompletionClientFactory.CanonicalBaseAddress);
            var credentials = options.AuthFile is null ? new CodexCliAuthFileCredentialProvider()
                : new CodexCliAuthFileCredentialProvider(options.AuthFile);
            CodexSubscriptionCredential credential = await credentials.GetCredentialAsync(cancellationToken);
            factory = new CodexSubscriptionCompletionClientFactory(credentials, credential.AccountFingerprint,
                productName: "DramaBoard", fallback: factory);
            catalog = new([selected]);
        }
        return new(new CompletionConnectionRegistry(catalog, factory), selected.Id, selected.ModelId);
    }

    public ValueTask DisposeAsync() => _registry.DisposeAsync();
}
