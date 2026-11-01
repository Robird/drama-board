using Atelia.DurableGraph;
using System.Globalization;

namespace DramaBoard.ResourceCycle;

/// <summary>Shared game background selected only from a character's visible observation.</summary>
/// <remarks>Contains semantic text, without Console management, layout markup or live world references.
/// Dynamic rule parameters are copied into the returned material when it is created.</remarks>
[DurableType("DramaBoard.ResourceCycle.Background", 1)]
public sealed partial class CycleBackground : IDurableObject {
    [DurableField(1)] private string _title;
    public string Title => _title;
    [DurableField(2)] private string _introduction;
    public string Introduction => _introduction;
    [DurableField(3)] private List<string> _rules;
    public IReadOnlyList<string> Rules => _rules.AsReadOnly();

    private CycleBackground(string title, string introduction, IEnumerable<string> rules) {
        _title = title;
        _introduction = introduction;
        _rules = new(rules);
    }

    public static CycleBackground For(CycleObservation observation) {
        ArgumentNullException.ThrowIfNull(observation);
        List<string> rules = [
            "行动命令与标识区分大小写，需提供完整命令和标识。",
            "采集按每份周期结算，不预留库存；all 固定为接受时的库存，不随再生增加。库存耗尽会提醒，结算时无货则结束，不会自动等待再生。",
            observation.IsExpedition
                ? "通路描述是眼前线索，不保证目的地或安全；移动途中不能取消。查看、补给与确认消息不重置活动或行程期限。"
                : "先完成先得；同刻完成按固定规则仲裁。别人只公开正在采集哪个点，不公开技能、速度或下一份完成时刻。"
        ];
        if (observation.Survival is not null) {
            rules.Add("余量范围 0–100；25 触发低值提醒，任一项耗尽至 0 则该角色死亡。镇与山之间可以往返，抵达镇口或山站不会结束生活；死亡角色不再行动，其他角色仍可继续。");
            rules.Add("补给不耗时，不确认历史警告；continue 确认并保留原期限，接受新的耗时行动也会确认已展示的警告。");
            rules.Add("温暖无法靠物品补充；庇护内或白天野外可回暖，夜间野外继续降温。");
        }
        if (observation.IsExpedition && (observation.Actor.Backpack.Count("item.wood") > 0 ||
            observation.Resources.Any(resource => resource.ItemId == "item.wood"))) {
            rules.Add("干木目前不能生火或直接取暖；在庇护中停留可以回暖，仍会消耗食物和饮水。");
        }
        if (observation.Weather is { } weather) {
            rules.Add($"天气窗口每 {Number((decimal)weather.IntervalTicks / CycleTime.Hour)} 小时出现一次；结果可能平静，也可能遭遇寒风。");
            rules.Add($"寒风使暴露者的温暖最多减少 {Number(weather.ExposedWarmthLoss)} 点；处于庇护内则免受这次损失。移动途中视为暴露。");
        }
        if (observation.Communication is { } communication) {
            rules.Add($"同地活跃角色可用 say 说话、give 赠与；在地点内可用 shout 大喊，传到本地及一跳声音连接的地点，途中角色听不到。三种交流共用每个角色每个游戏时刻 {communication.SendLimit} 次成功发送额度，不耗时；时间推进后恢复额度，换回合或确认消息不返还额度。");
            rules.Add("话语是署名角色的声明，不代表游戏确认了地图或事实。收到话语或赠与会请求响应，但不取消活动；continue 确认通信并保留原期限，独自闲置时须安排耗时行动或加入跟随才能放行时间。");
        }
        if (observation.IsExpedition) {
            rules.Add("同地且没有耗时活动时可 follow <角色编号> 主动跟随；成功后确认自己的提醒并等待领队，不必每段路重新输入。领队用普通 go 带实际已跟随者一起移动，不自动等待未加入的人。");
            rules.Add("跟随期间不能自行 go、gather 或 wait；身体提醒仍交给本人处理，可补给后 continue。领队到达后可 dismiss-followers 让大家分头行动；队员在地点内自己的决策窗口可 unfollow，途中不能退出。");
            rules.Add("解除跟随不确认任何人的提醒；完成或取消自己的活动后才可重新加入。跟随只授权移动，不共享私有目标、背包或地图记忆。");
        }
        return new CycleBackground(observation.IsExpedition ? LivingWorldContent.Name : "林间苗圃",
            observation.IsExpedition ? "你生活在山野镇附近。可以在镇中停留，也可以进山采集、探索，再返回继续生活；没有必须完成的主线或固定天数的终局。"
                : "林间苗圃的资源由在场角色共同采集。", rules);
    }

    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
