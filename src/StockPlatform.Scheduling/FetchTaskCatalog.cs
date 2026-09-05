namespace StockPlatform.Scheduling;

/// <summary>
/// 计划里能放的动作。**存进 fetch-plan.json 的是这个枚举的名字**，所以已有成员不能改名、
/// 不能复用旧名字表示别的意思——改了用户手上排好的计划就会读不出来。
/// </summary>
public enum FetchActionId
{
    FetchAll,
    FetchDay,
    RetryFailed,
    RepairQfq,
    FetchRawBars,
    RebuildAdjSeries,
    FetchEarningsSchedule,
    FetchBoards,
    FetchIndustry,
    FetchIndexCons,
    FetchShareholder,
    FetchFinancials,
    FetchDividend,
    BankRegulatory,
    ImportManual,
    BackfillDaily,
    FetchYear,
    OptimizeDatabase,

    // ───── 【拉取全部】拆出来的 13 个原子项（2026-09-02，见 doc/fetch-plan-atomic-tasks-design.md）─────
    // 名字同样进 json，不能改。前缀 Step 是为了跟老的复合动作一眼分开。
    StepRoster,
    StepNetInflow,
    StepAnnouncements,
    StepIndexBars,
    StepStockDayBars,
    StepStockHfqBars,
    StepStockRawBars,
    StepEtfBars,
    StepDelistedTails,
    StepBoardIndex,
    StepMargin,
    StepLhb,
    StepDayCoverage,

    // ───── 另外三处复合动作拆出来的（2026-09-02，设计文档 3.3 节）─────
    StepIndexCons,
    StepIndexWeight,
    StepEtfIndexMap,
    StepBoards,
    StepBoardList,
    StepBoardMembers,
    StepReparseBankPdf,
    StepFullAudit,

    // ───── 东财数据（2026-09-03）─────
    // 主环境实测只有 push2 域名不通，datacenter-web / push2his 都可达——此前"东财整体不可用"
    // 的判断是错的。这一项抓的是本地此前完全没有的数据，没有回退源。
    FetchEarningsForecast,
    FetchLhbSeat,
    FetchMoneyFlowDetail,
    FetchMarketEvents,
    FetchStockBoardMap,
}

/// <summary>
/// 配额组——**同组的动作打的是同一家服务器的同一套配额**。
///
/// 现在（2026-08-31）计划是严格串行执行的，这个字段还不参与调度，只用来在界面上告诉人
/// "这几项在抢同一份额度、别排得太挤"。留着它是因为将来若要做跨源并行，判据就是它：
/// 限流器各自独立**不等于**配额独立——项目里踩过的坑是财务报表接口按 3并发/1秒 跑到
/// 100 多个请求就被新浪返回 HTTP 456，整轮 350 个请求零成功（见 App.xaml.cs 里
/// financialProvider 那段注释），所以同组任务并行只会更快撞墙。
/// </summary>
public enum QuotaGroup
{
    /// <summary>本地操作，不联网。</summary>
    Local,
    /// <summary>新浪（vip.stock / money.finance 两个子域按同一套配额算）。</summary>
    Sina,
    /// <summary>沪深交易所官网。</summary>
    Exchange,
    /// <summary>横跨多家（腾讯K线 + 新浪 + 交易所 + 巨潮），内部自己串行。</summary>
    Mixed,
}

/// <summary>动作要不要额外参数——界面据此决定显示哪些输入框。</summary>
[Flags]
public enum FetchActionParams
{
    None = 0,
    /// <summary>用计划页顶部那两个全局参数：数据源、中标/订单公告关键词。</summary>
    GlobalFetchOptions = 1,
    /// <summary>要一个具体日期（yyyy-MM-dd）。</summary>
    Date = 2,
    /// <summary>要年份区间 + 覆盖前复权开关。</summary>
    YearRange = 4,
    /// <summary>
    /// 要"首次回看几年"。**只有【拉取全部】用得上**，所以它是那一行的参数、不是全局设置
    /// （2026-08-31 从全局参数行挪进表格）：这个值只决定"本地一条K线都没有的标的第一次抓多久历史"，
    /// 已经抓过的永远从自己上次抓到那天续，跟它无关。
    /// </summary>
    LookbackYears = 8,
    /// <summary>
    /// 要"中标/订单公告关键词"（2026-09-02 从计划页顶部的全局参数挪进行里）。
    ///
    /// 它原来是全局的，因为当时有三个复合动作都要用（拉取全部/补指定历史日/拉取区间数据）。
    /// 拆开之后真正用得上它的只剩【中标/订单公告】和【拉取区间数据】两行，
    /// 摆在顶上反而像"所有任务都吃这个设置"。留空＝用【手动】页那个框里的值。
    /// </summary>
    Keywords = 16,
    /// <summary>
    /// 有个开关要勾（2026-09-02）。目前只有【全库数据体检】用：勾上＝「彻底体检」，
    /// 忽略并清空"确认数据源没有"的白名单，全部重查一遍。
    /// </summary>
    Thorough = 32,
}

/// <summary>
/// 一项任务"抓哪一段"（2026-09-02 新增，见 doc/fetch-plan-atomic-tasks-design.md 第 2 节末）。
///
/// 这是拆分里的第三个维度：【拉取全部】【补指定历史日】【一键补齐每日历史】内部步骤高度重合，
/// 差别只在抓取的时间范围。**范围是参数，不是任务**——所以它们不再各自占一个动作，
/// 而是同一批原子项换个模式。
/// </summary>
[Flags]
public enum FetchMode
{
    /// <summary>默认：每个标的从自己的水位线续抓到今天（这是日常该用的）。</summary>
    Incremental = 1,

    /// <summary>只抓指定的那一个自然日（原【补指定历史日】）。要填日期，留空＝今天。</summary>
    SpecificDay = 2,

    /// <summary>
    /// 首次整段回补，不看水位线（原【补不复权历史】【一键补齐每日历史】）。
    /// 补完就不用再跑了——日常那一根增量在 <see cref="Incremental"/> 里已经带上。
    /// </summary>
    FirstBackfill = 4,
}

/// <summary>
/// 这一项的数据**什么时候才齐**（2026-09-02 新增）——决定它该不该卡「不早于」。
///
/// 起因是用户的问题："不是每天有数据的那些，早跑会有问题吗？"答案是不会，
/// 而这条信息原来只散在各项的说明文字里，界面没法据此提醒。
/// </summary>
public enum DataReadiness
{
    /// <summary>
    /// 随时都行。季度/不定期数据（股东、财务、分红、行业、指数成分）跟当天收盘无关；
    /// 融资余额是两所 **T+1** 发布、它抓的本来就是前几个交易日；公告回看 14 天、退市股补的是历史。
    /// 这些早跑最多是"今天那条明天才补上"，不会出错。
    /// </summary>
    Anytime,

    /// <summary>
    /// **要等当天收盘之后**。早跑不会报错，而是**静默拿到不完整的数据**：
    /// 盘中抓的K线是未收盘价（16 点后抓的才算最终，见 FetchOrchestrator.IsConfirmedFinal），
    /// 更麻烦的是数据源盘后**逐步**更新——2026-08-20 那轮 19:00 开跑，个股前复权只拿到
    /// 1773/5539 只，接口全部正常返回、失败名单是空的，人第二天才发现。
    /// 龙虎榜更直接：当晚才发布，早跑就是空的。
    /// </summary>
    AfterClose,
}

/// <summary>
/// 默认的五个组（2026-09-02）。组是**排期单位**——按"什么时候跑"分，不是按数据源或数据种类分。
/// 用户随时可以改名、改规则、把任务挪来挪去，这里只决定"第一次打开时长什么样"。
/// </summary>
public enum PlanGroupKind
{
    /// <summary>
    /// **每天产生的新数据**，当天就得更新：行情、资金、公告，以及跟着它们走的本地重算和补漏。
    /// 每工作日 18:00 到点就跑——日更有时效，不能等空闲。
    /// </summary>
    Daily,

    /// <summary>
    /// **按周期更新的数据**：财务、股东、分红、行业分类、指数成分。
    /// 每月 1 号到期，然后**空闲时**慢慢补——这类晚几天真没关系，而财务报表每轮只能 300 只、
    /// 全市场本来就要跨几天才啃得完。
    /// </summary>
    Periodic,

    /// <summary>
    /// **按需启动**：想起来才做的一次性活——往回补更早年份的历史、全库体检、建索引。
    /// 都是手动触发，没有"到期"这回事。
    /// </summary>
    OnDemand,
}

/// <summary>一个动作的元数据。</summary>
public sealed record FetchActionInfo(
    FetchActionId Id,
    string Name,
    string DataSource,
    QuotaGroup Quota,
    /// <summary>典型耗时，排计划时用来估时间轴。真实耗时随本地已有数据量浮动。</summary>
    TimeSpan Estimate,
    string Frequency,
    string Note,
    FetchActionParams Params = FetchActionParams.None,
    /// <summary>前置动作：它在同一轮里失败了，这一项就跳过（跑了也是白跑）。</summary>
    FetchActionId? DependsOn = null,
    /// <summary>
    /// **软**前置（2026-09-02 随拆分新增，同日改成可以有多个）：没它也能跑，只是结果会旧一点。
    ///
    /// 典型是"逐只抓的项 ← 股票名册"：名册没跑，用库里上次的名单照样能抓，只是当天新上市的
    /// 票不在里面。这种情况**不该跳过**，但要在日志里说一声，否则人不知道为什么少了几只。
    /// 硬前置（<see cref="DependsOn"/>）才是"跑了也白跑、直接跳过"。
    ///
    /// 为什么要能写多个：好几项其实吃两份输入——【板块指数合成】要板块成分**和**当天个股K线，
    /// 【当日覆盖率体检】要指数日K（交易日锚）**和**个股日K。只能填一个的话，另一条依赖就只能
    /// 活在注释里，界面上看不见、也没法自动校验顺序。
    /// </summary>
    IReadOnlyList<FetchActionId>? SoftDependsOn = null,
    /// <summary>
    /// 这一项支持哪些"抓哪一段"的模式（2026-09-02）。默认只支持增量。
    ///
    /// 界面按它决定模式下拉里列什么。不支持是有具体原因的，不是没做：比如流通市值/板块成分
    /// 这类**当下快照**接口根本没有"某天的值"，硬抓只会把今天的值错记成那天的；
    /// 后复权/不复权/ETF/指数在原来的【补指定历史日】里走的也一直是水位线增量、不是"只抓那天"。
    /// </summary>
    FetchMode SupportedModes = FetchMode.Incremental,
    /// <summary>
    /// **已退役**（2026-09-02）：这个动作已经被拆出来的原子项完全替代，不再出现在计划页上。
    ///
    /// 枚举成员和这条目录信息都得留着——老的 fetch-plan.json 里可能还排着它，读的时候要认得出来
    /// 才能把它换成等价的那几项（见 <see cref="FetchTaskCatalog.RetiredInto"/> 和
    /// <see cref="FetchPlan.MigrateRetired"/>）。留着也让"这个动作去哪了"有据可查。
    /// </summary>
    bool Retired = false,
    /// <summary>
    /// 这个动作能不能"只跑一部分"——给「空闲时」那类触发用（见 RepeatKind.WhenIdle）。
    ///
    /// true 的动作接受一个"本轮最多做多少"的上限，所以哪怕离下一个定时任务只剩半小时，
    /// 也能塞进去补一小批、到点前干净收尾。目前只有财务报表是这样（它本来就按"每轮 300 只"
    /// 分批跑）。false 的动作只能整轮跑，空闲窗口装不下它的预计耗时时就不启动，
    /// 免得跑到一半被定时任务打断。
    /// </summary>
    bool SupportsPartialRun = false,
    /// <summary>
    /// 这一项实际占用哪些**数据源**（2026-09-04 新增）——判断"两个任务能不能同时跑"的依据。
    ///
    /// 为什么不用 <see cref="Quota"/>：那个只有四档，而且一多半项是 Mixed
    /// （"横跨腾讯K线+新浪+交易所+巨潮"）。按它判的话【板块成分股】(东财 push2) 会跟
    /// 【个股日K】(腾讯) 判成冲突——而这两个根本不抢同一个源，恰恰是最该并行的组合。
    ///
    /// null = 没标注，按 Quota 保守推导（Mixed 视为占用全部联网源，跟谁都冲突）。
    /// </summary>
    IReadOnlyList<DataSourceId>? Sources = null)
{
    /// <summary>
    /// 这一项的数据什么时候才齐（2026-09-02）——决定它该不该卡「不早于」。
    /// 集中定义在 <see cref="FetchTaskCatalog.AfterCloseActions"/>，不逐条写在目录里：
    /// "哪些必须等收盘"要能一眼看全，散在二十几个条目里没人核得动。
    /// </summary>
    public DataReadiness Readiness => FetchTaskCatalog.ReadinessOf(Id);

    /// <summary>
    /// 实际占用的数据源。没标 <see cref="Sources"/> 的按 <see cref="Quota"/> 推导：
    /// 本地项不占源（永远可并发），Mixed 保守当成占用全部联网源（宁可挡住，别撞配额）。
    /// </summary>
    public IReadOnlySet<DataSourceId> EffectiveSources => Sources is { Count: > 0 }
        ? Sources.ToHashSet()
        : Quota switch
        {
            QuotaGroup.Local => [],
            QuotaGroup.Sina => [DataSourceId.Sina],
            QuotaGroup.Exchange => [DataSourceId.Exchange],
            _ => DataSourceCatalog.AllOnline.ToHashSet(),
        };

    /// <summary>占用的源，可读形式（给日志和界面）。</summary>
    public string SourcesText => EffectiveSources.Count == 0
        ? "本地计算（不占数据源）"
        : string.Join("、", EffectiveSources.Select(DataSourceCatalog.NameOf));
}

/// <summary>
/// 数据源编号（2026-09-04 新增）——占用表用它做键，判断两个任务抢不抢同一个源。
///
/// ════ 粒度按「限流边界」切，不按公司切 ════
/// 东财三个域名各有各的反爬策略：实测**只有 push2 会弹图片验证码**（那个只能人来点），
/// datacenter 和 push2his 都正常。把它们算成一个"东财"会白白挡掉能并行的组合——
/// 而【板块成分股】(push2) 跟【分档资金流】(push2his) 并行正是最有价值的一对。
/// </summary>
public enum DataSourceId
{
    /// <summary>腾讯行情（K线主源）。</summary>
    Tencent,
    /// <summary>新浪（vip.stock / money.finance 按同一套配额算）。</summary>
    Sina,
    /// <summary>沪深交易所官网。</summary>
    Exchange,
    /// <summary>巨潮资讯。</summary>
    Cninfo,
    /// <summary>中证指数官网 OSS。</summary>
    CsIndex,
    /// <summary>东财行情侧 push2——**弹图片验证码的就是这个**，只能人工过。</summary>
    EmPush2,
    /// <summary>东财行情历史侧 push2his（分档资金流）。跟 push2 不同域名、不同限流。</summary>
    EmPush2His,
    /// <summary>东财数据中心 datacenter（业绩预告、龙虎榜席位、市场事件、个股题材）。</summary>
    EmDataCenter,

    /// <summary>
    /// 东财 quote 站的静态资源（2026-09-05）——眼下只有板块名单那份 sidemenu_new.json。
    ///
    /// 单列一个源而不是并进 EmPush2：它跟 push2 **不是一回事**。push2 在这台机器上被网关拦、
    /// 会弹图片验证码、限流极敏感；quote 是普通网页站点，一轮就一个请求、随便跑。
    /// 并进去的话【概念和行业板块】会跟【板块成分股】互斥排队，而它们现在完全可以同时跑。
    /// </summary>
    EmQuote,
}

/// <summary>数据源编号 ↔ 可读名。程序里用编号，界面和日志上显示名字。</summary>
public static class DataSourceCatalog
{
    /// <summary>全部联网源——没标注的 Mixed 项按这个算（保守，跟谁都冲突）。</summary>
    public static readonly DataSourceId[] AllOnline =
    [
        DataSourceId.Tencent, DataSourceId.Sina, DataSourceId.Exchange, DataSourceId.Cninfo,
        DataSourceId.CsIndex, DataSourceId.EmPush2, DataSourceId.EmPush2His, DataSourceId.EmDataCenter,
        DataSourceId.EmQuote,
    ];

    public static string NameOf(DataSourceId id) => id switch
    {
        DataSourceId.Tencent => "腾讯",
        DataSourceId.Sina => "新浪",
        DataSourceId.Exchange => "交易所",
        DataSourceId.Cninfo => "巨潮",
        DataSourceId.CsIndex => "中证",
        DataSourceId.EmPush2 => "东财 push2",
        DataSourceId.EmPush2His => "东财 push2his",
        DataSourceId.EmDataCenter => "东财 datacenter",
        DataSourceId.EmQuote => "东财 quote",
        _ => id.ToString(),
    };
}

/// <summary>
/// 动作目录（2026-08-31 新增）——计划页左边那份清单，也是执行引擎认识动作的唯一依据。
///
/// ════ 耗时是怎么来的 ════
/// 全部取自实测：拉取全部约 2 小时（7400 个标的）、股东数据 1~2 小时（5500 只 × 2 个页面）、
/// 财务报表每轮上限 300 只约 90 分钟（接口配额严、单独降速到约 10 请求/分钟）、
/// 行业分类 1~2 分钟。这些数字只用于**画时间轴给人看**，不参与任何调度判断——
/// 引擎永远是"上一个真的跑完了才开下一个"，估错了不会导致抢跑。
///
/// ════ 为什么"一键拉取定期数据"不在这里 ════
/// 它本身就是 行业分类 → 指数成分/权重 → 股东数据 → 财务报表 → 分红 五步的固定串。
/// 在计划里把这五项分开排更灵活（比如只在财报季开财务、平时只跑行业分类），
/// 所以目录里只放这五项本身。那个按钮在【手动】页原样保留。
/// </summary>
public static class FetchTaskCatalog
{
    public static readonly IReadOnlyList<FetchActionInfo> All =
    [
        // ══════════════ 每日主流程的 13 个原子项（2026-09-02 从【拉取全部】拆出）══════════════
        // 顺序就是推荐的执行顺序，也是【添加每日模板】展开出来的顺序。
        // 拆分判据见 doc/fetch-plan-atomic-tasks-design.md 第 2 节：同一次请求拿回来的不拆
        // （名册+市值）、多源接力才有结果的不拆（ETF/退市/公告）、本地计算单独成项。

        new(FetchActionId.StepRoster, "股票名册与流通市值", "新浪列表分页", QuotaGroup.Sina,
            TimeSpan.FromMinutes(3), "每工作日",
            "刷新全市场名册（顺带发现当天新上市的票）+ 当下的流通市值快照——**同一个列表接口一次给两样**"
            + "（名册和 nmc 流通市值），所以是一项、拆不开，而且只扫一遍就够"
            + "（老的【拉取全部】是取名册扫一遍、市值又扫一遍，白花约 55 个请求）。\n"
            + "后面所有\"逐只\"的项都拿这份名册当输入，建议排在它们前面。\n"
            + "⚠ 市值只有\"当下\"、接口没有历史，所以这一项没有\"补某一天\"的用法。",
            FetchActionParams.GlobalFetchOptions),

        new(FetchActionId.StepNetInflow, "资金净流入", "新浪", QuotaGroup.Sina,
            TimeSpan.FromMinutes(30), "每工作日",
            "逐只抓主力资金净流入（约 5500 只，做资金流因子）。失败的进自己的重试名单。\n"
            + "模式选「只抓某一天」就按那一天精确取（原【补指定历史日】的做法），日期留空＝今天。",
            FetchActionParams.GlobalFetchOptions | FetchActionParams.Date,
            SoftDependsOn: [FetchActionId.StepRoster],
            SupportedModes: FetchMode.Incremental | FetchMode.SpecificDay),

        new(FetchActionId.StepAnnouncements, "中标/订单公告", "巨潮检索 + 正文", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(3), "每工作日",
            "按计划页顶部填的关键词搜中标/订单类公告并取正文，回看最近 14 天（重复扫同一天是安全的，主键去重）。"
            + "关键词清空就是空跑。搜索结果没有正文就没法筛金额，所以\"检索→取正文\"是一项、不拆。\n"
            + "模式选「只抓某一天」就只搜那一天。\n"
            + "关键词就填在这一行的参数格里（逗号分隔），留空＝用【手动】页那个框里的值；两边都空就不抓。",
            FetchActionParams.GlobalFetchOptions | FetchActionParams.Date | FetchActionParams.Keywords,
            SupportedModes: FetchMode.Incremental | FetchMode.SpecificDay,
            Sources: [DataSourceId.Cninfo]),

        new(FetchActionId.StepIndexBars, "指数日K", "腾讯（回退新浪）", QuotaGroup.Mixed,
            TimeSpan.FromSeconds(30), "每工作日",
            "大盘指数日K（十几个标的，水位线增量）。\n"
            + "⚠ 它还是全库的**交易日锚**：\"最近一个已收盘交易日是哪天\"就是看上证指数最新一根日线"
            + "（快照类数据归属日、当日覆盖率体检都靠它），所以别把它关掉。",
            FetchActionParams.GlobalFetchOptions | FetchActionParams.LookbackYears,
            Sources: [DataSourceId.Tencent, DataSourceId.Sina]),

        new(FetchActionId.StepStockDayBars, "个股日K·前复权", "腾讯（回退新浪）", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(30), "每工作日",
            "日常选股看盘用的主力价格序列，每只从自己上次抓到那天续抓（所以能自动补断档）。\n"
            + "两件顺带做的事也在这一项里：① 写完日线立刻重算该股**周线/月线**（本地计算，分析程序的"
            + "周线图和彬哥法/回调法都要用）；② 跟库里比对发现**复权基准漂移**就记进待重取名单，"
            + "交给【重取前复权】慢慢补。\n"
            + "模式选「只抓某一天」就不看水位线、只补那一天（原【补指定历史日】），日期留空＝今天。",
            FetchActionParams.GlobalFetchOptions | FetchActionParams.LookbackYears | FetchActionParams.Date,
            SoftDependsOn: [FetchActionId.StepRoster],
            // 只有前复权这一路支持"补某一天"：后复权/不复权/ETF/指数在原来的【补指定历史日】里
            // 走的也一直是水位线增量，不是"只抓那天"。
            SupportedModes: FetchMode.Incremental | FetchMode.SpecificDay,
            Sources: [DataSourceId.Tencent, DataSourceId.Sina]),

        new(FetchActionId.StepStockHfqBars, "个股日K·后复权", "腾讯（回退新浪）", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(30), "每工作日",
            "数据源口径的后复权日K，水位线独立于前复权。\n"
            + "⚠ 它是\"送转乘、分红加\"的混合式、会压低收益率（实测工商银行 ×0.625），**回测已经改用本地算的"
            + " day_adj**，这一条现在主要是对照和历史兼容。",
            FetchActionParams.GlobalFetchOptions | FetchActionParams.LookbackYears,
            SoftDependsOn: [FetchActionId.StepRoster],
            Sources: [DataSourceId.Tencent, DataSourceId.Sina]),

        new(FetchActionId.StepStockRawBars, "个股日K·不复权", "腾讯（回退新浪）", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(30), "每工作日",
            "原始成交价——全库唯一**不随分红变化**的序列，也是【重算回测序列】的输入。\n"
            + "模式：「增量」＝日常那一根；「首次整段回补」＝把每只补到跟前复权一样长"
            + "（原【补不复权历史】，实测约 24700 个请求、2 小时出头，跑完一次就基本不用再管）。\n"
            + "整段回补支持分批：设成重复「空闲时」就会在空档里一点点补、到点前收尾。",
            FetchActionParams.GlobalFetchOptions | FetchActionParams.LookbackYears,
            SoftDependsOn: [FetchActionId.StepRoster],
            SupportedModes: FetchMode.Incremental | FetchMode.FirstBackfill,
            SupportsPartialRun: true,
            Sources: [DataSourceId.Tencent, DataSourceId.Sina]),

        new(FetchActionId.StepEtfBars, "ETF日K", "新浪名单 + 腾讯K线", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(6), "每工作日",
            "全市场 ETF 的日K（约 1000 只，水位线增量）。代码带前缀存（sh510300），天然被挡在个股选股全集外。"
            + "名单和K线是两家，但\"没有名单就抓不了K线\"，所以是一项。",
            FetchActionParams.GlobalFetchOptions | FetchActionParams.LookbackYears,
            Sources: [DataSourceId.Sina, DataSourceId.Tencent]),

        new(FetchActionId.StepDelistedTails, "退市股收尾", "两所官网 + 腾讯K线", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(2), "每工作日",
            "刷新退市名单，给\"本地跟踪过、但最后一根K线还早于终止日\"的票补完最后那几天。\n"
            + "股票一退市数据源就不再更新它，这几天不补就**永久缺失**，回测会有幸存者偏差。",
            FetchActionParams.GlobalFetchOptions,
            Sources: [DataSourceId.Exchange, DataSourceId.Tencent]),

        new(FetchActionId.StepBoardIndex, "板块指数合成", "本地计算·不联网", QuotaGroup.Local,
            TimeSpan.FromMinutes(3), "每工作日",
            "用本地板块成分股 + 个股日K**等权合成**板块指数日K（非官方指数），做板块热度/宽度。\n"
            + "每次全量重算（成分股和个股数据都会变）。要读当天的个股K线，所以排在个股日K之后；"
            + "【拉取板块】更新了成分之后也该跑一次。",
            // 两份输入都要：板块成分（谁在这个板块里）+ 当天个股K线（拿什么价算）
            SoftDependsOn: [FetchActionId.StepBoardMembers, FetchActionId.StepBoardList, FetchActionId.StepStockDayBars]),

        new(FetchActionId.StepMargin, "融资余额", "交易所", QuotaGroup.Exchange,
            TimeSpan.FromMinutes(1), "每工作日",
            "两融余额。两所是 T+1 发布，所以按\"以今天为终点回看最近几个交易日、跳过本地已有的\"来抓，"
            + "不是只抓当天。彬哥法第 12 条（融资余额增长）用的就是它。\n"
            + "模式：「增量」＝以日期格那天（留空＝今天）为终点回看几个交易日；"
            + "「首次整段回补」＝从K线最早那天一路补到今天（原【一键补齐每日历史】的融资那半边），"
            + "跳过本地已有的交易日、幂等可反复跑。",
            FetchActionParams.Date,
            SupportedModes: FetchMode.Incremental | FetchMode.FirstBackfill),

        new(FetchActionId.StepLhb, "龙虎榜", "新浪", QuotaGroup.Sina,
            TimeSpan.FromSeconds(30), "每工作日",
            "当日龙虎榜席位明细，当晚就发布、抓当天即可。\n"
            + "模式：「增量」＝抓日期格那天（留空＝今天）；"
            + "「首次整段回补」＝从K线最早那天补到今天（原【一键补齐每日历史】的龙虎那半边），"
            + "跳过本地已有的交易日、幂等可反复跑。",
            FetchActionParams.Date,
            SupportedModes: FetchMode.Incremental | FetchMode.FirstBackfill),

        new(FetchActionId.StepDayCoverage, "当日覆盖率体检", "本地查库·不联网", QuotaGroup.Local,
            TimeSpan.FromMinutes(2), "每工作日",
            "以上证指数最新一根日线当交易日锚，查出\"上一个交易日有、这一天没有\"的个股，写进待重试名单。\n"
            + "**这一项是防静默漏抓的**：数据源盘后是逐步更新的，请求发早了接口会正常返回、里面却没有当天——"
            + "不报错、不进失败名单。2026-08-20 那轮 19:00 开跑，个股前复权只拿到 1773/5539 只，"
            + "而界面一切正常、失败名单是空的。\n"
            + "查出来的名单交给【重新拉取失败】补。建议排在所有K线项的最后面。",
            // 两份输入：指数日K 是交易日锚（"最近一个已收盘交易日是哪天"），个股日K 是被查的对象
            SoftDependsOn: [FetchActionId.StepIndexBars, FetchActionId.StepStockDayBars]),

        // ══════ 另外三处复合动作拆出来的（2026-09-02，设计文档 3.3 节）══════
        // 判据：成分名单和权重各自入库、各自能单独用 ⇒ 两件事；纯本地的加工（映射、合成、
        // PDF 重解析）一律独立成项——它们不吃配额，失败原因和重跑代价跟联网抓取完全不同。

        new(FetchActionId.StepIndexCons, "指数成分名单", "新浪", QuotaGroup.Sina,
            TimeSpan.FromMinutes(8), "季度",
            "内置的 732 个指数各自的成分股名单。成分名单本身就是一种选股全集（比如\"只在沪深300里选\"）。\n"
            + "新浪对某些老指数本来就没有成分，返回空不算失败；请求失败的进重试名单。"),

        new(FetchActionId.StepIndexWeight, "指数权重", "中证 OSS", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(8), "季度",
            "中证系指数的成分权重（非中证系没有权重文件、404 跳过，不算失败）。\n"
            + "⚠ 中证这一侧偏不稳、失败率高——**这正是它要跟成分名单分开的原因**：合在一起时"
            + "权重失败会把整项标成失败，连带成分名单也算没跑成。\n"
            + "⚠ 它也最容易触发对方的反爬，所以 2026-09-02 做了三件事：①单并发 + 每请求间隔 2 秒 + "
            + "每 30 个歇 60 秒；②本地这一期还新鲜（25 天内）就整批跳过——中证的权重文件是**月度**更新的；"
            + "③确认过\"没有权重文件\"的指数记下来，30 天内不再问（全集 732 个里有四五百个是非中证系，"
            + "每轮拿它们去敲一遍最容易把反爬撞醒，而且一条数据也拿不到）。\n"
            + "稳态下每轮实际发出的请求接近 0，只有月初那一轮才会真抓中证系那两三百个。\n"
            + "在季度组里**排最后**：它最容易撞墙，排末尾的话即使自己进了熔断，前面那些数据也早落库了。",
            SoftDependsOn: [FetchActionId.StepIndexCons],
            Sources: [DataSourceId.CsIndex]),

        new(FetchActionId.StepEtfIndexMap, "ETF指数映射", "本地计算·不联网", QuotaGroup.Local,
            TimeSpan.FromSeconds(20), "季度",
            "按名称把 ETF 匹配到指数（\"沪深300ETF华泰\"→沪深300），供\"股票→指数→ETF\"反查。\n"
            + "纯本地匹配，拉完 ETF 名单或指数成分之后跑一次即可。",
            SoftDependsOn: [FetchActionId.StepIndexCons]),

        new(FetchActionId.StepBoards, "板块行情与成分", "东财", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(20), "每工作日～每周",
            "⚠ **已退役**（2026-09-04）：拆成了【板块列表】+【板块成分股】两项。' + N + '"
            + "拆的理由是配额被挤掉了——限流器现在是「每 15 个请求主动歇 2 分钟」（东财实测连发"
            + "16~35 个就被切），而板块列表开头就要 9~10 个请求，等于每轮三分之二的配额花在列表上，"
            + "只剩 5 个才轮到那 2500 个成分股请求。' + N + '"
            + "而且列表一挂整项就退出，成分股一个都跑不成——可库里明明有上一次的板块名单，"
            + "照样能接着抓成分。' + N + '"
            + "老计划里排了它的，加载时会自动换成这两项。",
            Retired: true,
            Sources: [DataSourceId.EmPush2]),

        new(FetchActionId.StepBoardList, "概念和行业板块", "东财 quote（菜单JSON）", QuotaGroup.Mixed,
            TimeSpan.FromSeconds(10), "每工作日",
            "概念/题材板块 + 行业板块的**名单**，**一个请求拿全量**。' + N + '"
            + "✅ **2026-09-05 改走行情中心左侧菜单那份静态 JSON**（sidemenu_new.json），"
            + "从此**不碰 push2、不用浏览器通道、不会弹图片验证码**，也不再占用成分股那边的配额。' + N + '"
            + "换之前逐条比对过：概念 504 个代码和名称跟 push2 官方名单**一个不差**，"
            + "行业只多一个三级行业（BK1362 其他多元金融）——多出来是安全侧，不会误删。' + N + '"
            + "拿不到或解析不了时**自动退回 push2 分页**（那条路会慢很多、可能要人过验证）；"
            + "名单比库里少 5% 以上则**整轮放弃写库**，保留上一次的快照——"
            + "板块是快照数据，「旧的」永远好过「半批的」。' + N + '"
            + "⚠ 板块的涨跌幅/成交额不在这一步取，由【板块指数合成】用本地成分股日K算出来回填。",
            Sources: [DataSourceId.EmQuote]),

        new(FetchActionId.StepBoardMembers, "板块成分股", "东财 push2", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(20), "每周·空闲时补",
            "逐个板块查**官方成分名单**，1000+ 个板块约 2500 个请求——这是 push2 上最耗配额的一项。' + N + '"
            + "**不能用 datacenter 的 F10 报表替代**：实测 F10 会系统性漏股（液冷服务器 170 只漏 4 只，"
            + "含美的集团、拓普集团这种链上有实际业务的大票；PCB 漏 2 只），而且漏了不报错，"
            + "会一路带进板块营收中位数这类指标里。' + N + '"
            + "抓到的条数跟接口自报的 total 对不上就**整块丢弃、下轮重抓**，绝不写半批进库。' + N + '"
            + "**跑不完是常态、也没关系**：每个板块单独落库并记进度，下一轮自动跳过已成功的"
            + "（7 天内抓过就算新鲜）；连续失败 10 个判定被限流、提前收尾。' + N + '"
            + "软依赖【板块列表】：列表没跑也能抓，用库里上次的名单，只是漏掉当天新增的板块。",
            SoftDependsOn: [FetchActionId.StepBoardList],
            Sources: [DataSourceId.EmPush2]),

        new(FetchActionId.StepFullAudit, "全库数据体检", "本地查库·不联网", QuotaGroup.Local,
            TimeSpan.FromMinutes(15), "怀疑缺数据时",
            "逐只标的对照交易日历，找出它在自己存续期内**缺掉的交易日**，记进待补名单，"
            + "交给【重新拉取失败】去补。\n"
            + "**查哪些面**（2026-09-04 扩）：个股的前复权/后复权/不复权三套日线 + ETF + 指数——"
            + "这五个面能联网补，进待补名单；板块指数（本地合成）和回测序列 day_adj（本地重算）"
            + "只报数、并提示去跑【板块指数合成】或【重算回测序列】，因为它们抓不来；"
            + "退市股不查（数据源不再更新，报了也补不到，交给【退市股收尾】）。\n"
            + "**还查K线之外的日频表**：资金净流入、融资余额、龙虎榜，以及东财那三张"
            + "（资金流明细、龙虎榜席位、大宗交易）。判据是\"某个交易日这张表一行都没有\""
            + "（全市场那天集体没有融资余额是不可能的）＋\"行数不到平时两成、疑似只抓了一半\"；"
            + "按标的比对做不到——融资余额只有两融标的有、龙虎榜只有上榜的票有，那样必然满屏误报。"
            + "这几张同样**只报不补**（各表补法不同），报出是哪一天、该跑哪一项。\n"
            + "**为什么要查这么多面**：拆成原子项之后，后复权/不复权/ETF/指数各自是独立一项，"
            + "可以被漏排、可以单独失败。只体检前复权的话，这些面缺了没人发现——"
            + "而回测吃的 day_adj 是从不复权推出来的，不复权缺一天，回测就错一天。\n"
            + "**用途**：日更末尾那个【当日覆盖率体检】只查最新一个交易日的个股前复权，挡的是"
            + "\"跑早了、数据源还没更新完\"；要是程序停了几天、或某天那轮跑挂了，中间的缺口没人发现——"
            + "这一项就是补这个的。\n"
            + "⚠ 后复权和不复权**只有腾讯给**：数据源是新浪时这两组会原样留着不动（不会被误判成"
            + "\"数据源确实没有\"），切到 Tencent 再跑【重新拉取失败】才补得上。\n"
            + "**为什么不每天跑**：全库扫描是重活（千万行级比对）。日更本身已经有检查，正常不会有缺口；"
            + "真出问题多半是别的原因（停机、断电），隔一阵子手动跑一次就够。\n"
            + "⚠ **停牌跟漏抓在数据上分不开**——都是交易日历里有、这只票没有。所以流程是：体检只负责报，"
            + "【重新拉取失败】去抓，连抓两轮拿不到才判定\"数据源确实没有\"、写进白名单，以后体检跳过它。\n"
            + "⚠ 第一次跑查出来的量通常很大（十年下来的停牌天数全在里面），补一轮可能要几小时；"
            + "沉淀进白名单之后，往后每次体检就只剩零星新增了。\n"
            + "刚过去 2 天内的日子不算缺（数据源可能还没更新完）。\n"
            + "参数里的【彻底体检】＝清空那份白名单、全部重查一遍——数据源当时抽风、后来补上了的话用它。",
            FetchActionParams.Thorough),

        new(FetchActionId.StepReparseBankPdf, "重解析已有PDF", "本地计算·不联网", QuotaGroup.Local,
            TimeSpan.FromMinutes(5), "解析规则改了之后",
            "用**当前**规则把本地已下载的银行/券商/保险年报中报重跑一遍，一个网络请求都不发。\n"
            + "为什么单独成项：解析规则一直在改（版式差异不断暴露新坑——注释角标没清干净让拨备覆盖率"
            + "变成 3.0、目录页页码被当成资本充足率），改完想全库重跑时，原来只能连带把联网下载那一大段"
            + "也跑一遍。\n"
            + "⚠ 【金融监管指标】开头本来就会先重解析一遍，所以两项都排的话这一步会做两次（幂等，"
            + "只是多花几分钟）。"),

        new(FetchActionId.FetchAll, "拉取全部", "腾讯K线 + 新浪 + 交易所 + 巨潮", QuotaGroup.Mixed,
            TimeSpan.FromHours(2), "每工作日",
            "日常收盘后的主任务：个股/指数/ETF日K、流通市值、资金净流入、中标公告、融资余额、龙虎榜。"
            + "每只标的从自己上次抓到那天续抓，所以会自动补齐前几天的断档。\n"
            + "⚠ **这是个复合项**（2026-09-02）：它等于上面那 13 个原子项按顺序跑一遍。"
            + "⚠ **已退役**：它等于上面那 13 个原子项按顺序跑一遍，留着只会让人两边都排、"
            + "同一份活抓两遍。老计划里排了它的，加载时会自动换成那 13 项（设置照搬）。",
            FetchActionParams.GlobalFetchOptions | FetchActionParams.LookbackYears,
            Retired: true),

        new(FetchActionId.FetchDay, "补指定历史日", "同「拉取全部」", QuotaGroup.Mixed,
            TimeSpan.FromHours(2), "按需",
            "只抓指定那一天，不看每只股票上次抓到哪天、不补断档，耗时却和「拉取全部」一样。"
            + "日常请用「拉取全部」，这项只在要补某个过去的具体日期时才排。\n"
            + "⚠ **已退役**（2026-09-02）：改用模式——把要补的那几项（个股日K·前复权 / 资金净流入 / "
            + "中标公告 / 融资余额 / 龙虎榜）的模式设成「只抓某一天」、填上日期就行，"
            + "还能只补其中一两项，不用为了一天的龙虎榜跑两小时的全市场K线。"
            + "老计划里排了它的，加载时会自动换成等价的那一串（日期照搬）。",
            FetchActionParams.Date,
            Retired: true),

        new(FetchActionId.RetryFailed, "重新拉取失败", "各源（按失败名单）", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(10), "抓取之后",
            "重试失败名单（K线/市值/资金流/指数成分/权重/股东/分红各自独立记录）。"
            + "名单为空时秒过。注意：后台还有一套自动重试（跑完 1 小时后、且不早于当天 21:00，"
            + "最多 3 轮），它跟计划不冲突，这一项是给你想在固定时刻强制重试一次时用的。",
            FetchActionParams.GlobalFetchOptions,
            // 它消费的名单是前面那些步骤产生的，尤其是体检查出的"今天还缺谁"
            SoftDependsOn: [FetchActionId.StepDayCoverage],
            // ⚠ 必须显式声明源（2026-09-05）。不写的话 Mixed 会兜底成**全部 9 个联网源**
            //    （见 FetchTaskInfo.EffectiveSources 的"保守"分支），于是它一跑，
            //    计划里没有任何一项能通过 IsSourceBusy——实测把计划停摆了 2 小时 41 分，
            //    期间【拉取分档资金流】【拉取财务报表】排队 30 分钟后被硬超时掐断记成"失败"，
            //    而它们其实一个请求都没发。
            //
            //    这三个是按它真正会调的东西数出来的：
            //      · Tencent  ── K线主源（TencentThenSinaBarFetcher）
            //      · Sina     ── K线回退、流通市值、资金净流入、指数成分、股东、分红，全在新浪
            //      · CsIndex  ── 指数权重（中证 OSS）
            //    收窄之后，用 push2 / push2his / datacenter / quote 的那些项就能跟它并行跑。
            //
            //    ⚠ 唯一的例外：把 BarSource 配成 "EastMoney" 时 K线会走 push2his
            //    （EastMoneyBarFetcher 打的是 push2his.eastmoney.com/api/qt/stock/kline/get），
            //    那种配置下这里就少声明了一个源。眼下不管它——东财在本机网络下常年连不上，
            //    默认也不是它；真要长期用东财当K线源，这里得把 EmPush2His 加回来。
            Sources: [DataSourceId.Tencent, DataSourceId.Sina, DataSourceId.CsIndex]),

        new(FetchActionId.RepairQfq, "重取前复权", "腾讯（回退新浪）", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(30), "空闲时",
            "把**除权后基准变了**的股票，前复权历史整段按新基准重取。\n"
            + "数据源的前复权是「原价 − 之后累计分红送配」，某只票一分红，它全部历史的前复权值就都变了；"
            + "而本地历史是分批入库的，不重取就会在接缝处出现假跳空（实测有股票虚增 50%）。\n"
            + "日常抓取会自动比对最近 400 天、把除权了的票记进待重取名单（参数格里显示还剩多少只）。"
            + "**建议重复规则设成「空闲时」**：分红季一天可能上百只，每只要重抓十年，让它在空档里慢慢补。\n"
            + "取过的不会重取；没取完不要紧，下一轮日常比对还会把它检出来。后复权不受除权影响，不用重取。",
            // 待重取名单是抓前复权时顺带比对出来的，所以要排在它后面
            SoftDependsOn: [FetchActionId.StepStockDayBars], SupportsPartialRun: true,
            Sources: [DataSourceId.Tencent, DataSourceId.Sina]),

        new(FetchActionId.FetchEarningsSchedule, "拉取财报预约日", "巨潮", QuotaGroup.Mixed,
            TimeSpan.FromSeconds(20), "每工作日",
            "抓定期报告的**预约披露日**——交易所要求上市公司预约本期定期报告什么时候披露。\n"
            + "用途：主动仓的「财报日」列自动填上，不用再手工录；选股时也能避开「马上要出财报」的票"
            + "（跨财报持仓是回测参数里没有的事件风险）。\n"
            + "**必须每天跑，不能抓一次当定论**：实测沪市 2000 条样本里 12% 改过披露日期，"
            + "而且**提前的比延后的还多**（提前 55%、延后 44%，最多提前 44 天、最多延后 62 天）。"
            + "提前那半边尤其要紧——按原日期盯的话，财报已经出了你还不知道。\n"
            + "成本几乎为零：一期全市场 5500 条一个请求 0.3 秒就拿回来了，每次全量覆盖，不用管增量。"
            + "⚠ 数据源只给最近两期，所以有空窗：上一期都披露完、下一期预约表还没发布时，这一列会是空的。",
            SupportsPartialRun: false,
            Sources: [DataSourceId.Cninfo]),

        new(FetchActionId.FetchEarningsForecast, "拉取业绩预告/快报", "东财", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(3), "每工作日（披露密集期尤其要跑）",
            "抓**业绩预告**和**业绩快报**——本地此前完全没有这两份数据。\n"
            + "**比正式财报早一个月以上**：Q3 预告 10 月中出、财报 10 月底才出；年报预告 1 月底、年报要等到 4 月。\n"
            + "**强制披露规则正好对准剧变**：净利变动超 50%、扭亏、首亏都必须预告，也就是说"
            + "「业绩发生剧变的公司」全在这张表里——判断产业景气最该盯的就是这批。\n"
            + "预告里的**变动原因**是公司自述的（法定披露文件，不是研报转述），能从中提"
            + "「产品涨价」「供不应求」「产能满负荷」这类词，用来分辨真涨价还是纯讲故事。\n"
            + "增量按公告日走，并且**从本地最新那一天本身重抓**：同一天里公司是陆续发的，"
            + "从次日开始会漏掉当天后半段（主键去重，重抓不会产生重复行）。\n"
            + "首次全量约 20 万行、400 页、3 分钟；之后增量每次十几页。\n"
            + "⚠ 没有回退源——新浪/腾讯/交易所都不提供结构化预告，巨潮只有公告原文。东财不可用时整项跳过。",
            SupportsPartialRun: false,
            Sources: [DataSourceId.EmDataCenter]),

        new(FetchActionId.FetchLhbSeat, "拉取龙虎榜席位", "东财", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(4), "每工作日",
            "抓龙虎榜的**买卖前五营业部明细**——本地此前完全没有这份数据。\n"
            + "⚠ 跟已有的【龙虎榜】**不是一回事、不替换它**：那一项（新浪）只有"
            + "「某天某股上榜了、原因是涨跌幅偏离、成交额多少」，**没有营业部名单**。\n"
            + "而龙虎榜的全部价值就在于看**是谁在买**：机构专用席位、知名游资、还是深股通。"
            + "17 万行只有壳，补上这块才有用。\n"
            + "带营业部代码，所以能跨时间追踪同一个席位——自建游资库、算某个席位的历史胜率都靠它；"
            + "接口还直接给了该营业部近期上榜后的 3 日胜率。\n"
            + "**首次全量很久**：买方 131 万 + 卖方 133 万行、约 5200 页，按月切片跑（东财深分页到"
            + "上千页会拒绝），预计数小时。中途断了没关系——每 2000 行就落一次库，重跑从本地"
            + "最新交易日接着走，不会从头再来。\n"
            + "之后增量每次只有 40 页出头，几分钟。\n"
            + "⚠ 没有回退源——新浪/交易所都不提供结构化的营业部明细。",
            SupportsPartialRun: false,
            Sources: [DataSourceId.EmDataCenter]),

        new(FetchActionId.FetchMarketEvents, "拉取市场事件", "东财", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(25), "每工作日",
            "一次抓四份本地此前**完全没有**的数据：\n"
            + "**大宗交易**——带买卖双方营业部和折溢价率。大幅折价通常是股东减持套现，"
            + "溢价接盘可能是产业资本；跟龙虎榜席位是互补的两块筹码信息。"
            + "⚠ 这张表**只有一半是股票**（实测 A股 48%、可转债/债券 41%、基金 2%），用之前要按代码前缀筛，"
            + "否则折溢价分布被债券带偏；折溢价率存的是**小数**（-0.0628 = 折价 6.28%）。\n"
            + "**机构调研**——带参与调研的机构名单。一家公司突然被几十家机构集中调研，"
            + "常常早于股价异动。属软信号：它证明「有人在关注」，不证明「基本面变好」。"
            + "⚠ **接口只保留滚动一年**（实测自报 28.4 万行、最早只到一年前），跟别的几项能回溯到 2016 不同——"
            + "长历史只能靠每天抓、慢慢养。\n"
            + "**限售解禁**——⚠ **含未来的解禁计划**（实测有 2035 年的），是「日程表」不是「历史表」，"
            + "所以每次全量重取（3 万行、63 页）。解禁是次新股最明确的时间节点。\n"
            + "**股东增减持**——跟已有的十大股东表互补：那张说「季末谁持有多少」，"
            + "这张说「期间谁在买卖、多少、什么价」，是明确的内部人信号。\n"
            + "四项各自独立失败：一项挂了不影响其余（覆盖面和重要性本来就不一样）。\n"
            + "首次全量约 110 万行、25 分钟；之后增量每次几十页。\n"
            + "⚠ 没有回退源——这四份数据新浪/腾讯/交易所/巨潮都不提供结构化版本。",
            SupportsPartialRun: false,
            Sources: [DataSourceId.EmDataCenter]),

        new(FetchActionId.FetchMoneyFlowDetail, "拉取分档资金流", "东财", QuotaGroup.Mixed,
            TimeSpan.FromHours(3), "季度定期组·空闲时补",
            "抓**分档**资金流：超大单/大单/中单/小单各自的净额和净占比，共 10 个维度。\n"
            + "⚠ 跟已有的【资金净流入】**不是替换、是同一件事的不同精度**：那张表 1077 万行，"
            + "但每行只存了一个「主力净额合计」。\n"
            + "为什么要拆开看：同样是「主力净流入 1 亿」，**超大单进、小单出**（机构在建仓）跟"
            + "**大单进、超大单出**（游资接力）含义完全相反，合计数把这个信息抹平了。"
            + "判断一波行情是谁在买，靠的就是这个结构。\n"
            + "⚠ **接口只给最近约 120 个交易日**，lmt=0 也突破不了——所以拿不到长历史，"
            + "历史深度只能靠定期抓取慢慢养。短期内做不了长周期回测，但看「当下这波是谁在买」够用。\n"
            + "⚠ **只能按股票查**，没有「某天全市场」的入口，全市场一轮 5500+ 个请求、2 秒间隔约 3 小时。\n"
            + "断点续传按「这只票今天抓过没有」判断（接口是滚动窗口，没有增量入口，"
            + "不能像别的任务那样用数据日期做水位线）。跑不完下轮接着来，连续 15 只失败会判定被限流、提前收尾。",
            SupportsPartialRun: true,
            Sources: [DataSourceId.EmPush2His]),

        new(FetchActionId.FetchRawBars, "补不复权历史", "腾讯（回退新浪）", QuotaGroup.Mixed,
            TimeSpan.FromHours(2), "一次性（补完就不用再跑了）",
            "抓**不复权**日线（原始成交价）。\n"
            + "它是全库唯一**不随分红变化**的价格序列——抓一次永远有效，不会像前复权那样一除权就得整段重取。\n"
            + "用途：算回测专用的复权序列（下面那一项），以及回答「某天实际成交价是多少」。\n"
            + "**这是个一次性任务**：日常那一根增量已经并进【拉取全部】和【拉取当天】了，跟后复权一样。\n"
            + "这里只负责首次回补——把每只个股补到跟前复权一样长，实测约 24700 个请求、2 小时出头（腾讯源约 3 请求/秒，不像新浪报表接口那样卡配额）。\n"
            + "右边的待办量正常应该是 0；哪天不是 0（比如刚拉过区间历史、或新增了退市股），点一次执行补上即可。\n"
            + "⚠ **已退役**（2026-09-02）：【个股日K·不复权】把模式设成「首次整段回补」就是这一项，"
            + "跑的是同一段代码。老计划里排了它的，加载时会自动换过去。",
            SupportsPartialRun: true,
            Retired: true,
            Sources: [DataSourceId.Tencent, DataSourceId.Sina]),

        new(FetchActionId.RebuildAdjSeries, "重算回测序列", "本地计算·不联网", QuotaGroup.Local,
            TimeSpan.FromMinutes(20), "空闲时",
            "用不复权价 × 本地算的**乘法式**复权因子，生成回测专用的价格序列（day_adj）。\n"
            + "为什么不用数据源的后复权：那份是「送转乘、分红加」的混合式，加法项会阻尼波动——实测收益率"
            + "相对真实值 工商银行 ×0.625、中国石化 ×0.542、茅台 ×0.855，高股息低价股被压得最狠，"
            + "拿它回测会让这类股票显得「波动小、回撤浅」，因子排序被扭曲。\n"
            + "本序列非除权日的收益率**精确等于**真实收益率，写完还会自检一遍。\n"
            + "每条除权记录都用当天实际跳空校验过：对不上的一律不算数（破产重整的资本公积转增是给债权人的，"
            + "不分配给原股东、不产生除权，而数据源会把它当普通转增列出来）。\n"
            + "⚠ 前置：不复权历史。纯本地计算，不发任何网络请求。",
            // 前置指向拆开后的那一项（老的【补不复权历史】已退役）
            DependsOn: FetchActionId.StepStockRawBars, SupportsPartialRun: true),

        new(FetchActionId.FetchBoards, "拉取板块", "东财", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(5), "每工作日～每周",
            "概念/题材板块 + 行业板块的行情与成分股，拉完自动用本地K线重算板块指数（不联网）。\n"
            + "⚠ **已退役**（2026-09-02）：拆成了【板块行情与成分】+【板块指数合成】两项——"
            + "抓取失败不再连累合成，改了合成算法也能单独重算、不用重抓一遍板块。"
            + "老计划里排了它的，加载时会自动换成这两项。",
            Retired: true,
            Sources: [DataSourceId.EmPush2]),

        new(FetchActionId.FetchIndustry, "拉取行业分类", "交易所 + 新浪", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(2), "季度",
            "证监会两级行业分类。行业极少变动，跟财报同频跑一次即可，整体覆盖写入、反复跑无副作用。",
            Sources: [DataSourceId.Exchange, DataSourceId.Sina]),

        new(FetchActionId.FetchStockBoardMap, "拉取个股行业与题材", "东财", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(8), "季度",
            "东财**三级**行业分类 + 个股题材归属（带入选理由），一次抓取同时拿到两份。\n"
            + "⚠ **不替换上面那项**，两份并存：东财覆盖 5644/5753 只，剩下约 109 只仍要靠证监会分类兜底。\n"
            + "为什么要加它——证监会那套粒度不够用：实测 **1867 只（32.5%）大类为空**、只能退回门类，"
            + "而「制造业」一个门类就装了 3596 只（占 62%）。拿它做行业中性化等于没中性化，"
            + "FactorLab 的中性 IC 一直不准，根子在这。东财是一级 31 / 二级 128 / 三级 337，"
            + "最大的三级行业也才 627 只。\n"
            + "顺带落库的**题材入选理由**（原文取自互动易回复、公告）加上精确匹配标记，"
            + "是判断「实质业务 vs 蹭概念」目前唯一能自动化的判据。\n"
            + "**走 datacenter，不碰 push2**：push2 要人工在浏览器过一道反爬验证、验证还会过期，"
            + "不适合无人值守。\n"
            + "这张表没有时间维度、是当下快照，所以每次全量重取（约 9.4 万行、188 页、几分钟）。"
            + "先抓到第一批数据才清表——接口挂了的话库里旧的原样保留，不会被清空。",
            Sources: [DataSourceId.EmDataCenter]),

        new(FetchActionId.FetchIndexCons, "指数成分/权重", "新浪 + 中证", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(15), "季度",
            "指数成分名单（新浪）和成分权重（中证 OSS）。中证那侧偏不稳，失败进重试名单。\n"
            + "⚠ **已退役**（2026-09-02）：拆成了【指数成分名单】+【指数权重】+【ETF指数映射】三项——"
            + "成分和权重各自入库、各自能单独用，而中证那侧失败率高，合在一起时它会把整项标成失败。"
            + "老计划里排了它的，加载时会自动换成这三项。",
            Retired: true,
            Sources: [DataSourceId.Sina, DataSourceId.CsIndex]),

        new(FetchActionId.FetchShareholder, "拉取股东数据", "新浪", QuotaGroup.Sina,
            TimeSpan.FromHours(1.5), "季度",
            "逐只抓股东户数 + 十大股东 + 十大流通股东的全部历史。"
            + "十大流通股东里的「香港中央结算」就是北向资金的名义持有人。"),

        new(FetchActionId.FetchFinancials, "拉取财务报表", "新浪(报表接口·配额最严)", QuotaGroup.Sina,
            TimeSpan.FromMinutes(90), "季度·跨天分轮",
            "三张表 52 个科目的全部报告期。⚠ 这个接口配额很严，已单独降速到约 10 请求/分钟、"
            + "每轮上限 300 只（约 90 分钟），全市场要跨几天补完。"
            + "**建议把重复规则设成「空闲时」**：程序空着就自己补一批，到点前自动收尾给定时任务让路，"
            + "跨几天慢慢啃完。设成「每月某天」只会跑一轮 300 只，全市场根本补不完。\n"
            + "「要不要抓」按每只票的**实际披露日**判断（来自【拉取财报预约日】），不是法定截止日——"
            + "所以那一项要是好几天没跑成，这边会以为没人披露而少取。",
            SupportsPartialRun: true,
            SoftDependsOn: [FetchActionId.FetchEarningsSchedule]),

        new(FetchActionId.FetchDividend, "拉取分红送配", "新浪", QuotaGroup.Sina,
            TimeSpan.FromHours(2), "年度",
            "逐只抓历年全部分红方案，**同一次请求顺带抓配股**（源页面上分红和配股是两张表，不额外花请求）。\n"
            + "做股息率因子、核对除权除息日都靠分红这张表。\n"
            + "配股是A股第四类除权事件（前三类是现金分红/送股/转增），2026-09-01 起才抓——漏掉它，"
            + "回测序列 day_adj 会在配股除权日凭空多一根阴线：实测招商证券 2020 年那次 10配3@7.46 "
            + "让十年累计收益少算了 25 个百分点，中信证券少 12 个。配股集中在**银行和券商**，正是底仓的重点。\n"
            + "⚠ 抓完要再跑一次【重算回测序列】，配股才会体现到 day_adj 上。"),

        new(FetchActionId.BankRegulatory, "金融监管指标", "新浪(页面 + PDF文件)", QuotaGroup.Sina,
            TimeSpan.FromHours(1), "半年（年报/中报后）",
            "下载银行/券商/保险的年报和中报 PDF，解析三张报表里没有的监管指标"
            + "（不良率、拨备覆盖率、资本充足率、风险覆盖率、偿付能力充足率等）。"
            + "每次都会先用当前规则把本地已有 PDF 重解析一遍（不联网，几分钟）。"
            + "⚠ 前置：财务报表——机构类型是靠特征科目认出来的。\n"
            + "没披露的机构不会去翻公告列表（按实际披露日判断，来自【拉取财报预约日】）——"
            + "这一段限流很紧，披露季前期挨家去查全是空转。",
            DependsOn: FetchActionId.FetchFinancials,
            SoftDependsOn: [FetchActionId.FetchEarningsSchedule]),

        new(FetchActionId.ImportManual, "导入手工数据", "本地文件", QuotaGroup.Local,
            TimeSpan.FromSeconds(5), "人填完 CSV 后",
            "把填好的「待手工回填清单.csv」写回库。文件没填就是空跑，无副作用。",
            DependsOn: FetchActionId.BankRegulatory),

        new(FetchActionId.BackfillDaily, "一键补齐每日历史", "交易所", QuotaGroup.Exchange,
            TimeSpan.FromMinutes(30), "按需补洞",
            "把融资余额、龙虎榜的历史从K线最早那天补到今天，跳过本地已有的交易日。幂等、可反复跑。\n"
            + "⚠ **已退役**（2026-09-02）：【融资余额】和【龙虎榜】各自把模式设成「首次整段回补」"
            + "就是这一项的两半，跑的是同一段代码——好处是能只补其中一样（两家源不同、失败也互不相干）。"
            + "老计划里排了它的，加载时会自动换成这两项。",
            Retired: true),

        new(FetchActionId.FetchYear, "拉取区间数据", "腾讯 + 新浪 + 交易所", QuotaGroup.Mixed,
            TimeSpan.FromHours(1.7), "一次性回补",
            "**向前回补**：按年份区间往回补更早的历史（K线/退市股/资金流/融资/龙虎/公告），"
            + "只补本地还缺的部分。跟日更那些「向后增量」方向相反——那边每天往后追一段，这边把历史往前延长。\n"
            + "典型用法：原来只取了近 3 年，现在想要近 10 年，填 2016~2022 跑一次。\n"
            + "⚠ 别跟【个股日K】那行的「新标的补 N 年历史」搞混：那个只管**本地一条K线都没有**的新标的；"
            + "已经抓过的永远从自己的水位线往后续，改大它不会让已有标的的历史往前延长。\n"
            + "⚠ 快照型数据（流通市值/板块成分/指数成分权重）天生只有\"当下\"、没有历史可取，会明确跳过并说明原因。\n"
            + "补完之后，期间除过权的票会自动记进【重取前复权】的待办名单——新补的那段用的是数据源当前基准，"
            + "跟库里较新那段的基准可能对不上，接缝处会有假跳空。\n"
            + "⚠ 这一项**故意保持复合**（2026-09-02 评估）：它内部各段共享同一次"
            + "\"每只标的本地最早是哪天\"的预取，拆开的话每段都要把这几 GB 的库各扫一遍；"
            + "而它本来就是一次性整批回补、几乎没有\"只补某一类\"的用法。要只补某一类历史时，"
            + "用对应项的「首次整段回补」模式更省。",
            FetchActionParams.YearRange | FetchActionParams.GlobalFetchOptions | FetchActionParams.Keywords),

        new(FetchActionId.OptimizeDatabase, "优化数据库", "本地", QuotaGroup.Local,
            TimeSpan.FromMinutes(5), "一次性",
            "给几张大表补建二级索引。不联网。建完就是持久对象，之后不用再跑。"),
    ];

    /// <summary>
    /// 【拉取全部】等价的那 13 个原子项，**顺序就是执行顺序**（2026-09-02）。
    ///
    /// 这份清单是"拆完之后功能没丢"的唯一权威：【每日收盘后】模板照它展开，单元测试也拿它
    /// 核对模板有没有漏项。以后再往每日流程里加步骤，加在这里 + 目录里就够了。
    /// </summary>
    public static readonly IReadOnlyList<FetchActionId> FetchAllSteps =
    [
        FetchActionId.StepRoster,
        FetchActionId.StepNetInflow,
        FetchActionId.StepAnnouncements,
        FetchActionId.StepIndexBars,
        FetchActionId.StepStockDayBars,
        FetchActionId.StepStockHfqBars,
        FetchActionId.StepStockRawBars,
        FetchActionId.StepEtfBars,
        FetchActionId.StepDelistedTails,
        FetchActionId.StepBoardIndex,
        FetchActionId.StepMargin,
        FetchActionId.StepLhb,
        FetchActionId.StepDayCoverage,
    ];

    /// <summary>计划页上真正能排的动作——退役的不在其中（<see cref="FetchActionInfo.Retired"/>）。</summary>
    public static readonly IReadOnlyList<FetchActionInfo> Active =
        All.Where(a => !a.Retired).ToList();

    /// <summary>
    /// **必须等当天收盘之后**才跑的动作（2026-09-02）。不在这份名单里的＝什么时候跑都行。
    ///
    /// 集中列在这里而不是逐条写进目录：这是一份需要**整体核对**的清单——
    /// 少标一个就意味着那一项可能天天在盘中跑、静默拿到不完整的数据。
    ///
    /// 判据是"这一项的数据是不是当天收盘后才定稿"：
    ///   · K线各条 / 指数 / ETF：盘中是未收盘价，而且数据源盘后**逐步**更新，早跑会静默漏一批；
    ///   · 流通市值：盘中跑记的是今天、值却是盘中价；
    ///   · 资金净流入：当日资金流盘中不完整；
    ///   · 龙虎榜：交易所当晚才发布，早跑就是空的；
    ///   · 板块指数合成 / 当日覆盖率体检：读的是当天的个股K线（顺序上也必须排在它后面）；
    ///   · 重新拉取失败：它补的就是当天缺的那批，早跑等于白跑。
    ///
    /// **不在名单里的典型**：融资余额（两所 T+1 发布，抓的本来就是前几个交易日）、
    /// 中标公告（回看 14 天、重复扫安全）、退市股收尾（补的是历史）、
    /// 板块行情与成分（成分是慢变数据）、所有季度/不定期数据、所有本地计算。
    /// </summary>
    public static readonly IReadOnlySet<FetchActionId> AfterCloseActions = new HashSet<FetchActionId>
    {
        FetchActionId.StepRoster,
        FetchActionId.StepNetInflow,
        FetchActionId.StepIndexBars,
        FetchActionId.StepStockDayBars,
        FetchActionId.StepStockHfqBars,
        FetchActionId.StepStockRawBars,
        FetchActionId.StepEtfBars,
        FetchActionId.StepLhb,
        FetchActionId.StepBoardIndex,
        FetchActionId.StepDayCoverage,
        FetchActionId.RetryFailed,
        // 退役的两个复合项也标上：老计划迁移前的那一刻界面还会用到它们的信息
        FetchActionId.FetchAll,
        FetchActionId.FetchDay,
    };

    public static DataReadiness ReadinessOf(FetchActionId id) =>
        AfterCloseActions.Contains(id) ? DataReadiness.AfterClose : DataReadiness.Anytime;

    /// <summary>
    /// 一个动作**默认归哪个组**（2026-09-02 分组用）。只在两处用到：建默认计划、
    /// 把老的平铺计划迁进组。之后用户怎么挪都行，这份映射不再干预。
    ///
    /// 归组看的是"什么时候跑"，不是"数据源"或"数据种类"——组本来就是排期单位。
    /// </summary>
    public static PlanGroupKind DefaultGroupOf(FetchActionId id) => id switch
    {
        // ── 每天产生的新数据（含跟着它们走的本地重算和补漏）──
        // 【重取前复权】【重算回测序列】也在这儿：前者修的是"基准漂移导致历史K线出错"
        // （接缝假跳空，实测有股票虚增 50%），后者落后就等于回测用旧序列——都不是"晚几天没关系"的事。
        // 它们要不要做由数据判断（待重取名单 / 待重算量），名单空时秒过、一个请求都不发。
        _ when FetchAllSteps.Contains(id) => PlanGroupKind.Daily,
        FetchActionId.StepBoardList or FetchActionId.FetchEarningsSchedule
            or FetchActionId.RetryFailed or FetchActionId.RepairQfq
            or FetchActionId.RebuildAdjSeries
            or FetchActionId.FetchAll or FetchActionId.FetchDay
            or FetchActionId.FetchBoards => PlanGroupKind.Daily,

        // 东财的三项（2026-09-03）也归日更：抓的都是当天产生的东西——业绩预告随时公告、
        // 龙虎榜盘后发布、大宗交易和股东增减持也是按日出的。**增量都很快**（各自几十页、
        // 几分钟），排进日更不会拖累这一组。
        // ⚠ 首次全量是另一回事（龙虎榜席位 264 万行要数小时）：新加的计划项默认不启用
        // （见 FetchPlan.Normalize 的注释），先用那一行的【执行】手工跑完首轮，再勾启用转日常增量。
        FetchActionId.FetchEarningsForecast or FetchActionId.FetchLhbSeat
            or FetchActionId.FetchMarketEvents => PlanGroupKind.Daily,

        // ── 按周期更新、晚几天没关系的 ──
        FetchActionId.FetchIndustry
            // 【个股行业与题材】（2026-09-03）跟证监会分类同组同频：行业归属变动很慢，季度一轮够了。
            or FetchActionId.FetchStockBoardMap
            or FetchActionId.StepIndexCons or FetchActionId.StepIndexWeight
            or FetchActionId.StepEtfIndexMap or FetchActionId.FetchShareholder
            or FetchActionId.FetchDividend or FetchActionId.BankRegulatory
            or FetchActionId.StepReparseBankPdf or FetchActionId.ImportManual
            or FetchActionId.FetchFinancials
            // 分档资金流归定期而不是日更：接口是 120 天滚动窗口、**没有增量入口**，每轮都得把
            // 5500 只重抓一遍（约 3 小时），放日更会把每晚占满。而这一组是「空闲时补」，
            // 正适合这种"没有时效压力但耗时长"的活——跟财务报表同样的处境。
            or FetchActionId.FetchMoneyFlowDetail
            // 【板块成分股】(2026-09-04 拆出来）跟分档资金流同样的处境：请求量大（约 2500 个）、
            // 没有时效压力（7 天内抓过就算新鲜），正适合"空闲时补"，别占着日更那一段。
            or FetchActionId.StepBoardMembers
            or FetchActionId.FetchIndexCons => PlanGroupKind.Periodic,   // 最后这个已退役

        // ── 按需启动：想起来才做的一次性活（往回补历史、全库体检、建索引）──
        _ => PlanGroupKind.OnDemand,
    };

    /// <summary>
    /// 计划行里那些参数格的**默认值**（2026-09-02）。
    ///
    /// 为什么要有它：这些格子原来是留空的、留空表示"用【手动】页那个框里的值"。可空着的格子
    /// 看不出会用什么值——人得知道那一行到底按几年回看、按什么关键词搜，才能判断要不要改。
    /// 所以建计划项时直接把默认值填进去（跟【手动】页那几个框的初始值一致）。
    /// 清空之后仍旧退回全局值，这条兜底没变。
    /// </summary>
    public static string? DefaultParamText(FetchActionId id, FetchActionParams which) => which switch
    {
        // 只决定"本地一条K线都没有的标的第一次抓多久历史"，已抓过的永远从自己的水位线续
        FetchActionParams.LookbackYears => "3",
        // 中标/订单公告的搜索词，逗号分隔
        FetchActionParams.Keywords => "中标,签订合同",
        // 区间回补默认给"去年整年"，起止都填上人才看得出它要抓哪一段
        FetchActionParams.YearRange => (DateTime.Today.Year - 1).ToString(),
        // 日期故意**不填**：填死了明天就过期，而留空的语义正好是"今天"
        _ => null,
    };

    /// <summary>
    /// "算收盘后"的最早时刻。15:00 是连续竞价收盘，15:00~15:30 还有盘后定价交易，
    /// 数据源再逐步更新一阵——所以 16:00 之前开跑一定不完整。这只是**校验下限**，
    /// 实际建议 18:00 之后（默认计划就是 18:00）。
    /// </summary>
    public static readonly TimeOnly EarliestAfterClose = new(16, 0);

    /// <summary>一个退役动作该换成哪一项、用什么模式跑。</summary>
    /// <param name="CarryDate">true＝把原来那行填的日期一起搬过来（只对「只抓某一天」有意义）。</param>
    public sealed record RetiredExpansion(FetchActionId Into, FetchMode Mode = FetchMode.Incremental, bool CarryDate = false);

    /// <summary>
    /// 退役动作 → 等价的原子项（2026-09-02）。老计划加载时按这张表原地替换，见
    /// <see cref="FetchPlan.MigrateRetired"/>。
    ///
    /// "等价"的标准是**跑完之后库里的东西一样**：比如【补指定历史日】展开成同样这 13 项，
    /// 其中能按天抓的 5 项设成「只抓某一天」、其余仍走水位线增量——这正是老代码内部的行为
    /// （指数/ETF/后复权/不复权在"补指定历史日"里一直是增量，不是只抓那天）。
    /// </summary>
    public static readonly IReadOnlyDictionary<FetchActionId, IReadOnlyList<RetiredExpansion>> RetiredInto =
        new Dictionary<FetchActionId, IReadOnlyList<RetiredExpansion>>
        {
            [FetchActionId.FetchAll] = FetchAllSteps.Select(a => new RetiredExpansion(a)).ToList(),

            // ⚠ 这里查 All 而不是调 Info()：静态字段按声明顺序初始化，Info 靠的那个字典
            //   还在下面、这会儿是 null。
            [FetchActionId.FetchDay] = FetchAllSteps.Select(a =>
                All.First(x => x.Id == a).SupportedModes.HasFlag(FetchMode.SpecificDay)
                    ? new RetiredExpansion(a, FetchMode.SpecificDay, CarryDate: true)
                    : new RetiredExpansion(a)).ToList(),

            [FetchActionId.FetchRawBars] =
                [new(FetchActionId.StepStockRawBars, FetchMode.FirstBackfill)],

            [FetchActionId.BackfillDaily] =
            [
                new(FetchActionId.StepMargin, FetchMode.FirstBackfill),
                new(FetchActionId.StepLhb, FetchMode.FirstBackfill),
            ],

            [FetchActionId.FetchBoards] =
                [new(FetchActionId.StepBoardList), new(FetchActionId.StepBoardMembers),
                 new(FetchActionId.StepBoardIndex)],

            // 2026-09-04 拆分：列表那 10 个请求不该跟 2500 个成分股请求抢同一批配额
            [FetchActionId.StepBoards] =
                [new(FetchActionId.StepBoardList), new(FetchActionId.StepBoardMembers)],

            [FetchActionId.FetchIndexCons] =
            [
                new(FetchActionId.StepIndexCons),
                new(FetchActionId.StepIndexWeight),
                new(FetchActionId.StepEtfIndexMap),
            ],
        };

    /// <summary>
    /// **日更组的默认排法**（2026-09-02 用户定）——按数据性质分四族，读起来一目了然：
    ///
    ///   K线族（1~6）      名册与市值 → 个股三条日K → ETF → 指数
    ///   资金交易族（7~10）  资金净流入 → 融资余额 → 龙虎榜 → 中标公告
    ///   收尾与合成（11~15） 退市股收尾 → 板块行情与成分 → 板块指数合成 → 当日覆盖率体检 → 财报预约日
    ///   补漏与重算（16~18） 重新拉取失败 → 重取前复权 → 重算回测序列
    ///
    /// ════ 这里面只有四条是**硬约束**，其余随便排 ════
    ///   ① 名册要在所有"逐只抓"之前——它决定这一轮抓哪些票；
    ///   ② 板块指数合成 要在 板块行情与成分 **和** 个股日K 之后（拿当天的成分股 + 当天K线合成）；
    ///   ③ 当日覆盖率体检 要在 指数日K **和** 个股日K 之后（拿上证指数最新一根当交易日锚，再查个股缺不缺）；
    ///   ④ 补漏与重算那三项要在最后，而且重算回测序列要在不复权日K之后——它们消费的正是
    ///      前面那些步骤产生的名单（失败名单、漂移名单、待重算量）。
    /// 改顺序前先对一遍这四条，单元测试也守着它们。
    /// </summary>
    public static readonly IReadOnlyList<FetchActionId> DailyOrder =
    [
        // ── K线族 ──
        FetchActionId.StepRoster,
        FetchActionId.StepStockDayBars,
        FetchActionId.StepStockRawBars,
        FetchActionId.StepStockHfqBars,
        FetchActionId.StepEtfBars,
        FetchActionId.StepIndexBars,
        // ── 资金与交易 ──
        FetchActionId.StepNetInflow,
        FetchActionId.StepMargin,
        FetchActionId.StepLhb,
        // 【市场事件】大宗交易/机构调研/限售解禁/股东增减持，都是按日出的筹码面信息。
        // 首轮 25 分钟、之后几分钟，排在这儿不碍事。
        FetchActionId.FetchMarketEvents,
        FetchActionId.StepAnnouncements,
        // 【业绩预告/快报】跟公告放一起：都是"公司今天发了什么"，而且预告本来就是一种公告。
        // 首轮 3 分钟、之后十几页。
        FetchActionId.FetchEarningsForecast,
        // ── 收尾与合成 ──
        FetchActionId.StepDelistedTails,
        // 【当日覆盖率体检】提到板块**之前**（2026-09-03）：它只吃指数日K + 个股日K
        // （见它自己的 SoftDependsOn），跟板块半点关系没有。而板块那一步换成东财之后从 5 分钟
        // 涨到 20 分钟、还可能因为限流跨轮——体检排在它后面的话，"今天漏了谁"这个当天最该
        // 知道的结论会被一个跟它无关的慢活拖住。
        FetchActionId.StepDayCoverage,
        FetchActionId.FetchEarningsSchedule,
        // 【板块成分】+【板块指数合成】挪到日更靠后（2026-09-03）：换成东财之后要逐个板块查
        // 官方成分名单（1000+ 个板块、约 20 分钟，首轮还可能被限流跨几轮才抓完）。
        // 前面那些"当天必须拿到"的数据不该等它。两项必须**挨着**：合成不仅要用刚抓到的成分，
        // 还负责把板块涨跌幅/成交额回填进 Board 表——只跑前者的话热度页涨跌幅会是 0。
        FetchActionId.StepBoardList,
        FetchActionId.StepBoardIndex,
        // ── 补漏与本地重算（消费前面产生的名单，所以在最后）──
        FetchActionId.RetryFailed,
        FetchActionId.RepairQfq,
        FetchActionId.RebuildAdjSeries,

        // 【龙虎榜席位】排在**整组最末**（2026-09-03）。
        //
        // 它是日频数据、本该跟 StepLhb 挨着（那个抓"谁上榜了"，这个抓"是谁买的"），但**首轮
        // 是 264 万行、要跑几个小时**。日更组是「定时项」——跑多久算多久、后面顺延、不受时间窗
        // 约束（见 PlanRunner.FindIdleTask 的注释），所以把它排在中间的话，首轮那几小时会把
        // 后面的公告、板块、板块指数合成、当日覆盖率体检全堵在后面，当天的核心数据就废了。
        //
        // 放最末则最坏情况也只是"它自己跑到半夜"：前面该落库的早落完了。而且它每抓完一个月就
        // 落一次库、按本地最新交易日续抓，所以就算今晚没跑完，明晚接着来，几天自然补齐。
        // 首轮过后增量每次只有几十页、几分钟，那时排在哪儿都无所谓了。
        FetchActionId.FetchLhbSeat,
    ];

    /// <summary>
    /// 「季度定期」组的默认排法（2026-09-02）。整组「每月 1 号到期 + 空闲时补」。
    ///
    /// ⚠ 顺序上有两条依赖，别按"快的排前面"随手挪：
    ///   · 【金融监管指标】要在【财务报表】之后——机构类型（银行/券商/保险）是靠财务特征科目
    ///     认出来的，没有财务数据它一家都识别不出；
    ///   · 【导入手工数据】要在【金融监管指标】之后——它填的正是监管指标里没解析出来的那些格子。
    /// 财务报表最慢（每轮 300 只、跨几天），但它有冷却：补完一轮歇 20 分钟，
    /// 那段空档后面的小任务照样能跑，不会被饿死。
    /// </summary>
    public static readonly IReadOnlyList<FetchActionId> PeriodicOrder =
    [
        FetchActionId.FetchIndustry,
        // 紧跟着跑东财三级分类：两份行业数据一起更新，才不会出现"一份新一份旧"的错配
        FetchActionId.FetchStockBoardMap,
        // 指数那两项是拆开的：成分名单（新浪）/ ETF映射（本地）；权重排到最后，理由见末尾
        FetchActionId.StepIndexCons,
        FetchActionId.StepEtfIndexMap,
        FetchActionId.FetchShareholder,
        FetchActionId.FetchDividend,
        FetchActionId.FetchFinancials,        // 监管指标要靠它认机构类型，所以排在前面
        FetchActionId.BankRegulatory,
        FetchActionId.StepReparseBankPdf,
        FetchActionId.ImportManual,           // 填的是监管指标没解析出来的格子
        // 【分档资金流】（2026-09-03）：接口是 120 天滚动窗口、没有增量入口，每轮都要把 5500 只
        // 重抓一遍（约 3 小时），所以归"空闲时补"这一组而不是日更。放在权重前面——它虽然慢，
        // 但走的是 push2his，跟中证 OSS 不是一家，不会互相牵连。
        // 【板块成分股】(2026-09-04)：push2 上最耗配额的一项，跟下面的分档资金流是不同域名、
        // 独立限流，排在一起不会互相牵连。
        FetchActionId.StepBoardMembers,
        FetchActionId.FetchMoneyFlowDetail,
        // 【指数权重】排最后（2026-09-02 用户定）：中证 OSS 最容易触发反爬，而权重目前用得最少。
        // 排在末尾意味着即使它把自己撞进熔断，前面那些数据也早就落库了。
        FetchActionId.StepIndexWeight,
    ];

    /// <summary>
    /// 「按需启动」组的默认排法（2026-09-02）：顺序＝想起来要做时的自然工作流——
    /// 先体检看看缺不缺 → 决定要不要往回补 → 补完一大批再建一次索引。
    /// </summary>
    public static readonly IReadOnlyList<FetchActionId> OnDemandOrder =
    [
        FetchActionId.StepFullAudit,
        FetchActionId.FetchYear,
        FetchActionId.OptimizeDatabase,
    ];

    private static readonly Dictionary<FetchActionId, FetchActionInfo> ById =
        All.ToDictionary(a => a.Id);

    public static FetchActionInfo Info(FetchActionId id) => ById[id];

    /// <summary>枚举名认不出来时返回 null——手改过 json、或读到旧版本写的名字时不能崩。</summary>
    public static FetchActionInfo? TryInfo(string? rawId) =>
        Enum.TryParse<FetchActionId>(rawId, out var id) && ById.TryGetValue(id, out var info) ? info : null;
}
