using System.Globalization;
using DramaBoard.Playground;

/// <summary>A transient attribute projection of frozen observation material, shared by full and delta output.</summary>
internal sealed record PlaygroundAttributeView(string Field, string Label, decimal Value, decimal Minimum,
    decimal Maximum, RateBreakdown Rate, PlaygroundAttributeForecast Forecast, decimal WarningThreshold,
    bool WarningPending, bool ForecastEnabled) {
    public bool SameAs(PlaygroundAttributeView other) => Value == other.Value && Minimum == other.Minimum &&
        Maximum == other.Maximum && Rate.SameAs(other.Rate) && Forecast == other.Forecast &&
        WarningThreshold == other.WarningThreshold && WarningPending == other.WarningPending &&
        ForecastEnabled == other.ForecastEnabled;

    public const string ForecastAssumptions = "预测条件：不补给、保持当前庇护状态，计入已知昼夜变化；未计随机寒风，也不预知抵达后的环境。各项独立推算，不代表能存活至所有预测时刻。";

    public string Range => $"{Number(Minimum)}–{Number(Maximum)}";
    public string Limit => Value >= Maximum && Rate.Total > 0 ? "已达上限，增长受限" :
        Value <= Minimum && Rate.Total < 0 ? "已达下限，下降受限" : "";
    public string RateDescription => $"{Signed(Rate.Total)}/小时；速率构成：" +
        (Rate.Contributions.Count == 0 ? "无持续作用" : string.Join("；", Rate.Contributions.Select(
            contribution => $"{CharacterContextRenderer.DataLiteral(contribution.SourceLabel).Replace("|", "\\u007C", StringComparison.Ordinal)} {Signed(contribution.Amount)}/小时")));
    public string ForecastDescription => !ForecastEnabled ? "" :
        (Value <= WarningThreshold ? $"已处于警戒区（≤{Number(WarningThreshold)}）" :
            Forecast.WarningAt is { } warning ? $"预计降至警戒线 {Number(WarningThreshold)}：{Time(warning)}" :
            $"本次预测范围内不触及警戒线 {Number(WarningThreshold)}") + "；" +
        (Value <= Minimum ? $"已耗尽至 {Number(Minimum)}" :
            Forecast.DepletionAt is { } depletion ? $"预计耗尽至 {Number(Minimum)}：{Time(depletion)}" :
            "本次预测范围内不耗尽") + (WarningPending ? "；历史警告待确认" : "");
    public string Description => $"{Number(Value)}/{Number(Maximum)}（范围 {Range}）；{RateDescription}" +
        (Limit.Length == 0 ? "" : $"；{Limit}") + (ForecastDescription.Length == 0 ? "" : $"；{ForecastDescription}");

    public static IEnumerable<PlaygroundAttributeView> FromSurvival(PlaygroundSurvivalObservation survival, bool forecastEnabled = true) {
        yield return new("food", "食物", survival.Food, survival.Minimum, survival.Maximum, survival.FoodRate,
            survival.FoodForecast, survival.WarningThreshold, (survival.PendingWarningMask & 1) != 0, forecastEnabled);
        yield return new("water", "饮水", survival.Water, survival.Minimum, survival.Maximum, survival.WaterRate,
            survival.WaterForecast, survival.WarningThreshold, (survival.PendingWarningMask & 2) != 0, forecastEnabled);
        yield return new("warmth", "温暖", survival.Warmth, survival.Minimum, survival.Maximum, survival.WarmthRate,
            survival.WarmthForecast, survival.WarningThreshold, (survival.PendingWarningMask & 4) != 0, forecastEnabled);
    }

    private static string Time(DramaBoard.Kernel.Time.ModelTime time) =>
        PlaygroundUpdateRenderer.Format(PlaygroundVisibleValue.Numeric(time.Ticks, "ticks"));

    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Signed(int value) => (value > 0 ? "+" : "") + value.ToString(CultureInfo.InvariantCulture);
}
