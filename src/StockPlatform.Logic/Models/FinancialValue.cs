namespace StockPlatform.Logic.Models;

/// <summary>
/// FinancialReport 表里 <c>metric_key</c> 的规范键。
///
/// 2026-08-27 从 8 个扩到 52 个（用户要求"本地保存定期分析数据，下次只分析最新的"）。为什么这次
/// 扩充几乎没有成本：<see cref="Abstractions.IFinancialProvider"/> 的新浪实现抓的是
/// <c>vDOWN_{报表}/ctrl/all.phtml</c>——**一个请求返回该股上市以来所有报告期的整张报表**，原来
/// 读完只抽 8 行、其余全扔。加科目只是多留几行，不多发一个请求、不改表结构（FinancialReport 是
/// <c>(code, report_date, metric_key, value)</c> 的长表，天然容纳任意多科目）。
///
/// 加这些科目直接解决的问题（都是 2026-08-27 分析 603501 时被迫上网查的）：
///   · 营运资金占用拆解 → 现金流量表**附注**的存货/应收/应付变动（比用资产负债表两个时点相减准，
///     后者还原不出合并范围变动，实测差了近一倍）
///   · 净利率归因（毛利率只解释三成，其余在四费和公允价值变动里）→ <see cref="SellExpense"/> 等
///   · 收现比（要含税口径看）→ <see cref="SalesCash"/>
///   · EPS/BPS/PE/PB → <see cref="EpsBasic"/>、<see cref="ShareCapital"/>
///   · 有息负债 → <see cref="ShortLoan"/> / <see cref="LongLoan"/> / <see cref="BondPayable"/>
///
/// ⚠ 现金流量表的**附注**（间接法补充资料）里有些行名跟正表或利润表重复（"净利润""财务费用"
/// "少数股东权益"）。解析器对同名行取**第一次出现**的（见 SinaFinancialProvider.Parse 里的 rank
/// 逻辑），所以这里只收行名唯一的附注科目；净利润一律从利润表取，不从现金流量表取。
/// </summary>
public static class FinancialKeys
{
    /// <summary>
    /// **科目集版本**——抓取时连同数据一起记进 FinancialFetchState 表，增量判断靠它识别
    /// "这只票的数据是旧版代码抓的、科目不全"。
    ///
    /// ⚠ **改动下面任何科目（增、删、改 key 名）都必须把这个数 +1**，否则老数据不会被重抓，
    /// 新科目会永远是空的——2026-08-27 就踩了这个坑：科目从 8 个扩到 52 个之后跑了一次全量拉取，
    /// 结果 5780 只票里 5552 只因为"最新报告期已是 2026-03-31"被增量逻辑判定为无需重抓，
    /// 那 44 个新科目一条都没进库。增量判断只看报告期、不看科目是否齐全，这个版本号就是补这个洞。
    ///
    /// 版本历史：
    ///   1 = 最初的 8 个科目（revenue/oper_cost/net_profit/np_parent/assets/liab/equity_parent/ocf）
    ///   2 = 2026-08-27 扩到 52 个（利润表18 + 资产负债表20 + 现金流量表正表6 + 附注8）
    ///   3 = 2026-08-29 扩到 55 个：补银行专属的利息净收入/手续费及佣金净收入/其他业务支出，
    ///       同时给已有科目补上银行叫法的备选名（见 SinaFinancialProvider.Statements）。
    ///   4 = 2026-08-29 扩到 60 个：补券商（代理买卖/承销/资管净收入）和保险（已赚保费、赔付支出）
    ///       的特征科目，用来把"金融机构"细分成银行/券商/保险三类。
    ///       ⚠ 金融机构需要按此版本重抓一次才认得出类型；【券商保险监管指标】按钮会自动补抓
    ///         这一百来只，不必等全市场。
    ///   5 = 2026-09-10 **换源到东财**（科目集没变，仍是 60 个）——版本号在这里的作用不是
    ///       "科目变多了"，而是**逼全库重抓一遍**：新旧两源的口径和精度不同，
    ///       同一张表里前后期混着两个源的值，同比/环比会出现无从分辨的断层。
    ///       见 doc/financial-source-eastmoney-design.md。
    /// </summary>
    public const int Version = 5;

    // ══════════ 利润表 ══════════

    /// <summary>营业(总)收入（银行=一、营业收入）。</summary>
    public const string Revenue = "revenue";
    /// <summary>营业总成本（含四费和税金附加，跟 <see cref="OperCost"/> 不是一回事）。</summary>
    public const string TotalCost = "total_cost";
    /// <summary>营业成本（银行/券商没有，毛利率因子对它们为空）。毛利率 = 1 − 本项/<see cref="Revenue"/>。</summary>
    public const string OperCost = "oper_cost";
    /// <summary>营业税金及附加。</summary>
    public const string TaxSurcharge = "tax_surcharge";

    // ── 四费：净利率归因的主力。2026H1 的 603501 净利率掉 5.9pct，毛利率只解释 1.7pct，
    //    其余就在这四项和公允价值变动里 ──
    /// <summary>销售费用。</summary>
    public const string SellExpense = "sell_exp";
    /// <summary>管理费用。</summary>
    public const string AdminExpense = "admin_exp";
    /// <summary>财务费用（含汇兑损益，汇率大幅波动时是利润的重要扰动项）。</summary>
    public const string FinanceExpense = "fin_exp";
    /// <summary>研发费用。主动投入，跟被动的费用上升要区别看。</summary>
    public const string RdExpense = "rd_exp";

    /// <summary>资产减值损失。</summary>
    public const string ImpairmentLoss = "impair_loss";
    /// <summary>公允价值变动**收益**（利润表口径，赚钱为正）。持有的股权/金融资产随行就市的浮盈浮亏，
    /// 是非经常性的、会回摆的——判断"利润下滑可逆不可逆"时必须先把它剥掉。
    /// ⚠ 现金流量表附注里的同一件事叫"公允价值变动损失"（<see cref="FvChangeLoss"/>），**符号相反**。</summary>
    public const string FvChangeGain = "fv_gain";
    /// <summary>投资收益。</summary>
    public const string InvestIncome = "invest_income";

    // ── 银行专属三项（2026-08-29 新增）──────────────────────────────────────────────
    // 银行利润表的结构跟工商企业完全不同：没有"营业成本"，收入由"利息净收入 + 手续费及佣金净
    // 收入 + 投资净收益 + 公允价值变动"构成，支出侧是"业务及管理费用 + 信用减值"。原来这几行
    // 的名字在映射表里一个都没配（配的是"管理费用""投资收益"等工商企业叫法），导致 42 家银行
    // 只抓到 24/52 个科目，成本收入比、非息收入拆分这些银行核心指标全都算不出来。
    //
    // 有了这三项 + 已有的 TotalCost/TaxSurcharge/AdminExpense，就能推出：
    //   · 成本收入比 = 业务及管理费用 ÷ 营业收入
    //   · 信用减值损失 = 营业支出 − 营业税金及附加 − 业务及管理费用 − 其他业务支出
    //     （新浪的银行利润表没有单列"信用减值损失"行，"资产减值损失"那行恒为 0，只能倒推。
    //      招行 2026H1 倒推得 291.91 亿，与年报披露的信用成本口径一致。）
    //   · 非息收入拆分：手续费净收入=可持续，投资净收益+公允价值变动=靠行情、不可持续
    // 这几个都是派生值，按"派生值不入库、读取时现算"的原则（见 doc §9.5）不单独存 key。

    /// <summary>利息净收入（银行）。= 利息收入 − 利息支出，银行的主营收入。
    /// ÷ 平均总资产可近似净息差（真实口径的分母是生息资产、会略小，所以算出来偏低约 0.15pct，
    /// 只能自比趋势、不能跟年报披露的净利息收益率直接对齐）。</summary>
    public const string InterestNet = "interest_net";
    /// <summary>手续费及佣金净收入（银行）。非息收入里**可持续**的那部分——代表真实客户经营能力，
    /// 跟靠债市行情吃饭的投资收益要分开看（这正是"非息收入看可持续来源"那条的落点）。</summary>
    public const string FeeCommissionNet = "fee_net";
    /// <summary>其他业务支出。倒推银行信用减值损失时要从营业支出里扣掉它，见上面的注释。</summary>
    public const string OtherOperExpense = "other_oper_exp";

    // ── 券商 / 保险专属（2026-08-29 新增）──────────────────────────────────────────
    // 三类金融机构的利润表结构互不相同，光靠"没有营业成本"只能认出"是金融机构"，认不出是哪一类。
    // 这几个科目是各自的**身份特征**（实测中信证券/中国平安/中国太保的报表）：
    //   券商：代理买卖证券 98.56亿、证券承销 30.23亿、资管 71.82亿；利息净收入只占营收 3.5%
    //   保险：已赚保费 平安 2792.55亿(占营收48.6%)、太保 1432.96亿(占67.5%)
    // 对比银行的利息净收入占营收 60%+，三者靠这几项能干净地分开。

    /// <summary>已赚保费（保险）。保险公司的主营收入，也是判定"这是保险公司"的特征科目。</summary>
    public const string PremiumEarned = "premium_earned";
    /// <summary>赔付支出（保险）。跟已赚保费一起看赔付率。</summary>
    public const string ClaimExpense = "claim_expense";
    /// <summary>代理买卖证券业务净收入（券商）。经纪业务，最靠天吃饭的一块。</summary>
    public const string BrokerageNet = "brokerage_net";
    /// <summary>证券承销业务净收入（券商）。投行业务。</summary>
    public const string UnderwritingNet = "underwriting_net";
    /// <summary>受托客户资产管理业务净收入（券商）。资管业务，收入最稳的一块。</summary>
    public const string AssetMgmtNet = "asset_mgmt_net";
    /// <summary>营业利润。</summary>
    public const string OperProfit = "oper_profit";
    /// <summary>利润总额。</summary>
    public const string TotalProfit = "total_profit";
    /// <summary>所得税费用。</summary>
    public const string IncomeTax = "income_tax";

    /// <summary>净利润（利润表"五、净利润"，含少数股东损益；现金流量表附注里同名行不取，见类注释）。</summary>
    public const string NetProfit = "net_profit";
    /// <summary>归属于母公司所有者的净利润（银行叫"归属于母公司的净利润"）。</summary>
    public const string NetProfitParent = "np_parent";
    /// <summary>少数股东损益。<see cref="NetProfit"/> − 本项 = <see cref="NetProfitParent"/>。</summary>
    public const string MinorityPl = "minority_pl";
    /// <summary>基本每股收益（元/股）。**数据源直接给，不用自己拿净利除股本**——加权平均股本跟期末
    /// 股本不是一回事（可转债转股、增发都会让两者分叉），自己算容易错。</summary>
    public const string EpsBasic = "eps_basic";

    // ══════════ 资产负债表 ══════════

    /// <summary>货币资金。</summary>
    public const string Cash = "cash";
    /// <summary>应收票据。</summary>
    public const string NoteReceivable = "note_recv";
    /// <summary>应收账款。</summary>
    public const string AccountsReceivable = "ar";
    /// <summary>预付款项。</summary>
    public const string Prepayment = "prepay";
    /// <summary>存货。</summary>
    public const string Inventory = "inventory";
    /// <summary>流动资产合计。</summary>
    public const string CurrentAssets = "cur_assets";
    public const string TotalAssets = "assets";

    /// <summary>短期借款。</summary>
    public const string ShortLoan = "st_loan";
    /// <summary>应付票据。</summary>
    public const string NotePayable = "note_pay";
    /// <summary>应付账款。</summary>
    public const string AccountsPayable = "ap";
    /// <summary>预收款项。**新准则的"合同负债"在新浪 TSV 里仍归到这一行**（网页版才显示"合同负债"）。
    /// 它减少 = 客户新下单时的预付款在变少，是需求走弱最前置的信号之一。</summary>
    public const string AdvanceReceipts = "advance_recv";
    /// <summary>流动负债合计。</summary>
    public const string CurrentLiabilities = "cur_liab";
    /// <summary>长期借款。</summary>
    public const string LongLoan = "lt_loan";
    /// <summary>应付债券（可转债在转股前挂这里，转股后归零——净资产、股本、资产负债率会同时跳变）。</summary>
    public const string BondPayable = "bond_pay";
    public const string TotalLiabilities = "liab";

    /// <summary>实收资本(或股本)——**股数**不是金额（A股面值1元，所以数值上等于股本股数）。
    /// 算 PE/总市值要用它，别用流通市值倒推。</summary>
    public const string ShareCapital = "share_capital";
    /// <summary>归属于母公司股东权益（一般/银行叫法不同，取不到时退回所有者权益合计）。</summary>
    public const string EquityParent = "equity_parent";
    /// <summary>少数股东权益。**可以是负数**（子公司累计亏损吃穿了少数股东出资，603501 就是），
    /// 这时 <c>assets − liab</c> 会略大于 <see cref="EquityParent"/>，别误以为字段口径错了。</summary>
    public const string MinorityEquity = "minority_equity";
    /// <summary>所有者权益(或股东权益)合计。</summary>
    public const string EquityTotal = "equity_total";
    /// <summary>未分配利润（分红能力的上限之一）。</summary>
    public const string UndistributedProfit = "undist_profit";

    // ══════════ 现金流量表（正表） ══════════

    /// <summary>销售商品、提供劳务收到的现金。÷营业收入 = 收现比，但**收到的现金含增值税、营收不含税**，
    /// 所以正常值在 1.13 左右（13%税率）而不是 1.0——低于 1.13 就说明回款偏慢。</summary>
    public const string SalesCash = "sales_cash";
    /// <summary>经营活动产生的现金流量净额。</summary>
    public const string Ocf = "ocf";
    /// <summary>投资活动产生的现金流量净额。</summary>
    public const string Icf = "icf";
    /// <summary>筹资活动产生的现金流量净额。三者一起看才知道"扩张的钱是自己挣的还是借的"。</summary>
    public const string Fcf = "fcf";
    /// <summary>购建固定资产、无形资产和其他长期资产所支付的现金（资本开支）。</summary>
    public const string Capex = "capex";
    /// <summary>期末现金及现金等价物余额。</summary>
    public const string CashEnd = "cash_end";

    // ══════════ 现金流量表（附注 / 间接法补充资料） ══════════
    //
    // 这一组是本次扩充里最值得加的：它把"净利润→经营现金流"的每一步差异都列了出来，
    // 实测能对平（603501 2026H1：净利12.03 + 折旧摊销6.28 + 减值1.89 + … − 存货9.25
    // − 经营性应收13.72 + 经营性应付0.53 + … = 4.08 = 表内直接法数字）。
    // 用资产负债表两个时点相减是**还原不出来**的，实测存货差 5.89 vs 9.25、应收差 7.12 vs 13.72。

    /// <summary>资产减值准备（附注加回项）。</summary>
    public const string ImpairmentProvision = "impair_provision";
    /// <summary>固定资产折旧、油气资产折耗、生产性物资折旧。</summary>
    public const string Depreciation = "depreciation";
    /// <summary>无形资产摊销。</summary>
    public const string AmortIntangible = "amort_intangible";
    /// <summary>长期待摊费用摊销。</summary>
    public const string AmortLongPrepaid = "amort_lt_prepaid";
    /// <summary>公允价值变动**损失**（附注口径，亏钱为正——跟利润表的 <see cref="FvChangeGain"/> 符号相反）。</summary>
    public const string FvChangeLoss = "fv_loss";
    /// <summary>存货的减少（**负数=存货增加=占用现金**）。口径含合并范围变动，比资产负债表相减准。</summary>
    public const string InventoryDecrease = "inv_decrease";
    /// <summary>经营性应收项目的减少（**负数=应收增加=占用现金**）。含应收账款+票据+预付+其他应收，
    /// 所以比单看应收账款的变动大。</summary>
    public const string ReceivableDecrease = "recv_decrease";
    /// <summary>经营性应付项目的增加（**正数=占用上游资金=释放现金**）。</summary>
    public const string PayableIncrease = "pay_increase";
}

/// <summary>一只股票某报告期某科目的值（单位：元）。报表值是**年内累计**口径（A股定期报告惯例），
/// TTM/单季由消费端换算。
///
/// 例外：<see cref="FinancialKeys.EpsBasic"/> 是元/股，<see cref="FinancialKeys.ShareCapital"/>
/// 数值上是股数（A股面值1元）——两者都不是"元"，做单位换算时要跳过。</summary>
public class FinancialValue
{
    public string Code { get; set; } = "";
    /// <summary>报告期（季度末：0331/0630/0930/1231）。注意这不是公告日——数据源没有公告日，
    /// 消费端按法定披露截止日估计可用时点（见 FactorLab 的说明）。</summary>
    public DateTime ReportDate { get; set; }
    public string Key { get; set; } = "";
    public double Value { get; set; }
}
