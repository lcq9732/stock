using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 券商和保险的体检表（2026-08-29 新增）——跟银行那张同样是"一列值、一列参考范围"，
/// 但**指标体系完全不同**，这正是把 isFin 拆成三类的意义。
///
/// ════ 三类金融机构在看什么 ════
///   银行：钱借出去收不收得回来（不良率/拨备/资本充足率），见 <see cref="BankHealthCheckBuilder"/>
///   券商：净资本够不够扛住自营和业务风险（风险覆盖率/资本杠杆率/自营敞口）
///   保险：赔得起吗、承保本身赚不赚钱（偿付能力充足率/综合成本率）
/// 拿任何一套去套另外两类都会得出荒谬结论，所以三张表分开做。
///
/// ════ 参考值全部来自监管硬性规定 ════
/// 跟银行那边"相对性阈值要用行业分位"不同，券商和保险的核心指标都有**监管明文红线**
/// （风险覆盖率≥100%、综合偿付能力≥100%…），而且实务上还有一档"预警标准"= 红线×1.2，
/// 触及预警就要报送整改方案。所以这里直接用监管值，不需要行业分位——这些线不会随周期漂移。
/// </summary>
public static class BrokerInsurerHealthCheckBuilder
{
    private const double Yi = 1e8;

    // ── 券商风控指标的监管红线与预警线（《证券公司风险控制指标管理办法》）──
    private const double RiskCoverageMin = 100, RiskCoverageWarn = 120;
    private const double CapitalLeverageMin = 8, CapitalLeverageWarn = 9.6;
    private const double LiquidityMin = 100, LiquidityWarn = 120;
    private const double NetCapToNetAssetsMin = 20;
    private const double NetCapToLiabMin = 8;
    /// <summary>自营敞口是**上限**型（越高越激进），跟上面几条方向相反。</summary>
    private const double EquityPropMax = 100, NonEquityPropMax = 500;

    // ── 保险（偿二代二期）──
    private const double CoreSolvencyMin = 50, ComprehensiveSolvencyMin = 100;
    /// <summary>综合成本率 100% 是承保盈亏平衡线：低于它靠承保就能赚钱，高于它只能靠投资收益补。</summary>
    private const double CombinedBreakeven = 100;

    /// <summary>券商体检表。</summary>
    public static AnalysisSection BuildBroker(
        FinancialSnapshot cur, FinancialSnapshot? prior, List<FinancialSnapshot> history,
        double? price, List<BankRegulatoryMetric>? regulatory, double? totalShares = null)
    {
        var lines = new List<AnalysisLine>();
        double? Reg(string key) => regulatory?
            .FirstOrDefault(m => m.MetricKey == key && m.ReportDate == cur.ReportDate)?.Value;

        // ── 01 风险覆盖率：券商的头号指标，相当于银行的资本充足率 ──
        AddRatio(lines, 1, "风险覆盖率", Reg(BrokerRegulatoryKeys.RiskCoverage),
            $"监管 ≥{RiskCoverageMin:F0}%　预警 {RiskCoverageWarn:F0}%",
            v => v < RiskCoverageMin ? Verdict.Bad : v < RiskCoverageWarn ? Verdict.Warn : Verdict.Good,
            "净资本 ÷ 各项风险资本准备之和。券商最核心的一条——净资本要能覆盖住全部业务风险，"
            + "低于 120% 就进预警、要报送整改方案");

        // ── 02 资本杠杆率 ──
        AddRatio(lines, 2, "资本杠杆率", Reg(BrokerRegulatoryKeys.CapitalLeverage),
            $"监管 ≥{CapitalLeverageMin:F0}%　预警 {CapitalLeverageWarn:F1}%",
            v => v < CapitalLeverageMin ? Verdict.Bad : v < CapitalLeverageWarn ? Verdict.Warn : Verdict.Good,
            "核心净资本 ÷ 表内外资产总额。管的是总杠杆，越低说明摊子相对本金铺得越大");

        // ── 03 流动性两条 ──
        AddRatio(lines, 3, "流动性覆盖率", Reg(BrokerRegulatoryKeys.LiquidityCoverage),
            $"监管 ≥{LiquidityMin:F0}%　预警 {LiquidityWarn:F0}%",
            v => v < LiquidityMin ? Verdict.Bad : v < LiquidityWarn ? Verdict.Warn : Verdict.Good,
            "未来 30 天极端情况下的现金流缺口能不能被优质流动性资产盖住");
        AddRatio(lines, 3, "净稳定资金率", Reg(BrokerRegulatoryKeys.NetStableFunding),
            $"监管 ≥{LiquidityMin:F0}%　预警 {LiquidityWarn:F0}%",
            v => v < LiquidityMin ? Verdict.Bad : v < LiquidityWarn ? Verdict.Warn : Verdict.Good,
            "长期资产有没有用长期资金去匹配，防的是短债长投");

        // ── 04 净资本质量 ──
        AddRatio(lines, 4, "净资本 / 净资产", Reg(BrokerRegulatoryKeys.NetCapitalToNetAssets),
            $"监管 ≥{NetCapToNetAssetsMin:F0}%",
            v => v < NetCapToNetAssetsMin ? Verdict.Bad : Verdict.Good,
            "净资产里有多大比例能真正算作净资本。偏低说明账面净资产里有不少扣减项（长期股权、"
            + "商誉等），实际可用于承担风险的本金没那么多");
        AddRatio(lines, 4, "净资本 / 负债", Reg(BrokerRegulatoryKeys.NetCapitalToLiabilities),
            $"监管 ≥{NetCapToLiabMin:F0}%",
            v => v < NetCapToLiabMin ? Verdict.Bad : Verdict.Good, "");

        // ── 05 自营敞口：**上限型**，方向跟前面全部相反 ──
        AddRatio(lines, 5, "自营权益类 / 净资本", Reg(BrokerRegulatoryKeys.EquityPropToNetCapital),
            $"监管 ≤{EquityPropMax:F0}%（上限）",
            v => v > EquityPropMax ? Verdict.Bad : v > EquityPropMax * 0.8 ? Verdict.Warn : Verdict.Good,
            "自营股票盘子相对净资本有多大。**这条是越低越保守**，跟上面几条方向相反——"
            + "券商业绩的波动主要就来自这块");
        AddRatio(lines, 5, "自营非权益类 / 净资本", Reg(BrokerRegulatoryKeys.NonEquityPropToNetCapital),
            $"监管 ≤{NonEquityPropMax:F0}%（上限）",
            v => v > NonEquityPropMax ? Verdict.Bad : v > NonEquityPropMax * 0.8 ? Verdict.Warn : Verdict.Good,
            "自营债券等固收盘子。占用净资本的大头通常在这里");

        // ── 06 收入结构：这几块从利润表算得出来，看的是"赚的是哪门子钱" ──
        double? rev = cur.Get(FinancialKeys.Revenue);
        if (rev is > 0)
        {
            void Share(string label, string key, string note)
            {
                if (cur.Get(key) is not { } v) return;
                double? p = prior?.Get(key);
                double? pRev = prior?.Get(FinancialKeys.Revenue);
                lines.Add(new AnalysisLine
                {
                    ClauseNo = 6, Label = label, Value = $"{v / rev.Value * 100:F2}%",
                    Change = p.HasValue && pRev is > 0 ? $"去年同期 {p.Value / pRev.Value * 100:F2}%" : "",
                    Reference = "占营业收入比重", Verdict = Verdict.Neutral, Note = note,
                });
            }
            Share("经纪业务占比", FinancialKeys.BrokerageNet, "代理买卖证券，最靠市场成交量吃饭的一块");
            Share("投行业务占比", FinancialKeys.UnderwritingNet, "证券承销，受发行节奏和政策影响大");
            Share("资管业务占比", FinancialKeys.AssetMgmtNet, "受托资产管理，收入最稳的一块，占比高是加分项");

            double selfRun = (cur.Get(FinancialKeys.InvestIncome) ?? 0) + (cur.Get(FinancialKeys.FvChangeGain) ?? 0);
            if (selfRun != 0)
                lines.Add(new AnalysisLine
                {
                    ClauseNo = 6, Label = "自营投资占比", Value = $"{selfRun / rev.Value * 100:F2}%",
                    Reference = "占营业收入比重", Verdict = selfRun / rev.Value > 0.5 ? Verdict.Warn : Verdict.Neutral,
                    Note = "投资收益＋公允价值变动。占比过半意味着业绩基本跟着市场走，"
                         + "牛市好看熊市难看，不能按稳定盈利去估值",
                });
        }

        AddCommonReturn(lines, 7, cur, prior, history, price, totalShares);

        bool hasReg = regulatory is { Count: > 0 };
        return new AnalysisSection
        {
            Title = "券商体检表",
            IsHealthCheck = true,
            Lines = lines,
            Conclusion = "→ 券商看的是「净资本够不够扛住业务和自营风险」，跟银行的资产质量、"
                       + "保险的偿付能力是三套互不通用的体系。"
                       + (hasReg
                            ? "风控指标来自年报/中报的「净资本及风险控制指标」表。"
                            : "风控指标需要跑一次 Fetcher 的【券商保险监管指标】才能补上。")
                       + "参考值全部是监管明文红线与预警线，不随周期漂移，所以不用行业分位。"
                       + MetricSources.PendingOcrNote(regulatory, cur.ReportDate),
        };
    }

    /// <summary>保险体检表。</summary>
    public static AnalysisSection BuildInsurer(
        FinancialSnapshot cur, FinancialSnapshot? prior, List<FinancialSnapshot> history,
        double? price, List<BankRegulatoryMetric>? regulatory, double? totalShares = null)
    {
        var lines = new List<AnalysisLine>();
        double? Reg(string key) => regulatory?
            .FirstOrDefault(m => m.MetricKey == key && m.ReportDate == cur.ReportDate)?.Value;

        // ── 01 偿付能力：保险的生命线 ──
        AddRatio(lines, 1, "核心偿付能力充足率", Reg(InsurerRegulatoryKeys.CoreSolvency),
            $"监管 ≥{CoreSolvencyMin:F0}%",
            v => v < CoreSolvencyMin ? Verdict.Bad : v < CoreSolvencyMin * 2 ? Verdict.Warn : Verdict.Good,
            "核心资本 ÷ 最低资本。核心资本是质量最高的那部分，低于 50% 直接触及监管红线");
        AddRatio(lines, 1, "综合偿付能力充足率", Reg(InsurerRegulatoryKeys.ComprehensiveSolvency),
            $"监管 ≥{ComprehensiveSolvencyMin:F0}%",
            v => v < ComprehensiveSolvencyMin ? Verdict.Bad : v < 150 ? Verdict.Warn : Verdict.Good,
            "实际资本 ÷ 最低资本。⚠ 集团型公司（平安/太保）年报里集团口径和各子公司口径会同时出现，"
            + "解析取的是先出现的那个（通常是集团），用的时候留意口径");

        // ── 02 承保盈利：综合成本率 100% 是分水岭 ──
        AddRatio(lines, 2, "综合成本率（财险）", Reg(InsurerRegulatoryKeys.CombinedRatio),
            $"{CombinedBreakeven:F0}% 是承保盈亏平衡线",
            v => v < 97 ? Verdict.Good : v < CombinedBreakeven ? Verdict.Neutral : Verdict.Bad,
            "综合赔付率 ＋ 综合费用率。**低于 100% 才是靠承保本身赚钱**；高于 100% 说明保费不够"
            + "覆盖赔付和费用，全靠投资收益补窟窿——那样的话利润就跟着股债市场走了");
        AddRatio(lines, 2, "车险综合成本率", Reg(InsurerRegulatoryKeys.AutoCombinedRatio),
            $"{CombinedBreakeven:F0}% 是分水岭",
            v => v < 97 ? Verdict.Good : v < CombinedBreakeven ? Verdict.Neutral : Verdict.Bad,
            "车险是财险里占比最大的险种，最能反映真实的承保和定价能力");

        // ── 03 赔付率：从利润表直接算，不依赖 PDF ──
        double? premium = cur.Get(FinancialKeys.PremiumEarned);
        double? claim = cur.Get(FinancialKeys.ClaimExpense);
        if (premium is > 0 && claim is > 0)
        {
            double ratio = claim.Value / premium.Value * 100;
            double? priorRatio = null;
            if (prior?.Get(FinancialKeys.PremiumEarned) is > 0 and var pp
                && prior.Get(FinancialKeys.ClaimExpense) is > 0 and var pc)
                priorRatio = pc / pp * 100;
            lines.Add(new AnalysisLine
            {
                ClauseNo = 3, Label = "赔付支出 / 已赚保费", Value = $"{ratio:F2}%",
                Change = priorRatio.HasValue ? $"去年同期 {priorRatio.Value:F2}%（{ratio - priorRatio.Value:+0.00;-0.00}pct）" : "",
                Reference = "越低越好，但要跟准备金变动一起看",
                Verdict = priorRatio.HasValue && ratio > priorRatio.Value + 3 ? Verdict.Warn : Verdict.Neutral,
                Note = "从利润表直接算，不依赖 PDF。⚠ 这是粗口径——没有扣掉摊回赔付、也没算准备金提转差，"
                     + "跟年报披露的综合赔付率不是一回事，只适合自比趋势",
            });
        }

        // ── 04 保费结构 ──
        double? rev2 = cur.Get(FinancialKeys.Revenue);
        if (premium is > 0 && rev2 is > 0)
            lines.Add(new AnalysisLine
            {
                ClauseNo = 4, Label = "已赚保费占营收", Value = $"{premium.Value / rev2.Value * 100:F2}%",
                Change = $"已赚保费 {premium.Value / Yi:N0} 亿",
                Reference = "占比越高，越靠承保而非投资",
                Verdict = Verdict.Neutral,
                Note = "剩下的主要是投资收益。保险公司利润有两个引擎——承保和投资，"
                     + "这个比例决定了它更像哪一种",
            });

        AddCommonReturn(lines, 5, cur, prior, history, price, totalShares);

        bool hasReg = regulatory is { Count: > 0 };
        return new AnalysisSection
        {
            Title = "保险体检表",
            IsHealthCheck = true,
            Lines = lines,
            Conclusion = "→ 保险看两件事：**赔得起吗**（偿付能力充足率）和**承保本身赚不赚钱**"
                       + "（综合成本率 100% 是分水岭）。"
                       + (hasReg ? "偿付能力与承保指标来自年报。"
                                 : "偿付能力与综合成本率需要跑一次【券商保险监管指标】才能补上。")
                       + "内含价值(EV)/新业务价值(NBV)是金额不是比率，尚未接入。"
                       + MetricSources.PendingOcrNote(regulatory, cur.ReportDate),
        };
    }

    /// <summary>一条来自监管指标表的比率行；值为空就显示"待接入"而不是省略——缺什么要让人看得见。</summary>
    private static void AddRatio(List<AnalysisLine> lines, int clause, string label, double? value,
        string reference, Func<double, Verdict> judge, string note)
    {
        lines.Add(value.HasValue
            ? new AnalysisLine
            {
                ClauseNo = clause, Label = label, Value = $"{value.Value:F2}%",
                Reference = reference, Verdict = judge(value.Value), Note = note,
            }
            : new AnalysisLine
            {
                ClauseNo = clause, Label = label, Value = "待接入",
                Reference = reference, Verdict = Verdict.Missing, Note = note,
            });
    }

    /// <summary>ROE / ROA / PB 三条通用回报指标，三类机构都要看。</summary>
    private static void AddCommonReturn(List<AnalysisLine> lines, int clause,
        FinancialSnapshot cur, FinancialSnapshot? prior, List<FinancialSnapshot> history, double? price,
        double? totalShares = null)
    {
        double? npp = cur.Get(FinancialKeys.NetProfitParent);
        double? eq = cur.Get(FinancialKeys.EquityParent);
        var prevAny = history.FirstOrDefault(h => h.ReportDate < cur.ReportDate);
        double? eqBeg = prevAny?.Get(FinancialKeys.EquityParent);

        double? roe = null;
        if (npp.HasValue && eq is > 0)
        {
            double avgEq = eqBeg is > 0 ? (eq.Value + eqBeg.Value) / 2 : eq.Value;
            roe = cur.AnnualizeCumulative(npp.Value) / avgEq * 100;
            double? roeP = null;
            if (prior?.Get(FinancialKeys.NetProfitParent) is { } nppP
                && prior.Get(FinancialKeys.EquityParent) is > 0)
                roeP = prior.AnnualizeCumulative(nppP) / prior.Get(FinancialKeys.EquityParent)!.Value * 100;
            lines.Add(new AnalysisLine
            {
                ClauseNo = clause, Label = "ROE（年化）", Value = $"{roe.Value:F2}%",
                Change = roeP.HasValue ? $"去年同期 {roeP.Value:F2}%" : "",
                Reference = "跟自己的历史比更有意义",
                Verdict = Verdict.Neutral,
                // 券商 ROE 跟着行情大起大落，拿某一年的数去外推是最常见的误判。
                Note = "券商/保险的 ROE 受市场行情影响很大，单期高低说明不了持续能力，要看多期",
            });
        }

        // 股数：优先总股本，拿不到才回退报表实收资本并标识（口径差异见 MetricKeys.TotalShares）。
        double? share = totalShares is > 0 ? totalShares : cur.Get(FinancialKeys.ShareCapital);
        bool shareIsFallback = totalShares is not > 0;
        if (price is > 0 && share is > 0 && eq is > 0)
        {
            double pb = price.Value / (eq.Value / share.Value);
            lines.Add(new AnalysisLine
            {
                ClauseNo = clause, Label = "PB", Value = $"{pb:F2}",
                Change = $"每股净资产 {eq.Value / share.Value:F2} 元"
                       + (shareIsFallback ? "　⚠ 没有总股本，按报表实收资本算" : ""),
                Reference = "券商常在 1~2 倍，保险看内含价值倍数更准",
                Verdict = Verdict.Neutral,
                Note = "股本含 H 股，A 股价格乘全部股本会略高估有 H 股的公司"
                     + (shareIsFallback ? "。⚠ 这里用的是报表实收资本（金额），面值不是 1 元的公司会算错" : ""),
            });
            if (roe is > 0)
                lines.Add(new AnalysisLine
                {
                    ClauseNo = clause, Label = "ROE / PB", Value = $"{roe.Value / pb:F1}",
                    Reference = "隐含股东回报率，越高越划算", Verdict = Verdict.Neutral,
                });
        }
    }
}
