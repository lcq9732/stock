using System.Globalization;
using System.Text.RegularExpressions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 从回购公告的标题和正文里抽出结构化进展，见 doc/watch-item-design.md §6.7。
/// **纯计算**，所以能拿真实公告原文直接单测。
///
/// ════ 规则是从 5 份真实公告归纳的，不是猜的 ════
/// 样本：宁德时代 300750（方案、未实施进展）、美的集团 000333（已实施进展、达1%）、
/// 格力电器 000651（首次回购）。三种正文句式都实测过。
///
/// ════ 两条从真样本才看得出来的约束 ════
/// ① **数字和日期里会有空格**——格力那份是 <c>495,600 股</c>、<c>2026 年 8 月 17 日</c>，
///    PDF 转文本的产物。所以每条正则都得 <c>\s*</c> 容忍；不容忍的话会**只对一部分公司生效**，
///    而失败的那些看起来就像"这家没在回购"。
/// ② <b>as_of_date 有两种写法</b>：「截至X日」直接取；「公司于 A 至 B」取区间末 B。
///
/// ════ 0 和 null 不是一回事 ════
/// <c>CumAmount=0</c> 是公告明说"尚未实施"，<c>null</c> 是没抽到。
/// 混成一个的话，"抽取坏了"会显示成"公司没买"——这正是这一项要盯的那个信号，不能出错。
/// </summary>
public static class PlanAnnouncementExtractor
{
    // ── 标题判据 ──
    // ⚠ 必须先排除这一类：《关于回购股份事项前十名股东和前十名无限售条件股东持股情况的公告》。
    // 它标题含"回购"，但内容是股东名册，跟回购进度毫无关系。不排除的话会解析出一条各字段全空的
    // "进展"，**看起来像回购停滞**——本项唯一会产生错误结论（而非漏数据）的坑。
    //
    // ⚠ 这份排除名单**放宽过一次**（2026-09-11 实机跑全市场发现）。原来只挡了股东名册，
    // 结果混进来一堆标题带"回购"、实质完全无关的公告，真实误收样本：
    //   · 「关于部分**限制性股票回购注销**完成的公告」——股权激励收回员工股份，
    //     跟"公司拿钱从二级市场买自己股票"是两码事，金额量级也差几个数量级
    //   · 「关于回购股份**集中竞价减持**计划的公告」——减持，方向正好相反
    //   · 「2024年限制性股票**激励计划**第一个归属期部分归属结果公告（回购股份）」
    //   · 「H股公告：**翌日披露报表**-回购股份」——港股口径，跟 A 股回购账不能混
    //   · 「关于**注销**回购股份并减少注册资本暨**通知债权人**的公告」——回购完成后的注销程序
    // 这些进来之后会被当成各字段全空的「进展」，而那在库里**看起来就像回购停滞**，
    // 还会让 GetOpenPlans 把根本没在回购的公司判成"方案进行中"。
    private static readonly Regex NotProgressTitle = new(
        @"前十名股东|无限售条件股东|股东[会大]会|独立财务顾问|法律意见"
        + @"|限制性股票|激励计划|股权激励"          // 股权激励的回购注销，不是股份回购
        + @"|减持|增持"                              // 方向相反的事
        + @"|翌日披露报表|H\s*股公告"                // 港股口径
        + @"|债权人|注册资本"                        // 回购完成后的注销程序
        + @"|要约收购|协议转让"
        + @"|贷款承诺|融资支持|专项贷款"           // 「获得回购股份融资支持」是融资公告，不是回购进展
        + @"|权益投资|土地|资产回购|债券回购"      // 回购的不是自家股份
        + @"|融资租赁|回购担保|担保"               // 「为客户提供融资租赁业务回购担保」是担保义务
        + @"|提议",                                // 「控股股东提议回购」连方案都还没有
        RegexOptions.Compiled);

    private static readonly Regex ProposalTitle = new(@"方案|报告书", RegexOptions.Compiled);
    private static readonly Regex FirstBuyTitle = new(@"首次回购", RegexOptions.Compiled);
    private static readonly Regex MilestoneTitle = new(@"达\s*总股本|达到\s*总股本", RegexOptions.Compiled);
    private static readonly Regex DoneTitle = new(@"实施完[毕成]|回购完成|期限届满|实施结果", RegexOptions.Compiled);
    private static readonly Regex TerminatedTitle = new(@"终止", RegexOptions.Compiled);
    private static readonly Regex ProgressTitle = new(@"进展", RegexOptions.Compiled);
    private static readonly Regex BuybackTitle = new(@"回购", RegexOptions.Compiled);

    // ── 正文判据 ──
    // N  = 数字，允许夹空格和千分位逗号（PDF 转文本会到处塞空格）
    // NU = 数字 + **可选的「万/亿」单位**
    //
    // ⚠ 单位这一条是硬伤：实测「首次回购股份 123.25 **万**股…已支付的总金额为 1,524.73 **万**元」，
    // 漏掉「万」不是抽不到，是**抽出一个小 10000 倍的数**——那比抽不到危险得多，
    // 因为它看起来完全正常（"回购了 1524 元"不会有人察觉是解析错了）。
    private const string N = @"([\d\s,，]+(?:\.\s*\d+)?)";
    private const string NU = @"([\d\s,，]+(?:\.\s*\d+)?)\s*(万|亿)?";

    /// <summary>"截至2026年8月31日" / "截至 2026 年 8 月 31 日"</summary>
    private static readonly Regex AsOfCn = new(
        @"截\s*至\s*(\d{4})\s*年\s*(\d{1,2})\s*月\s*(\d{1,2})\s*日", RegexOptions.Compiled);

    /// <summary>"公司于 2026 年 8 月 17 日至 2026 年 8 月 18 日" —— 取区间末那个。</summary>
    private static readonly Regex DateRangeEnd = new(
        @"至\s*(\d{4})\s*年\s*(\d{1,2})\s*月\s*(\d{1,2})\s*日\s*(?:以|采用|通过)", RegexOptions.Compiled);

    /// <summary>
    /// 「2026 年 6 月 12 日，公司通过集中竞价交易方式首次回购股份…」——**单日**的首次回购公告
    /// 常这么写，既没有"截至"也没有区间。不认它的话 as_of_date 会空掉一大半（实测 25%）。
    /// </summary>
    private static readonly Regex DateThenCompany = new(
        @"(\d{4})\s*年\s*(\d{1,2})\s*月\s*(\d{1,2})\s*日\s*[，,]?\s*(?:公司|本公司)", RegexOptions.Compiled);

    // ⚠ 下面这几条**放宽过一次**（2026-09-11 实机跑全市场发现）：
    // 原版是拿宁德/美的/格力三家的公告归纳的，跑 103 条真实进展公告只抽到 18 条（17%）。
    // 三家样本归纳不出全市场的句式，实测到的变体：
    //   · 「尚未**开始实施**股份回购」——中间比"尚未实施"多一个词
    //   · 「**回购公司股份** 206,000 股」——没有"累计…数量为"
    //   · 「**成交总金额**为**人民币** 1,863,178 元」——不是"支付的总金额"
    // 所以每条都按"骨架词 + 可选修饰"来写，别把某一家的措辞当通例。

    /// <summary>
    /// 「还没开始买」的各种说法。★ 这条**漏一个说法的代价最大**：漏掉之后那条记录既没有
    /// 金额、也没被判成"未实施"，落进库里是一片 null——而 null 在界面上是"数据缺失"，
    /// 不是"没买"。**把一个明确的答案变成了看似的故障。**
    ///
    /// 实测到的说法：尚未实施／尚未开始实施／**暂未**通过回购专用证券账户…回购。
    /// 所以骨架放成「尚未|暂未|未 + (中间十来个字随便) + 回购」，
    /// 但**限定在同一句话内**（不跨越 。；）——跨句会把"尚未收到…回购款"这种误判进来。
    /// </summary>
    private static readonly Regex NotYetStarted = new(
        @"(?:尚未|暂未|暂无|未曾)[^。；;]{0,20}?回购(?:公司)?(?:[A-Za-z]\s*股)?股?份?", RegexOptions.Compiled);

    /// <summary>骨架＝「回购 … 股份 … 数字 股」，中间的"公司/A股/数量/为"全可选，单位可带万/亿。</summary>
    private static readonly Regex CumShares = new(
        @"回购(?:公司)?(?:[A-Za-z]\s*股)?股份(?:的)?(?:数量)?(?:为|共计|合计)?\s*" + NU + @"\s*股",
        RegexOptions.Compiled);
    /// <summary>「占公司目前总股本的1.31%」「累计占公司总股本的比例为 0.14%」都要能匹配。</summary>
    private static readonly Regex PctOfCapital = new(
        @"占(?:公司)?(?:目前|当前)?总股本(?:的)?(?:比例)?(?:为|约为)?\s*" + N + @"\s*%", RegexOptions.Compiled);
    /// <summary>「最高成交价为」和「成交的最高价为」词序不同，都见过。</summary>
    private static readonly Regex PriceHigh = new(
        @"(?:最高成交价|成交的?最高价|最高价)(?:格)?为\s*" + N + @"\s*元\s*/\s*股", RegexOptions.Compiled);
    private static readonly Regex PriceLow = new(
        @"(?:最低成交价|成交的?最低价|最低价)(?:格)?为\s*" + N + @"\s*元\s*/\s*股", RegexOptions.Compiled);
    /// <summary>
    /// 「支付的/已支付的/成交/回购」+「总金额」+ 数字。可带"人民币"，单位可带万/亿。
    ///
    /// ⚠ **「为」必须是可选的**（2026-09-11 实机查出来的）。原来写成 <c>总金额为</c>（必需），
    /// 结果漏掉了「成交总金额 3,033,800.00 元」这种直接跟数字的写法——
    /// 就差一个「为」字，却让 437 条（进展类的 33%）金额抽不到。
    /// 而这些公告股数、占比、成交价都抽到了，唯独金额空着，在界面上看起来像"没买"。
    /// </summary>
    private static readonly Regex CumAmount = new(
        @"(?:已)?(?:支付的?|成交的?|回购|使用资金)?\s*总金额(?:为)?\s*(?:人民币)?\s*" + NU + @"\s*元",
        RegexOptions.Compiled);

    /// <summary>
    /// 方案的价格上限。三种真实写法都要吃下：
    ///   · 宁德「回购**价格上限为** 573 元/股」
    ///   · 「回购**价格不超过** 573 元/股」
    ///   · 三环「本次回购**的价格不超过**人民币 135 元/股（含）」← 中间多了"的"、"本次回购"打头
    ///
    /// 所以骨架是「价格 + 上限为/不超过/不高于 + 数字 + 元/股」，
    /// 前面的"回购/本次回购的"全可选。**「/股」后缀是这条的身份标记**——
    /// 没有它就会跟资金总额的"不超过…元"撞车（见 <see cref="AmountHigh"/> 的注释）。
    /// </summary>
    private static readonly Regex CapPrice = new(
        @"价格(?:上限)?(?:为|不超过|不高于)\s*(?:人民币)?\s*" + N + @"\s*元\s*/\s*股", RegexOptions.Compiled);

    /// <summary>
    /// 方案资金区间：'不低于人民币200亿元…不超过人民币400亿元'。亿/万要换算。
    ///
    /// ⚠ 末尾的 <c>(?!\s*/\s*股)</c> 是必须的（2026-09-11 踩出来的语序陷阱）：
    /// 三环集团那份写的是「回购资金总额不低于人民币 4.5 亿元且不超过人民币 9 亿元」，
    /// 但下一句是「**不超过人民币 135 元/股**（含）」——那是**价格**上限。
    /// 没有这个否定断言时，正则在全文里找第一个"不超过…元"就抓到了 135，
    /// 于是资金上限被算成 135 元、显示时四舍五入成「0 亿」。
    /// 「元/股」和「元」共用一个"元"字，是中文公告的天然歧义，只能靠单位后缀排除。
    /// </summary>
    private static readonly Regex AmountLow = new(
        @"不低于\s*(?:人民币)?\s*" + N + @"\s*(亿|万)?元(?!\s*/\s*股)", RegexOptions.Compiled);
    private static readonly Regex AmountHigh = new(
        @"不超过\s*(?:人民币)?\s*" + N + @"\s*(亿|万)?元(?!\s*/\s*股)", RegexOptions.Compiled);

    /// <summary>
    /// 标题是不是一条值得解析的回购公告。false＝直接丢掉，别落库。
    ///
    /// ⚠ 判据是 **含"回购" + 不在排除名单 + 能认出明确 stage** 三条都过。
    /// 第三条是 2026-09-11 加的：原来 <see cref="ClassifyStage"/> 认不出就兜底当「进展」，
    /// 结果这些标题全落进了进展类，各字段抽不到、在库里看起来像回购停滞：
    ///   · 「关于**权益投资**被回购的公告」——投资被回购，不是股份回购
    ///   · 「关于子公司…**土地有偿回购**交易的公告」——土地
    ///   · 「关于控股股东**提议**公司回购股份的公告」——提议，连方案都还没有
    ///   · 「关于年度权益分派实施后**调整回购股份价格上限**的公告」——方案参数修订
    /// 真正的进展公告标题里一定有"进展/首次回购/方案/报告书/完毕/达总股本"之一，
    /// 所以要求认得出 stage 是安全的，而且把精度从 37% 拉上来。
    /// </summary>
    public static bool IsBuybackAnnouncement(string title)
        => !string.IsNullOrWhiteSpace(title)
           && BuybackTitle.IsMatch(title)
           && !NotProgressTitle.IsMatch(title)
           && ClassifyStageOrNull(title) is not null;

    /// <summary>标题定 stage。顺序有讲究：先判最具体的。认不出返回 <see cref="PlanStage.Progress"/>。</summary>
    public static string ClassifyStage(string title)
        => ClassifyStageOrNull(title) ?? PlanStage.Progress;

    /// <summary>认不出就返回 null——<see cref="IsBuybackAnnouncement"/> 据此把它挡在库外。</summary>
    private static string? ClassifyStageOrNull(string title)
    {
        if (TerminatedTitle.IsMatch(title)) return PlanStage.Terminated;
        if (DoneTitle.IsMatch(title)) return PlanStage.Done;
        if (FirstBuyTitle.IsMatch(title)) return PlanStage.FirstBuy;
        if (MilestoneTitle.IsMatch(title)) return PlanStage.Milestone;
        // 「方案」放在「进展」之后判：格力那份叫"首次回购股份暨回购股份进展"，
        // 而宁德的方案公告叫"回购公司股份方案的公告暨回购股份报告书"——两者都可能同时含多个词，
        // 靠上面几条更具体的先截走，剩下的才轮到这两条。
        if (ProgressTitle.IsMatch(title)) return PlanStage.Progress;
        if (ProposalTitle.IsMatch(title)) return PlanStage.Proposal;
        return null;
    }

    /// <summary>把标题和正文抽成一条记录。<paramref name="content"/> 为空时只按标题定 stage。</summary>
    public static PlanAnnouncement Extract(
        string code, string name, string title, DateTime announceDate,
        string? content, string artCode = "", string sourceUrl = "")
    {
        var rec = new PlanAnnouncement
        {
            Code = code,
            Name = name,
            Kind = PlanKind.Buyback,
            AnnounceDate = announceDate,
            Stage = ClassifyStage(title),
            Title = title,
            ArtCode = artCode,
            SourceUrl = sourceUrl,
        };
        if (string.IsNullOrWhiteSpace(content)) return rec;

        // ⚠ **方案公告不抽 as_of_date**（2026-09-11 修）。
        // 「数据截止日」这个概念只对进展类有意义（截至上月末累计回购了多少）；方案公告里出现的
        // 日期是别的语境——股本基准日、董事会决议日、前 30 个交易日均价的起算日…
        // 硬抽的后果是拿一个不相干的日期当"值所属日期"：实测宁德 07-25 那份方案被抽成 06-30、
        // 山东高速 08-26 那份被抽成 03-31，观察项的触发记录就按这个错日期归档了。
        // 抽不到就让它是 null，下游自己退回用公告日（见 SqliteStockEventSource.Buyback）。
        if (rec.Stage != PlanStage.Proposal)
        {
            // 三种写法按优先级试：「截至X日」最准 → 区间取末尾 → 单日的「X日，公司…」
            rec.AsOfDate = MatchDate(AsOfCn, content)
                           ?? MatchDate(DateRangeEnd, content)
                           ?? MatchDate(DateThenCompany, content);
        }

        if (NotYetStarted.IsMatch(content))
        {
            // 明确的「尚未实施」→ 记 0，不是 null。这是"到底买没买"的答案本身。
            rec.CumShares = 0;
            rec.CumAmount = 0;
            rec.PctOfCapital = 0;
        }
        else
        {
            // ⚠ 这两条必须走 ScaledNum（带万/亿换算），不能用 Num——见 NU 的注释：
            // 漏掉「万」会抽出一个小 10000 倍、但看起来完全正常的数。
            rec.CumShares = ScaledNum(CumShares, content);
            rec.CumAmount = ScaledNum(CumAmount, content);
            rec.PctOfCapital = Num(PctOfCapital, content);
            rec.PriceHigh = Num(PriceHigh, content);
            rec.PriceLow = Num(PriceLow, content);
        }

        if (rec.Stage == PlanStage.Proposal)
        {
            rec.PlanCapPrice = Num(CapPrice, content);
            rec.PlanAmountLow = ScaledNum(AmountLow, content);
            rec.PlanAmountHigh = ScaledNum(AmountHigh, content);
        }
        return rec;
    }

    /// <summary>去掉千分位、全角逗号和所有空白，再转数。抽不到返回 null。</summary>
    private static double? Num(Regex re, string text)
    {
        var m = re.Match(text);
        if (!m.Success) return null;
        var raw = m.Groups[1].Value.Replace(",", "").Replace("，", "");
        raw = Regex.Replace(raw, @"\s+", "");
        return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>带「亿/万」单位的金额 → 元。</summary>
    private static double? ScaledNum(Regex re, string text)
    {
        var m = re.Match(text);
        if (!m.Success) return null;
        var raw = Regex.Replace(m.Groups[1].Value.Replace(",", "").Replace("，", ""), @"\s+", "");
        if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return null;
        return m.Groups[2].Value switch { "亿" => v * 1e8, "万" => v * 1e4, _ => v };
    }

    private static DateTime? MatchDate(Regex re, string text)
    {
        var m = re.Match(text);
        if (!m.Success) return null;
        static int I(Group g) => int.Parse(Regex.Replace(g.Value, @"\s+", ""), CultureInfo.InvariantCulture);
        try { return new DateTime(I(m.Groups[1]), I(m.Groups[2]), I(m.Groups[3])); }
        catch { return null; }
    }
}
