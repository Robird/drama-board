using System.Globalization;
using DramaBoard.ResourceCycle;

internal enum PlayInputKind { Invalid, Look, Help, Quit, Action }

/// <summary>Parsed console intent, before checking whether an action is legal in the current world.</summary>
internal sealed record PlayInput {
    public PlayInputKind Kind { get; }
    public CycleCommand? Command { get; }

    private PlayInput(PlayInputKind kind, CycleCommand? command = null) {
        Kind = kind;
        Command = command;
    }

    /// <remarks>Case-sensitive, whitespace-separated syntax. Numeric range, resource identity and
    /// activity restrictions are domain checks; EOF is handled by the console owner.</remarks>
    public static PlayInput Parse(string input) {
        ArgumentNullException.ThrowIfNull(input);
        string[] parts = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["quit"]) { return new(PlayInputKind.Quit); }
        if (parts is ["look"]) { return new(PlayInputKind.Look); }
        if (parts is ["help"]) { return new(PlayInputKind.Help); }
        CycleCommand command;
        if (parts is ["wait", var hours] && int.TryParse(hours, NumberStyles.None, CultureInfo.InvariantCulture, out int waitHours)) {
            command = CycleCommand.Wait(waitHours);
        } else if (parts is ["gather", var allResource, "all"]) {
            command = CycleCommand.GatherAll(allResource);
        } else if (parts is ["gather", var resourceId, var quantity] && int.TryParse(quantity, NumberStyles.None, CultureInfo.InvariantCulture, out int count)) {
            command = CycleCommand.Gather(resourceId, count);
        } else if (parts is ["continue"]) {
            command = CycleCommand.Continue();
        } else if (parts is ["cancel"]) {
            command = CycleCommand.Cancel();
        } else if (parts is ["go", var passageId]) {
            command = CycleCommand.Go(passageId);
        } else if (parts is ["use", var itemId]) {
            command = CycleCommand.Use(itemId);
        } else {
            return new(PlayInputKind.Invalid);
        }
        return new(PlayInputKind.Action, command);
    }
}
