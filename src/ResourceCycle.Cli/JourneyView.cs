using DramaBoard.ResourceCycle;

internal static class JourneyView {
    public static string Route(CycleJourneyObservation journey) =>
        $"从{journey.OriginName}出发 → {journey.KnownDestinationName ?? "未知目的地（尚未探索）"}；通路 `{journey.PassageId}`：{journey.PassageDescription}";
}
