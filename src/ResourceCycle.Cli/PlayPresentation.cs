using System.Collections.ObjectModel;

/// <summary>Console-session preferences only; not character memory or durable game state.</summary>
internal sealed class PlayPresentation {
    private readonly IReadOnlyDictionary<string, IPlayLayout> _layouts;
    public IReadOnlyList<IPlayLayout> Layouts { get; }
    public IReadOnlyDictionary<string, PlayOutputMode> SupportedModes { get; }
    public IPlayLayout Current { get; private set; }
    public PlayOutputMode Mode { get; private set; }

    public PlayPresentation(IPlayLayout initial, params IPlayLayout[] alternatives)
        : this(false, initial, alternatives) { }

    private PlayPresentation(bool supportsDelta, IPlayLayout initial, params IPlayLayout[] alternatives) {
        Current = initial;
        Layouts = Array.AsReadOnly(alternatives.Prepend(initial).ToArray());
        _layouts = Layouts.ToDictionary(layout => layout.Id, StringComparer.Ordinal);
        var modes = new Dictionary<string, PlayOutputMode>(StringComparer.Ordinal) { ["full"] = PlayOutputMode.Full };
        if (supportsDelta) { modes.Add("delta", PlayOutputMode.Delta); }
        SupportedModes = new ReadOnlyDictionary<string, PlayOutputMode>(modes);
        Mode = supportsDelta ? PlayOutputMode.Delta : PlayOutputMode.Full;
    }

    public static PlayPresentation ForMode(bool survival) => survival
        ? new(true, new DecisionPlayLayout(), new ClassicPlayLayout())
        : new(new ClassicPlayLayout(), new DecisionPlayLayout());

    public bool TrySelectMode(string id) {
        if (!SupportedModes.TryGetValue(id, out PlayOutputMode selected)) { return false; }
        Mode = selected;
        return true;
    }

    public bool TrySelect(string id) {
        if (!_layouts.TryGetValue(id, out IPlayLayout? selected)) { return false; }
        Current = selected;
        return true;
    }
}

internal enum PlayOutputMode { Full, Delta }
