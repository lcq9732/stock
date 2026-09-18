namespace StockPlatform.Logic.Services;

/// <summary>抓完一天之后，对"确认没有数据"名单要做什么。</summary>
public enum NoDataAction
{
    /// <summary>什么都不做。</summary>
    None,
    /// <summary>定案：这个源那天就是没有数据，往后不再为它发请求。</summary>
    Confirm,
    /// <summary>撤销：之前定过案，但源后来把数据补上了。</summary>
    Revoke,
}

/// <summary>
/// 抓完一天之后「要不要动『确认没有数据』名单」的判据（2026-09-18 抽出来）。
///
/// ════ 为什么单独成类 ════
/// 这套判据原来在 <c>FetchOrchestrator</c> 里**写了两份**（<c>FetchMarginRecentAsync</c> 一份、
/// <c>BackfillDailyAsync</c> 一份），实质相同、各写各的。两融迁成新式任务时会出现第三份——
/// 所以抽成纯函数，跟 <see cref="DailyBackfillGate"/> 同层同风格（都能单测）。
///
/// ⚠ **cutoff 这条不能省**：两所是 T+1 发布、龙虎榜当晚才出，当天/昨天拿到 0 行多半只是
/// "还没发"而不是"没有"。写成"0 行就定案"的话，那天会被**永久钉死**、往后再也不去抓
/// （名单是一次定案的，闸③直接跳过）。这正是 <see cref="DailyBackfillGate"/> 类注释里说的
/// 那种"静默漏数据"——日志上只会显示"跳过 N 天"，跟正常收敛长得一模一样。
/// </summary>
public static class DailyNoDataGate
{
    /// <summary>几天以前的空才敢定案。两所 T+1、龙虎榜当晚发布，留 3 天足够。</summary>
    public const int ConfirmAfterDays = 3;

    /// <param name="rows">这天实际抓到多少行。</param>
    /// <param name="day">抓的是哪天。</param>
    /// <param name="today">今天（传进来而不是现查，纯函数才好测）。</param>
    /// <param name="alreadyConfirmed">这天是不是已经在"确认没有"名单里了。</param>
    public static NoDataAction Evaluate(int rows, DateOnly day, DateOnly today, bool alreadyConfirmed)
    {
        // 抓到数据了：之前若定过案，说明源后来补上了，撤销结论。
        if (rows > 0) return alreadyConfirmed ? NoDataAction.Revoke : NoDataAction.None;

        // 0 行：够旧才敢定案（见类注释的 ⚠）。已经在名单里的不用重复写。
        if (alreadyConfirmed) return NoDataAction.None;
        return day <= today.AddDays(-ConfirmAfterDays) ? NoDataAction.Confirm : NoDataAction.None;
    }
}
