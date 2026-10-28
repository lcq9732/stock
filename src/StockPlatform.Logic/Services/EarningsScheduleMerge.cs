using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 预约披露日**两家合并成一份**（2026-10-27，见 doc/financial-fetch-disclosure-race-design.md）。
/// 纯计算，不碰文件也不碰库。
///
/// ════ 为什么合并要在这里、而不是在每个查询里 ════
/// 第一版把 UNION 写进了仓储的四个查询（闸门两个、界面两个），于是同一套规则抄了四遍，
/// 而且**再加一个查询方法就会漏掉合并**——消费端有财务抓取、股东抓取、金融监管指标、
/// 界面的财报日列，将来只会更多。合并是**判据**，判据归 Logic；仓储只负责读写。
///
/// 所以改成：抓取时就把两家合成一份落进 EarningsScheduleMerged，**消费端只读那一张表**，
/// 查询恢复成最简单的单表 SQL，谁都不需要知道底下有两个数据源。
///
/// ════ 三条规则，方向各不相同 ════
/// ① **披露了没有** → 并集：任一家标了就算披露了。这是闸门，要的是**早**。
///    巨潮作为交易所指定披露网站，标记要走正式入库流程，实测滞后半天以上（2026-10-27
///    当天 00:39 标了 1325 只、14:12 变 1690 只）；东财先标的票能早一轮进待抓名单。
/// ② **实际披露日的值** → 两家都标时取**较晚**的。它是「披露超 3 天、数据源仍没有这一期」
///    那条告警的起算点（FinancialFetchPlanner.SourceLagAlarm），取早的会让告警提前触发、
///    把真故障淹在误报里。**跟①方向相反**，这是最容易写错的地方。
/// ③ **预约日和改期轨迹** → 以巨潮为准，东财只在巨潮没有这一条时补上。这一列是给人看的
///    （底仓/主动仓的「财报日」），官方口径更稳，也不会因为两家差一天就来回跳。
///
/// ════ 下限就是现状 ════
/// 东财那边没有数据时（任务没跑过、那条腿断了、它漏了这只票），合并结果**跟巨潮原样一致**。
/// 这是整个方案敢直接上线、不必先验证"东财是否更快"的根据，<c>东财缺席时与巨潮逐字段相同</c>
/// 那条测试就是盯着它的。
/// </summary>
public static class EarningsScheduleMerge
{
    /// <summary>
    /// 合并两家的行。按 (股票, 报告期) 对齐，任一家独有的行原样保留。
    /// 返回的顺序不保证，调用方要稳定顺序自己排。
    /// </summary>
    public static List<EarningsScheduleRow> Merge(
        IEnumerable<EarningsScheduleRow> cninfo, IEnumerable<EarningsScheduleEmRow> em)
    {
        var byKey = new Dictionary<(string Code, DateTime Period), EarningsScheduleRow>();

        foreach (var c in cninfo) byKey[(c.Code, c.ReportPeriod)] = c;

        foreach (var e in em)
        {
            var key = (e.Code, e.ReportPeriod);
            if (byKey.TryGetValue(key, out var c))
            {
                byKey[key] = MergeOne(c, e);
                continue;
            }

            // 巨潮没有这一条（实测两家差 3 条，方向两边都有）——东财原样补上。
            // 预约日对齐巨潮的语义：东财的 AppointDate 已经是算过改期的当前值，
            // 而 EarningsScheduleRow.EffectiveDate 是 change3??change2??change1??appoint，
            // 所以这里放它的**首次**预约日，改期轨迹照搬，算出来才是同一个意思。
            byKey[key] = new EarningsScheduleRow(
                e.Code, e.ReportPeriod,
                e.FirstAppointDate ?? e.AppointDate,
                e.Change1, e.Change2, e.Change3,
                e.ActualDate);
        }

        return [.. byKey.Values];
    }

    /// <summary>两家都有这一条时怎么合——三条规则都在这儿。</summary>
    private static EarningsScheduleRow MergeOne(EarningsScheduleRow c, EarningsScheduleEmRow e)
        => c with
        {
            // ①＋② 披露了没有取并集，日期值取较晚的。
            //      一家为 null ⇒ 取有值的那个（＝并集）；两家都有 ⇒ 取较晚的；都没有 ⇒ 仍是 null。
            ActualDate = Later(c.ActualDate, e.ActualDate),

            // ③ 预约日和改期轨迹以巨潮为准，巨潮为空才用东财的。
            //    注意是**逐字段**兜底而不是整条替换：巨潮有预约日、东财有改期记录的情况真实存在，
            //    整条替换会把其中一边丢掉。
            AppointDate = c.AppointDate ?? e.FirstAppointDate ?? e.AppointDate,
            Change1 = c.Change1 ?? e.Change1,
            Change2 = c.Change2 ?? e.Change2,
            Change3 = c.Change3 ?? e.Change3,
        };

    private static DateTime? Later(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : (a > b ? a : b);
}
