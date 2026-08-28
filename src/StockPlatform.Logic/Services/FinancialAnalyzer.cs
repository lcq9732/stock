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

    /// <summary>经营活动贡献的资金占（经营+筹资）的比例。低于 20% 说明扩张主要靠借钱/融资。</summary>
    private const double SelfFundingWarn = 0.20;

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
    /// 生成分析报告。
    /// <paramref name="history"/> 是该股全部报告期的科目（<see cref="Abstractions.IFinancialRepository.GetAllByCode"/>，
    /// 降序）；<paramref name="latestClose"/>/<paramref name="dividendPerShare"/> 用于估值和股息率，
    /// 传 null 就跳过那几行。
    /// </summary>
    public FinancialAnalysisReport Analyze(string code, string name, List<FinancialSnapshot> history,
        double? latestClose = null, double? dividendPerShare = null)
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
        bool isFin = G(cur, FinancialKeys.OperCost) is null or 0;

        var sections = new List<AnalysisSection>();
        var alerts = new List<string>();

        void Collect(AnalysisSection sec)
        {
            sections.Add(sec);
            foreach (var l in sec.Lines.Where(l => l.Verdict == Verdict.Bad))
                alerts.Add($"{l.Label} {l.Value}{(l.Change.Length > 0 ? $"（{l.Change}）" : "")}"
                           + (l.Note.Length > 0 ? $" —— {l.Note}" : ""));
        }

        Collect(BuildScaleVsEfficiency(cur, prior, prevInYear, priorPrevInYear, isFin));
        if (!isFin) Collect(BuildMarginAttribution(cur, prior));
        Collect(BuildCashQuality(cur, prior, isFin));
        Collect(BuildFundingSource(cur, prior, prevPeriod, isFin));
        Collect(BuildReturnAndValuation(cur, prior, history, latestClose, dividendPerShare, isFin));

        return new FinancialAnalysisReport
        {
            Code = code,
            Name = name,
            ReportDate = cur.ReportDate,
            PriorYearDate = prior?.ReportDate,
            PeriodName = PeriodName(cur.ReportDate),
            IsFinancialInstitution = isFin,
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
                trend = qYoY < profitYoY - 0.03 ? "单季降幅比累计更大 —— 恶化在加速"
                    : qYoY > profitYoY + 0.03 ? "单季降幅小于累计 —— 降幅在收窄"
                    : "跟累计降幅基本一致";
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

    private AnalysisSection BuildCashQuality(FinancialSnapshot cur, FinancialSnapshot? prior, bool isFin)
    {
        var lines = new List<AnalysisLine>();
        double? ocf = cur.Get(FinancialKeys.Ocf), ocfP = prior?.Get(FinancialKeys.Ocf);
        double? npp = cur.Get(FinancialKeys.NetProfitParent), nppP = prior?.Get(FinancialKeys.NetProfitParent);

        var ocfYoY = Ratio(ocf, ocfP);
        var profYoY = Ratio(npp, nppP);
        lines.Add(Money("经营现金流", ocf, ocfP,
            ocf is < 0 ? Verdict.Bad : ocfYoY is < -0.5 ? Verdict.Bad : ocfYoY is < -0.2 ? Verdict.Warn : Verdict.Good,
            ocf is < 0 ? "**经营活动净流出** —— 主营业务在烧钱"
            : ocfYoY.HasValue && profYoY.HasValue && ocfYoY < profYoY - 0.15
                ? $"跌得比利润还狠（{ocfYoY * 100:F1}% vs {profYoY * 100:F1}%）—— 这不是账面项目造成的"
                : ""));

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
                    Verdict = occupied > 0 && npp is > 0 && occupied > npp.Value ? Verdict.Bad
                        : occupied > 0 ? Verdict.Warn : Verdict.Good,
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
        }

        return new AnalysisSection
        {
            Title = "三、利润有没有变成现金（最该看的一块）",
            Lines = lines,
            Conclusion = CashConclusion(cur, ocfYoY, profYoY, cov, isFin),
        };
    }

    private static string CashConclusion(FinancialSnapshot cur, double? ocfYoY, double? profYoY,
        double? cov, bool isFin)
    {
        if (isFin) return "金融机构的经营现金流受同业往来和存贷款影响很大，跟工商企业不是一个含义，别直接对比。";
        var parts = new List<string>();
        if (ocfYoY.HasValue && profYoY.HasValue && ocfYoY < profYoY - 0.15)
            parts.Add("**现金流比利润跌得更狠**，说明失血是经营性的 —— 汇兑损失和公允价值浮亏根本不走经营现金流，"
                      + "所以不能用“一次性因素”解释掉");
        double? invDec = cur.Get(FinancialKeys.InventoryDecrease);
        double? recvDec = cur.Get(FinancialKeys.ReceivableDecrease);
        double? advance = cur.Get(FinancialKeys.AdvanceReceipts);
        if (recvDec is < 0 && invDec is < 0)
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
        List<FinancialSnapshot> history, double? price, double? dps, bool isFin)
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

        double? share = cur.Get(FinancialKeys.ShareCapital);
        if (price is > 0 && share is > 0)
        {
            if (eq is > 0)
            {
                double bps = eq.Value / share.Value;
                lines.Add(new AnalysisLine
                {
                    Label = "PB", Value = $"{price.Value / bps:F2}",
                    Change = $"每股净资产 {bps:F2} 元",
                    Verdict = Verdict.Neutral,
                    Note = "股本用报表的实收资本，不是流通市值倒推",
                });
            }
            // TTM：年报直接用；中间期用"上年年报 − 上年同期 + 本期"
            double? ttm = TtmProfit(cur, prior, history);
            if (ttm is > 0)
                lines.Add(new AnalysisLine
                {
                    Label = "PE (TTM)", Value = $"{price.Value * share.Value / ttm.Value:F1}",
                    Change = $"TTM 归母净利 {ttm.Value / Yi:F2} 亿",
                    Verdict = Verdict.Neutral,
                });
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
        double? npp = cur.Get(FinancialKeys.NetProfitParent);
        if (npp == null) return null;
        if (cur.ReportDate.Month == 12) return npp;
        var lastAnnual = history.FirstOrDefault(h => h.ReportDate.Month == 12 && h.ReportDate.Year == cur.ReportDate.Year - 1);
        double? annual = lastAnnual?.Get(FinancialKeys.NetProfitParent);
        double? priorSame = prior?.Get(FinancialKeys.NetProfitParent);
        if (annual == null || priorSame == null) return null;
        return annual.Value - priorSame.Value + npp.Value;
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
        if (ocfYoY.HasValue && profYoY.HasValue && ocfYoY < profYoY - 0.15)
            parts.Add($"**现金流失血比利润更严重**（{ocfYoY * 100:F1}% vs {profYoY * 100:F1}%）");
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
    private static double? Ratio(double? cur, double? prior) =>
        cur.HasValue && prior is > 0 ? cur.Value / prior.Value - 1 : null;

    /// <summary>简单相除；分母非正或缺失返回 null。</summary>
    private static double? Ratio2(double? a, double? b) => a.HasValue && b is > 0 ? a.Value / b.Value : null;
}
