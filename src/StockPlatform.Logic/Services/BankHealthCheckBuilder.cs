using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 银行体检表（2026-08-29 新增）——**一列值、一列参考范围**，像体检报告那样看银行。
///
/// ════ 十二条从哪来 ════
/// 起点是网上流传的"银行股七项排雷清单"（资产质量／拨备覆盖&gt;200%／核心一级&gt;12%／ROE&gt;10%／
/// ROA&gt;0.75%＋净息差／非息收入／PB×ROE）。拿 42 家 A 股上市银行实测后，七条里三条要改、
/// 另有五个维度完全没覆盖，于是补成十二条：
///
///   01 资产质量        保留（全清单最好的一条：不看单一不良率，而是不良+关注类+逾期一起看）
///   02 拨备覆盖        **改指标**：目的抓得准（防"少提拨备虚增利润"），但拨备覆盖率是存量、
///                      抓不到"少提"这个流量行为——覆盖率 250% 达标的银行照样能当期少提。
///                      真正抓得到的是"信用减值损失同比"和"信用减值÷营业收入"，这两个能算。
///                      另外原清单只防少提、没防反向的多提藏利润（&gt;350% 是利润蓄水池）。
///   03 核心一级资本    **阈值过苛**：行业平均 10.55%、超过 13% 的只有 6 家、监管红线才 7.5%。
///                      且"防弹衣够厚"只对一半——贵阳银行核心一级&gt;13% 而 ROE 仅 7.71%、PB 0.30，
///                      它的资本厚是因为资产放不出去，是经营乏力的症状而非安全的证明。
///   04 ROE            **阈值过时**：净息差从约 2.2% 降到 1.37%，行业 ROE 中位数只剩 9.07%，
///                      10% 这条线把工建农中交邮六大行全部排除。改用行业分位。
///   05 ROA ＋ 净息差   保留，全清单最聪明的设计：ROE/ROA 交叉验证能抓出"杠杆撑起来的假 ROE"
///                      （长沙银行 ROE 10.26%✓ 但 ROA 0.678%✗）。反向也有用——建行/沪农商行/
///                      张家港行 ROE 不达标而 ROA 达标，是低杠杆经营被 ROE 单指标冤枉。
///   06 非息收入        **需细化**：必须拆"手续费及佣金"(可持续) vs "投资收益+公允价值"(靠债市
///                      行情、不可持续)，合并看等于没看。
///   07 PB × ROE        保留，实操化为 ROE/PB 排序（民生 PB 0.26 全场最便宜但 ROE 4.59%，
///                      正确地被排除）。
///   08 贷款结构集中度  **新增**：银行真正的雷全在这里（房地产/城投敞口、区域集中、大客户集中），
///                      原清单一条没覆盖。郑州银行 ROE 3.46% 是结果，事前信号是区域和地产敞口。
///   09 趋势方向        **新增**：原七条全是静态截面，抓不到"正在恶化"的。招行是活例子——
///                      不良率 0.94% 纹丝不动，关注类迁徙率却从 28.66% 飙到 39.94%。
///   10 股息率与可持续  **新增**：原清单完全没有股息率。它是"选好银行"的框架，不是"选好底仓"的
///                      框架，对吃分红的底仓策略这一条应排在 ROE 前面。
///   11 成本收入比      **新增**：ROA 低不一定是息差低，也可能是成本高——邮储净息差在大行里不算低，
///                      ROA 却是全行业最低(0.49%)，差在成本收入比。
///   12 报表可信度      **新增**：前十一条默认"财报数字是真的"，但银行最大的操纵空间恰恰在 01/02
///                      本身。要靠逾期90天+/不良、核销规模、关注类迁徙率互相印证。
///
/// ════ 哪些现在算得出 ════
/// 01/03/08 的原始数据只在财报 PDF 附注里（新浪三表接口取不到），12 依赖它们，所以这四条显示
/// "待接入"而不是拿别的指标凑——报告里如实标出来，比假装能算重要。
/// 其余八条从利润表/资产负债表能算，其中成本收入比和非息拆分是这次补上银行科目映射才有的
/// （见 <see cref="FinancialKeys.InterestNet"/>）。
///
/// ════ 参考值的两类阈值 ════
/// 结构性的写死在本类常量里（成本收入比 30/35/45——不随周期漂移）；相对性的走
/// <see cref="BankPeerStats"/> 的行业分位（ROE/ROA/净息差/PB——钉死就会重蹈 ROE&gt;10% 的覆辙）。
/// </summary>
public static class BankHealthCheckBuilder
{
    // ── 结构性阈值：不随行业周期漂移，可以写死 ──

    /// <summary>成本收入比分档（%）。&lt;30 优秀、30~35 良好、35~45 一般、&gt;45 偏高。
    /// 银行业惯用口径，多年稳定；招行 2026H1 是 29.70%。</summary>
    private const double CostIncomeGood = 30, CostIncomeFair = 35, CostIncomeBad = 45;

    /// <summary>拨备覆盖率上限——超过这个数就不是"更安全"，而是把利润存进蓄水池留待以后释放，
    /// 财政部 2019 年专门点名过。下限 150% 是监管红线，200% 是原清单门槛。</summary>
    private const double ProvisionUpper = 350;

    /// <summary>利息净收入占营业收入多少以上算银行（区别于同样有利息净收入的券商，
    /// 券商这一项通常不到 15%）。</summary>
    public const double BankInterestShareThreshold = 0.40;

    private const double Yi = 1e8;

    /// <summary>
    /// 判定机构类型。只看科目、不查行业表——行业表覆盖率不满，漏判会让整份报告显示一堆 n/a。
    /// </summary>
    public static FinancialInstitutionKind ClassifyInstitution(FinancialSnapshot cur)
    {
        // 有"营业成本"就是工商企业。银行/券商/保险的利润表里没有这一行。
        if (cur.Get(FinancialKeys.OperCost) is > 0) return FinancialInstitutionKind.NonFinancial;

        // 判定顺序按"特征科目的排他性"排：已赚保费只有保险有、代理买卖证券只有券商有，
        // 这两个一命中就能定性；利息净收入三类都有（券商做两融、保险有存款利息），
        // 所以银行必须靠**占比**而不是"有没有"来判——实测中信证券利息净收入只占营收 3.5%，
        // 而银行在 60% 上下。
        double? rev = cur.Get(FinancialKeys.Revenue);

        if (cur.Get(FinancialKeys.PremiumEarned) is > 0)
            return FinancialInstitutionKind.Insurer;

        if (cur.Get(FinancialKeys.BrokerageNet) is > 0
            || cur.Get(FinancialKeys.UnderwritingNet) is > 0
            || cur.Get(FinancialKeys.AssetMgmtNet) is > 0)
            return FinancialInstitutionKind.Broker;

        double? ii = cur.Get(FinancialKeys.InterestNet);
        if (rev is > 0 && ii is > 0 && ii.Value / rev.Value >= BankInterestShareThreshold)
            return FinancialInstitutionKind.Bank;

        // 没有 v4 特征科目的老数据落到这里——安全降级：宁可少出一张体检表，
        // 也不能把券商当银行体检。重抓后会自动归位。
        return FinancialInstitutionKind.OtherFinancial;
    }

    /// <summary>
    /// 生成体检表。<paramref name="peers"/> 提供相对性指标的参考分位；
    /// <paramref name="price"/>/<paramref name="dps"/> 缺失时相关行自动跳过。
    /// </summary>
    public static AnalysisSection Build(
        FinancialSnapshot cur, FinancialSnapshot? prior, List<FinancialSnapshot> history,
        BankPeerStats peers, double? price, double? dps,
        List<BankRegulatoryMetric>? regulatory = null, double? totalShares = null)
    {
        var lines = new List<AnalysisLine>();
        double? G(FinancialSnapshot? s, string k) => s?.Get(k);

        double? rev = G(cur, FinancialKeys.Revenue), revP = G(prior, FinancialKeys.Revenue);

        // 从 PDF 解析出的监管指标里取本期/去年同期的值。报告期严格对齐——拿去年的不良率当本期
        // 用比不显示更糟。
        BankRegulatoryMetric? Reg(string key, string basis = "") =>
            regulatory?.FirstOrDefault(m => m.MetricKey == key && m.Basis == basis
                                            && m.ReportDate == cur.ReportDate);
        double? RegPrior(string key, string basis = "") =>
            prior == null ? null
            : regulatory?.FirstOrDefault(m => m.MetricKey == key && m.Basis == basis
                                              && m.ReportDate == prior.ReportDate)?.Value;

        // ── 01 资产质量 ──
        var npl = Reg(BankRegulatoryKeys.NplRatio);
        if (npl != null)
        {
            double? nplP = RegPrior(BankRegulatoryKeys.NplRatio);
            var mig = Reg(BankRegulatoryKeys.SpecialMentionMigration);
            double? migP = RegPrior(BankRegulatoryKeys.SpecialMentionMigration);
            lines.Add(new AnalysisLine
            {
                ClauseNo = 1, Label = "不良贷款率", Value = $"{npl.Value:F2}%",
                Change = nplP.HasValue ? $"去年同期 {nplP.Value:F2}%（{npl.Value - nplP.Value:+0.00;-0.00}pct）" : "",
                Reference = $"行业加权平均 {peers.NplRatioAvg:F2}%",
                Verdict = npl.Value <= peers.NplRatioAvg ? Verdict.Good : Verdict.Warn,
                Note = "别只看这一个数——它能靠展期、重组、核销腾挪，要跟下面的迁徙率一起读",
            });
            if (Reg(BankRegulatoryKeys.LoanProvisionRatio) is { } lpr)
                lines.Add(new AnalysisLine
                {
                    ClauseNo = 1, Label = "贷款拨备率", Value = $"{lpr.Value:F2}%",
                    Reference = "监管要求 ≥2.5%", Verdict = lpr.Value >= 2.5 ? Verdict.Good : Verdict.Warn,
                });
            if (mig != null)
                lines.Add(new AnalysisLine
                {
                    ClauseNo = 1, Label = "关注类贷款迁徙率", Value = $"{mig.Value:F2}%",
                    Change = migP.HasValue ? $"去年同期 {migP.Value:F2}%（{mig.Value - migP.Value:+0.00;-0.00}pct）" : "",
                    Reference = "越低越好，看变化方向",
                    // 这一项才是"正在恶化"的探针：招行不良率 0.94% 两年没动，迁徙率却从 28.66%
                    // 涨到 39.94%——压力在积累，只是还没体现到不良率上。
                    Verdict = migP.HasValue && mig.Value > migP.Value + 5 ? Verdict.Warn : Verdict.Neutral,
                    Note = migP.HasValue && mig.Value > migP.Value + 5
                        ? "⚠ 关注类转不良的比例明显上升——不良率还没动，但压力在积累"
                        : "关注类里有多大比例掉进不良，比不良率本身更早反映资产质量变化",
                });
        }
        else
        {
            lines.Add(NoData(1, "资产质量", "不良率 / 关注类 / 逾期",
                $"行业加权平均不良率 {peers.NplRatioAvg:F2}%",
                "全清单最该保留的一条：不良率能靠展期、重组、核销腾挪，关注类和逾期是先行指标。"
                + "跑 Fetcher 的【银行监管指标】可以补上这一条。"));
        }

        // ── 02 拨备：存量(拨备覆盖率)来自 PDF，流量(信用减值损失)从利润表倒推 ──
        var pc = Reg(BankRegulatoryKeys.ProvisionCoverage);
        if (pc != null)
        {
            double? pcP = RegPrior(BankRegulatoryKeys.ProvisionCoverage);
            lines.Add(new AnalysisLine
            {
                ClauseNo = 2, Label = "拨备覆盖率", Value = $"{pc.Value:F2}%",
                Change = pcP.HasValue ? $"去年同期 {pcP.Value:F2}%（{pc.Value - pcP.Value:+0.00;-0.00}pct）" : "",
                Reference = $"行业均值 {peers.ProvisionCoverageAvg:F2}%　下限 200　上限 {ProvisionUpper:F0}",
                Verdict = pc.Value < 200 ? Verdict.Bad
                        : pc.Value > ProvisionUpper ? Verdict.Warn : Verdict.Good,
                Note = pc.Value > ProvisionUpper
                    ? $"超过 {ProvisionUpper:F0}%——这不是更安全，而是把利润存进蓄水池留待以后释放，同样是失真"
                    : pc.Value < 200 ? "低于常用门槛 200%，安全垫偏薄" : "",
            });
            if (Reg(BankRegulatoryKeys.CreditCost) is { } cc)
                lines.Add(new AnalysisLine
                {
                    ClauseNo = 2, Label = "信用成本（年化）", Value = $"{cc.Value:F2}%",
                    Reference = "计提力度，越高越审慎", Verdict = Verdict.Neutral,
                });
        }
        else
        {
            lines.Add(NoData(2, "拨备覆盖", "拨备覆盖率",
                $"行业均值 {peers.ProvisionCoverageAvg:F2}%　下限 200%　上限 {ProvisionUpper:F0}%",
                $"200% 只是略低于行业均值、区分度弱；超过 {ProvisionUpper:F0}% 则是把利润存进蓄水池"
                + "留待以后释放，同样失真，只是方向相反。跑【银行监管指标】可以补上。"));
        }

        // 信用减值损失 = 营业支出 − 营业税金及附加 − 业务及管理费用 − 其他业务支出。
        // 新浪的银行利润表没有单列"信用减值损失"行（"资产减值损失"那行恒为 0），只能这样倒推。
        double? imp = CreditImpairment(cur), impP = CreditImpairment(prior);
        if (imp.HasValue)
        {
            double? yoy = impP is > 0 ? (imp.Value / impP.Value - 1) * 100 : null;
            lines.Add(new AnalysisLine
            {
                ClauseNo = 2, Label = "信用减值损失（倒推）",
                Value = $"{imp.Value / Yi:N2} 亿",
                Change = yoy.HasValue ? $"同比 {yoy.Value:+0.0;-0.0}%" : "",
                Reference = "同比为负 = 少提嫌疑",
                // 这一行才是原清单"少提拨备虚增利润的整容脸"真正抓得到的地方。
                Verdict = yoy is < 0 ? Verdict.Warn : Verdict.Good,
                Note = yoy is < 0
                    ? "当期计提比去年少——利润里可能有一部分是靠少提拨备腾出来的，要结合净利增速一起看"
                    : yoy.HasValue ? "在多提拨备，利润是压着报的，不是靠释放拨备撑出来的" : "",
            });
            if (rev is > 0)
                lines.Add(new AnalysisLine
                {
                    ClauseNo = 2, Label = "信用减值 / 营业收入",
                    Value = $"{imp.Value / rev.Value * 100:F2}%",
                    Change = impP is > 0 && revP is > 0 ? $"去年 {impP.Value / revP.Value * 100:F2}%" : "",
                    Reference = "越高越审慎",
                    Verdict = Verdict.Neutral,
                });
        }

        // ── 03 核心一级资本充足率 ──
        // 横向比较**必须用权重法**：只有六大行+招行等少数获批高级法，两个口径能差 2pct 以上
        // （招行 14.07% vs 11.84%），混着比会把行业分位算歪。
        var ct1 = Reg(BankRegulatoryKeys.CoreTier1Car, BankRegulatoryKeys.BasisWeighted)
                  ?? Reg(BankRegulatoryKeys.CoreTier1Car, BankRegulatoryKeys.BasisAdvanced);
        if (ct1 != null)
        {
            bool advanced = ct1.Basis == BankRegulatoryKeys.BasisAdvanced;
            var other = advanced ? null : Reg(BankRegulatoryKeys.CoreTier1Car, BankRegulatoryKeys.BasisAdvanced);
            lines.Add(new AnalysisLine
            {
                ClauseNo = 3, Label = "核心一级资本充足率", Value = $"{ct1.Value:F2}%",
                Change = advanced ? "高级法（无权重法数据）"
                       : other != null ? $"权重法；高级法 {other.Value:F2}%" : "权重法",
                Reference = $"行业均值 {peers.CoreTier1Avg:F2}%　红线 7.5%（系统重要性行 8.5~9%）",
                Verdict = ct1.Value >= peers.CoreTier1Avg ? Verdict.Good
                        : ct1.Value >= 9.0 ? Verdict.Neutral : Verdict.Warn,
                Note = "别用 12% 一刀切——行业均值才 10.55%，超过 13% 的只有 6 家。而且资本厚有两种"
                     + "相反成因：盈利强内生补充快（招行），或风险偏好低、资产放不出去（贵阳银行"
                     + "核心一级>13% 而 ROE 仅 7.71%）。要跟上面的 ROE 一起读",
            });
            if (Reg(BankRegulatoryKeys.TotalCar, ct1.Basis) is { } tc)
                lines.Add(new AnalysisLine
                {
                    ClauseNo = 3, Label = "资本充足率", Value = $"{tc.Value:F2}%",
                    Change = advanced ? "高级法" : "权重法",
                    Reference = "监管红线 10.5%",
                    Verdict = tc.Value >= 10.5 ? Verdict.Good : Verdict.Bad,
                });
        }
        else
        {
            lines.Add(NoData(3, "核心一级资本", "核心一级资本充足率",
                $"行业均值 {peers.CoreTier1Avg:F2}%　监管红线 7.5%（系统重要性行 8.5~9%）",
                "原清单要求 12% 明显过苛（超过 13% 的只有 6 家）。而且高资本充足率有两种相反成因："
                + "盈利强、内生补充快（招行），或风险偏好低、资产放不出去（贵阳银行核心一级>13% 而 "
                + "ROE 仅 7.71%）。跑【银行监管指标】可以补上这一条。"));
        }

        // ── 04 ROE：相对性指标，参考值走行业分位 ──
        double? npp = G(cur, FinancialKeys.NetProfitParent);
        double? eq = G(cur, FinancialKeys.EquityParent);
        var prevAny = history.FirstOrDefault(h => h.ReportDate < cur.ReportDate);
        double? eqBeg = prevAny?.Get(FinancialKeys.EquityParent);
        double? roe = null;
        if (npp.HasValue && eq is > 0)
        {
            double avgEq = eqBeg is > 0 ? (eq.Value + eqBeg.Value) / 2 : eq.Value;
            roe = cur.AnnualizeCumulative(npp.Value) / avgEq * 100;
            double? roeP = null;
            if (G(prior, FinancialKeys.NetProfitParent) is { } nppP && G(prior, FinancialKeys.EquityParent) is > 0)
                roeP = prior!.AnnualizeCumulative(nppP) / G(prior, FinancialKeys.EquityParent)!.Value * 100;
            lines.Add(new AnalysisLine
            {
                ClauseNo = 4, Label = "ROE（年化）", Value = $"{roe.Value:F2}%",
                Change = roeP.HasValue ? $"去年同期 {roeP.Value:F2}%" : "",
                Reference = $"行业中位 {peers.RoeMedian:F2}　75分位 {peers.RoeP75:F2}",
                Verdict = roe >= peers.RoeP75 ? Verdict.Good
                        : roe >= peers.RoeMedian ? Verdict.Neutral : Verdict.Warn,
                Note = roe >= peers.RoeP75 ? "优于行业 75 分位"
                     : roe >= peers.RoeMedian ? "在行业中位以上"
                     : "低于行业中位。注意别用固定的 10% 去卡——那条线现在会把六大行全部排除",
            });
        }

        // ── 05 ROA + 净息差：ROE/ROA 交叉验证是全清单最有价值的设计 ──
        double? ni = G(cur, FinancialKeys.NetProfit);
        double? assets = G(cur, FinancialKeys.TotalAssets);
        double? assetsBeg = prevAny?.Get(FinancialKeys.TotalAssets);
        double? roa = null;
        if (ni.HasValue && assets is > 0)
        {
            double avgA = assetsBeg is > 0 ? (assets.Value + assetsBeg.Value) / 2 : assets.Value;
            roa = cur.AnnualizeCumulative(ni.Value) / avgA * 100;
            // 交叉验证：ROE 达标而 ROA 不达标 = 高杠杆撑起来的 ROE；反之是低杠杆被 ROE 冤枉。
            string note = (roe.HasValue, roa >= peers.RoaMedian) switch
            {
                (true, false) when roe >= peers.RoeP75 =>
                    "⚠ ROE 优于行业但 ROA 落后——这个 ROE 是靠高杠杆撑起来的，不是资产效率",
                (true, true) when roe < peers.RoeMedian =>
                    "ROE 低而 ROA 不差——低杠杆经营，ROE 被资本充足率埋没了，质量其实不差",
                _ => "",
            };
            lines.Add(new AnalysisLine
            {
                ClauseNo = 5, Label = "ROA（年化）", Value = $"{roa.Value:F3}%",
                Reference = $"行业中位 {peers.RoaMedian:F3}　75分位 {peers.RoaP75:F3}",
                Verdict = roa >= peers.RoaP75 ? Verdict.Good
                        : roa >= peers.RoaMedian ? Verdict.Neutral : Verdict.Warn,
                Note = note,
            });
        }

        // 净息差优先用财报披露的官方值（分母是生息资产）；没有 PDF 数据时才退回
        // "利息净收入÷平均总资产"的近似——分母偏大，结果系统性偏低约 0.15pct。
        double? ii = G(cur, FinancialKeys.InterestNet);
        var officialNim = Reg(BankRegulatoryKeys.Nim);
        if (officialNim != null)
        {
            double? nimP = RegPrior(BankRegulatoryKeys.Nim);
            lines.Add(new AnalysisLine
            {
                ClauseNo = 5, Label = "净息差（披露）", Value = $"{officialNim.Value:F2}%",
                Change = nimP.HasValue ? $"去年同期 {nimP.Value:F2}%（{officialNim.Value - nimP.Value:+0.00;-0.00}pct）" : "",
                Reference = $"行业 {peers.NimAvg:F2}%（连续六年下降）",
                Verdict = officialNim.Value >= peers.NimAvg ? Verdict.Good : Verdict.Warn,
                Note = "官方口径（分母为生息资产平均余额）。全行业息差从 2019 年约 2.2% 降到现在 1.37%，"
                     + "这正是固定 ROE 阈值失效的根源",
            });
        }
        else if (ii is > 0 && assets is > 0)
        {
            double avgA = assetsBeg is > 0 ? (assets.Value + assetsBeg.Value) / 2 : assets.Value;
            double nim = cur.AnnualizeCumulative(ii.Value) / avgA * 100;
            lines.Add(new AnalysisLine
            {
                ClauseNo = 5, Label = "净息差（近似）", Value = $"{nim:F2}%",
                Reference = peers.IsLive ? $"行业中位 {peers.NimAvg:F2}（同口径）" : $"行业 {peers.NimAvg:F2}（官方口径）",
                Verdict = Verdict.Neutral,
                // 口径差异必须说清楚，否则用户拿它跟年报披露的净利息收益率对不上会以为算错了。
                Note = "分母用平均总资产近似生息资产，比年报披露的净利息收益率低约 0.15pct。"
                     + "跑【银行监管指标】后这里会换成财报披露的官方值",
            });
        }

        // ── 06 非息收入：必须拆开，合并看等于没看 ──
        double? fee = G(cur, FinancialKeys.FeeCommissionNet);
        double? inv = G(cur, FinancialKeys.InvestIncome), fv = G(cur, FinancialKeys.FvChangeGain);
        if (rev is > 0)
        {
            if (ii is > 0)
                lines.Add(new AnalysisLine
                {
                    ClauseNo = 6, Label = "利息净收入占比", Value = $"{ii.Value / rev.Value * 100:F2}%",
                    Change = G(prior, FinancialKeys.InterestNet) is { } iiP && revP is > 0
                        ? $"去年 {iiP / revP.Value * 100:F2}%" : "",
                    Reference = "银行主营，占比越稳越好", Verdict = Verdict.Neutral,
                });
            if (fee is > 0)
            {
                double? feeP = G(prior, FinancialKeys.FeeCommissionNet);
                double? feeYoy = feeP is > 0 ? (fee.Value / feeP.Value - 1) * 100 : null;
                lines.Add(new AnalysisLine
                {
                    ClauseNo = 6, Label = "手续费及佣金净收入占比（可持续）",
                    Value = $"{fee.Value / rev.Value * 100:F2}%",
                    Change = feeYoy.HasValue ? $"同比 {feeYoy.Value:+0.0;-0.0}%" : "",
                    Reference = "越高越好",
                    Verdict = feeYoy is >= 0 ? Verdict.Good : Verdict.Warn,
                    Note = "非息收入里真正代表客户经营能力的部分",
                });
            }
            double nonSust = (inv ?? 0) + (fv ?? 0);
            if (nonSust != 0)
            {
                double nonSustP = (G(prior, FinancialKeys.InvestIncome) ?? 0) + (G(prior, FinancialKeys.FvChangeGain) ?? 0);
                double? yoy = nonSustP > 0 ? (nonSust / nonSustP - 1) * 100 : null;
                lines.Add(new AnalysisLine
                {
                    ClauseNo = 6, Label = "投资收益＋公允价值占比（不可持续）",
                    Value = $"{nonSust / rev.Value * 100:F2}%",
                    Change = yoy.HasValue ? $"同比 {yoy.Value:+0.0;-0.0}%" : "",
                    Reference = "越低越稳",
                    Verdict = nonSust / rev.Value > 0.20 ? Verdict.Warn : Verdict.Neutral,
                    Note = "靠债市行情吃饭的部分，行情一回调就没了。2024–2025 很多银行靠它撑利润",
                });
            }
        }

        // ── 07 PB × ROE：便宜必须有质量 ──
        // 股数：优先总股本，拿不到才回退报表实收资本并标识（口径差异见 MetricKeys.TotalShares）。
        double? share = totalShares is > 0 ? totalShares : G(cur, FinancialKeys.ShareCapital);
        bool shareIsFallback = totalShares is not > 0;
        if (price is > 0 && share is > 0 && eq is > 0)
        {
            double pb = price.Value / (eq.Value / share.Value);
            lines.Add(new AnalysisLine
            {
                ClauseNo = 7, Label = "PB", Value = $"{pb:F2}",
                Change = $"每股净资产 {eq.Value / share.Value:F2} 元"
                       + (shareIsFallback ? "　⚠ 没有总股本，按报表实收资本算" : ""),
                Reference = "全行业普遍破净（中位约 0.54）",
                Verdict = Verdict.Neutral,
                Note = "股本含 H 股，A 股价格乘全部股本会略高估有 H 股的银行"
                     + (shareIsFallback ? "。⚠ 这里用的是报表实收资本（金额），面值不是 1 元的银行会算错" : ""),
            });
            if (roe is > 0)
                lines.Add(new AnalysisLine
                {
                    ClauseNo = 7, Label = "ROE / PB（隐含股东回报率）", Value = $"{roe.Value / pb:F1}",
                    Reference = "行业区间约 10~23，越高越划算",
                    Verdict = Verdict.Neutral,
                    Note = "低 PB 必须匹配低 ROE 才合理。民生银行 PB 0.26 全场最便宜，但 ROE 只有 4.59%——便宜有便宜的道理",
                });
        }

        // ── 08 贷款结构与集中度 ──
        // 参考值直接用报表自带的"标准值"列（≤10 / ≥25）——监管自己就是按"值 + 标准值"两列
        // 披露的，这是体检表参考值最权威的来源，比自己拍一个数强。
        var top1 = Reg(BankRegulatoryKeys.Top1LoanRatio);
        var top10 = Reg(BankRegulatoryKeys.Top10LoanRatio);
        if (top1 != null || top10 != null)
        {
            if (top1 != null)
                lines.Add(new AnalysisLine
                {
                    ClauseNo = 8, Label = "单一最大客户贷款比例", Value = $"{top1.Value:F2}%",
                    Reference = string.IsNullOrEmpty(top1.StandardValue) ? "监管标准 ≤10" : $"监管标准 {top1.StandardValue}",
                    Verdict = top1.Value <= 10 ? Verdict.Good : Verdict.Bad,
                });
            if (top10 != null)
                lines.Add(new AnalysisLine
                {
                    ClauseNo = 8, Label = "前十大客户贷款比例", Value = $"{top10.Value:F2}%",
                    Reference = "越分散越好", Verdict = top10.Value <= 30 ? Verdict.Good : Verdict.Warn,
                });
            if (Reg(BankRegulatoryKeys.Lcr) is { } lcr)
                lines.Add(new AnalysisLine
                {
                    ClauseNo = 8, Label = "流动性覆盖率", Value = $"{lcr.Value:F2}%",
                    Reference = string.IsNullOrEmpty(lcr.StandardValue) ? "监管标准 ≥100" : $"监管标准 {lcr.StandardValue}",
                    Verdict = lcr.Value >= 100 ? Verdict.Good : Verdict.Bad,
                });
            lines.Add(new AnalysisLine
            {
                ClauseNo = 8, Label = "房地产 / 城投敞口", Value = "待接入",
                Reference = "行业口径不统一", Verdict = Verdict.Missing,
                Note = "客户集中度已有，但分行业/分区域的敞口拆分各行披露口径不一，暂未解析",
            });
        }
        else
        {
            lines.Add(NoData(8, "贷款结构与集中度", "房地产/城投敞口、区域集中度、前十大客户占比",
                "单一最大客户 ≤10%（监管标准值）",
                "银行真正的雷全在这里，原七项一条没覆盖。郑州银行 ROE 3.46% 是结果，"
                + "事前信号是它的区域经济和地产敞口。跑【银行监管指标】可以补上客户集中度。"));
        }

        // ── 09 趋势方向：静态截面抓不到"正在恶化" ──
        if (roe.HasValue && prior != null)
        {
            var dirs = new List<string>();
            if (G(prior, FinancialKeys.NetProfitParent) is { } nppP2 && G(prior, FinancialKeys.EquityParent) is > 0)
            {
                double roeP2 = prior.AnnualizeCumulative(nppP2) / G(prior, FinancialKeys.EquityParent)!.Value * 100;
                dirs.Add($"ROE {(roe.Value >= roeP2 ? "↑" : "↓")}");
            }
            double? cirCur = CostIncomeRatio(cur), cirPrior = CostIncomeRatio(prior);
            if (cirCur.HasValue && cirPrior.HasValue)
                dirs.Add($"成本收入比 {(cirCur.Value <= cirPrior.Value ? "↓改善" : "↑恶化")}");
            if (imp.HasValue && impP is > 0)
                dirs.Add($"信用减值计提 {(imp.Value >= impP.Value ? "↑" : "↓")}");
            if (dirs.Count > 0)
                lines.Add(new AnalysisLine
                {
                    ClauseNo = 9, Label = "同比方向", Value = string.Join("　", dirs),
                    Reference = "看连续 4 个季度才算趋势",
                    Verdict = Verdict.Neutral,
                    Note = "完整的趋势项还需要不良率、关注类迁徙率、拨备覆盖率的连续变化——那几项要等 PDF 数据接入。"
                         + "招行是活例子：不良率 0.94% 纹丝不动，关注类迁徙率却从 28.66% 飙到 39.94%",
                });
        }

        // ── 10 股息率：原清单完全没有，但对底仓策略这是第一位的 ──
        if (dps is > 0 && price is > 0)
        {
            double dy = dps.Value / price.Value * 100;
            lines.Add(new AnalysisLine
            {
                ClauseNo = 10, Label = "股息率", Value = $"{dy:F2}%",
                Reference = "底仓法门槛 4.5%",
                Verdict = dy >= 4.5 ? Verdict.Good : Verdict.Neutral,
                Note = dy >= 4.5
                    ? "达到底仓法门槛"
                    : "不到底仓门槛（可以是成长/波段标的）。⚠ 股息率取最近完整年度，用滚动12个月会把两个年度的分红算进同一窗口、虚高近一倍",
            });
        }

        // ── 11 成本收入比：结构性阈值，写死 ──
        double? cir = CostIncomeRatio(cur);
        if (cir.HasValue)
        {
            double? cirP = CostIncomeRatio(prior);
            lines.Add(new AnalysisLine
            {
                ClauseNo = 11, Label = "成本收入比", Value = $"{cir.Value:F2}%",
                Change = cirP.HasValue ? $"去年同期 {cirP.Value:F2}%（{cir.Value - cirP.Value:+0.00;-0.00}pct）" : "",
                Reference = $"<{CostIncomeGood:F0} 优秀　{CostIncomeGood:F0}~{CostIncomeFair:F0} 良好　"
                          + $"{CostIncomeFair:F0}~{CostIncomeBad:F0} 一般　>{CostIncomeBad:F0} 偏高",
                Verdict = cir < CostIncomeGood ? Verdict.Good
                        : cir < CostIncomeFair ? Verdict.Neutral
                        : cir < CostIncomeBad ? Verdict.Warn : Verdict.Bad,
                Note = "业务及管理费用 ÷ 营业收入。ROA 低不一定是息差低——邮储净息差在大行里不算低，"
                     + "ROA 却是全行业最低(0.49%)，差就差在这一项",
            });
        }

        // ── 12 报表可信度交叉验证 ──
        // 有 PDF 数据时能做一件很有价值的事：拿披露的成本收入比跟**我们从利润表倒推的**对一遍。
        // 两者本该完全相等（招行 2026H1 都是 29.70%），对不上就说明科目映射或倒推口径有问题——
        // 这既验证了报表内部一致性，也验证了本程序自己的解析。
        var pdfCir = Reg(BankRegulatoryKeys.CostIncomeRatio);
        if (pdfCir != null && cir.HasValue)
        {
            double diff = Math.Abs(pdfCir.Value - cir.Value);
            lines.Add(new AnalysisLine
            {
                ClauseNo = 12, Label = "成本收入比 交叉校验",
                Value = $"披露 {pdfCir.Value:F2}% / 倒推 {cir.Value:F2}%",
                Reference = "两者应当相等",
                Verdict = diff < 0.05 ? Verdict.Good : Verdict.Warn,
                Note = diff < 0.05
                    ? "财报披露值与从利润表倒推的值一致——科目映射和倒推口径都正确"
                    : "⚠ 对不上。可能是科目映射取错了行，或该行披露口径与通用定义不同",
            });
        }
        if (Reg(BankRegulatoryKeys.NormalMigration) is { } nm
            && Reg(BankRegulatoryKeys.SpecialMentionMigration) is { } smm)
        {
            lines.Add(new AnalysisLine
            {
                ClauseNo = 12, Label = "迁徙率链条", Value = $"正常 {nm.Value:F2}% → 关注 {smm.Value:F2}%",
                Reference = "看逐级恶化的速度",
                Verdict = Verdict.Neutral,
                Note = "正常类迁徙率低而关注类迁徙率高，说明新增问题不多、但存量问题在加速劣化",
            });
        }
        lines.Add(new AnalysisLine
        {
            ClauseNo = 12, Label = "逾期90天+/不良、核销规模", Value = "待接入",
            Reference = "偏离度 >100% = 认定不严", Verdict = Verdict.Missing,
            Note = "前十一条默认「财报数字是真的」，但银行最大的操纵空间恰恰在不良认定和拨备计提上——"
                 + "也就是第 01、02 条本身。这两个数在附注的贷款质量分析表里，尚未解析。",
        });

        int have = lines.Count(l => l.Verdict != Verdict.Missing);
        int missing = lines.Count(l => l.Verdict == Verdict.Missing);
        // 十二条里哪几条真正有数据，取决于监管指标有没有抓过——文案必须跟着实际情况走，
        // 否则会出现"明明显示了不良率、结论里还说它取不到"这种自相矛盾。
        var clausesWithData = lines.Where(l => l.Verdict != Verdict.Missing)
                                   .Select(l => l.ClauseNo).Distinct().Count();
        bool hasRegulatory = regulatory is { Count: > 0 };
        string source = hasRegulatory
            ? "01/02/03/08 来自财报 PDF（Fetcher 的【银行监管指标】），其余来自三张报表"
            : "01/02/03/08 需要跑一次 Fetcher 的【银行监管指标】才能补上——那几项在财报 PDF 正文里，三表接口取不到";
        string peerNote = peers.IsLive
            ? $"参考值为本地库当期实算（{peers.SampleSize} 家银行，{peers.AsOf:yyyy-MM-dd}）"
            : $"参考值为内置基准（{peers.AsOf:yyyy 年第一季度} 42 家上市银行实测），非当期实算";

        return new AnalysisSection
        {
            Title = "银行体检表（十二条）",
            IsHealthCheck = true,
            Lines = lines,
            Conclusion = $"→ 十二条覆盖到 {clausesWithData} 条，共 {have} 项有数据"
                       + (missing > 0 ? $"、{missing} 项待接入" : "")
                       + $"。{source}。{peerNote}。"
                       + MetricSources.PendingOcrNote(regulatory, cur.ReportDate),
        };
    }

    /// <summary>
    /// 信用减值损失 = 营业支出 − 营业税金及附加 − 业务及管理费用 − 其他业务支出。
    /// 新浪的银行利润表不单列这一行（"资产减值损失"恒为 0），只能倒推。
    /// 招行 2026H1 倒推得 291.91 亿；同法算出的成本收入比 29.70%、利息净收入占比 62.87%
    /// 与年报披露值完全一致，可以佐证倒推口径正确。
    /// </summary>
    private static double? CreditImpairment(FinancialSnapshot? s)
    {
        if (s == null) return null;
        double? opx = s.Get(FinancialKeys.TotalCost);
        if (opx is not > 0) return null;
        double tax = s.Get(FinancialKeys.TaxSurcharge) ?? 0;
        double adm = s.Get(FinancialKeys.AdminExpense) ?? 0;
        double oth = s.Get(FinancialKeys.OtherOperExpense) ?? 0;
        if (adm <= 0) return null;                 // 业管费缺失说明科目没抓全，倒推不可靠
        double v = opx.Value - tax - adm - oth;
        return v > 0 ? v : null;
    }

    /// <summary>成本收入比 = 业务及管理费用 ÷ 营业收入（%）。</summary>
    private static double? CostIncomeRatio(FinancialSnapshot? s)
    {
        if (s == null) return null;
        double? adm = s.Get(FinancialKeys.AdminExpense), rev = s.Get(FinancialKeys.Revenue);
        return adm is > 0 && rev is > 0 ? adm.Value / rev.Value * 100 : null;
    }

    /// <summary>"待接入"的一行——如实标出没有数据，而不是拿别的指标凑。</summary>
    private static AnalysisLine NoData(int clause, string clauseName, string label, string reference, string note) =>
        new()
        {
            ClauseNo = clause, Label = label, Value = "待接入",
            Change = clauseName, Reference = reference,
            Verdict = Verdict.Missing, Note = note,
        };
}
