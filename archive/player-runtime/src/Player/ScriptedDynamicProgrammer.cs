using DramaBoard.Protocol;

namespace DramaBoard.Player;

/// <summary>Answers requests from a finite, ordered sequence of program factories.</summary>
public sealed class ScriptedDynamicProgrammer : IDynamicProgrammer {
    private readonly Queue<Func<ProgrammingRequest, ProgrammingResponse>> _script;

    /// <summary>Initializes a programmer whose factories are consumed in request order.</summary>
    public ScriptedDynamicProgrammer(IEnumerable<Func<ProgrammingRequest, ProgrammingResponse>> script) {
        ArgumentNullException.ThrowIfNull(script);

        Func<ProgrammingRequest, ProgrammingResponse>[] factories = [.. script];
        if (factories.Any(factory => factory is null)) {
            throw new ArgumentException("Programming script cannot contain null factories.", nameof(script));
        }

        _script = new Queue<Func<ProgrammingRequest, ProgrammingResponse>>(factories);
    }

    /// <inheritdoc />
    public ValueTask<ProgrammingResponse> ProgramAsync(
        ProgrammingRequest request,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_script.TryDequeue(out Func<ProgrammingRequest, ProgrammingResponse>? factory)) {
            throw new InvalidOperationException("The scripted Programmer has no program remaining for this request.");
        }

        ProgrammingResponse response = factory(request)
            ?? throw new InvalidOperationException("A scripted program factory returned null.");
        return ValueTask.FromResult(response);
    }
}
