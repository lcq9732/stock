namespace StockPlatform.Data.Orchestration;

/// <summary>自动重试这一次该怎么办。</summary>
public enum AutoRetryDecision
{
    /// <summary>名单清零了，不排。</summary>
    Cleared,

    /// <summary>上一轮之后待重试数没有减少——剩下的多半是停牌、数据源确实没有，再试也白试。</summary>
    NoProgress,

    /// <summary>已经连着自动重试满了轮数。</summary>
    MaxRounds,

    /// <summary>排下一次。</summary>
    Schedule,
}

/// <summary>
/// 要不要排一次自动重试——从 <c>MainViewModel.ScheduleAutoRetry</c> 抽出来的纯判断（2026-10-16）。
///
/// ⚠ <paramref name="backlog"/> 必须是**刚从 manifest 现读的**，不能是界面缓存的那份。
/// 2026-10-16 实测踩的：一项跑完时界面先发起后台刷新名单（异步），紧接着就排自动重试——读到的是
/// 这一轮开始前的缓存，于是名单明明清零了（日志上一行刚写"失败名单已全部清零"），还是排了一次 21:00 的重试，
/// 括号里报的也是旧名单（"不复权空洞 26 段"）。抽出来就是为了让"拿什么判"在签名上写死、能单测。
/// </summary>
public static class AutoRetryPolicy
{
    /// <param name="round">已经连着自动重试了几轮（0＝这次是手动/计划跑完后的第一次）。</param>
    /// <param name="lastPending">上一次排程时的待重试数。</param>
    public static AutoRetryDecision Decide(RetryBacklog backlog, int round, int lastPending, int maxRounds)
    {
        if (!backlog.Any) return AutoRetryDecision.Cleared;
        if (round > 0 && backlog.ActionableCount >= lastPending) return AutoRetryDecision.NoProgress;
        if (round >= maxRounds) return AutoRetryDecision.MaxRounds;
        return AutoRetryDecision.Schedule;
    }
}
