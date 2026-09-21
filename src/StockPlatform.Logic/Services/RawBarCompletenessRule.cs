namespace StockPlatform.Logic.Services;

/// <summary>
/// 「这只标的的不复权日线补齐了没有」——纯判据，零 IO。
/// 2026-09-21 从 <c>FetchOrchestrator.RawBarsComplete</c> 抽出来。
///
/// ════ 判据只有一条，但错法很贵 ════
/// **两头都要比**：尾巴要跟前复权一样新，**开头也要跟前复权一样早**。
///
/// ⚠ 只比尾巴会出大事（2026-09-01 实测踩到）：不复权并进日更之后走的是水位线窗口——
/// 库里没有就按回看年数（3 年）抓。于是它抢在【整段回补】前头把最近 3 年填上，
/// 判据一看"最新日期追上了"就归零、界面显示「已补齐」，**前面 7 年再也没人去补**。
/// 当时 5781 只里有 5232 只就这么卡在 3 年上，而 day_hfq 是完整的 10 年。
///
/// ════ 为什么留 30 天容差 ════
/// 个别标的的不复权历史本来就比前复权短几天（数据源口径差异）。不留容差的话，
/// 它们每一轮都会被判成"没补齐"、反复重抓，永远收敛不了。
/// </summary>
public static class RawBarCompletenessRule
{
    /// <summary>开头允许比前复权晚这么多天，仍算补齐。</summary>
    public const int ToleranceDays = 30;

    /// <param name="dayEarliest">前复权（day）本地最早那根。</param>
    /// <param name="dayLatest">前复权本地最新那根。</param>
    /// <param name="rawEarliest">不复权（day_raw）本地最早那根；null＝一根都没有。</param>
    /// <param name="rawLatest">不复权本地最新那根；null＝一根都没有。</param>
    public static bool IsComplete(
        DateTime dayEarliest, DateTime dayLatest, DateTime? rawEarliest, DateTime? rawLatest)
        => rawLatest is { } rl && rl.Date >= dayLatest.Date
        && rawEarliest is { } re && re.Date <= dayEarliest.Date.AddDays(ToleranceDays);
}

/// <summary>
/// 「后复权/不复权这一轮是不是该主动中止」——纯判据，零 IO。
/// 2026-09-21 从 <c>FetchOrchestrator.FetchHfqBarsAsync</c> 的熔断那一段抽出来。
///
/// ════ 为什么要熔断 ════
/// 接口挂了/被限流/换了返回格式时，逐只抓会**一路失败到底**——几千只票、几小时，
/// 结果一行数据都没有（用户 2026-07-30 反馈：取不到数据就该直接停）。
/// 两道闸：起飞前探一只（那是任务里的动作，不是判据），跑起来之后看失败率。
///
/// ⚠ 门槛不能去掉：样本太少时偶发失败会把正常的一轮误判成"接口挂了"。
/// </summary>
public static class HfqProbeGate
{
    /// <summary>完成这么多只之后才开始判失败率。</summary>
    public const int AbortCheckAfter = 30;

    /// <summary>失败率超过它就中止本轮。</summary>
    public const double AbortFailRatio = 0.9;

    /// <param name="finished">本轮已经跑完多少只（成功+失败）。</param>
    /// <param name="failed">其中失败多少只。</param>
    public static bool ShouldAbort(int finished, int failed)
        => finished >= AbortCheckAfter && failed > finished * AbortFailRatio;
}
