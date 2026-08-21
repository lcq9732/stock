using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 分红相关的派生指标（2026-08-20 新增，供"底仓法"筛选和"底仓"持仓页共用）——纯计算，
/// 数据从 <see cref="Abstractions.IDividendRepository"/> 取。
///
/// 为什么单独一个类：底仓的核心问题是"**未来还会不会继续分红**"，而库里的
/// <c>GetTrailingCashDividendPerShare</c> 只看近12个月，分不出"连分十年的电力股"和"去年头一回
/// 分红、今年就停了的票"。前者是底仓该买的，后者的高股息率纯属巧合。这里把历年派息序列变成
/// 几个能直接当筛选条件用的量。
///
/// **口径统一**：入参的每股股息都是"元/股"（调用方已把表里的每10股口径除过10），年份都是
/// **除权除息日所属年**（钱实际到账那一年，不是公告年也不是业绩年度）。
/// </summary>
public static class DividendMetrics
{
    /// <summary>
    /// 连续分红年数——从"最近一个有派息记录的年份"往前数连续不断的年数。
    ///
    /// **为什么起点不写死成今年**：A股绝大多数公司在年中（4~7月）实施上一年度的分红，所以在
    /// 每年前几个月，"今年还没分"是完全正常的，拿今年当起点会把所有好公司误判成中断。这里改成
    /// 从**实际最新的派息年**往前数，同时用 <paramref name="referenceYear"/> 做中断判定：
    /// 最新派息年比"去年"还早，说明去年和今年都没分过，这才算真中断，返回 0。
    ///
    /// 例（referenceYear=2026）：有2026派息→从2026往前数；只到2025→从2025往前数（不惩罚，
    /// 今年可能还没到分红季）；只到2024→返回0（去年就停了）。
    /// </summary>
    public static int ConsecutiveYears(IReadOnlyList<(int Year, double PerShare)> byYear, int referenceYear)
    {
        if (byYear == null || byYear.Count == 0) return 0;

        var years = new HashSet<int>();
        foreach (var (y, dps) in byYear) if (dps > 0) years.Add(y);
        if (years.Count == 0) return 0;

        int latest = years.Max();
        // 去年和今年都没有派息记录 → 分红已中断。
        if (latest < referenceYear - 1) return 0;

        int count = 1;
        while (years.Contains(latest - count)) count++;
        return count;
    }

    /// <summary>
    /// 派息趋势的展示文本（只提示、不做筛选条件）——底仓最怕的不是股息率低一点，而是
    /// "股息率看着高，其实派息一年比一年少"。分四种：
    /// <c>递增</c>（末年高于首年且没有哪年比上年腰斩）、<c>持平</c>（波动在±15%内）、
    /// <c>波动</c>（有过大幅回落但没断）、<c>中断</c>（中间缺年）。
    ///
    /// **趋势判定和明细展示用同一个窗口**（都是最近 <paramref name="years"/> 年）。2026-08-20
    /// 修正：原来趋势基于传进来的全部历史（十几年）判 gap、明细只显示近5年，于是出现"格力电器
    /// 标中断、但连续分红8年、近5年明细一年不缺"这种自相矛盾的显示——断点在窗口外，看不见。
    /// 现在两者对齐；"更早年份断过"这件事由 <see cref="ConsecutiveYears"/> 那一列去反映。
    /// </summary>
    public static string TrendText(IReadOnlyList<(int Year, double PerShare)> byYear, int years)
    {
        var (shape, list) = TrendCore(byYear, years);
        if (list.Count == 0) return shape;
        return $"{shape}｜{string.Join(" ", list.Select(x => $"{x.Year % 100}年{x.PerShare:F3}"))}";
    }

    /// <summary>只要结论那两个字（递增/持平/波动/中断）——结果表格里那一列用。
    /// 2026-08-20：原来 Grid 里塞的是"波动｜22年0.510 23年0.430 …"整串，一列宽度不够、
    /// 扫一眼也读不出重点。现在 Grid 只放结论，逐年明细改由条件详情里的柱状图呈现。</summary>
    public static string TrendShape(IReadOnlyList<(int Year, double PerShare)> byYear, int years)
        => TrendCore(byYear, years).Shape;

    /// <summary>最近 <paramref name="years"/> 年的逐年每股派息（升序）——条件详情里画柱状图用。</summary>
    public static List<(int Year, double PerShare)> RecentYears(
        IReadOnlyList<(int Year, double PerShare)> byYear, int years)
        => TrendCore(byYear, years).List;

    private static (string Shape, List<(int Year, double PerShare)> List) TrendCore(
        IReadOnlyList<(int Year, double PerShare)> byYear, int years)
    {
        var empty = new List<(int Year, double PerShare)>();
        if (byYear == null || byYear.Count == 0) return ("无派息记录", empty);

        var all = byYear.Where(x => x.PerShare > 0).OrderBy(x => x.Year).ToList();
        if (all.Count == 0) return ("无派息记录", empty);

        // 只看最近 years 年——趋势判定和明细展示用同一批数据，见方法注释。
        int cutoff = all[^1].Year - years + 1;
        var list = all.Where(x => x.Year >= cutoff).ToList();
        if (list.Count == 1) return ("仅1年", list);

        int span = list[^1].Year - list[0].Year + 1;
        bool hasGap = span > list.Count;

        double first = list[0].PerShare, last = list[^1].PerShare;
        bool anyHalved = false;
        for (int i = 1; i < list.Count; i++)
            if (list[i - 1].PerShare > 0 && list[i].PerShare / list[i - 1].PerShare < 0.5) anyHalved = true;

        string shape =
            hasGap ? "中断" :
            last > first * 1.15 && !anyHalved ? "递增" :
            Math.Abs(last / first - 1) <= 0.15 && !anyHalved ? "持平" :
            "波动";

        return (shape, list);
    }

    /// <summary>
    /// 最近一个有派息记录的年度、那一整年的每股派息（元/股）。<c>Year=0</c> 表示没有记录。
    ///
    /// **为什么需要它**：滚动12个月的口径（IDividendRepository.GetTrailingCashDividendPerShare）
    /// 在分红季前后会把**两个年度**的年度分红同时框进一个窗口，算出来的股息率虚高近一倍。
    /// 2026-08-20 实测：华邦健康按滚动12个月算是 10.11%，而它近5年每年只派 0.20~0.25 元、
    /// 现价 4.45——真实水平约 5.6%，多出来的是 2025 和 2026 两次派息被加在了一起。
    /// 底仓要拿几年，这种虚高会把不该进的票筛进来，所以筛选要拿年度口径去卡上限。
    /// </summary>
    public static (int Year, double PerShare) LatestYearDividend(IReadOnlyList<(int Year, double PerShare)> byYear)
    {
        if (byYear == null || byYear.Count == 0) return (0, 0);
        var withCash = byYear.Where(x => x.PerShare > 0).ToList();
        if (withCash.Count == 0) return (0, 0);
        int latest = withCash.Max(x => x.Year);
        return (latest, withCash.Where(x => x.Year == latest).Sum(x => x.PerShare));
    }

    /// <summary>近 <paramref name="years"/> 年的每股派息合计（元/股）——算平均股息率和分红率用。</summary>
    public static double SumPerShare(IReadOnlyList<(int Year, double PerShare)> byYear, int years, int referenceYear)
    {
        if (byYear == null) return 0;
        return byYear.Where(x => x.Year > referenceYear - years).Sum(x => x.PerShare);
    }

    // ── 红利税：财税[2015]101号（税档）+ 财税[2012]85号（持股期限算法、先进先出） ──
    //
    // 持股期限 = 取得股票之日 → **转让交割该股票之日前一日**。分三档：
    //   ≤1个月            全额计入，实际税负 20%
    //   1个月~1年（含1年） 50%计入，实际税负 10%
    //   >1年              暂免征收
    // 派息时对持股1年以内的**暂不扣缴**，等转让时由中登按持股期限算出税额、券商从资金账户扣收。
    // 所以底仓只要一直不卖、跨过1年，这笔税不仅免掉，期间根本没被扣过。
    //
    // ⚠ 先进先出：同一证券账户内先买入的视为先卖出。所以同一只票如果既有底仓又做波段，
    //   卖波段那笔会按 FIFO 认定成卖掉了最早买入的底仓份额，把底仓的免税时钟往后推。
    //   软件里给成交打标记**不能**改变这个认定——要隔开只能靠"同一只票不两头做"或分开证券账户。

    /// <summary>持股期限（天）——按 财税[2012]85号：买入日 至 卖出日**前一日**。
    /// <paramref name="sellDate"/> 传 null 表示"到今天还没卖"。</summary>
    public static int HoldingDays(DateTime buyDate, DateTime? sellDate)
    {
        var end = (sellDate ?? DateTime.Today).AddDays(-1);
        return (int)(end.Date - buyDate.Date).TotalDays;
    }

    /// <summary>这一笔的红利税实际税负：0.20 / 0.10 / 0（免征）。</summary>
    public static double TaxRate(DateTime buyDate, DateTime? sellDate = null)
    {
        int days = HoldingDays(buyDate, sellDate);
        var end = (sellDate ?? DateTime.Today).AddDays(-1).Date;
        if (end > buyDate.Date.AddYears(1)) return 0;        // 严格"超过1年"才免征
        if (days <= 31) return 0.20;                          // 1个月以内（含）
        return 0.10;                                          // 1个月以上至1年（含1年）
    }

    /// <summary>
    /// 这一笔最早能免税卖出的日期。持股期限要**严格超过**1年，而期限算到卖出日前一日，
    /// 所以最早卖出日 = 买入日 + 1年 + 2天：<c>(S-1) &gt; D+1年</c> ⟹ <c>S ≥ D+1年+2天</c>。
    /// 1年整（+1年+1天卖出）仍落在 10% 那档，差一天就是 10% vs 0，实操再留几天更稳。
    /// </summary>
    public static DateTime TaxFreeSellDate(DateTime buyDate) => buyDate.Date.AddYears(1).AddDays(2);

    /// <summary>
    /// 某一笔买入至今累计收到的每股股息（元/股，**税前**）——只算这笔买入**之后**才发生的分红。
    ///
    /// 判"这笔有没有权"用**股权登记日**：登记日收盘时在册才有权分红，所以买入日 ≤ 登记日
    /// 才算得上。数据源没给登记日时（老方案偶有缺失）退回用除权除息日，此时要求买入日 &lt; 除权日
    /// ——除权日当天买入已经不含权了。
    /// </summary>
    public static double ReceivedPerShare(IEnumerable<DividendRow> rows, DateTime buyDate, DateTime? sellDate = null)
    {
        double sum = 0;
        var buy = buyDate.Date;
        var sell = sellDate?.Date;
        foreach (var r in rows)
        {
            if (!string.Equals(r.Progress, "实施", StringComparison.Ordinal)) continue;
            if (r.DividendYuan <= 0) continue;

            DateTime? entitle = r.RecordDate ?? r.ExDate?.AddDays(-1);
            if (entitle == null) continue;
            var d = entitle.Value.Date;

            if (buy > d) continue;                    // 买在登记日之后 → 这次分红没份
            if (sell != null && sell <= d) continue;  // 登记日之前就卖了 → 也没份
            sum += r.DividendYuan / 10.0;             // 表里是每10股口径
        }
        return sum;
    }
}
