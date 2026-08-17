using DramaBoard.Host;
using DramaBoard.Protocol;

namespace DramaBoard.Player.Llm;

/// <summary>Runs one self-contained LLM cognitive loop for a single actor.</summary>
public sealed class LlmPlayerDriver : IPlayerDriver
{
    private const string RetryInstruction =
        "[格式纠正]\n上次回复无法解析，请严格按格式重新回复，并确保【行动】包含单个合法 JSON 对象。";

    private readonly CharacterCard _characterCard;
    private readonly ILlmChatBackend _backend;
    private readonly Action<LlmTurnTrace>? _turnTraceSink;
    private readonly IReadOnlyList<ReferenceMaterial> _referenceMaterials;
    private IReadOnlyList<KnownFact> _previousKnownFacts = [];
    private string _currentMemory;

    /// <summary>Initializes one actor's driver and private memory document.</summary>
    public LlmPlayerDriver(
        CharacterCard characterCard,
        string initialMemory,
        ILlmChatBackend backend,
        Action<LlmTurnTrace>? turnTraceSink = null,
        IReadOnlyList<ReferenceMaterial>? referenceMaterials = null)
    {
        ArgumentNullException.ThrowIfNull(characterCard);
        ArgumentNullException.ThrowIfNull(initialMemory);
        ArgumentNullException.ThrowIfNull(backend);

        _characterCard = characterCard;
        _currentMemory = initialMemory;
        _backend = backend;
        _turnTraceSink = turnTraceSink;
        _referenceMaterials = referenceMaterials is null ? [] : [.. referenceMaterials];
    }

    /// <summary>Gets the actor's latest complete private memory document.</summary>
    public string CurrentMemory => _currentMemory;

    /// <inheritdoc />
    public async ValueTask<PlayerDecision> DecideAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        LlmChatRequest prompt = PromptRenderer.Render(
            _characterCard,
            _currentMemory,
            request,
            _previousKnownFacts,
            _referenceMaterials);
        string response = await _backend.CompleteAsync(prompt, cancellationToken);
        LlmOutputParseResult parsed = LlmOutputParser.Parse(response);
        int attemptCount = 1;
        if (!parsed.IsSuccess)
        {
            attemptCount = 2;
            var retry = prompt with { User = $"{prompt.User}\n\n{RetryInstruction}" };
            response = await _backend.CompleteAsync(retry, cancellationToken);
            parsed = LlmOutputParser.Parse(response);
        }

        if (!parsed.IsSuccess)
        {
            return CreateDecision(request, new Intent(ActionKinds.Wait));
        }

        PlayerDecision decision = CreateDecision(request, parsed.Intent!);
        string previousMemory = _currentMemory;
        IReadOnlyList<KnownFact> previousKnownFacts = _previousKnownFacts;
        _currentMemory = parsed.Memory;
        _previousKnownFacts = [.. request.Observation.KnownFacts];
        try
        {
            _turnTraceSink?.Invoke(new LlmTurnTrace(
                request,
                decision,
                parsed.Monologue,
                parsed.Dialogue,
                parsed.Memory,
                attemptCount));
        }
        catch
        {
            _currentMemory = previousMemory;
            _previousKnownFacts = previousKnownFacts;
            throw;
        }

        return decision;
    }

    private static PlayerDecision CreateDecision(DecisionRequest request, Intent intent) =>
        new(
            request.DecisionId,
            request.BasedOnWorldVersion,
            request.LineageId,
            intent);
}
