using DramaBoard.Kernel.Time;

namespace DramaBoard.ResourceCycle;

public enum CycleCommandKind { Wait, Gather }

/// <summary>Runtime intent. Only its validated effects are recorded in durable facts.</summary>
public sealed record CycleCommand(CycleCommandKind Kind, int Hours = 0, string? PlantId = null) {
    public static CycleCommand Wait(int hours) => new(CycleCommandKind.Wait, Hours: hours);
    public static CycleCommand Gather(string plantId) => new(CycleCommandKind.Gather, PlantId: plantId);
}

public interface ICycleDecisionSource {
    /// <summary>Does not advance logical time. A host can ask a human, run a model loop, or return a script action.</summary>
    ValueTask<CycleCommand> ChooseAsync(CycleWorld world, string actorId, CancellationToken cancellationToken);
}

public static class CycleActions {
    /// <summary>Returns null for an executable action, otherwise a displayable reason. This query never changes state.</summary>
    public static string? Check(CycleWorld world, string actorId, CycleCommand? command) {
        ArgumentNullException.ThrowIfNull(world);
        if (world.Character is not { } actor || actor.Id != actorId) { return "当前世界没有这个角色。"; }
        if (actor.WaitUntil is not null) { return "角色正在等待，行动完成后才能再次选择。"; }
        if (command is null) { return "没有提供行动。"; }
        switch (command.Kind) {
            case CycleCommandKind.Wait:
                if (command.Hours is < 1 or > 24 || command.PlantId is not null) { return "等待需要 1 到 24 的整数小时数。"; }
                if (world.Time.Ticks > long.MaxValue - command.Hours * CycleTime.Hour) { return "等待结束时间超出支持范围。"; }
                return null;
            case CycleCommandKind.Gather:
                if (command.Hours != 0) { return "采集不接受等待时长。"; }
                CyclePlant? plant = world.Plants.SingleOrDefault(p => p.Id == command.PlantId);
                if (plant is null) { return "没有这个资源点。"; }
                if (!plant.IsMature) { return "资源尚未成熟，不能采集。"; }
                if (actor.Backpack.Count(plant.Id) == int.MaxValue) { return "背包中该物品的数量已达到上限。"; }
                return null;
            default: return "未知行动。";
        }
    }
}
