using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 【板块成分股】这一轮抓哪些板块、按什么顺序抓——纯判据，零 IO。
/// 2026-09-21 从 <c>FetchOrchestrator.FetchBoardMembersCoreAsync</c> 抽出来。
///
/// ════ 为什么「先小后大」 ════
/// 大板块是这条路上最贵也最容易失败的一类：<c>pz</c> 被网页锁在 20，800 只就要翻 40 页、
/// 耗几分钟，中途撞上限流或验证码的概率跟页数成正比——而一旦没取全就整个作废
/// （total 对账不允许半截名单），几分钟白花。小板块一两页就完事、几乎不会失败。
/// 所以先把小的收干净再去啃大的：同样的时间窗口里能落库的板块数最多。
///
/// ⚠ 排序键是"上次抓到的只数"，而**没抓过的是 0**——不能让它们排最前（那等于"完全不知道
/// 多大的先抓"，撞上 1444 只那种就前功尽弃），也不能排最后（库里中位数才 21 只，绝大多数
/// 没抓过的其实很小）。按中等对待，排在已知的小板块之后、已知的大板块之前。
/// </summary>
public static class BoardMemberPlanner
{
    /// <summary>没抓过的板块按多大对待。</summary>
    public const int UnknownSizeRank = 200;

    /// <summary>超过这个只数算"大板块"（只用在日志里报个数）。</summary>
    public const int BigBoardThreshold = 400;

    /// <summary>排序键：上次抓到的只数，没抓过的按 <see cref="UnknownSizeRank"/>。</summary>
    public static int SizeRank(Board b) => b.MemberCount > 0 ? b.MemberCount : UnknownSizeRank;

    /// <summary>
    /// 这一轮要抓的板块，已排好序。
    /// </summary>
    /// <param name="all">库里全部板块。</param>
    /// <param name="freshCodes">成分股还新鲜、本轮跳过的板块代码。</param>
    public static List<Board> Plan(IEnumerable<Board> all, IReadOnlySet<string> freshCodes)
        => all.Where(b => !freshCodes.Contains(b.BoardCode))
              .OrderBy(SizeRank)                            // 升序＝小的先抓
              .ThenBy(b => b.BoardCode, StringComparer.Ordinal)   // 同样大小时定个稳定次序，便于对比两轮日志
              .ToList();

    /// <summary>计划里有几个是大板块（排在最后那些）。</summary>
    public static int CountBig(IEnumerable<Board> plan) => plan.Count(b => SizeRank(b) > BigBoardThreshold);

    /// <summary>
    /// **统计**口径的时间界——"全库成分股状态：最新 N 个"里那个"最新"按它算。
    ///
    /// ⚠ 跟抓取判据（<c>MemberFreshFor</c> 推出来的那条时间线）**必须分开**（2026-09-07 修过）：
    /// 后者在不节流的通道下是 <see cref="DateTime.MaxValue"/>，拿它统计会把刚抓成功的
    /// 1031 个全算成"待重试"，界面上那句"补完一轮之后会归零"在那条通道上就永远不成立。
    /// </summary>
    public static DateTime StatsSince(TimeSpan memberFreshFor, DateTime today)
        => memberFreshFor > TimeSpan.Zero ? today - memberFreshFor : today;
}

/// <summary>
/// 「连续失败这么多次，就别再往下打了」——纯判据，零 IO。
/// 2026-09-21 从 <c>FetchBoardMembersCoreAsync</c> 抽出来。
///
/// 连续失败说明已经被限流了，再打只是白费请求、还会让封禁更久。已经抓到的都逐个落了库，
/// 剩下的下一轮继续——这正是逐板块落库的意义。
///
/// ⚠ 阈值 15 是跟 <c>RateLimiter</c> 的熔断阈值对齐的（2026-09-04 定）：实测正常波动里
/// 连续失败能到 7 个，设成 10 会误判成"被限流"、白白提前收尾。
/// </summary>
public static class ConsecutiveFailureGate
{
    /// <summary>板块成分股那条路的阈值。</summary>
    public const int BoardMemberThreshold = 15;

    /// <summary>连续失败 <paramref name="consecutive"/> 次，该不该收尾。</summary>
    public static bool ShouldStop(int consecutive, int threshold = BoardMemberThreshold)
        => consecutive >= threshold;
}
