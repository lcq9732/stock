using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 财务分析（2026-08-27 新增）——把 <see cref="FinancialKeys"/> 那 52 个科目翻译成人能一眼看懂的
/// 判断，产出 <see cref="FinancialAnalysisReport"/>。纯计算，不联网。
///
/// ════ 阈值都写死在本类的常量里，不做成配置项 ════
/// 一上来就做成可配置，用户还得先去想每个数填多少。先按常规财务分析的经验值定，每条都在注释里
/// 写明依据；用一段时间发现哪条老是误报再调。
///
/// ════ 关于同比口径 ════
/// A股定期报告是**年内累计**口径（一季报=Q1、半年报=Q1+Q2…），所以同比必须拿**同一个月份**的
/// 报告期比（2026-06-30 对 2025-06-30），不能跟上一期（2026-03-31）比。单季数据用"本期累计 −
/// 上期累计"算。
///
/// ════ 金融机构 ════
/// 银行/券商/保险没有"营业成本"，毛利率算不出来；"存货""收现比""营运资金占用"对它们也没意义。
/// 用**数据本身**判定（营业成本缺失或为0）而不是查行业表——行业表覆盖率不满，漏判会让整份报告
/// 显示一堆 n/a。判定为金融机构时只出能算的那几节。
/// </summary>
public class FinancialAnalyzer
{
    // ── 阈值 ──

    /// <summary>经营现金流/归母净利的警戒线。0.6 以下说明利润没有充分变成现金；健康的制造业
    /// 常年在 0.8~1.2。低于 0.3 视为严重。</summary>
    private const double OcfCoverageWarn = 0.6;
    private const double OcfCoverageBad = 0.3;

    /// <summary>收现比（销售商品收到的现金 ÷ 营业收入）的基准。**收到的现金含增值税、营收不含税**，
    /// 所以正常值在 1.13 左右（13% 税率）而不是 1.0——这一点极容易看错，把 1.02 当成"正常"。
    /// 低于 1.05 说明回款偏慢。</summary>
    private const double CashRatioBenchmark = 1.13;
    private const double CashRatioWarn = 1.05;

    /// <summary>应收账款增速超过营收增速多少个百分点算异常。收入靠赊账撑出来时这两条线必然分叉——
    /// 15pct 还能用"个别大客户账期放宽"解释，30pct 基本只剩"放宽信用政策换收入"一个解释。
    /// 绝对水平（应收/营收）不设阈值：工程类天生就高，钉死一个数是在筛行业不是在筛公司。</summary>
    private const double ArGrowthGapWarnPct = 15;
    private const double ArGrowthGapBadPct = 30;

    /// <summary>应收+票据占营收低于这个比例，整组应收行就不显示。预收款生意（白酒、部分消费品）
    /// 的应收接近 0，小基数下同比动辄 ±90%，摆出来全是噪音——茅台 2026H1 应收 0.01 亿、
    /// 占营收 0.0%、周转 0.0 天，三行都是废话。</summary>
    private const double ArTrivialShare = 0.02;

    /// <summary>应收账款周转天数同比拉长的警戒线：相对拉长 20% 或绝对多 10 天，满足一条就算。
    /// 只看相对会让本来账期就只有 20 天的公司动辄报警，只看绝对会漏掉长账期行业的恶化。</summary>
    private const double ArDaysRiseWarnRatio = 0.20;
    private const double ArDaysRiseWarnAbs = 10;

    /// <summary>经营活动贡献的资金占（经营+筹资）的比例。低于 20% 说明扩张主要靠借钱/融资。</summary>
    private const double SelfFundingWarn = 0.20;

    /// <summary>
    /// 算"市值里有多少是已赚到的利润撑的"时用的合理估值倍数（≈8.3% 盈利收益率）。
    ///
    /// ⚠ **不是市场中位数**。用当期全市场中位（总股本口径 38.6 倍）算出来，东方电气会是
    ///   261%、思泉新材 24.3%，跟人工口径差 2.6 倍；反推 12.3×1.00 和 131.8×0.09
    ///   都指向 12。两者回答的问题不同：
    ///     市场中位数 → "比同侪贵不贵"（相对）
    ///     12 倍      → "市值里有多少是已赚的钱撑的"（绝对）
    ///   这里要的是后者。
    ///
    /// 顺带：12 倍只落在全市场第 7 百分位 —— 能做到"已赚的利润撑起全部市值"的票很少。
    /// </summary>
    private const double BaseValuationPe = 12.0;

    /// <summary>归母净利同比跌幅的警戒线。</summary>
    private const double ProfitDropWarn = -0.15;
    private const double ProfitDropBad = -0.30;

    /// <summary>毛利率同比变化（百分点）的警戒线。毛利率是最难改善的指标，掉 2pct 就值得注意。</summary>
    private const double GrossMarginDropWarnPct = -1.5;

    /// <summary>年化 ROE 的警戒线。低于 5% 意味着股东回报接近理财产品。</summary>
    private const double RoeWarn = 5.0;
    private const double RoeBad = 3.0;

    /// <summary>资产负债率警戒线（非金融）。</summary>
    private const double DebtRatioWarn = 0.70;

    /// <summary>趋势图取多少期。</summary>
    private const int TrendPeriods = 8;

    private const double Yi = 1e8;

    /// <summary>
    /// 没拿到总股本、PE/PB 回退用报表实收资本时挂在那两行上的标识（2026-09-14）。
    ///
    /// 为什么必须标而不是静默回退：这两个口径**只有面值 1.00 元时才相等**，而错的时候
    /// 错得离谱且看不出来——中国移动回退值算出 PE 348.8（真值 16.1）、分众传媒 0.5（真值 20.1）。
    /// 用户看到的是一个正常格式的数字，没有任何迹象表明它不可信。
    ///
    /// ⚠ 不能改成"自动判断对不对"：偏大那一类（H 股会计口径）**没有本地判据能发现**，
    /// 「流通市值÷收盘价 &gt; 报表股本」只抓得出偏小的那些。所以只要没有总股本就一律标，
    /// 不去猜这一只到底准不准。
    /// </summary>
    private const string ShareCountFallbackNote = "　⚠ 没有总股本，按报表实收资本算";

    /// <summary>
    /// 银行体检表的行业参考分位（2026-08-29）。为 null 时用 <see cref="BankPeerStats.Builtin"/>
    /// 那份带日期的实测快照。调用方拿得到全行业数据时应该注入实算值——ROE/ROA/净息差这些
    /// 相对性指标钉死阈值就会随时代失效（见 <see cref="BankPeerStats"/> 的类注释）。
    /// </summary>
    private readonly BankPeerStats? _bankPeers;

    public FinancialAnalyzer(BankPeerStats? bankPeers = null) => _bankPeers = bankPeers;

    /// <summary>
    /// 生成分析报告。
    /// <paramref name="history"/> 是该股全部报告期的科目（<see cref="Abstractions.IFinancialRepository.GetAllByCode"/>，
    /// 降序）；<paramref name="latestClose"/>/<paramref name="dividendPerShare"/> 用于估值和股息率，
    /// 传 null 就跳过那几行。
    /// </summary>
    /// <param name="regulatory">银行监管指标（从财报 PDF 解析，见 BankReportParser）。只有银行用得上，
    /// 传 null 时体检表的 01/02/03/08 显示"待接入"。</param>
    /// <param name="peStats">
    /// 全市场 PE 分位，给「PE (TTM)」那行当参考值。传 null 用
    /// <see cref="MarketPeStats.Builtin"/> 内置快照——现有调用点不用改。
    /// </param>
    /// <param name="industryPe">
    /// 这只票所属行业的 PE 分位（2026-09-14）。传 null 就只显示全市场那半句——
    /// 没有行业归属的票（退市股、个别新股，实测 41 只）本来就该是这个行为。
    /// 取数在调用方，本类零 IO。
    /// </param>
    /// <param name="priceDate">
    /// <paramref name="latestClose"/> 是哪一天的收盘价（2026-09-14 新增）。只用于显示——
    /// 本地库未必抓到了今天，估值三行算的是"那一天的估值"，不标出来会被当成现价。
    /// </param>
    /// <param name="totalShares">
    /// 总股本（**股**），PE/PB 的股数（2026-09-14 新增）。传 null 或非正数就回退用财报的
    /// <c>share_capital</c> 并在那两行上标识——那是「实收资本」，是**金额**，
    /// 等于 总股本 × 每股面值，只有面值 1.00 元时才碰巧等于股数。
    /// 取数在调用方（<c>MetricKeys.TotalShares</c>，日更），本类零 IO。
    /// </param>
    public FinancialAnalysisReport Analyze(string code, string name, List<FinancialSnapshot> history,
        double? latestClose = null, double? dividendPerShare = null,
        List<BankRegulatoryMetric>? regulatory = null, MarketPeStats? peStats = null,
        double? totalShares = null, DateTime? priceDate = null, IndustryPeStats? industryPe = null)
    {
        if (history == null || history.Count == 0)
            return new FinancialAnalysisReport
            {
                Code = code, Name = name,
                Error = "本地库里没有这只票的财务数据。请在 Fetcher 里跑一次【拉取财务报表】"
                        + "（或勾选【空闲时自动补财务】），补上后再看。",
            };

        var cur = history[0];
        var prior = history.FirstOrDefault(h => h.ReportDate == cur.ReportDate.AddYears(-1));
        // 单季用的上一期（同一年内的前一个报告期）
        var prevInYear = cur.ReportDate.Month == 3
            ? null
            : history.FirstOrDefault(h => h.ReportDate.Year == cur.ReportDate.Year
                                          && h.ReportDate.Month == cur.ReportDate.Month - 3);

        // 去年的「上一期」——算去年同一个单季要用
        var priorPrevInYear = prevInYear == null
            ? null
            : history.FirstOrDefault(h => h.ReportDate == prevInYear.ReportDate.AddYears(-1));

        // 上一个报告期（不限同年）——存量项目的对比基准，见 BuildFundingSource 里的说明
        var prevPeriod = history.FirstOrDefault(h => h.ReportDate < cur.ReportDate);

        double? G(FinancialSnapshot? s, string key) => s?.Get(key);

        // 2026-08-29：原来只有一个 isFin 布尔，把银行/券商/保险混成一类。现在细分——银行有一套
        // 自己的监管指标体系（十二条体检表），券商看净资本/风险覆盖率、保险看内含价值/偿付能力，
        // 三者互不通用，不拆开就会把银行那套套到券商保险上。判定见 ClassifyInstitution。
        var kind = BankHealthCheckBuilder.ClassifyInstitution(cur);
        bool isFin = kind != FinancialInstitutionKind.NonFinancial;

        var sections = new List<AnalysisSection>();
        var alerts = new List<string>();

        void Collect(AnalysisSection sec)
        {
            sections.Add(sec);
            foreach (var l in sec.Lines.Where(l => l.Verdict == Verdict.Bad))
                alerts.Add($"{l.Label} {l.Value}{(l.Change.Length > 0 ? $"（{l.Change}）" : "")}"
                           + (l.Note.Length > 0 ? $" —— {l.Note}" : ""));
        }

        // 体检表排在最前面：它是这类标的真正要看的东西，通用几节只是补充。
        // 三类金融机构各一张表——银行看资产质量、券商看净资本、保险看偿付能力，互不通用。
        if (kind == FinancialInstitutionKind.Bank)
            Collect(BankHealthCheckBuilder.Build(cur, prior, history, _bankPeers ?? BankPeerStats.Builtin,
                latestClose, dividendPerShare, regulatory, totalShares));
        else if (kind == FinancialInstitutionKind.Broker)
            Collect(BrokerInsurerHealthCheckBuilder.BuildBroker(cur, prior, history, latestClose, regulatory, totalShares));
        else if (kind == FinancialInstitutionKind.Insurer)
            Collect(BrokerInsurerHealthCheckBuilder.BuildInsurer(cur, prior, history, latestClose, regulatory, totalShares));

        Collect(BuildScaleVsEfficiency(cur, prior, prevInYear, priorPrevInYear, isFin));
        if (!isFin) Collect(BuildMarginAttribution(cur, prior));
        Collect(BuildCashQuality(cur, prior, history, isFin));
        Collect(BuildFundingSource(cur, prior, prevPeriod, isFin));
        Collect(BuildReturnAndValuation(cur, prior, history, latestClose, dividendPerShare, isFin, peStats, totalShares, industryPe));

        return new FinancialAnalysisReport
        {
            Code = code,
            Name = name,
            ReportDate = cur.ReportDate,
            PriorYearDate = prior?.ReportDate,
            PeriodName = PeriodName(cur.ReportDate),
            Price = latestClose,
            PriceDate = priceDate,
            Kind = kind,
            Headline = BuildHeadline(cur, prior, isFin),
            Sections = sections,
            Alerts = alerts,
            Trends = BuildTrends(history, isFin),
        };
    }

    private static string PeriodName(DateTime d) => d.Month switch
    {
        3 => $"{d.Year}年一季报",
        6 => $"{d.Year}年中报",
        9 => $"{d.Year}年三季报",
        _ => $"{d.Year}年年报",
    };

    // ══════════ 一、规模 vs 效率 ══════════

    private AnalysisSection BuildScaleVsEfficiency(FinancialSnapshot cur, FinancialSnapshot? prior,
        FinancialSnapshot? prevInYear, FinancialSnapshot? priorPrevInYear, bool isFin)
    {
        var lines = new List<AnalysisLine>();
        double? rev = cur.Get(FinancialKeys.Revenue), revP = prior?.Get(FinancialKeys.Revenue);
        double? npp = cur.Get(FinancialKeys.NetProfitParent), nppP = prior?.Get(FinancialKeys.NetProfitParent);

        lines.Add(Money("营业收入", rev, revP, Verdict.Neutral,
            RevNote(rev, revP)));

        double? profitYoY = Ratio(npp, nppP);
        lines.Add(Money("归母净利", npp, nppP,
            profitYoY switch
            {
                null => Verdict.Missing,
                <= ProfitDropBad => Verdict.Bad,
                <= ProfitDropWarn => Verdict.Warn,
                _ => Verdict.Good,
            },
            profitYoY is <= ProfitDropBad ? "跌幅超过30%" : ""));

        // 单季：本期累计 − 上期累计。能看出"降幅在收窄还是扩大"，比只看累计有用得多。
        if (prevInYear != null && npp.HasValue && prevInYear.Get(FinancialKeys.NetProfitParent) is { } prevCum)
        {
            double q = npp.Value - prevCum;
            // 去年同一个单季 = 去年同期累计 − 去年上一期累计。有了它才能回答
            // 「降幅是在收窄还是扩大」，这比只看累计降幅有用得多。
            double? qPrior = null;
            if (priorPrevInYear?.Get(FinancialKeys.NetProfitParent) is { } pPrevCum
                && prior?.Get(FinancialKeys.NetProfitParent) is { } pCum)
                qPrior = pCum - pPrevCum;
            var qYoY = Ratio(q, qPrior);
            string trend = "";
            if (qYoY.HasValue && profitYoY.HasValue)
                // ⚠ **"降幅"这个词只在真的负增长时才能用**。
                //   原来只比相对大小，华鲁恒升单季 +43.4%、累计 +50.0% 也被说成
                //   "单季降幅比累计更大 —— 恶化在加速"，而两个都是增长。
                trend = DescribeQuarterTrend(qYoY!.Value, profitYoY!.Value);
            lines.Add(new AnalysisLine
            {
                Label = "  └ 本季单季",
                Value = $"{q / Yi:+0.00;-0.00} 亿",
                Change = qYoY.HasValue ? $"同比 {qYoY * 100:+0.0;-0.0}%" : "",
                Verdict = Verdict.Neutral,
                Note = trend.Length > 0 ? trend : "累计口径减出来的单季数",
            });
        }

        if (!isFin)
        {
            double? gm = GrossMargin(cur), gmP = GrossMargin(prior);
            double? gmDelta = gm.HasValue && gmP.HasValue ? (gm - gmP) * 100 : null;
            lines.Add(new AnalysisLine
            {
                Label = "毛利率",
                Value = gm.HasValue ? $"{gm.Value * 100:F2}%" : "—",
                Change = gmDelta.HasValue ? $"{gmDelta.Value:+0.00;-0.00} pct" : "",
                Verdict = gmDelta switch
                {
                    null => Verdict.Missing,
                    <= GrossMarginDropWarnPct => Verdict.Warn,
                    _ => Verdict.Good,
                },
                Note = gmDelta <= GrossMarginDropWarnPct
                    ? "毛利率是最难改善的指标，下移通常是产品结构或竞争格局变化，不会自己回来"
                    : "",
            });
        }

        double? nm = Ratio2(cur.Get(FinancialKeys.NetProfit), rev);
        double? nmP = Ratio2(prior?.Get(FinancialKeys.NetProfit), revP);
        double? nmDelta = nm.HasValue && nmP.HasValue ? (nm - nmP) * 100 : null;
        lines.Add(new AnalysisLine
        {
            Label = "净利率",
            Value = nm.HasValue ? $"{nm.Value * 100:F2}%" : "—",
            Change = nmDelta.HasValue ? $"{nmDelta.Value:+0.00;-0.00} pct" : "",
            Verdict = nmDelta is <= -2 ? Verdict.Warn : Verdict.Neutral,
            Note = "净利润总额 ÷ 营业收入（分子含少数股东损益）",
        });

        return new AnalysisSection
        {
            Title = "一、赚钱的规模 vs 赚钱的效率",
            Lines = lines,
            Conclusion = ScaleConclusion(rev, revP, npp, nppP),
        };
    }

    private static string RevNote(double? rev, double? revP)
    {
        var yoy = Ratio(rev, revP);
        return yoy switch
        {
            null => "",
            > 0.15 => "收入在快速扩张",
            >= -0.03 => "生意规模基本没动",
            > -0.15 => "收入小幅下滑",
            _ => "收入明显萎缩",
        };
    }

    private static string ScaleConclusion(double? rev, double? revP, double? npp, double? nppP)
    {
        var revYoY = Ratio(rev, revP);
        var profYoY = Ratio(npp, nppP);
        if (revYoY == null || profYoY == null) return "";

        // 这个对比是整份报告最重要的一句：规模没变而利润大跌，问题就在盈利能力，不在生意本身
        if (revYoY > -0.05 && profYoY < -0.20)
            return $"营收 {revYoY * 100:+0.0;-0.0}%、归母净利 {profYoY * 100:+0.0;-0.0}% —— "
                   + "东西照样卖得出去，问题在**每块钱收入留下的越来越少**，也就是盈利能力而不是规模。";
        if (revYoY < -0.10 && profYoY < -0.10)
            return $"营收和利润同步下滑（{revYoY * 100:+0.0;-0.0}% / {profYoY * 100:+0.0;-0.0}%）—— "
                   + "这是需求端的问题，不只是成本或费用。";
        if (revYoY > 0.10 && profYoY > 0.10)
            return $"营收 {revYoY * 100:+0.0;-0.0}%、利润 {profYoY * 100:+0.0;-0.0}%，规模和效率同步改善。";
        return $"营收 {revYoY * 100:+0.0;-0.0}%、归母净利 {profYoY * 100:+0.0;-0.0}%。";
    }

    // ══════════ 二、净利率归因 ══════════

    private AnalysisSection BuildMarginAttribution(FinancialSnapshot cur, FinancialSnapshot? prior)
    {
        var lines = new List<AnalysisLine>();
        double? rev = cur.Get(FinancialKeys.Revenue), revP = prior?.Get(FinancialKeys.Revenue);
        double? gm = GrossMargin(cur), gmP = GrossMargin(prior);
        double? nm = Ratio2(cur.Get(FinancialKeys.NetProfit), rev);
        double? nmP = Ratio2(prior?.Get(FinancialKeys.NetProfit), revP);

        string conclusion = "";
        if (nm.HasValue && nmP.HasValue && gm.HasValue && gmP.HasValue)
        {
            double nmDelta = (nm.Value - nmP.Value) * 100;
            double gmDelta = (gm.Value - gmP.Value) * 100;
            double rest = nmDelta - gmDelta;

            lines.Add(new AnalysisLine
            {
                Label = "净利率变化", Value = $"{nmDelta:+0.00;-0.00} pct", Verdict = Verdict.Neutral,
                Note = "下面把它拆成两块",
            });
            lines.Add(new AnalysisLine
            {
                Label = "  ├ 毛利率贡献", Value = $"{gmDelta:+0.00;-0.00} pct",
                Change = nmDelta != 0 ? $"占 {Math.Abs(gmDelta / nmDelta) * 100:F0}%" : "",
                Verdict = Verdict.Neutral,
                Note = "**结构性** —— 产品结构/竞争格局，不会自己回来",
            });
            lines.Add(new AnalysisLine
            {
                Label = "  └ 毛利以下贡献", Value = $"{rest:+0.00;-0.00} pct",
                Change = nmDelta != 0 ? $"占 {Math.Abs(rest / nmDelta) * 100:F0}%" : "",
                Verdict = Verdict.Neutral,
                Note = "四费 + 公允价值变动 + 税等，**部分可逆**",
            });

            if (nmDelta < -1 && Math.Abs(gmDelta) < Math.Abs(rest))
                conclusion = $"净利率掉了 {Math.Abs(nmDelta):F2}pct，其中毛利率只解释 {Math.Abs(gmDelta):F2}pct"
                             + $"（{Math.Abs(gmDelta / nmDelta) * 100:F0}%），大头在毛利以下。"
                             + "**所以“公司变坏了”和“只是一次性因素”两种说法都不准确** —— "
                             + "要看下面四费里哪些是主动投入（研发）、哪些会回摆（汇兑、公允价值）。";
            else if (nmDelta < -1)
                conclusion = $"净利率掉了 {Math.Abs(nmDelta):F2}pct，主要来自毛利率下移"
                             + $"（{Math.Abs(gmDelta):F2}pct）—— 这是结构性的，比费用问题更难扭转。";
        }

        // 四费明细
        if (rev is > 0)
        {
            foreach (var (label, key, note) in new[]
                     {
                         ("销售费用", FinancialKeys.SellExpense, ""),
                         ("管理费用", FinancialKeys.AdminExpense, ""),
                         ("财务费用", FinancialKeys.FinanceExpense, "含汇兑损益，汇率波动时会明显扰动"),
                         ("研发费用", FinancialKeys.RdExpense, "主动投入，跟被动的费用上升要区别看"),
                     })
            {
                double? v = cur.Get(key);
                if (v == null) continue;
                double share = v.Value / rev.Value * 100;
                double? vP = prior?.Get(key);
                double? shareP = vP.HasValue && revP is > 0 ? vP.Value / revP.Value * 100 : null;
                lines.Add(new AnalysisLine
                {
                    Label = $"  {label}",
                    Value = $"{v.Value / Yi:F2} 亿",
                    Change = shareP.HasValue
                        ? $"占营收 {share:F2}%（{share - shareP.Value:+0.00;-0.00} pct）"
                        : $"占营收 {share:F2}%",
                    Verdict = Verdict.Neutral,
                    Note = note,
                });
            }

            double? fv = cur.Get(FinancialKeys.FvChangeGain);
            if (fv != null && Math.Abs(fv.Value) > 0.01 * Yi)
                lines.Add(new AnalysisLine
                {
                    Label = "  公允价值变动收益",
                    Value = $"{fv.Value / Yi:+0.00;-0.00} 亿",
                    Verdict = Verdict.Neutral,
                    Note = "持仓浮盈浮亏，**非经常性、会回摆** —— 判断利润下滑可逆性时要先剥掉它",
                });
        }

        return new AnalysisSection { Title = "二、净利率的变化，掉在哪（归因拆解）", Lines = lines, Conclusion = conclusion };
    }

    // ══════════ 三、现金流质量 ══════════

    private AnalysisSection BuildCashQuality(FinancialSnapshot cur, FinancialSnapshot? prior,
        List<FinancialSnapshot> history, bool isFin)
    {
        var lines = new List<AnalysisLine>();
        // 应收账款的存量判定——声明在方法作用域，因为本节的结论句也要用
        double? arYoY = null, revYoY = null, arGap = null;
        double? ocf = cur.Get(FinancialKeys.Ocf), ocfP = prior?.Get(FinancialKeys.Ocf);
        double? npp = cur.Get(FinancialKeys.NetProfitParent), nppP = prior?.Get(FinancialKeys.NetProfitParent);

        var ocfYoY = Ratio(ocf, ocfP);
        var profYoY = Ratio(npp, nppP);
        lines.Add(Money("经营现金流", ocf, ocfP,
            ocf is < 0 ? Verdict.Bad : ocfYoY is < -0.5 ? Verdict.Bad : ocfYoY is < -0.2 ? Verdict.Warn : Verdict.Good,
            ocf is < 0 ? "**经营活动净流出** —— 主营业务在烧钱"
            : DescribeCashVsProfit(ocfYoY, profYoY, Ratio2(ocf, npp))));

        double? cov = Ratio2(ocf, npp);
        double? covP = Ratio2(ocfP, nppP);
        if (cov.HasValue)
            lines.Add(new AnalysisLine
            {
                Label = "现金流 / 归母净利",
                Value = $"{cov.Value:F2}",
                Change = covP.HasValue ? $"去年 {covP.Value:F2}" : "",
                Verdict = cov < OcfCoverageBad ? Verdict.Bad : cov < OcfCoverageWarn ? Verdict.Warn : Verdict.Good,
                Note = cov < OcfCoverageWarn
                    ? $"利润没有充分变成现金（健康区间 0.8~1.2，警戒线 {OcfCoverageWarn:F1}）"
                    : "利润基本都收成了现金",
            });

        if (!isFin)
        {
            double? sales = cur.Get(FinancialKeys.SalesCash), rev = cur.Get(FinancialKeys.Revenue);
            double? scr = Ratio2(sales, rev);
            if (scr.HasValue)
                lines.Add(new AnalysisLine
                {
                    Label = "收现比",
                    Value = $"{scr.Value:F2}",
                    Change = $"基准 ≈{CashRatioBenchmark:F2}",
                    Verdict = scr < CashRatioWarn ? Verdict.Warn : Verdict.Good,
                    Note = $"销售收现 ÷ 营收。**收到的现金含增值税、营收不含税**，所以正常值在 "
                           + $"{CashRatioBenchmark:F2} 左右而不是 1.0"
                           + (scr < CashRatioWarn ? " —— 现在低于基准，回款偏慢" : ""),
                });

            // 营运资金占用：用现金流量表**附注**的口径，不用资产负债表两个时点相减。
            // 附注含合并范围变动，且"经营性应收项目"覆盖应收账款+票据+预付+其他应收；
            // 实测两种口径能差近一倍（603501 2026H1：存货 5.89 vs 9.25、应收 7.12 vs 13.72）。
            // ── 应收账款（存量口径）──
            // 上面那组是**流量**口径，只回答"本期被占走多少现金"，而且是应收账款+票据+预付+
            // 其他应收的合计，分不清是客户欠钱还是预付给了供应商。存量口径回答的是另一个问题：
            // 欠款在不在逐年累积、账期有没有在变长。两者缺一不可——600285 2026H1 流量口径只占用
            // 1.61 亿、看着平平无奇，存量是应收 +23.2% 对营收 +2.3%、周转 34.8→43.5 天，
            // 两年间应收从 3.11 亿涨到 6.07 亿而营收只涨 12.7%。
            // 判定要先于流量行算出来：下面"营运资金净占用"用它避免两行报同一件事。
            double? ar = cur.Get(FinancialKeys.AccountsReceivable);
            arYoY = Ratio(ar, prior?.Get(FinancialKeys.AccountsReceivable));
            revYoY = Ratio(rev, prior?.Get(FinancialKeys.Revenue));
            arGap = arYoY.HasValue && revYoY.HasValue ? (arYoY.Value - revYoY.Value) * 100 : null;
            var arVerdict = arGap switch
            {
                null => Verdict.Neutral,
                > ArGrowthGapBadPct => Verdict.Bad,
                > ArGrowthGapWarnPct => Verdict.Warn,
                _ => Verdict.Good,
            };

            // 周转天数的期初取**上年末**（半年报的期初是上年 12-31，不是上年 6-30）
            var yearEnd = history.FirstOrDefault(h => h.ReportDate == new DateTime(cur.ReportDate.Year - 1, 12, 31));
            var priorYearEnd = history.FirstOrDefault(h => h.ReportDate == new DateTime(cur.ReportDate.Year - 2, 12, 31));
            double? arDays = ReceivableDays(cur, yearEnd);
            double? arDaysPrior = ReceivableDays(prior, priorYearEnd);
            bool arDaysRise = arDays.HasValue && arDaysPrior is > 0
                              && (arDays.Value - arDaysPrior.Value > ArDaysRiseWarnAbs
                                  || arDays.Value / arDaysPrior.Value - 1 > ArDaysRiseWarnRatio);
            // 应收本身就无足轻重的（预收款生意），整组不显示，也不拿它去降级流量行——
            // 小基数上的"同比 +200%"是噪音，不是信号。
            double noteRecv = cur.Get(FinancialKeys.NoteReceivable) ?? 0;
            double? arShare = ar.HasValue ? Ratio2(ar.Value + noteRecv, rev) : null;
            bool arTrivial = arShare is < ArTrivialShare;
            bool arFlagged = !arTrivial && (arVerdict is Verdict.Warn or Verdict.Bad || arDaysRise);

            double? invDec = cur.Get(FinancialKeys.InventoryDecrease);
            double? recvDec = cur.Get(FinancialKeys.ReceivableDecrease);
            double? payInc = cur.Get(FinancialKeys.PayableIncrease);
            if (invDec.HasValue || recvDec.HasValue || payInc.HasValue)
            {
                double occupied = -(invDec ?? 0) - (recvDec ?? 0) - (payInc ?? 0);
                lines.Add(new AnalysisLine
                {
                    Label = "营运资金净占用",
                    Value = $"{occupied / Yi:+0.00;-0.00} 亿",
                    // 占用额超过当期净利是量级问题，跟应收那行说的不是一回事，该 Bad 还是 Bad；
                    // 单纯"占用了现金"的 Warn 在应收行已经点名时降成陈述，免得异常汇总里两条重样。
                    Verdict = occupied > 0 && npp is > 0 && occupied > npp.Value ? Verdict.Bad
                        : occupied <= 0 ? Verdict.Good
                        : arFlagged ? Verdict.Neutral
                        : Verdict.Warn,
                    Note = occupied > 0 && npp is > 0 && occupied > npp.Value
                        ? "占用金额超过了当期归母净利 —— 赚的钱全被营运资金吃掉了"
                        : "正数=占用现金，负数=释放现金",
                });
                if (recvDec is < 0)
                    lines.Add(Sub("├ 经营性应收占用", -recvDec.Value,
                        "含应收账款+票据+预付+其他应收，所以比单看应收账款大"));
                if (invDec is < 0)
                    lines.Add(Sub("├ 存货占用", -invDec.Value, "备货或积压，要结合下游需求判断"));
                if (payInc.HasValue)
                    lines.Add(Sub(payInc > 0 ? "└ 经营性应付释放" : "└ 经营性应付占用",
                        Math.Abs(payInc.Value), payInc > 0 ? "占用上游资金 = 释放自己的现金" : "提前付款给上游"));
            }

            if (arTrivial) arGap = null;   // 结论句也不提它
            if (ar.HasValue && !arTrivial)
            {
                var arChange = new List<string>();
                if (arYoY.HasValue) arChange.Add($"同比 {arYoY * 100:+0.0;-0.0}%");
                double? yearEndAr = yearEnd?.Get(FinancialKeys.AccountsReceivable);
                // 年报的"上年末"就是同比基准，再显示一遍是重复
                if (cur.ReportDate.Month != 12 && yearEndAr is > 0)
                    arChange.Add($"较上年末 {(ar.Value / yearEndAr.Value - 1) * 100:+0.0;-0.0}%");
                lines.Add(new AnalysisLine
                {
                    Label = "应收账款",
                    Value = $"{ar.Value / Yi:F2} 亿",
                    Change = string.Join("　", arChange),
                    Verdict = arVerdict,
                    Note = arGap > ArGrowthGapWarnPct
                        ? $"**增速比营收（{revYoY * 100:+0.0;-0.0}%）快 {arGap:F1} pct** —— 这部分收入是赊出去的，"
                          + "不是卖出去的，回不回得来另说"
                        : "要跟营收增速比着看：涨得比营收快才是问题，单看金额涨没有意义",
                });

                if (arDays.HasValue)
                    lines.Add(new AnalysisLine
                    {
                        Label = "  └ 应收账款周转天数",
                        Value = $"{arDays.Value:F1} 天",
                        Change = arDaysPrior.HasValue ? $"去年同期 {arDaysPrior.Value:F1} 天" : "",
                        Verdict = arDaysRise ? Verdict.Warn : Verdict.Neutral,
                        Note = arDaysRise
                            ? "账期在变长 —— 要么客户在变差，要么是为了保收入主动放宽了信用"
                            : "平均应收 ÷ 累计营收 × 期间天数；期初取上年末余额",
                    });

                if (arShare is { } share)
                    lines.Add(new AnalysisLine
                    {
                        Label = "  └ 应收+票据 占营收",
                        Value = $"{share * 100:F1}%",
                        Verdict = Verdict.Neutral,
                        Note = $"其中应收票据 {noteRecv / Yi:F2} 亿。绝对水平**不设阈值**——账期长的行业（工程、设备）天生就高，"
                               + "钉死一个数是在筛行业；只看它自己一年年怎么变"
                               + (cur.ReportDate.Month != 12 ? "。非年报口径的营收只有大半年，比例天然比年报高" : ""),
                    });
            }
        }

        return new AnalysisSection
        {
            Title = "三、利润有没有变成现金（最该看的一块）",
            Lines = lines,
            Conclusion = CashConclusion(cur, ocfYoY, profYoY, cov, arGap, arYoY, revYoY, isFin),
        };
    }

    private static string CashConclusion(FinancialSnapshot cur, double? ocfYoY, double? profYoY,
        double? cov, double? arGap, double? arYoY, double? revYoY, bool isFin)
    {
        if (isFin) return "金融机构的经营现金流受同业往来和存贷款影响很大，跟工商企业不是一个含义，别直接对比。";
        var parts = new List<string>();
        // ⚠ 只有**两个都在跌**时才是"失血"。都在涨、只是现金流涨得慢，那是转化效率问题，
        //   不是失血——而且 cov 健康时第三节自己已经说了"利润基本都收成了现金"，再提一遍自相矛盾。
        if (ocfYoY is < 0 && profYoY is < 0 && ocfYoY < profYoY - 0.15)
            parts.Add("**现金流比利润跌得更狠**，说明失血是经营性的 —— 汇兑损失和公允价值浮亏根本不走经营现金流，"
                      + "所以不能用“一次性因素”解释掉");
        else if (ocfYoY is < 0 && profYoY is >= 0)
            parts.Add("**利润在增长但现金流在往下走**，增长没有同步变成现金");
        double? invDec = cur.Get(FinancialKeys.InventoryDecrease);
        double? recvDec = cur.Get(FinancialKeys.ReceivableDecrease);
        double? advance = cur.Get(FinancialKeys.AdvanceReceipts);
        // 存量背离比流量口径说得准，有它就不再说流量那句——流量只说明"这期被占了钱"，
        // 存量说明的是"欠款在累积"，后者才是该写进结论的那条。
        if (arGap > ArGrowthGapWarnPct)
            parts.Add($"**应收账款同比 {arYoY * 100:+0.0;-0.0}%、营收只有 {revYoY * 100:+0.0;-0.0}%**，"
                      + "收入是赊出去的 —— 这是最该盯的一条");
        else if (recvDec is < 0 && invDec is < 0)
            parts.Add("应收和存货**同时**增加，是下游走弱的典型组合（卖得慢了、客户付款也慢了）");
        if (cov is < OcfCoverageWarn)
            parts.Add($"现金流覆盖率只有 {cov:F2}");
        return parts.Count > 0 ? string.Join("；", parts) + "。" : "现金流质量正常。";
    }

    // ══════════ 四、扩张的钱从哪来 ══════════

    private AnalysisSection BuildFundingSource(FinancialSnapshot cur, FinancialSnapshot? prior,
        FinancialSnapshot? prevPeriod, bool isFin)
    {
        var lines = new List<AnalysisLine>();
        double? ocf = cur.Get(FinancialKeys.Ocf);
        double? icf = cur.Get(FinancialKeys.Icf);
        double? fcf = cur.Get(FinancialKeys.Fcf);

        if (ocf.HasValue && fcf.HasValue)
        {
            double inflow = ocf.Value + fcf.Value;
            double selfShare = inflow != 0 ? ocf.Value / inflow : 0;
            lines.Add(new AnalysisLine
            {
                Label = "经营造血", Value = $"{ocf.Value / Yi:+0.00;-0.00} 亿",
                Change = inflow != 0 ? $"占资金来源 {selfShare * 100:F1}%" : "",
                Verdict = selfShare < SelfFundingWarn ? Verdict.Bad : Verdict.Good,
                Note = selfShare < SelfFundingWarn
                    ? $"低于 {SelfFundingWarn * 100:F0}% —— **扩张主要靠融资，不是靠自己挣**"
                    : "",
            });
            lines.Add(new AnalysisLine
            {
                Label = "筹资流入", Value = $"{fcf.Value / Yi:+0.00;-0.00} 亿",
                Change = inflow != 0 ? $"占资金来源 {(1 - selfShare) * 100:F1}%" : "",
                Verdict = Verdict.Neutral,
            });
        }
        if (icf.HasValue)
            lines.Add(new AnalysisLine
            {
                Label = "投资流出", Value = $"{icf.Value / Yi:+0.00;-0.00} 亿",
                Change = cur.Get(FinancialKeys.Capex) is { } cap ? $"其中资本开支 {cap / Yi:F2} 亿" : "",
                Verdict = Verdict.Neutral,
            });

        // 有息负债 + 资产负债率。这里是最容易被误读的地方：负债率可能因为权益端一次性变大
        // （可转债转股）而下降，同期有息负债其实在增加。
        //
        // 存量项目要跟「上一期末」比，不能只跟去年同期比（2026-08-27 验证时发现）：
        // 603501 的有息负债 vs 去年同期是 65.24→58.19（降），vs 上期末却是 36.97→58.19（半年翻着涨）。
        // 只看同比会把「最近半年在大举借钱」完全掩盖掉。所以存量给两个基准；流量（营收/利润/
        // 现金流）仍然只看同比——那些是区间累计值，跟上期末比没有意义。
        double? assets = cur.Get(FinancialKeys.TotalAssets), liab = cur.Get(FinancialKeys.TotalLiabilities);
        double? dar = Ratio2(liab, assets), darP = Ratio2(prior?.Get(FinancialKeys.TotalLiabilities),
            prior?.Get(FinancialKeys.TotalAssets));
        double? darBeg = Ratio2(prevPeriod?.Get(FinancialKeys.TotalLiabilities),
            prevPeriod?.Get(FinancialKeys.TotalAssets));
        if (dar.HasValue)
            lines.Add(new AnalysisLine
            {
                Label = "资产负债率", Value = $"{dar.Value * 100:F2}%",
                Change = string.Join("　", new[]
                {
                    darP.HasValue ? $"同比 {(dar.Value - darP.Value) * 100:+0.00;-0.00} pct" : "",
                    darBeg.HasValue ? $"较上期末 {(dar.Value - darBeg.Value) * 100:+0.00;-0.00} pct" : "",
                }.Where(x => x.Length > 0)),
                Verdict = isFin ? Verdict.Neutral : dar > DebtRatioWarn ? Verdict.Warn : Verdict.Good,
                Note = isFin ? "金融机构天然高杠杆，这个数跟工商企业不可比" : "",
            });

        double? ib = InterestBearing(cur), ibP = InterestBearing(prior), ibBeg = InterestBearing(prevPeriod);
        if (ib.HasValue)
        {
            var ibYoY = Ratio(ib, ibP);
            var ibQoQ = Ratio(ib, ibBeg);
            lines.Add(new AnalysisLine
            {
                Label = "有息负债", Value = $"{ib.Value / Yi:F2} 亿",
                Change = string.Join("　", new[]
                {
                    ibP.HasValue ? $"同比 {(ib.Value - ibP.Value) / Yi:+0.00;-0.00} 亿" : "",
                    ibBeg.HasValue ? $"较上期末 {(ib.Value - ibBeg.Value) / Yi:+0.00;-0.00} 亿" : "",
                }.Where(x => x.Length > 0)),
                // 同比降、但较上期末大涨，是最需要点出来的形态——只看同比会以为在去杠杆
                Verdict = ibQoQ is > 0.3 || ibYoY is > 0.3 ? Verdict.Warn : Verdict.Neutral,
                Note = ibQoQ is > 0.3 && ibYoY is < 0
                    ? "同比是降的，但最近一期在大举增加 —— 只看同比会误判成在去杠杆"
                    : "短期借款+长期借款+应付债券",
            });
            double? st = cur.Get(FinancialKeys.ShortLoan);
            var stQoQ = Ratio(st, prevPeriod?.Get(FinancialKeys.ShortLoan));
            var stYoY = Ratio(st, prior?.Get(FinancialKeys.ShortLoan));
            if (stQoQ is > 0.5 || stYoY is > 0.5)
                lines.Add(Sub("└ 短期借款", st!.Value,
                    (stQoQ is > 0.5 ? $"较上期末 {stQoQ * 100:F0}%" : $"同比 {stYoY * 100:F0}%")
                    + " —— 短债扩张比长债更需要留意，它靠不断续借维持"));
            double? bond = cur.Get(FinancialKeys.BondPayable), bondP = prior?.Get(FinancialKeys.BondPayable);
            if (bondP is > 0 && bond is 0)
                lines.Add(new AnalysisLine
                {
                    Label = "  └ 应付债券", Value = "0.00 亿",
                    Change = $"去年 {bondP.Value / Yi:F2} 亿 → 0",
                    Verdict = Verdict.Neutral,
                    Note = "⚠ 可转债转股或到期兑付。**转股会一次性做大净资产**，"
                           + "从而压低资产负债率、摊薄每股收益 —— 这时负债率下降不代表经营改善",
                });
        }

        return new AnalysisSection
        {
            Title = "四、扩张的钱从哪来",
            Lines = lines,
            Conclusion = FundingConclusion(cur, prevPeriod, ocf, fcf, dar, darBeg),
        };
    }

    private static string FundingConclusion(FinancialSnapshot cur, FinancialSnapshot? prevPeriod,
        double? ocf, double? fcf, double? dar, double? darBeg)
    {
        var parts = new List<string>();
        if (ocf.HasValue && fcf.HasValue)
        {
            double inflow = ocf.Value + fcf.Value;
            if (inflow != 0 && ocf.Value / inflow < SelfFundingWarn)
                parts.Add($"经营只贡献了资金来源的 {ocf.Value / inflow * 100:F0}%，扩张靠借钱和融资撑着");
        }
        // 那个陷阱：负债率降了，但有息负债其实在涨。用「上期末」作基准——存量跟去年同期比
        // 会掩盖最近半年的变化（603501 就是同比降、较上期末翻倍）。
        double? ib = InterestBearing(cur), ibP = InterestBearing(prevPeriod);
        if (dar.HasValue && darBeg.HasValue && dar < darBeg && ib.HasValue && ibP.HasValue && ib > ibP)
            parts.Add("⚠ **资产负债率下降但有息负债在增加** —— 负债率是被权益端做大摊薄的（多半是转债转股或增发），"
                      + "不是还债还下来的。只看这个比率会得出相反的结论");
        return parts.Count > 0 ? string.Join("；", parts) + "。" : "";
    }

    // ══════════ 五、回报与估值 ══════════

    private AnalysisSection BuildReturnAndValuation(FinancialSnapshot cur, FinancialSnapshot? prior,
        List<FinancialSnapshot> history, double? price, double? dps, bool isFin,
        MarketPeStats? peStats, double? totalShares, IndustryPeStats? industryPe)
    {
        var lines = new List<AnalysisLine>();
        double? npp = cur.Get(FinancialKeys.NetProfitParent);
        double? eq = cur.Get(FinancialKeys.EquityParent);

        // 期初净资产取上一个报告期（不是去年同期）——ROE 分母用期初期末均值
        var prevAny = history.FirstOrDefault(h => h.ReportDate < cur.ReportDate);
        double? eqBeg = prevAny?.Get(FinancialKeys.EquityParent);
        if (npp.HasValue && eq is > 0)
        {
            double avgEq = eqBeg is > 0 ? (eq.Value + eqBeg.Value) / 2 : eq.Value;
            double roe = cur.AnnualizeCumulative(npp.Value) / avgEq * 100;
            double? roeP = null;
            if (prior?.Get(FinancialKeys.NetProfitParent) is { } nppP && prior.Get(FinancialKeys.EquityParent) is > 0)
                roeP = prior.AnnualizeCumulative(nppP) / prior.Get(FinancialKeys.EquityParent)!.Value * 100;
            lines.Add(new AnalysisLine
            {
                Label = "年化 ROE", Value = $"{roe:F1}%",
                Change = roeP.HasValue ? $"去年 {roeP.Value:F1}%" : "",
                Verdict = roe < RoeBad ? Verdict.Bad : roe < RoeWarn ? Verdict.Warn : Verdict.Good,
                Note = roe < RoeWarn ? $"低于 {RoeWarn:F0}%，股东回报接近理财产品" : "",
            });
        }

        // 股数：优先用抓来的总股本，拿不到才回退报表的实收资本并标识（见 ShareCountFallbackNote）。
        double? share = totalShares is > 0 ? totalShares : cur.Get(FinancialKeys.ShareCapital);
        bool shareIsFallback = totalShares is not > 0;
        string shareNote = shareIsFallback ? ShareCountFallbackNote : "";
        if (price is > 0 && share is > 0)
        {
            if (eq is > 0)
            {
                double bps = eq.Value / share.Value;
                lines.Add(new AnalysisLine
                {
                    Label = "PB", Value = $"{price.Value / bps:F2}",
                    Change = $"每股净资产 {bps:F2} 元" + shareNote,
                    Verdict = Verdict.Neutral,
                    Note = shareIsFallback
                        ? "股本用报表的实收资本，不是流通市值倒推"
                        : "总股本含限售股和 H/B 股，不是流通市值倒推",
                });
            }
            // TTM：年报直接用；中间期用"上年年报 − 上年同期 + 本期"
            double? ttm = TtmProfit(cur, prior, history);
            if (ttm is > 0)
            {
                double cap = price.Value * share.Value;
                double pe = cap / ttm.Value;
                // 已赚到的利润按合理估值能撑起多少市值。剩下的部分是纯粹对未来的定价。
                double supported = BaseValuationPe * ttm.Value;
                double earnedPct = supported / cap * 100;
                var mkt = peStats ?? MarketPeStats.Builtin;
                lines.Add(new AnalysisLine
                {
                    Label = "PE (TTM)", Value = $"{pe:F1}",
                    Change = $"TTM 归母净利 {ttm.Value / Yi:F2} 亿" + shareNote,
                    // ⚠ 只能是 Neutral。FactorLab 十分组实测 D1（最贵那组）年化 +10.6%、
                    //   是十组里最高的，曲线呈 U 型不单调 —— "贵"并不预示跌。
                    //   界面按 Verdict 上色，标黄标红就是在暗示一个数据不支持的结论。
                    Verdict = Verdict.Neutral,
                    // 行业在前、全市场在后（2026-09-14）。两句答的不是一个问题：
                    // 行业分位答"同行里高不高"，全市场分位答"绝对位置在哪"。
                    // 只留行业的话会丢掉"银行整体就便宜"这个信息——银行 6.3 的行业中位
                    // 本身就是市场对整个行业的定价。
                    Reference = industryPe is { } ind
                        ? $"{ind.Describe(pe)}　·　全市场中位 {mkt.Median:F1} 倍"
                        : $"全市场中位 {mkt.Median:F1} 倍　{mkt.DescribePosition(pe)}",
                    Note = earnedPct >= 100
                        ? $"已赚到的利润按 {BaseValuationPe:F0} 倍能撑 {supported / Yi:F0} 亿，"
                          + $"超过市值 {cap / Yi:F0} 亿 —— 现价没有为未来付钱"
                        : $"市值 {cap / Yi:F0} 亿里，已赚到的利润按 {BaseValuationPe:F0} 倍能撑 "
                          + $"{supported / Yi:F0} 亿，其余 {100 - earnedPct:F0}% 是对未来的定价",
                });
            }
            if (dps is > 0)
            {
                double dy = dps.Value / price.Value * 100;
                lines.Add(new AnalysisLine
                {
                    Label = "股息率", Value = $"{dy:F2}%",
                    Verdict = Verdict.Neutral,
                    Note = dy < 4.5 ? "底仓法门槛是 4.5%，不到就不是底仓标的（可以是成长/波段标的）" : "达到底仓法门槛",
                });
            }
        }

        return new AnalysisSection { Title = "五、回报与估值", Lines = lines };
    }

    private static double? TtmProfit(FinancialSnapshot cur, FinancialSnapshot? prior, List<FinancialSnapshot> history)
    {
        var lastAnnual = history.FirstOrDefault(h => h.ReportDate.Month == 12 && h.ReportDate.Year == cur.ReportDate.Year - 1);
        return TtmFromCumulative(
            cur.ReportDate,
            cur.Get(FinancialKeys.NetProfitParent),
            lastAnnual?.Get(FinancialKeys.NetProfitParent),
            prior?.Get(FinancialKeys.NetProfitParent));
    }

    /// <summary>
    /// TTM 归母净利：年报直接用，中间期用「上年年报 − 上年同期 + 本期」。
    /// A股定期报告是**年内累计**口径，所以这个减法才成立。
    ///
    /// ⚠ **算行业/全市场 PE 分位的一方必须调这个函数**（2026-09-14 抽出来就是为了这个）。
    /// 参考分位跟个股值口径不同的话，比了等于没比——银行体检表那边踩过同样的坑：
    /// ROE 分母一边用期末、一边用期初期末均值，个股一比就系统性地"优于行业"，全是假的。
    /// 分位那侧数据形状不一样（不会为 3900 只票各建一份 FinancialSnapshot 列表），
    /// 所以口径只能靠这个共用的纯函数保证，不能靠"两边各写一遍小心点"。
    /// </summary>
    /// <param name="period">本期报告期。</param>
    /// <param name="current">本期归母净利（年内累计）。</param>
    /// <param name="lastAnnual">上年年报的归母净利。</param>
    /// <param name="priorSamePeriod">去年同期（同月份）的归母净利。</param>
    public static double? TtmFromCumulative(DateTime period, double? current,
                                            double? lastAnnual, double? priorSamePeriod)
    {
        if (current == null) return null;
        if (period.Month == 12) return current;
        if (lastAnnual == null || priorSamePeriod == null) return null;
        return lastAnnual.Value - priorSamePeriod.Value + current.Value;
    }

    // ══════════ 趋势 ══════════

    private List<TrendSeries> BuildTrends(List<FinancialSnapshot> history, bool isFin)
    {
        // 只取同月份的报告期，避免"累计口径"把趋势画成锯齿（Q1 低、年报高不是变差变好）
        int month = history[0].ReportDate.Month;
        var same = history.Where(h => h.ReportDate.Month == month)
            .OrderByDescending(h => h.ReportDate).Take(TrendPeriods)
            .OrderBy(h => h.ReportDate).ToList();

        var defs = new List<(string Name, Func<FinancialSnapshot, double?> Sel, string Unit)>
        {
            ("营业收入", s => s.Get(FinancialKeys.Revenue) / Yi, "亿"),
            ("归母净利", s => s.Get(FinancialKeys.NetProfitParent) / Yi, "亿"),
            ("经营现金流", s => s.Get(FinancialKeys.Ocf) / Yi, "亿"),
        };
        if (!isFin)
            defs.Add(("毛利率", s => GrossMargin(s) * 100, "%"));
        // 这条比率在净利很小的年份会爆表（603501 2023H1 净利仅 1.53 亿、比率 20.44），
        // 一个点就把整张图的纵轴压平。截到 [-3, 3]：超出这个范围只需知道「极端」，不需要具体值。
        defs.Add(("现金流/净利", s =>
        {
            var r = Ratio2(s.Get(FinancialKeys.Ocf), s.Get(FinancialKeys.NetProfitParent));
            return r.HasValue ? Math.Clamp(r.Value, -3, 3) : null;
        }, ""));

        // 所有序列共用**同一条报告期轴**，某期算不出来就放 NaN 占位（2026-08-27 修）。
        //
        // 原来是把算不出的点直接过滤掉，于是各序列点数不同、索引各自重编号——几张图并排放时，
        // 同一列位置对应的不是同一期。实测 603501：2023H1 归母净利为负，"现金流/净利"这条算不出来
        // 被删掉，那张图的 x 轴就只剩 7 个标签、22/06 和 24/06 直接相邻，看着像连续的、
        // 而且跟上面几张图对不齐。横向扫描多张图的前提就是 x 轴一致，所以宁可留空也不能错位。
        var periods = same.Select(s => s.ReportDate).ToList();
        var result = new List<TrendSeries>();
        foreach (var (name, sel, unit) in defs)
        {
            var pts = same.Select(s => (s.ReportDate, V: sel(s) ?? double.NaN)).ToList();
            // 至少要有两个真实值才值得画
            if (pts.Count(x => !double.IsNaN(x.V)) >= 2)
                result.Add(new TrendSeries { Name = name, Points = pts, Unit = unit });
        }
        return result;
    }

    // ══════════ 小工具 ══════════

    private static string BuildHeadline(FinancialSnapshot cur, FinancialSnapshot? prior, bool isFin)
    {
        var revYoY = Ratio(cur.Get(FinancialKeys.Revenue), prior?.Get(FinancialKeys.Revenue));
        var profYoY = Ratio(cur.Get(FinancialKeys.NetProfitParent), prior?.Get(FinancialKeys.NetProfitParent));
        var ocfYoY = Ratio(cur.Get(FinancialKeys.Ocf), prior?.Get(FinancialKeys.Ocf));
        var cov = Ratio2(cur.Get(FinancialKeys.Ocf), cur.Get(FinancialKeys.NetProfitParent));

        if (revYoY == null || profYoY == null) return "数据不足，无法给出总体判断。";

        var parts = new List<string>();
        parts.Add(revYoY > -0.05 ? "营收持稳" : revYoY > -0.15 ? "营收小幅下滑" : "营收明显萎缩");
        parts.Add(profYoY > 0.15 ? "利润快速增长"
            : profYoY > -0.05 ? "利润基本持平"
            : profYoY > -0.30 ? "利润下滑" : "利润大幅下滑");
        // 带上具体数字（2026-08-27 用户要求）——原来这句只有结论没有数，用户还得去底部异常项
        // 里找那个 "-78.4% vs -39.9%"，两处说同一件事。合并到这一句里，标题行就自足了。
        // ⚠ "失血"只能用在真的负增长上，见 DescribeCashVsProfit 的说明。
        if (ocfYoY is < 0 && profYoY is < 0 && ocfYoY < profYoY - 0.15)
            parts.Add($"**现金流失血比利润更严重**（{ocfYoY * 100:F1}% vs {profYoY * 100:F1}%）");
        else if (ocfYoY is < 0 && profYoY is >= 0)
            parts.Add($"**利润在增长但现金流在往下走**（{ocfYoY * 100:F1}% vs +{profYoY * 100:F1}%）");
        else if (cov is < OcfCoverageWarn)
            parts.Add($"利润的现金含量偏低（现金流/净利 {cov:F2}）");
        return string.Join("、", parts) + "。";
    }

    private static AnalysisLine Money(string label, double? v, double? prior, Verdict verdict, string note) => new()
    {
        Label = label,
        Value = v.HasValue ? $"{v.Value / Yi:+0.00;-0.00} 亿" : "—",
        Change = Ratio(v, prior) is { } r ? $"{r * 100:+0.0;-0.0}%" : "",
        Verdict = v.HasValue ? verdict : Verdict.Missing,
        Note = note,
    };

    private static AnalysisLine Sub(string label, double value, string note) => new()
    {
        Label = $"  {label}", Value = $"{value / Yi:F2} 亿", Verdict = Verdict.Neutral, Note = note,
    };

    /// <summary>
    /// 应收账款周转天数 = 平均应收 ÷ 累计营收 × 期间天数。
    /// 期初取**上年末**余额（半年报的期初是上年 12-31，不是上年 6-30），这是标准口径。
    /// 期初缺失直接返回 null 而不退化成期末余额：退化会低估天数，且跟同比那侧口径不一致
    /// （一个用平均一个用期末），对比出来的"变化"是假的，宁可整行不显示。
    /// </summary>
    private static double? ReceivableDays(FinancialSnapshot? s, FinancialSnapshot? yearEnd)
    {
        double? ar = s?.Get(FinancialKeys.AccountsReceivable);
        double? rev = s?.Get(FinancialKeys.Revenue);
        double? begin = yearEnd?.Get(FinancialKeys.AccountsReceivable);
        if (ar == null || rev is not > 0 || begin == null) return null;
        int days = s!.ReportDate.Month switch { 3 => 90, 6 => 180, 9 => 270, _ => 360 };
        return (ar.Value + begin.Value) / 2 / rev.Value * days;
    }

    private static double? GrossMargin(FinancialSnapshot? s)
    {
        double? rev = s?.Get(FinancialKeys.Revenue), cost = s?.Get(FinancialKeys.OperCost);
        return rev is > 0 && cost is > 0 ? 1 - cost.Value / rev.Value : null;
    }

    private static double? InterestBearing(FinancialSnapshot? s)
    {
        if (s == null) return null;
        double? st = s.Get(FinancialKeys.ShortLoan), lt = s.Get(FinancialKeys.LongLoan), bd = s.Get(FinancialKeys.BondPayable);
        if (st == null && lt == null && bd == null) return null;
        return (st ?? 0) + (lt ?? 0) + (bd ?? 0);
    }

    /// <summary>同比变化率；分母非正或缺失时返回 null（负基数算出来的百分比没有意义）。</summary>
    /// <summary>
    /// 单季相对累计的趋势措辞。**按正负号选词**——"降幅"只能用在负增长上。
    ///
    /// 踩过的坑：原来只比相对大小（<c>qYoY &lt; profitYoY - 0.03</c>），华鲁恒升
    /// 单季 +43.4%、累计 +50.0% 两个都在涨，却被说成"单季降幅比累计更大 —— 恶化在加速"。
    /// </summary>
    private static string DescribeQuarterTrend(double qYoY, double cumYoY)
    {
        const double Eps = 0.03;
        bool bothDown = qYoY < 0 && cumYoY < 0;
        if (qYoY < cumYoY - Eps)
            return bothDown ? "单季降幅比累计更大 —— 恶化在加速"
                 : qYoY < 0 ? "累计还是增长，但单季已经转负"
                 : "单季增速低于累计 —— 增长在放缓";
        if (qYoY > cumYoY + Eps)
            return bothDown ? "单季降幅小于累计 —— 降幅在收窄"
                 : cumYoY < 0 ? "累计仍是负的，但单季已经转正"
                 : "单季增速高于累计 —— 增长在提速";
        return bothDown ? "跟累计降幅基本一致" : "跟累计增速基本一致";
    }

    /// <summary>
    /// 经营现金流相对净利的措辞。**同样按正负号分档**，而且两个都在涨时看
    /// <paramref name="cov"/>（现金流/净利）决定要不要提示——cov 健康的话，
    /// 同一节里已经有"利润基本都收成了现金"那一行，再提示增速差就是自相矛盾。
    /// </summary>
    private static string DescribeCashVsProfit(double? ocfYoY, double? profYoY, double? cov)
    {
        if (ocfYoY is not { } o || profYoY is not { } p) return "";
        if (o >= p - 0.15) return "";

        if (o < 0 && p < 0)
            return $"跌得比利润还狠（{o * 100:F1}% vs {p * 100:F1}%）—— 这不是账面项目造成的";
        if (o < 0)
            return $"利润在增长（+{p * 100:F1}%）但现金流在往下走（{o * 100:F1}%）";
        // 两个都在涨：只有现金含量确实偏低时才值得说一句
        return cov is < OcfCoverageWarn
            ? $"现金流增速慢于利润（+{o * 100:F1}% vs +{p * 100:F1}%），现金含量只有 {cov:F2}"
            : "";
    }

    private static double? Ratio(double? cur, double? prior) =>
        cur.HasValue && prior is > 0 ? cur.Value / prior.Value - 1 : null;

    /// <summary>简单相除；分母非正或缺失返回 null。</summary>
    private static double? Ratio2(double? a, double? b) => a.HasValue && b is > 0 ? a.Value / b.Value : null;
}
