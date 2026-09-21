namespace StockPlatform.Logic.Services;

/// <summary>
/// 「这次拿回来的 ETF 名单能不能用」——纯判据，零 IO。
/// 2026-09-21 从 <c>FetchOrchestrator.FetchEtfBarsAsync</c> 抽出来。
///
/// ════ 为什么要这道闸 ════
/// 名单接口被限流/半途断连时**不会报错，只会少给**。拿一份半截名单去跑的后果不是"少抓几只"，
/// 而是那些没在名单里的 ETF **这一轮完全不被处理**——而且日志上看起来一切正常。
///
/// 所以：拿回来的比库里存量少 <see cref="MinKeepRatio"/> 以上，就判定是半截，
/// 改用库里的存量名单跑增量（代价只是这一轮发现不了新上市的 ETF），并记一条错误留痕。
/// 库里也没有存量时才是真没辙——那就整项跳过，别对着空名单空跑。
/// </summary>
public static class EtfListGuard
{
    /// <summary>拿回来的至少要有库里存量的这个比例，才认为是完整名单。</summary>
    public const double MinKeepRatio = 0.95;

    /// <summary>这一轮用哪份名单。</summary>
    public enum Decision
    {
        /// <summary>用拿回来的那份（正常路径）。</summary>
        UseFetched,
        /// <summary>拿回来的是半截，改用库里的存量。</summary>
        UseLocal,
        /// <summary>拿回来的是半截、库里也没有存量——这一轮没法跑。</summary>
        Abort,
    }

    /// <param name="fetchedCount">这次从数据源拿到几只。</param>
    /// <param name="localCount">库里存量有几只。</param>
    public static Decision Judge(int fetchedCount, int localCount)
    {
        // 两边都空：接口没给、库里也没有——这一轮真的没得跑。
        // ⚠ 老代码里这条分支写了、却永远走不到（判据是 `fetched < local * 0.95`，
        //   local=0 时右边是 0，不可能成立），于是那种情况会一路走到"共 0 只 ETF"当成正常跑完。
        //   2026-09-21 迁移时按它原本的意图接上：记成「跳过」，今天恢复了还能再试；
        //   记成「完成」的话 AlreadyRanOn 会认为今天已经跑过了。请求数两种写法都是 0。
        if (fetchedCount == 0 && localCount == 0) return Decision.Abort;
        if (fetchedCount >= localCount * MinKeepRatio) return Decision.UseFetched;
        return localCount == 0 ? Decision.Abort : Decision.UseLocal;
    }
}
