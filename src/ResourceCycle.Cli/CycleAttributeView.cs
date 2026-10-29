using System.Globalization;
using DramaBoard.ResourceCycle;

/// <summary>A transient attribute projection of frozen observation material, shared by full and delta output.</summary>
internal sealed record CycleAttributeView(string Field, string Label, decimal Value, decimal Minimum,
    decimal Maximum, RateBreakdown Rate) {
    public bool SameAs(CycleAttributeView other) => Value == other.Value && Minimum == other.Minimum &&
        Maximum == other.Maximum && Rate.SameAs(other.Rate);

    public string Range => $"{Number(Minimum)}–{Number(Maximum)}";
    public string Limit => Value >= Maximum && Rate.Total > 0 ? "已达上限，增长受限" :
        Value <= Minimum && Rate.Total < 0 ? "已达下限，下降受限" : "";
    public string RateDescription => $"{Signed(Rate.Total)}/小时；速率构成：" +
        (Rate.Contributions.Count == 0 ? "无持续作用" : string.Join("；", Rate.Contributions.Select(
            contribution => $"{CharacterContextRenderer.DataLiteral(contribution.SourceLabel).Replace("|", "\\u007C", StringComparison.Ordinal)} {Signed(contribution.Amount)}/小时")));
    public string Description => $"{Number(Value)}/{Number(Maximum)}（范围 {Range}）；{RateDescription}" +
        (Limit.Length == 0 ? "" : $"；{Limit}");

    public static IEnumerable<CycleAttributeView> FromSurvival(CycleSurvivalObservation survival) {
        yield return new("food", "食物", survival.Food, survival.Minimum, survival.Maximum, survival.FoodRate);
        yield return new("water", "饮水", survival.Water, survival.Minimum, survival.Maximum, survival.WaterRate);
        yield return new("warmth", "温暖", survival.Warmth, survival.Minimum, survival.Maximum, survival.WarmthRate);
    }

    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Signed(int value) => (value > 0 ? "+" : "") + value.ToString(CultureInfo.InvariantCulture);
}
