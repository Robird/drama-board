using DramaBoard.Playground;

internal static class JourneyView {
    public static string Route(PlaygroundJourneyObservation journey) =>
        $"从{journey.OriginName}出发 → {journey.KnownDestinationName ?? "另一端未知（这条入口去向未确认）"}；入口 `{journey.EntranceId}`：{journey.PassageDescription}";
}
