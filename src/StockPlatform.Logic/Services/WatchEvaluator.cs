using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>一个观察项的当前取值——由调用方从各张表里取好喂进来，求值器不碰 IO。</summary>
/// <param name="TradeDate">这个值**所属的交易日**，不是求值那天。</param>
/// <param name="Value">当前值。取不到＝null。</param>
/// <param name="PrevValue">上一个**不同的**值（见 <see cref="WatchEvaluator"/> 的自然日坑）。</param>
/// <param name="StageText">stage 类的当前状态文字（如回购的 "首次回购"）。</param>
/// <param name="PrevStageText">上一次记录到的 stage，用来判跃迁。</param>
public sealed record WatchReading(
    DateTime? TradeDate,
    double? Value = null,
    double? PrevValue = null,
    string? StageText = null,
    string? PrevStageText = null);

/// <summary>
/// 把一条观察项 + 它的当前取值，判成"触发/没触发"。见 doc/watch-item-design.md §4.3。
/// **纯计算**，可单测。
///
/// ════ ⚠ 行业指标的自然日坑 ════
/// <c>IndustryIndicatorValue</c> 是**自然日序列**，不是交易日序列：周末沿用周五的值，
/// 工作日里也常连续两天同值（生意社不是每天出新数）。实测 EMI00662659：
/// <code>09-04 周五 377.07 / 09-05 周六 377.07 / 09-06 周日 377.07 / 09-07 周一 356.69</code>
/// 所以 **"值没变" ≠ "没更新"**。环比必须跟**上一个不同值**比（<see cref="WatchReading.PrevValue"/>
/// 的语义就是这个），跟上一行比会在周末恒等于 0，且分不清"真没动"和"没发布"。
///
/// ════ Manual 只提醒，不判定 ════
/// <see cref="WatchKind.Manual"/> 是给"库里确实没有这个数据"的变量留的诚实出口
/// （如月度动力电池装车份额）。它产出"该去查了"，**不产出"已触发"**——
/// 假装能自动判会让人以为没触发就是安全的。
/// </summary>
public static class WatchEvaluator
{
    /// <summary>判一条。返回 null＝没触发。</summary>
    public static WatchHit? Evaluate(WatchItem item, WatchReading reading)
    {
        if (!item.Enabled) return null;

        // Manual：只在有截止日提醒时产出"该去查了"，措辞上绝不说"已触发"
        if (item.Kind == WatchKind.Manual || item.Op == WatchOp.Remind)
            return Hit(item, reading, reading.Value,
                $"【需人工核对】{item.Reason}（库里没有这个数据源，请自行查证）");

        // stage 跃迁：跟上次记录的不一样就报
        if (item.Op == WatchOp.StageChange)
        {
            if (string.IsNullOrEmpty(reading.StageText)) return null;
            if (reading.StageText == reading.PrevStageText) return null;
            var msg = string.IsNullOrEmpty(reading.PrevStageText)
                ? $"{item.Reason} → 现状：{reading.StageText}"
                : $"{item.Reason}：{reading.PrevStageText} → **{reading.StageText}**";
            return Hit(item, reading, reading.Value, msg);
        }

        if (reading.Value is not { } v) return null;

        switch (item.Op)
        {
            case WatchOp.Lt:
                if (item.Threshold is { } lt && v < lt)
                    return Hit(item, reading, v, $"{item.Reason}：当前 {Fmt(v)} < 阈值 {Fmt(lt)}");
                break;

            case WatchOp.Gt:
                if (item.Threshold is { } gt && v > gt)
                    return Hit(item, reading, v, $"{item.Reason}：当前 {Fmt(v)} > 阈值 {Fmt(gt)}");
                break;

            case WatchOp.CrossDown:
                // 有阈值就是"下穿阈值"，没阈值就是"比上一个不同值低"。
                // ⚠ PrevValue 必须是**上一个不同的值**，不是上一行——见类注释的自然日坑。
                if (item.Threshold is { } cd)
                {
                    if (reading.PrevValue is { } pd && pd >= cd && v < cd)
                        return Hit(item, reading, v, $"{item.Reason}：下穿 {Fmt(cd)}（{Fmt(pd)} → {Fmt(v)}）");
                }
                else if (reading.PrevValue is { } p2 && v < p2)
                {
                    var pct = p2 == 0 ? 0 : (v / p2 - 1) * 100;
                    return Hit(item, reading, v, $"{item.Reason}：{Fmt(p2)} → {Fmt(v)}（{pct:+0.0;-0.0}%）");
                }
                break;

            case WatchOp.CrossUp:
                if (item.Threshold is { } cu)
                {
                    if (reading.PrevValue is { } pu && pu <= cu && v > cu)
                        return Hit(item, reading, v, $"{item.Reason}：上穿 {Fmt(cu)}（{Fmt(pu)} → {Fmt(v)}）");
                }
                else if (reading.PrevValue is { } p3 && v > p3)
                {
                    var pct = p3 == 0 ? 0 : (v / p3 - 1) * 100;
                    return Hit(item, reading, v, $"{item.Reason}：{Fmt(p3)} → {Fmt(v)}（{pct:+0.0;-0.0}%）");
                }
                break;
        }
        return null;
    }

    private static WatchHit Hit(WatchItem item, WatchReading reading, double? value, string msg) => new()
    {
        ItemId = item.ItemId,
        Code = item.Code,
        Name = item.Name,
        // 值所属交易日；实在没有才退回今天（Manual 项常常没有）
        TriggerTradeDate = reading.TradeDate ?? DateTime.Today,
        ObservedValue = value,
        Message = msg,
        Priority = item.Priority,
    };

    private static string Fmt(double v)
        => Math.Abs(v) >= 1e8 ? $"{v / 1e8:0.##} 亿"
           : Math.Abs(v) >= 1e4 ? $"{v / 1e4:0.##} 万"
           : $"{v:0.####}";

    /// <summary>
    /// 从一段**自然日**序列里取出"当前值"和"上一个不同的值"。
    /// 这是上面那个自然日坑的通用解法，行业指标那一路都该走它。
    /// </summary>
    /// <param name="series">按日期升序的 (日期, 值)。</param>
    public static WatchReading ReadLatestDistinct(IReadOnlyList<(DateTime Date, double Value)> series)
    {
        if (series.Count == 0) return new WatchReading(null);
        var last = series[^1];
        for (int i = series.Count - 2; i >= 0; i--)
            if (Math.Abs(series[i].Value - last.Value) > 1e-12)
                return new WatchReading(last.Date, last.Value, series[i].Value);
        return new WatchReading(last.Date, last.Value);   // 整段都是同一个值
    }
}
