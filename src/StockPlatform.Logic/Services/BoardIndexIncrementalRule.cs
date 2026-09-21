using System.Security.Cryptography;
using System.Text;

namespace StockPlatform.Logic.Services;

/// <summary>某个板块上一次合成时的状态——判断这一轮能不能只追加，全靠它。</summary>
/// <param name="MemberHash">当时的成分股名单指纹。</param>
/// <param name="LastBarDate">当时合成出来的最后一根日K的日期。</param>
/// <param name="MemberEarliest">当时成分股 <c>day_adj</c> 的最早日期（发现"补了更早历史"用）。</param>
public sealed record BoardIndexState(string MemberHash, DateTime LastBarDate, DateTime? MemberEarliest);

/// <summary>这一轮该怎么做。</summary>
public enum BoardIndexPlanKind
{
    /// <summary>整段重算（历史整体失效了）。</summary>
    Full,
    /// <summary>只追加新交易日。</summary>
    Append,
    /// <summary>已经是最新的，一根都不用动。</summary>
    UpToDate,
}

/// <summary>
/// 「这个板块的指数能不能只追加、还是必须整段重算」——纯判据，零 IO。
/// 2026-09-21 新增，见 doc/board-index-synth-design.md §2。
///
/// ════ 为什么大部分时候能只追加 ════
/// 板块指数是**路径依赖**的：每天的指数涨幅 = 当天有数据的成分股各自 (今收/昨收−1) 的均值，
/// 从基点 1000 累乘（见 <see cref="BoardIndexSynthesizer"/>）。所以追加一天只需要
/// "上一根 close × (1 + 当天平均涨幅)"——前面的历史一个字都不用动。
///
/// 迁移前这一项**每个工作日全量重算**：950 个板块 × 约 4,900 根 ≈ 468 万行重写一遍，
/// 而真正变的通常只有最新那一根。
///
/// ════ 历史会整体失效的三种情况，每种都要能发现 ════
/// | 来源 | 为什么 | 这里怎么发现 |
/// |---|---|---|
/// | 成分股名单变了 | 历史每一天的均值都是按当前名单算的，名单一变整条历史都变 | 名单指纹对不上 |
/// | <c>day_adj</c> 被【重算回测序列】重写 | 历史收益率跟着变 | 那一项跑完置一个全量标记，这里 <paramref name="forceFull"/> 收到 |
/// | 某成分股补上了更早的历史 | 早期那些天的均值变了 | 成分股最早日期往前挪了 |
///
/// ⚠ 判据漏一种失效来源就是**静默的错值**——板块指数只有本地这一份，没有官方值可对。
/// 所以「首次整段回补」那个模式必须留着当兜底（它让 <paramref name="forceFull"/> 恒为 true）。
/// </summary>
public static class BoardIndexIncrementalRule
{
    /// <summary>
    /// 成分股名单的指纹。**排序后再算**——名单的顺序不影响指数（等权平均），
    /// 顺序变了却判成"名单变了"会白白触发一次整段重算。
    /// </summary>
    public static string MemberFingerprint(IEnumerable<string> memberCodes)
    {
        var joined = string.Join(",", memberCodes.Distinct(StringComparer.Ordinal)
                                                 .OrderBy(c => c, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16];
    }

    /// <summary>这一轮怎么做，以及要追加的话从哪天起。</summary>
    /// <param name="state">上一次合成时的状态；null＝没合成过。</param>
    /// <param name="currentHash">这一轮的成分股名单指纹。</param>
    /// <param name="currentMemberEarliest">这一轮成分股 <c>day_adj</c> 的最早日期。</param>
    /// <param name="existingLastBar">库里这个板块指数最后一根的日期；null＝库里没有。</param>
    /// <param name="today">今天。</param>
    /// <param name="forceFull">强制整段重算（「首次整段回补」模式、或 day_adj 刚被重写过）。</param>
    public static (BoardIndexPlanKind Kind, DateTime From) Decide(
        BoardIndexState? state, string currentHash, DateTime? currentMemberEarliest,
        DateTime? existingLastBar, DateTime today, bool forceFull)
    {
        if (forceFull || state is null || existingLastBar is not { } lastBar)
            return (BoardIndexPlanKind.Full, default);

        // 状态里记的最后一根跟库里对不上 → 有人动过这个板块的 bar，别猜，重算
        if (state.LastBarDate.Date != lastBar.Date)
            return (BoardIndexPlanKind.Full, default);

        if (!string.Equals(state.MemberHash, currentHash, StringComparison.Ordinal))
            return (BoardIndexPlanKind.Full, default);

        // 成分股补上了更早的历史 → 早期那些天的均值变了
        if (currentMemberEarliest is { } now && state.MemberEarliest is { } was && now.Date < was.Date)
            return (BoardIndexPlanKind.Full, default);

        // 以前没记过最早日期（老状态行），保守起见重算一次把它补上
        if (state.MemberEarliest is null && currentMemberEarliest is not null)
            return (BoardIndexPlanKind.Full, default);

        var from = lastBar.Date.AddDays(1);
        return from > today.Date
            ? (BoardIndexPlanKind.UpToDate, default)
            : (BoardIndexPlanKind.Append, from);
    }
}
