namespace DramaBoard.Kernel.Journal;

/// <summary>Tracks event kind identifiers and their expected CLR payload types.</summary>
public sealed class EventKindRegistry
{
    private readonly Dictionary<string, (EventKind Kind, Type PayloadType)> _registrations =
        new(StringComparer.Ordinal);

    /// <summary>Gets the number of registered event kind identifiers.</summary>
    public int Count => _registrations.Count;

    /// <summary>Registers an event kind identifier for the supplied payload type.</summary>
    public void Register<TPayload>(EventKind kind) => Register(kind, typeof(TPayload));

    /// <summary>Registers an event kind identifier for the expected CLR payload type.</summary>
    public void Register(EventKind kind, Type payloadType)
    {
        ArgumentNullException.ThrowIfNull(payloadType);

        if (_registrations.TryAdd(kind.Id, (kind, payloadType)))
        {
            return;
        }

        (EventKind existingKind, Type existingPayloadType) = _registrations[kind.Id];
        throw new InvalidOperationException(
            $"Event kind id '{kind.Id}' is already registered as version {existingKind.Version} " +
            $"with payload type '{existingPayloadType.FullName}'.");
    }
}