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

    /// <summary>【导入基金除权除息】——从东财终端本地文件补 ETF 的分红/份额折算事件。</summary>
    ImportFundExDividend,

    /// <summary>【ETF日K·不复权】——ETF 的 day_raw，回测序列的输入。</summary>
    StepEtfRawBars,

    /// <summary>【补全退市名单】——巨潮全市场名单减去在市名单，把确实交易过的补进 DelistedStock。</summary>
    StepDelistedSupplement,
    StepBoards,
    StepBoardList,
    StepIndustryIndicator,
    StepWatchIndicator,
    StepPlanWatch,
    StepCompanyProfile,
    StepCustomerSupplier,
    StepSubsidiaryExtract,
    StepBoardMembers,
    StepReparseBankPdf,
    StepFullAudit,

    // ───── 东财数据（2026-09-03）─────
    // 主环境实测只有 push2 域名不通，datacenter-web / push2his 都可达——此前"东财整体不可用"
    // 的判断是错的。这一项抓的是本地此前完全没有的数据，没有回退源。
    FetchEarningsForecast,
    FetchLhbSeat,
    FetchMoneyFlowDetail,
    /// <summary>
    /// 【分档资金流快照】（2026-09-12 从 <see cref="FetchMoneyFlowDetail"/> 拆出来）——
    /// push2delay 全市场当日快照那一条通道。
    ///
    /// 拆的理由是**时效性**：原来两条通道挤在一项里，被"逐股补历史耗时长、没时效压力"
    /// 那个归类连累进了季度组·空闲时补；可快照恰恰是全库时效性最强的数据之一——接口只给
    /// **最近一个交易日**，当天收盘后没跑，下一个交易日开盘一到就永久取不回来了。
    /// 2026-09-09 全市场分档资金流整天缺失就是这么来的（当天没轮到跑，隔天补不回来，
    /// 而逐股那条通道要 5500 个请求才能补一天）。
    /// </summary>
    FetchMoneyFlowSnapshot,

    /// <summary>
    /// 【大宗交易】（2026-09-17 从 <see cref="FetchMarketEvents"/> 拆出来）。
    ///
    /// 拆的理由是**批的粒度**：另外三张（机构调研/限售解禁/股东增减持）按年切片、十几片
    /// 几分钟跑完；大宗改成**按交易日整日替换**之后是两千多片，绑在一起会让那三张陪着
    /// 跑完整个历史回补。而体检层面它们本来就已经分开了——日频残缺日体检只认大宗。
    ///
    /// 为什么非改按日不可：主键第三列原来存东财的 <c>DAILY_RANK</c>，那个值**跨抓取不稳定**，
    /// UPSERT 认不出"同一笔"、每次重抓都多一份副本（最近一个月的金额普遍虚高一倍）。
    /// 详见 doc/block-trade-task-design.md。
    /// </summary>
    FetchBlockTrade,
    FetchMarketEvents,
    FetchStockBoardMap,

    /// <summary>
    /// 【拉取总股本】（2026-09-14）——走东财条件选股接口，一个请求拿全市场。
    /// 修的是 PE/PB 拿财报 <c>share_capital</c>（实收资本，金额）当股数用这个老问题，
    /// 详见 <c>MetricKeys.TotalShares</c>。
    /// </summary>
    FetchTotalShares,

    /// <summary>【分红对账】（2026-09-18）——拿东财全量比一遍，把新浪漏掉的除权记录补进来。
    /// 只加不删，见 doc/dividend-reconcile-design.md。</summary>
    FetchDividendReconcile,

    // ───── 本地维护（2026-09-07）─────
    StepFillProbeFloor,

    /// <summary>
    /// 龙虎榜换源：整段用东财重抓、覆盖新浪那份历史。**已退役**（2026-09-10 跑过一次就完成了使命）。
    ///
    /// ⚠ 枚举值本身**不能删**：用户的 fetch-plan.json 里存的是动作名字符串，
    /// <c>JsonStringEnumConverter</c> 读到认不出的名字会抛异常，而 <c>FetchPlanStore.Load</c>
    /// 的 catch 会把**整份计划重建成默认**——用户排过的顺序和时刻全没了。
    /// 留着这个值，那一行才能被 <see cref="RetiredInto"/> 干净地清掉。
    /// </summary>
    StepLhbMigrate,

    // ───── 新式任务（2026-09-08 起，实现在 StockPlatform.Tasks，见 IFetchTask）─────
    StepTradingCalendar,

    /// <summary>把 Bar.volume 里按"股"存的历史行改成"手"（2026-09-10）。纯本地、幂等。</summary>
    StepFixVolumeUnit,

    /// <summary>按「融券余量 × 当日收盘价」补算沪市缺失的融券余额（2026-09-16）。纯本地、幂等。</summary>
    StepFillShortBalance,
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
    /// 摆在顶上反而像"所有任务都吃这个设置"。留空＝这一项不抓公告（日志里会说明原因）。
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
    ///
    /// **只补缺的、已有的不动**——这是它跟 <see cref="Thorough"/> 的分界（2026-09-11 定）。
    /// 「整段」有多长由各项自己的数据源决定：K线那几行是从开市首日抓起，
    /// 【拉取分档资金流】的接口只给最近 120 个交易日，那个窗口就是它的"整段"。
    /// </summary>
    FirstBackfill = 4,

    /// <summary>
    /// 彻底重查（2026-09-09，只有【全库数据体检】用）：忽略并清空那三张"确认没有"的结论名单
    /// ——「确认没有」白名单、「数据源没有更早数据」水位、日频表空日名单。
    ///
    /// ⚠ 词义分界（2026-09-11 用户定）：**彻底重查＝不管原来有没有、全部重来一次**；
    /// 只补缺、不动已有的那种是 <see cref="FirstBackfill"/>。【拉取分档资金流】原来挂的是这个模式，
    /// 但它只把缺行的票排进队、齐了的一个请求都不发，按这条分界已经改回 FirstBackfill。
    ///
    /// 原来这是界面上一个单独的勾（<c>PlanItemViewModel.ThoroughAudit</c>）。收成模式是因为
    /// 项目里"模式是参数、不是任务"这个概念已经在了（见 doc/fetch-plan-atomic-tasks-design.md），
    /// 而 catalog 的 <see cref="FetchAction.SupportedModes"/> 天然能声明"这一项支持哪几个模式"、
    /// 界面复用现成的模式下拉——比再加一个只服务一项的勾干净。
    /// </summary>
    Thorough = 8,

    /// <summary>
    /// 只补**待办清单**里属于这一项的那些（2026-09-13 新增，见 doc/retry-backlog-design.md）。
    ///
    /// 待办从哪来：这一项自己抓失败的、全库体检查出它这个口径有历史空洞或值问题的、
    /// 日更末尾发现当天数据还没到位的——全都按任务 id 记在
    /// <c>Manifest.Todos</c> 里（见 <c>RetryTodo</c>）。
    ///
    /// ⚠ **跟 <see cref="Incremental"/> 的分界是"目标从哪来"**，这是它必须单独成一个模式的原因：
    /// 增量是按每只标的的**水位线**往后续抓，而历史空洞正好在水位线**之下**——
    /// 拿增量去补空洞，跑一整轮 5500 只也补不上一段（见 BarFetchTaskBase.FillGapAsync 的注释）。
    ///
    /// 【重新拉取失败】就是拿这个模式把各项挨个跑一遍；单独给某一项设成这个模式也行，
    /// 那就是"只把这一项欠的补上"。
    /// </summary>
    FillBacklog = 16,
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
    IReadOnlyList<DataSourceId>? Sources = null,
    /// <summary>
    /// 这一项允许"一句话都不说"多久（2026-09-08）——超过就判定卡死掐断，见 <see cref="QuietWatchdog"/>。
    ///
    /// null＝用默认的 5 分钟，绝大多数项都该是 null：它们逐只/逐日地干活，本来就一直在报进度。
    ///
    /// 要填值的只有**黑盒步骤**——整段是一次没法插进度的同步调用，从外面看跟卡死一模一样。
    /// 这类项等于退回到"按时长判"，是明知的代价，所以必须逐个写清楚理由，别顺手放宽。
    /// </summary>
    TimeSpan? MaxQuiet = null)
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
    ///
    /// ⚠ 板块那两项的源**由运行期配置决定**，见 <see cref="FetchTaskCatalog.BoardChannel"/>：
    /// 通道选 terminal 时它们只读本地文件、一个请求都不发，这里必须返回空集，
    /// 否则会被别的东财任务挡在"它根本不会去打的源"上。
    /// </summary>
    public IReadOnlySet<DataSourceId> EffectiveSources =>
        FetchTaskCatalog.IsLocalOnlyNow(Id) ? []
        : Sources is { Count: > 0 }
        ? Sources.ToHashSet()
        : Quota switch
        {
            QuotaGroup.Local => [],
            QuotaGroup.Sina => [DataSourceId.Sina],
            QuotaGroup.Exchange => [DataSourceId.Exchange],
            _ => DataSourceCatalog.AllOnline.ToHashSet(),
        };

    /// <summary>
    /// 数据源那一列显示什么（2026-09-06）。多数项就是目录里写死的
    /// <see cref="DataSource"/>；板块那两项在 terminal 通道下改说本地文件——
    /// 否则会出现"数据源：东财行情，占用：本地计算（不占数据源）"这种自相矛盾的一行。
    /// </summary>
    public string DataSourceText =>
        FetchTaskCatalog.IsLocalOnlyNow(Id) ? "东财终端本地文件（不联网）" : DataSource;

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
    /// <summary>东财行情历史侧 push2his（分档资金流逐股补历史）。跟 push2 不同域名、不同限流。</summary>
    EmPush2His,

    /// <summary>
    /// 东财延时行情 push2delay（分档资金流的全市场当日快照，2026-09-11 补登记）。
    ///
    /// 单列一个源：它跟 push2his 是**不同域名、独立计数**，谁被切都不影响另一条，限流参数也差着
    /// 一个量级（这条 1 秒间隔跑 60 页，那条 5 秒间隔、每 15 个歇 2 分钟）。此前【拉取分档资金流】
    /// 只登记了 push2his，快照打的这个域名在占用表上是隐身的。
    /// </summary>
    EmPush2Delay,
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
        DataSourceId.EmQuote, DataSourceId.EmPush2Delay,
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
        DataSourceId.EmPush2Delay => "东财 push2delay",
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
/// ════ 为什么没有"一键拉取定期数据" ════
/// 它本身就是 行业分类 → 指数成分/权重 → 股东数据 → 财务报表 → 分红 五步的固定串。
/// 在计划里把这五项分开排更灵活（比如只在财报季开财务、平时只跑行业分类），所以目录里
/// 只放这五项本身。那个按钮原先留在【手动】页上，2026-09-08 随那一页一起撤了——
/// 要"一次点完"就用组头的【执行整组】，勾哪几项跑哪几项。
/// </summary>
public static class FetchTaskCatalog
{
    public static readonly IReadOnlyList<FetchActionInfo> All =
    [
        // ══════════════ 每日主流程的 13 个原子项（2026-09-02 从【拉取全部】拆出）══════════════
        // 顺序就是推荐的执行顺序，也是【添加每日模板】展开出来的顺序。
        // 拆分判据见 doc/fetch-plan-atomic-tasks-design.md 第 2 节：同一次请求拿回来的不拆
        // （名册+市值）、多源接力才有结果的不拆（ETF/退市/公告）、本地计算单独成项。

        new(FetchActionId.StepRoster, "股票名册与流通市值", "新浪 + 上交所官方", QuotaGroup.Sina,
            TimeSpan.FromMinutes(3), "每工作日",
            "刷新全市场名册（顺带发现当天新上市的票）+ 当下的流通市值快照——**同一个列表接口一次给两样**"
            + "（名册和 nmc 流通市值），所以是一项、拆不开，而且只扫一遍就够"
            + "（老的【拉取全部】是取名册扫一遍、市值又扫一遍，白花约 55 个请求）。\n"
            + "后面所有\"逐只\"的项都拿这份名册当输入，建议排在它们前面。\n"
            + "**名册是两个源合并的**（2026-09-17）：新浪 hs_a+kcb 打底、上交所官方名单兜沪市。"
            + "新浪会静默漏票——实测漏 14 只，含当天上市的新股、改名\"退市XX\"仍在退市整理期交易的、"
            + "以及 *ST 那几只；漏掉的票两融数据一直在更新、K线却一根都没有。"
            + "市值/最新价只有新浪那条路给，所以**新浪必须排在前面**（见 CompositeStockListProvider）。\n"
            + "⚠ 市值只有\"当下\"、接口没有历史，所以这一项没有\"补某一天\"的用法。\n"
            + "2026-09-18 迁到新任务框架：一批＝**一轮**（一次请求拿回全市场），所以"
            + "「只补待办」就是\"整轮重来\"——待办里那批代码是当时那批的全体，不是逐只失败。\n"
            + "快照归到哪个交易日：先问本地交易日历（稳态下零请求），问不出才抓上证指数日线兜底。",
            FetchActionParams.GlobalFetchOptions,
            SupportedModes: FetchMode.Incremental | FetchMode.FillBacklog),

        new(FetchActionId.StepNetInflow, "资金净流入", "新浪", QuotaGroup.Sina,
            TimeSpan.FromMinutes(30), "每工作日",
            "逐只抓主力资金净流入（约 5500 只，做资金流因子）。失败的进自己的重试名单。\n"
            + "模式选「只抓某一天」就按那一天精确取（原【补指定历史日】的做法），日期留空＝今天。\n"
            + "2026-09-18 迁到新任务框架（NetInflowTask）：一批 30 只、组内并发，"
            + "所以这个 1.75 小时的活**能分批跑、能中途停**了（以前停了就是整轮白费）。\n"
            + "「只补待办」一次把三类都补上：失败名单按各自水位线重抓；整天缺失和体检查出的残缺日"
            + "合并成一轮全市场（数据源一次返回整只票全历史，补几天跟补一天一样贵）"
            + "——残缺日以前**没有人补**，只会每轮报一句\"只能人工处理\"。",
            FetchActionParams.GlobalFetchOptions | FetchActionParams.Date,
            SoftDependsOn: [FetchActionId.StepRoster],
            SupportedModes: FetchMode.Incremental | FetchMode.SpecificDay | FetchMode.FillBacklog),

        new(FetchActionId.StepAnnouncements, "中标/订单公告", "巨潮检索 + 正文", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(3), "每工作日",
            "按计划页顶部填的关键词搜中标/订单类公告并取正文，回看最近 14 天（重复扫同一天是安全的，主键去重）。"
            + "关键词清空就是空跑。搜索结果没有正文就没法筛金额，所以\"检索→取正文\"是一项、不拆。\n"
            + "模式选「只抓某一天」就只搜那一天。\n"
            + "关键词就填在这一行的参数格里（逗号分隔），留空＝不抓公告（日志里会说明是因为没填）。",
            FetchActionParams.GlobalFetchOptions | FetchActionParams.Date | FetchActionParams.Keywords,
            SupportedModes: FetchMode.Incremental | FetchMode.SpecificDay,
            Sources: [DataSourceId.Cninfo]),

        new(FetchActionId.StepIndexBars, "指数日K", "腾讯", QuotaGroup.Mixed,
            TimeSpan.FromSeconds(30), "每工作日",
            "大盘指数日K（十几个标的）。\n"
            + "⚠ 它还是全库的**交易日锚**：\"最近一个已收盘交易日是哪天\"就是看上证指数最新一根日线"
            + "（快照类数据归属日、当日覆盖率体检都靠它），所以别把它关掉。\n"
            + "模式：「增量」＝每条指数从自己的水位线续到今天，日常用它。\n"
            + "「首次整段回补」＝不看水位线、也不看回看年数，从 A股开市首日 1990-12-19 抓起。\n"
            + "**往指数清单里加了新指数之后必须跑一次这个**：水位线只往后走，新指数第一次被增量"
            + "抓到的只有回看年数那几年（默认 3 年），之后水位线就钉在最新一根上，"
            + "再也不会回头补前面的历史——2026-09-09 加深证综指等三条时踩到过，只抓到 3 年，"
            + "而龙虎榜的偏离值要拿深证综指当基准回溯到 2004。\n"
            + "不会覆盖已有数据：入库走 InsertOrRefreshUnconfirmed，已确认的行原样跳过，"
            + "复权基准不受影响；多花的只是翻页请求（九条指数合计一两分钟）。"
            + "早于各指数发布日的部分数据源自然返回空（创业板综 2010 才有、北证50 2021 才有），"
            + "跑完会逐条报出\"本地最早到哪天\"。",
            FetchActionParams.GlobalFetchOptions | FetchActionParams.LookbackYears,
            SupportedModes: FetchMode.Incremental | FetchMode.FirstBackfill | FetchMode.FillBacklog,
            Sources: [DataSourceId.Tencent, DataSourceId.Sina]),

        new(FetchActionId.StepStockDayBars, "个股日K·前复权", "腾讯", QuotaGroup.Mixed,
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
            SupportedModes: FetchMode.Incremental | FetchMode.SpecificDay | FetchMode.FillBacklog,
            Sources: [DataSourceId.Tencent, DataSourceId.Sina]),

        new(FetchActionId.StepStockHfqBars, "个股日K·后复权", "腾讯", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(30), "每工作日",
            "数据源口径的后复权日K，水位线独立于前复权。\n"
            + "⚠ 它是\"送转乘、分红加\"的混合式、会压低收益率（实测工商银行 ×0.625），**回测已经改用本地算的"
            + " day_adj**，这一条现在主要是对照和历史兼容。",
            FetchActionParams.GlobalFetchOptions | FetchActionParams.LookbackYears,
            SoftDependsOn: [FetchActionId.StepRoster],
            SupportedModes: FetchMode.Incremental | FetchMode.FillBacklog,
            Sources: [DataSourceId.Tencent, DataSourceId.Sina]),

        new(FetchActionId.StepStockRawBars, "个股日K·不复权", "腾讯", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(30), "每工作日",
            "原始成交价——全库唯一**不随分红变化**的序列，也是【重算回测序列】的输入。\n"
            + "模式：「增量」＝日常那一根；「首次整段回补」＝把每只补到跟前复权一样长"
            + "（原【补不复权历史】，实测约 24700 个请求、2 小时出头，跑完一次就基本不用再管）。\n"
            + "整段回补支持分批：设成重复「空闲时」就会在空档里一点点补、到点前收尾。",
            FetchActionParams.GlobalFetchOptions | FetchActionParams.LookbackYears,
            SoftDependsOn: [FetchActionId.StepRoster],
            SupportedModes: FetchMode.Incremental | FetchMode.FirstBackfill | FetchMode.FillBacklog,
            SupportsPartialRun: true,
            Sources: [DataSourceId.Tencent, DataSourceId.Sina]),

        new(FetchActionId.StepEtfBars, "ETF日K", "新浪名单 + 腾讯K线", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(6), "每工作日",
            "全市场 ETF 的日K（约 1000 只，水位线增量）。代码带前缀存（sh510300），天然被挡在个股选股全集外。"
            + "名单和K线是两家，但\"没有名单就抓不了K线\"，所以是一项。",
            FetchActionParams.GlobalFetchOptions | FetchActionParams.LookbackYears,
            SupportedModes: FetchMode.Incremental | FetchMode.FillBacklog,
            Sources: [DataSourceId.Sina, DataSourceId.Tencent]),

        new(FetchActionId.StepEtfRawBars, "ETF日K·不复权", "腾讯", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(8), "每工作日",
            "ETF 的原始成交价，跟个股的【个股日K·不复权】对称——**没有它 ETF 完全不能回测**：\n"
            + "现在 ETF 只有源给的减法式前复权，非除权日的收益率也是错的"
            + "（同口径实测：拿官方板块指数当标尺，前复权算出的收益率相关系数只有 0.454、每天差 5.88%）。\n"
            + "**省掉八成请求**：1659 只里只有 323 只除权过（东财终端 fund_cqcx 覆盖），"
            + "其余 1336 只的 day 逐值等于 day_raw，直接从库里复制。\n"
            + "复制有闸门、不会静默抄错：首次抓一页真 day_raw 逐值比对（1 个请求/只），"
            + "日常增量比库里两条序列在同一天的收盘价（**0 个请求**）——前复权的基准是最新价，"
            + "一旦除权整条 day 历史都会变，所以\"老日子上还相等\"就等于\"此后没除过权\"。",
            FetchActionParams.GlobalFetchOptions,
            SoftDependsOn: [FetchActionId.StepEtfBars, FetchActionId.ImportFundExDividend],
            SupportedModes: FetchMode.Incremental | FetchMode.FirstBackfill | FetchMode.FillBacklog,
            SupportsPartialRun: true,
            Sources: [DataSourceId.Tencent]),

        new(FetchActionId.ImportFundExDividend, "基金除权除息", "东财终端本地文件", QuotaGroup.Local,
            TimeSpan.FromSeconds(20), "不定期",
            "从东财终端自己落盘的 fund_cqcx.db 导 ETF 的分红/份额折算事件（5845 条 / 1017 只基金）。"
            + "**纯本地读文件，一个请求都不发**；没装终端就整项跳过、不算失败。\n"
            + "它是 ETF 能回测的另一半——day_adj ＝ 不复权价 × 本地算的因子，而因子要靠这些事件。\n"
            + "两个字段的单位都用恒等式验过：Diviratioa＝每10份现金（对得上 raw−qfq 的累计），"
            + "Diviratiob＝每10份折算后的份数（510500 两次折算，理论跳空 +256.7%/−12.70% "
            + "对实际的 +261.1%/−14.13%）。份额合并时送转比例是**负数**，这是合法输入。\n"
            + "⚠ 别跟 full_cqcx_hs_V3.dat 搞混——那份 10 万条里一条 ETF 都没有，全是个股和新三板。",
            SoftDependsOn: [FetchActionId.StepEtfBars]),

        new(FetchActionId.StepDelistedTails, "退市股收尾", "两所官网 + 腾讯K线", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(2), "每工作日",
            "刷新退市名单，给\"本地跟踪过、但最后一根K线还早于终止日\"的票补完最后那几天。\n"
            + "股票一退市数据源就不再更新它，这几天不补就**永久缺失**，回测会有幸存者偏差。",
            FetchActionParams.GlobalFetchOptions,
            Sources: [DataSourceId.Exchange, DataSourceId.Tencent]),

        new(FetchActionId.StepDelistedSupplement, "补全退市名单", "巨潮 + 本地档案 + 腾讯K线", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(2), "每工作日",
            "**退市名单是全库公共设施**：财报/股东/分红的待抓判据、日常轮询的过滤、全库体检、"
            + "FactorLab 选池都只读 DelistedStock 和 StockMeta.type，谁都不该自己去拼退市判据——"
            + "名单不全就在这一项补。\n"
            + "两所官网的终止上市名单漏三类，这一项补上：\n"
            + "① **科创板退市股整类缺失**——上交所终止上市名单(stockType=5)里 68 开头的是 0 只，"
            + "688086 紫晶存储/688555 泽达易盛/688287 观典防务 在它家所有 stockType 里都查不到；\n"
            + "② 已换代码的老号（600849 上海医药、601313 江南嘉捷这类），原代码不再交易、实质等同退市；\n"
            + "③ **还挂在在市名册里的已退市票**（2026-09-19）——巨潮那条差集要减去在市名册，"
            + "于是\"名册说在市、档案说退市\"的票永远不是候选，两边谁也纠正不了谁。920305 云创退就这么"
            + "卡着：K线停在 2026-07-29、名字都带\"退\"，type 还是 stock，每天的K线/资金流/名册轮询都在"
            + "白抓它。第三路读**本地公司档案的 listing_state='2'**（东财给的上市状态），不减名册、"
            + "零新增请求。⚠ 只认 '2'：9 是待上市/暂缓（有几只还在正常交易）、10 是换代码吸收合并。\n"
            + "判据＝[巨潮 − 在市名单] ∪ [档案说已退市] − 已知退市，再逐只向数据源要一次日K：**给得出才算交易过**。"
            + "候选里混着从未上市的（蚂蚁集团、浙江国祥那类过会后撤回的），写进退市表会污染分红抓取和选池。\n"
            + "⚠ 巨潮**不能**直接并进在市名单：Upsert 默认写 type=stock 且是 INSERT OR REPLACE，"
            + "会把几百行 delisted 冲成 stock，退市股重新进日常轮询。\n"
            + "⚠ 终止日一律留空，**不拿最后一根K线去推**：实测 318 只有官方终止日的票，"
            + "\"终止日 − 最后K线\"中位数 15 天、尾部到 2525 天（摘牌在退市整理期结束之后），"
            + "推出来会偏早，可能把该有的一期财报判成\"不该有\"——那是静默漏抓。",
            SoftDependsOn: [FetchActionId.StepRoster],
            Sources: [DataSourceId.Tencent]),

        new(FetchActionId.StepBoardIndex, "板块指数合成", "本地计算·不联网", QuotaGroup.Local,
            TimeSpan.FromMinutes(3), "每工作日",
            "用本地板块成分股 + 个股日K**等权合成**板块指数日K（非官方指数），做板块热度/宽度。\n"
            + "平时**只追加新交易日**；成分股名单变了、或 day_adj 被【重算回测序列】重写过，那个板块才整段重算。\n"
            + "要读当天的个股K线，所以排在个股日K之后；"
            + "【拉取板块】更新了成分之后也该跑一次。",
            // 两份输入都要：板块成分（谁在这个板块里）+ 当天个股K线（拿什么价算）
            SoftDependsOn: [FetchActionId.StepBoardMembers, FetchActionId.StepBoardList, FetchActionId.StepStockDayBars],
            // 「首次整段回补」＝不看合成状态、所有板块重算一遍（2026-09-21 加）。
            // 日常那一路改成增量之后，这一项是**唯一的兜底**：增量判据万一漏了某种失效来源，
            // 错的历史会一直留着——板块指数只有本地这一份，没有官方值可对。
            SupportedModes: FetchMode.Incremental | FetchMode.FirstBackfill),

        new(FetchActionId.StepTradingCalendar, "交易日历", "深交所官网", QuotaGroup.Exchange,
            TimeSpan.FromSeconds(20), "每工作日",
            "把交易日历落进 TradingDay 表——**全库公共设施**，凡\"按交易日取数\"的地方都读它，"
            + "尤其是【融资余额】【龙虎榜】的逐日回补：在它之前那两项只跳周末，每个节假日每轮都要"
            + "白发一次请求（龙虎榜从 2002 年补一轮就是几百个）。\n"
            + "两个来源按年份分工：**2005-01 起**走深交所官网按月接口（官方权威，一月一个请求）；"
            + "**2004-12 及以前**深交所不提供，从本地全市场日K归纳——那段是死历史、建一次就固定。\n"
            + "模式：「增量」＝拉本月+下月（2 个请求；11 月起一路拉到次年 12 月，因为交易所年底才"
            + "发布下一年的日历）；「首次整段回补」＝重建整份日历（264 个请求 + 一次全库日期扫描，"
            + "23GB 库上几分钟），补过历史K线之后也该用它重跑一次。\n"
            + "首次/重建时顺带做一次对账：官方日历里有、本地全市场却一根K线都没有的日子会报出来——"
            + "那是**整天漏抓**的信号。",
            FetchActionParams.None,
            SupportedModes: FetchMode.Incremental | FetchMode.FirstBackfill,
            Sources: [DataSourceId.Exchange]),

        new(FetchActionId.StepMargin, "融资余额", "交易所", QuotaGroup.Exchange,
            TimeSpan.FromMinutes(1), "每工作日",
            "两融余额。两所是 T+1 发布，所以按\"以今天为终点回看最近几个交易日\"来抓，不是只抓当天。"
            + "彬哥法第 12 条（融资余额增长）用的就是它。\n"
            + "本地已有的日子跳过，但**最近 5 个交易日无条件重抓**（2026-09-08）——两所分批发布，"
            + "早抓到的可能只是一部分，\"有行就跳过\"会把残缺状态永久固化；重抓幂等、主键去重。\n"
            + "模式：「增量」＝以日期格那天（留空＝今天）为终点回看几个交易日；"
            + "「首次整段回补」＝从 2010-03-31（融资融券开市首日，早于此日两融业务还不存在、"
            + "两所一天数据都没有）一路补到今天（原【一键补齐每日历史】的融资那半边），幂等可反复跑。\n"
            + "非交易日由【交易日历】挡掉、\"确认没有数据\"的日子由空日名单挡掉，都不再白发请求。\n"
            + "2026-09-18 迁到新任务框架（MarginTask）：四条模式的口径统一了，"
            + "「只补待办」＝补体检查出的残缺日（原来编排在 orchestrator），"
            + "整段回补现在支持分批跑和到点收尾（约 3900 个交易日，停了下轮接着走）。",
            FetchActionParams.Date,
            SoftDependsOn: [FetchActionId.StepTradingCalendar],
            SupportedModes: FetchMode.Incremental | FetchMode.SpecificDay
                          | FetchMode.FirstBackfill | FetchMode.FillBacklog),

        new(FetchActionId.StepLhb, "龙虎榜", "东财", QuotaGroup.Mixed,
            TimeSpan.FromSeconds(30), "每工作日",
            "当日龙虎榜概要（哪只票、因为什么指标上榜、龙虎榜买卖额），当晚发布。\n"
            + "**2026-09-09 从新浪换成东财**：两源逐条比对过（09-08 单日票集 55 vs 55 双向零差异、"
            + "收盘价全对上、成交额换算比值精确 1.000000、数据起点同为 2004-06-25）。换的理由是口径——"
            + "东财的上榜原因是**交易所原文**，跟【拉取龙虎榜席位】那张表同源，两张表终于能按 "
            + "(日期,代码,原因) join；新浪那份把原因归并成 28 种粗类，且对应值跟原因错配。"
            + "要退回新浪：改 fetcher-settings.json 的 LhbSource。\n"
            + "模式：「增量」＝以今天为终点**回看 31 个交易日**——不只是等盘后陆续公布，更是为了"
            + "**上榜后 N 日涨跌幅**那几列：它们是滞后字段，当天抓一律为空，d30 要等 30 个交易日才有值。"
            + "东财按月切片，31 个交易日≈2~3 个请求，比原来逐日抓 5 天还省。"
            + "日期格**填了**就只抓那一天，并绕过\"确认没有\"名单（人点名要，就是要重查它）。\n"
            + "「首次整段回补」＝从 2004-06-25（东财这张表的第一天）补到今天，幂等可反复跑。\n"
            + "⚠ 东财不给\"对应值\"和\"成交量\"这两列。对应值本地补：换手率/涨跌幅直接用源给的，"
            + "单日偏离值和振幅本地算（拿真值验过，主板命中 91~100%），**连续N日累计偏离值一律留空**"
            + "——起算日由交易所判定，本地复现不出来，最好也只有 55%。每一行都在 deviation_source 列标明来路。\n"
            + "**成交量一律留空**：本想从本地日K补，实测发现 Bar.volume 的单位在**科创板是股、"
            + "其余板块是手**（09-08 全市场 688/689 的 613 只 vs 其余 4949 只），且有些行的量额本身就不全"
            + "（603999 那天记 6914 万、实际 2.13 亿）——那是 Bar 表自己的毛病，不该在龙虎榜这儿绕过去。"
            + "量能信息用成交额、换手率、龙虎榜买卖额那几列。",
            FetchActionParams.Date,
            SoftDependsOn: [FetchActionId.StepTradingCalendar],
            SupportedModes: FetchMode.Incremental | FetchMode.SpecificDay
                          | FetchMode.FirstBackfill | FetchMode.FillBacklog),

        new(FetchActionId.StepLhbMigrate, "龙虎榜·换源重抓", "东财", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(20), "已退役",
            "⚠ **已退役（2026-09-10）**——一次性迁移工具，跑过一次就完成了使命，实现已删除。\n"
            + "它当时做的事：把 2004-06-25 至今的龙虎榜整段用东财重抓、覆盖掉新浪那份历史。"
            + "实跑 23 分钟、268 个月片、267,986 行，跑完 source 100% 是 em、"
            + "跟【拉取龙虎榜席位】的全量 join 覆盖率 100.00%（179,944/179,945）。"
            + "换源前的新浪历史备份在 data/local/backup/Lhb-20260910-091818.sqlite（库外独立文件）。\n"
            + "**为什么不留着**：它是\"重抓 580 个请求 + 顺带重算 2 列派生\"。真需要重算 deviation 时"
            + "（比如累计偏离值的规则哪天搞明白了），输入全在库里，该写本地重算、1 分钟跑完，"
            + "而不是把那 580 个请求重发一遍拿回一份一模一样的数据。可复用的零件都留着："
            + "EastMoneyLhbProvider.FetchSlicesAsync（按月切片，2026-09-17 由 FetchRangeAsync 改名并改成异步枚举）、"
            + "ILhbRepository.ReplaceDays（整天替换）、"
            + "LhbDeviationDeriver（派生）、ExportTo（库外备份）——真要重来，六十行编排随时能再写。\n"
            + "换源的完整始末见 doc/data-dictionary.md 的 Lhb 小节。",
            FetchActionParams.None,
            Retired: true,
            SupportedModes: FetchMode.Incremental),

        new(FetchActionId.StepFixVolumeUnit, "统一成交量单位", "本地查库·不联网", QuotaGroup.Local,
            TimeSpan.FromMinutes(5), "一次性",
            "把 Bar.volume 里按\"股\"存进去的历史行改成\"手\"。**一个请求都不发**，纯 UPDATE。\n"
            + "**修的是什么**：三个数据源口径不一样——腾讯主板给手、**科创板给股**；新浪一律给股；"
            + "东财都给手。而两个 fetcher 原来把源给的数字原样入库，于是这一列里并存两种单位："
            + "2026-09-08 那天全市场日线，主板 3203 只、创业板 1404 只、北交所 342 只是手，"
            + "**科创板 688/689 的 613 只是股**，差 100 倍。解析层已经归一化（BarVolumeUnit），"
            + "这一项负责已经躺在库里的历史（约 300 万行）。\n"
            + "**为什么一直没被发现**：单票内部的分析用的都是比值（放量比、量能排序、K线图），"
            + "单位约掉了怎么看都正常。只有跨股票累加才露馅——板块指数把成分股成交量加总，"
            + "含科创板的板块常年虚高 100 倍，而这不会报任何错。\n"
            + "**判据是逐行的量额比**（amount/(volume×close) 落在 0.8~1.25 才算\"股\"），不是按代码前缀"
            + "一刀切——要修的不止科创板：新浪回退（已于 2026-09-10 拆除）每触发一次就往那只票历史里"
            + "掺一段股口径的行，哪只票哪一段全凭当时的网络抖动。\n"
            + "**幂等**：改完的行比值变成 ≈100，再跑不会被选中。中断了直接重跑，不用记断点。\n"
            + "指数、板块合成、ETF **不动**：它们的\"成交量\"是汇总值或按份计，量额比没有物理意义。\n"
            + "⚠ 跑完**要再跑一次【板块指数合成】**——它存的成交量是按旧单位加总出来的。",
            FetchActionParams.None,
            // 实测（2026-09-10 日志）这一项开头那句"开始扫描"之后**哑了 17 分 31 秒**：
            // Bar 表 2540 万行、granularity 无索引，第一步就是一条走全表的查询——
            // 它是**一条 SQL，没有可切分的心跳单位**，喂不了狗（对比：按只/按天的循环都改成
            // 每单位喂狗了，见 QuietWatchdog.IBeatOnlySink）。所以这一项只能抬阈值。
            // 手动点的时候没事（那条路没有看门狗），但它是可以被排进计划的。
            MaxQuiet: TimeSpan.FromMinutes(30)),

        new(FetchActionId.StepFillShortBalance, "融券余额补算", "本地查库·不联网", QuotaGroup.Local,
            TimeSpan.FromMinutes(6), "每日",
            "按交易所官方公式「**融券余量 × 当日收盘价**」补算**沪市**缺失的融券余额。一个请求都不发。\n"
            + "**修的是什么**：上交所接口的 rqylje（融券余额）**恒为 null**，而解析用的 GetNum 把 null 读成 0"
            + "——于是 1675 只沪市票的融券余额**历史上从来没有过非 0 值**（3,139,554 行沪市数据里 "
            + "short_balance>0 的有 0 行）。深市那张 xlsx 第 6 列直接给了余额，所以只有沪市这一半瞎。"
            + "后果是静默的：资金面诊断的「融券余额变化」对沪市票算不出来，界面上还显示成「融券余额 0」。\n"
            + "**凭什么能算**：这条公式是深交所报表页脚自己写的（本日融券余额=本日融券余量×本日收盘价）。"
            + "实测两市都精确成立——深市 1603 只（对它自己给的余额）误差中位 0.0000%、99.7% 在 0.01% 以内；"
            + "沪市 456 只（对东财公布值）误差中位 0.0000%。\n"
            + "**只动沪市**：深市 31% 的行 short_balance 本来就是 0（当天确实没融券余量），那是真值。"
            + "判据不能只看「是不是 0」，必须先按 MarketClassifier 分市场。\n"
            + "**ETF 要按带前缀的代码找收盘价**：两融标的里有 465 只 ETF，它们的K线在 Bar 里存成 sh510050，"
            + "两融表里却是裸码 510050——只按裸码找会让 323 只本来有数据的 ETF 也算成补不上。\n"
            + "**幂等**：补过的行不再满足缺值判据，重跑是空转；按交易日分批提交，中断最多丢当天那一批"
            + "（单事务写几百万行曾把 WAL 撑到 162GB，这里不重蹈）。\n"
            + "**补不上的留 NULL 不写 0**：实测 2.46% 的行没有当日收盘价（基本是从未抓到过K线的 ETF 和"
            + "退市股），留 NULL 下次还有机会补，写 0 就永久变成「确实没有融券」了。\n"
            + "⚠ 排在【融资余额】和【个股日K】**之后**——两样都落库了才算得出来。\n"
            + "**两档模式**：「日常增量」只补缺值（幂等、空转，每天跑就用它）；"
            + "「彻底重查」**重算已有值**——取价口径改过之后必须用这一档，"
            + "否则旧的错值永远留在库里。2026-09-16 就踩了：第一版取价用前复权 day，"
            + "已填的 263 万行偏低（茅台 2020-06-01 低 18%、2026-06-01 低 2.1%，越往前越错），"
            + "改成 day_raw 之后**必须跑一次重算**才能纠正。全历史重算约 48 分钟。",
            FetchActionParams.None,
            // 软前置（2026-09-18 随挪进日更一起登记）：这两样是算式的两个因子——
            // 融券余量来自【融资余额】、当日不复权收盘价来自【个股日K·不复权】。
            // 缺哪个都不该拦住它（它本来就是"能算多少算多少、算不出来留 NULL"），
            // 但日志里得留一行，否则某天沪市又出现一批空值时没人知道是前置没跑。
            SoftDependsOn: [FetchActionId.StepMargin, FetchActionId.StepStockRawBars],
            SupportedModes: FetchMode.Incremental | FetchMode.Thorough,
            // 实测最长静默 3 分钟（4001 个交易日、每 100 天一句），对 5 分钟的默认阈值余量太薄，
            // 而"彻底重查"整段要 48 分钟、库越大越慢。它跟上面那项一样是本地大查询，
            // 心跳粒度归 Data 层的 filler 管，这里抬阈值兜住。
            MaxQuiet: TimeSpan.FromMinutes(15)),

        new(FetchActionId.StepFillProbeFloor, "回填\"无更早数据\"水位", "本地查库·不联网", QuotaGroup.Local,
            TimeSpan.FromMinutes(1), "一次性",
            "不发一个请求，直接从本地已有历史推出每只票\"数据源在这天之前没有数据\"的水位，写进 BarProbeFloor 表。\n"
            + "**为什么值得跑一次**：【拉取区间数据】往前补历史时，一只 2020 年上市的票被请求 1990~2016 必然返回空，"
            + "而这个结论以前不落库——2026-09-07 实测一轮区间回补 5558 只 × 3 个粒度、四个半小时、写入为零。"
            + "跑过这一项之后，那些票连请求都不会发。\n"
            + "**判据**：前复权/后复权/不复权三路的最早一根落在同一天。三路是三次独立抓取，都停在同一天说明"
            + "那就是数据源的起点（本机实测 5874 只个股里 5841 只符合；退市股拿两所官网上市日交叉验证，313/319 对得上）。"
            + "三路不一致的、以及只有前复权一路的（ETF/大盘指数/板块指数）都不填，留给真探测。\n"
            + "⚠ **务必在K线补齐之后再跑**（尤其【重新拉取失败】之后）：它读的是本地三路的最早一根，"
            + "要是跑在补齐之前，水位会按旧的（更晚的）最早日记下，那段真实存在的历史反而会被永久跳过。"
            + "同理，将来若又补到了更早的历史，这里记的水位就过期了——用「彻底体检」清空重探。\n"
            + "幂等、可反复跑。要作废这些结论，跑【全库数据体检】并勾「彻底体检」。",
            SoftDependsOn: [FetchActionId.StepStockDayBars, FetchActionId.StepStockHfqBars, FetchActionId.StepStockRawBars]),

        new(FetchActionId.StepDayCoverage, "当日完整性体检", "本地查库·不联网", QuotaGroup.Local,
            TimeSpan.FromMinutes(2), "每工作日",
            "以上证指数最新一根日线当交易日锚，把**当天该有的数据挨个查一遍**，不齐的记进待重试名单。\n"
            + "**这一项是防静默漏抓的**：数据源盘后是逐步更新的，请求发早了接口会正常返回、里面却没有当天——"
            + "不报错、不进失败名单。2026-08-20 那轮 19:00 开跑，个股前复权只拿到 1773/5539 只，"
            + "而界面一切正常、失败名单是空的。\n"
            + "查三类东西（2026-09-16 从只查个股K线扩开）：\n"
            + "　① **K线**——个股三个口径、ETF、指数，各按\"上一个交易日有、这天没有\"逐只对齐；\n"
            + "　② **日更表**——资金净流入、融资余额(T+1)、龙虎榜、席位、大宗交易：整天没有、"
            + "行数远低于邻近水平、某个交易所整天缺，三条判据任一命中就报；\n"
            + "　③ **覆盖式快照**——板块行情、总股本/流通市值这类库里只留最新一份的，看它停在哪天。\n"
            + "①②查出来的名单交给【重新拉取失败】补（它就排在本项后面，当轮就能补上）；③只报，"
            + "该跑哪一项日志里写着。\n"
            + "不查分档资金流（那一项自己收尾时就核对了，它的窗口只有一个交易日、等到这儿才发现就晚了），"
            + "也不查公告/业绩预告/股东增减持这些按事件出的表——某天一条都没有本来就正常。\n"
            + "建议排在所有K线项的最后面。",
            // 三份输入：指数日K 是交易日锚（"最近一个已收盘交易日是哪天"），个股日K 和 ETF日K 是被查的对象
            SoftDependsOn: [FetchActionId.StepIndexBars, FetchActionId.StepStockDayBars,
                            FetchActionId.StepEtfBars]),

        // ══════ 另外三处复合动作拆出来的（2026-09-02，设计文档 3.3 节）══════
        // 判据：成分名单和权重各自入库、各自能单独用 ⇒ 两件事；纯本地的加工（映射、合成、
        // PDF 重解析）一律独立成项——它们不吃配额，失败原因和重跑代价跟联网抓取完全不同。

        new(FetchActionId.StepIndexCons, "指数成分名单", "新浪", QuotaGroup.Sina,
            TimeSpan.FromMinutes(8), "季度",
            "内置的 732 个指数各自的成分股名单。成分名单本身就是一种选股全集（比如\"只在沪深300里选\"）。\n"
            + "新浪对某些老指数本来就没有成分，返回空不算失败；请求失败的进重试名单。\n"
            + "2026-09-18 迁到新任务框架：一批＝一个指数，所以能分批跑、能到点收尾"
            + "（以前中断就是整轮白费）；「只补待办」＝只抓失败名单里那些。",
            SupportedModes: FetchMode.Incremental | FetchMode.FillBacklog),

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
            + "在季度组里**排最后**：它最容易撞墙，排末尾的话即使自己进了熔断，前面那些数据也早落库了。\n"
            + "2026-09-18 迁到新任务框架：一批＝一个指数，能分批跑、能到点收尾；"
            + "「只补待办」＝只抓失败名单（不过那两道筛子——那些就是要重试的）。",
            SupportedModes: FetchMode.Incremental | FetchMode.FillBacklog,
            SoftDependsOn: [FetchActionId.StepIndexCons],
            Sources: [DataSourceId.CsIndex]),

        new(FetchActionId.StepEtfIndexMap, "ETF指数映射", "本地计算·不联网", QuotaGroup.Local,
            TimeSpan.FromSeconds(20), "季度",
            "按名称把 ETF 匹配到指数（\"沪深300ETF华泰\"→沪深300），供\"股票→指数→ETF\"反查。\n"
            + "纯本地匹配，拉完 ETF 名单或指数成分之后跑一次即可。",
            SoftDependsOn: [FetchActionId.StepIndexCons]),

        new(FetchActionId.StepBoards, "板块行情与成分", "东财", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(20), "每工作日～每周",
            "⚠ **已退役**（2026-09-04）：拆成了【板块列表】+【板块成分股】两项。\n"
            + "拆的理由是配额被挤掉了——限流器现在是「每 15 个请求主动歇 2 分钟」（东财实测连发"
            + "16~35 个就被切），而板块列表开头就要 9~10 个请求，等于每轮三分之二的配额花在列表上，"
            + "只剩 5 个才轮到那 2500 个成分股请求。\n"
            + "而且列表一挂整项就退出，成分股一个都跑不成——可库里明明有上一次的板块名单，"
            + "照样能接着抓成分。\n"
            + "老计划里排了它的，加载时会自动换成这两项。",
            Retired: true,
            Sources: [DataSourceId.EmPush2]),

        new(FetchActionId.StepBoardList, "概念和行业板块", "东财 quote（菜单JSON）", QuotaGroup.Mixed,
            TimeSpan.FromSeconds(10), "每工作日",
            "概念/题材板块 + 行业板块的**名单**，**一个请求拿全量**。\n"
            + "✅ **2026-09-05 改走行情中心左侧菜单那份静态 JSON**（sidemenu_new.json），"
            + "从此**不碰 push2、不用浏览器通道、不会弹图片验证码**，也不再占用成分股那边的配额。\n"
            + "换之前逐条比对过：概念 504 个代码和名称跟 push2 官方名单**一个不差**，"
            + "行业只多一个三级行业（BK1362 其他多元金融）——多出来是安全侧，不会误删。\n"
            + "拿不到或解析不了时**自动退回 push2 分页**（那条路会慢很多、可能要人过验证）；"
            + "名单比库里少 5% 以上则**整轮放弃写库**，保留上一次的快照——"
            + "板块是快照数据，「旧的」永远好过「半批的」。\n"
            + "⚠ 板块的涨跌幅/成交额不在这一步取，由【板块指数合成】用本地成分股日K算出来回填。",
            Sources: [DataSourceId.EmQuote]),

        new(FetchActionId.StepIndustryIndicator, "行业景气指标", "东财 datacenter", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(4), "每日",
            "周期品的**价格和库存**：猪粮比价、螺纹钢期货价与库存、焦煤、原油、铜铝锌铅、"
            + "水泥价格指数、国房景气指数、全国汽车销量…共 116 个指标，日/周/月频。\n"
            + "这是**传统行业分析**那一路的输入，跟风口分析分开看——风口看的是叙事能不能兑现成"
            + "别人的报表，周期股看的是价格本身，而价格是**日周频的、比季报早一个季度**。\n"
            + "覆盖 658 只票（约 12%），全是周期股；成长题材一个都没有，这是它的能力边界。\n"
            + "⚠ 历史只到 2024-04（约 2 年），够看当下位置和同比，**不够跑长周期回测**。\n"
            + "约 120 个请求：3 个拉目录、116 个逐指标拉序列（各自按水位线增量，每个只回几行）。\n"
            + "**走 datacenter，不碰 push2**，无需人工过验证码。",
            Sources: [DataSourceId.EmDataCenter]),

        new(FetchActionId.StepPlanWatch, "回购公告进展", "巨潮检索 + 东财正文", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(3), "每日",
            "回答一个问题：**这家公司的回购到底开始买了没有。**\n"
            + "法定披露里这个信号最快就是 T+1——首次回购股份的事实发生后**次一交易日**必须公告；"
            + "回购期间每月前三个交易日披露截至上月末进展；累计每增加总股本 1% 再公告一次。\n"
            + "⚠ 回购走集中竞价，盘中混在主力资金里，**资金流/大宗/席位都识别不出来**——"
            + "没有比公告更快的合法渠道，这一项就是天花板。\n"
            + "抓回来的 stage（方案/首次回购/进展/达标/完毕/终止）是【观察项】自动摘除的依据："
            + "方案实施完毕后该把待办撤下来，而这个动作人一定会忘。\n"
            + "复用【中标/订单公告】那两条已验证的通道：巨潮全市场标题检索「回购」+ 东财正文（免解 PDF）。\n"
            + "回看 14 天（首轮回看 90 天，把还在进行的方案接上），重复扫同一天安全（主键去重）。\n"
            + "⚠ 首轮**不能回看更久**：正文靠「该股最近 100 条公告」匹配，再往前只会抓回一堆没数值的空壳。\n"
            + "⚠ 库里 `cum_amount=0` 是公告明说「尚未实施」，`NULL` 是没抽到——两者不是一回事。",
            // ⚠ **故意不填 Sources**（2026-09-11 修，这是踩出来的）。
            //
            // 原先填了 `[EmDataCenter]`，结果两处都错：发现那一步走的是**巨潮**全文检索
            // （跟【中标/订单公告】同一个 provider），正文走的是 **np-anotice / np-cnotice**
            // 而不是 datacenter —— 枚举里根本没有对应 np-* 的源。
            //
            // 而**填了 Sources 这个动作本身**会关掉 QuotaGroup.Mixed 的保守兜底
            // （见 FetchTaskInfo.EffectiveSources：填了就只认填的，没填才退回 AllOnline）。
            // 于是这一项在占用表上只占了一个它根本不打的源，调度的冲突检查形同虚设——
            // 实测它跟【拉取分档资金流】并发跑起来没被拦，push2his 那边连续 15 只全失败。
            //
            // **填一个错的比不填更糟**，正是 EffectiveSources 注释里"宁可挡住，别撞配额"要防的。
            // 这一项横跨巨潮 + 东财 np-*，在 np-* 有自己的枚举项之前，让 Mixed 保守占用全部
            // 联网源是正确的：它首轮要跑几十分钟，期间本来也不该有别的联网任务插进来。
            Sources: null,
            // 10 分钟而不是默认 5 分钟（2026-09-14）：每月前三个交易日是法定的进展披露窗口，
            // 单日两三百条，取正文 1.3 秒/条——241 条就是 5 分 13 秒，刚好越线被判成卡死掐断
            // （09-14 08:25 真发生过，停在 09-02）。任务侧已改成每 20 条报一次进度，本来就够了；
            // 这里是第二道保险，给月初高峰叠上网络抖动留余量。同【补分档资金流历史】的处置。
            MaxQuiet: TimeSpan.FromMinutes(10)),

        new(FetchActionId.StepWatchIndicator, "观察指标映射", "本地计算", QuotaGroup.Local,
            TimeSpan.FromSeconds(5), "每日",
            "把 `data/watch-indicator-rules.json` 里「**板块 → 该盯的行业指标**」的规则，"
            + "铺成「个股 → 行业指标」的映射。**纯本地查库，一个请求都不发。**\n"
            + "**为什么需要它**：东财自带的映射只给**上游资源股**挂原材料价格——锂电池板块 33 只成分股里"
            + "只有 1 只有映射，碳酸锂指数只挂给 6 只上游锂矿，**宁德时代一个指标都没挂**，"
            + "而锂价正是它的核心成本变量。「中游对上游价格的敏感度」是判断不是结构，自动映射给不出来。\n"
            + "**写在单独一张 StockWatchIndicator 表里**，东财那张原样不动："
            + "那张是 DELETE FROM 全表快照替换，补进去下一轮【行业景气指标】跑完就被静默清空。\n"
            + "⚠ 每轮只重建 `origin='rule'` 的行，**人手挂的（`origin='manual'`）一行不碰**。\n"
            + "**默认启用锂电池那两条**（BK1303/BK1033 → 碳酸锂指数），开箱产出约 107 条映射；\n"
            + "不想要就把配置里那行注释掉重跑——规则派生的行每轮整组重建，撤掉规则它自然就没了。\n"
            + "指标码/板块码写错会在日志里逐条告警，不会静默失效。",
            Sources: []),

        new(FetchActionId.StepCompanyProfile, "公司档案", "东财 datacenter", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(2), "季度",
            "5634 家 A 股的公司档案：全称、省份、成立/上市日期、注册资本、员工数、实控人、董监高、中介机构、主营业务，以及公司简介/沿革/经营范围/经营评述四段长文本。\n"
            + "**直接用途是给【客户与供应商】做对手方还原**——年报里写的是「福建时代星云科技有限公司」这种全称，本地只有简称「宁德时代」，对不上；有了全称才能把对手方还原成股票代码。\n"
            + "但它本身也是一份公司基本面档案，而且这些字段是**同一个请求一起带回来的**，存下来不额外花抓取成本。\n"
            + "14 页、约 2 分钟。长文本单独存一张表（经营评述平均 4186 字、最长 4.6 万字，一个字段占整条记录体积的 69%）。\n"
            + "**走 datacenter，不碰 push2**。",
            Sources: [DataSourceId.EmDataCenter]),

        new(FetchActionId.StepCustomerSupplier, "客户与供应商", "东财 datacenter", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(25), "季度",
            "公司年报里披露的**前五大客户和前五大供应商**，带交易金额和占比。\n"
            + "供应商＝上游、客户＝下游 —— 这是找「产业链上/中/下游」标签一路找空之后能拿到的**最硬的产业链数据**：不是别人的分类判断，是年报里的金额。\n"
            + "（标签那条路已经查死：东财网页、终端本地文件、终端「数据」/「分析」菜单、F10 全部栏目都没有；F10 前端代码里确实有 003=产业链 的分支，但线上一条数据都没有。）\n"
            + "**当下就能用的是集中度**：前五大占多少、「其余」占多少 —— 大客户依赖风险、议价能力变化。\n"
            + "把对手名对到上市主体、连成供应链网络是第二期的事（实测对手名 53% 是真名，71% 的股票至少有一个真名对手，其余是「第一名」这类匿名披露）。\n"
            + "76.5 万行、2002 年至今、2025 年覆盖 5284 只（92%）。**按年切片抓**，不做深分页：全表 1531 页，单年只有约 128 页。\n"
            + "首轮全量约 1531 个请求（20 分钟上下）；之后每轮只抓今年和去年约 260 个 —— 年报是分批披露的，抓到一次不等于抓全了。\n"
            + "**走 datacenter，不碰 push2**。",
            Sources: [DataSourceId.EmDataCenter],
            // 软依赖：没有公司档案也能抓，只是对手方还原不出来、partner_code 全是 NULL；
            // 下轮档案有了会自动补上。所以不是硬前置。
            SoftDependsOn: [FetchActionId.StepCompanyProfile]),

        new(FetchActionId.StepSubsidiaryExtract, "年报子公司名单", "新浪(公告页 + PDF文件)", QuotaGroup.Sina,
            TimeSpan.FromMinutes(40), "季度",
            "解析年报 PDF，提出「合并财务报表范围」那张表里的**子公司名单**。\n"
            + "⚠ **2026-09-15 起会联网**：本地缺年报时，按「被别人写进前五大客户/供应商的次数」"
            + "排出前 50 家自己去下（只下年报——半年报附注是简版，没有完整名单）。"
            + "改版前它是纯本地零请求的。\n"
            + "**零请求自愈仍然成立**：目标报告期从日历算（4 月 30 日是年报法定披露截止），"
            + "文件都在就一个请求都不发——ParserVersion 改版触发的全量重跑照样不联网。\n"
            + "**干什么用**：给【客户与供应商】的对手方还原补第三档。年报里的客户写的是"
            + "「中国建筑第六工程局有限公司」，本地股票池里只有母公司「中国建筑 601668」，直接对不上——"
            + "库里 10.9 万个未还原的对手名里有相当一部分是上市公司的子公司。\n"
            + "⚠ 归并是**假设**不是事实：子公司跟你做生意不等于母公司跟你做生意。所以单独标"
            + "match_type='subsidiary'，置信度低于 exact/normalized，用的时候能分开。\n"
            + "**双策略解析**：有线框的表按线框还原成二维单元格（窄表单元格内会换行，纯文本行处理不了）；"
            + "没线框的按坐标聚类成行。\n"
            + "**实测**（32 家非金融样本）：可用率 81%、提出 773 家子公司，"
            + "拿去还原对手名命中 148 个、回填 920 行、多连出 **328 条全新产业链边**（现有 6632 条，+4.9%）。\n"
            + "读 publish/data/reports —— 2026-09-15 起跟【金融监管指标】的 PDF 共用一个目录。"
            + "合并之前分开放是怕误删（非金融年报混进去会被当成下错的文件），现在那道护栏收在"
            + "FinancialInstitutionRoster.OwnsPdfOf，有一组用例钉着。"
            + "**只吃 *-12-31.pdf**：金融股的半年报也躺在这个目录里，但半年报没有完整名单。\n"
            + "解析规则改了就把 SubsidiaryParser.ParserVersion +1，下轮自动重跑已处理过的。",
            // ⚠ **不能软依赖【客户与供应商】**，虽然优先下载清单确实来自它的「被点名次数」：
            //   那一项反过来要用本项产出的子公司名单做消歧第④档，两者互为依赖、会成环。
            //   既有顺序（档案 → 子公司名单 → 客户与供应商）是对的，让步的该是下载清单——
            //   它**用上一轮的数据完全没问题**：被点名次数是按年报累积的，一轮之间几乎不动，
            //   而且它只决定"先下谁的年报"，下错顺序最多是晚一轮拿到，不会产出错数据。
            //   清单为空（从没跑过客户与供应商）时退化成纯本地解析，跟改版前行为一致。
            SoftDependsOn: [FetchActionId.StepCompanyProfile],
            // PDF 几 MB，下载和解析都慢，5 分钟的默认静默阈值太贴脸。
            MaxQuiet: TimeSpan.FromMinutes(10)),

        new(FetchActionId.StepBoardMembers, "板块成分股", "东财 push2", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(20), "每周·空闲时补",
            "逐个板块查**官方成分名单**，1000+ 个板块约 2500 个请求——这是 push2 上最耗配额的一项。\n"
            + "**不能用 datacenter 的 F10 报表替代**：实测 F10 会系统性漏股（液冷服务器 170 只漏 4 只，"
            + "含美的集团、拓普集团这种链上有实际业务的大票；PCB 漏 2 只），而且漏了不报错，"
            + "会一路带进板块营收中位数这类指标里。\n"
            + "抓到的条数跟接口自报的 total 对不上就**整块丢弃、下轮重抓**，绝不写半批进库。\n"
            + "**跑不完是常态、也没关系**：每个板块单独落库并记进度，下一轮自动跳过已成功的"
            + "（7 天内抓过就算新鲜）；连续失败 10 个判定被限流、提前收尾。\n"
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
            + "模式选「彻底重查」＝清空那份白名单、全部重查一遍——数据源当时抽风、后来补上了的话用它"
            + "（2026-09-09 从一个单独的勾收成模式，见 FetchMode.Thorough）。",
            FetchActionParams.None,
            SupportedModes: FetchMode.Incremental | FetchMode.Thorough),

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
            //    期间【拉取分档资金流】【拉取财务报表】排队 30 分钟后被当时的「硬超时」掐断、
            //    记成"失败"，而它们其实一个请求都没发。（那道硬超时 2026-09-08 已换成
            //    静默看门狗，排队本身也早改成了"让路/抢占"，不会再这样堆着等——见 QuietWatchdog。）
            //
            //    这三个是按它真正会调的东西数出来的：
            //      · Tencent  ── K线主源（纯腾讯；新浪回退 2026-09-10 拆除，见 App.xaml.cs）
            //      · Sina     ── K线回退、流通市值、资金净流入、指数成分、股东、分红，全在新浪
            //      · CsIndex  ── 指数权重（中证 OSS）
            //    收窄之后，用 push2 / push2his / datacenter / quote 的那些项就能跟它并行跑。
            //
            //    ⚠ 唯一的例外：把 BarSource 配成 "EastMoney" 时 K线会走 push2his
            //    （EastMoneyBarFetcher 打的是 push2his.eastmoney.com/api/qt/stock/kline/get），
            //    那种配置下这里就少声明了一个源。眼下不管它——东财在本机网络下常年连不上，
            //    默认也不是它；真要长期用东财当K线源，这里得把 EmPush2His 加回来。
            Sources: [DataSourceId.Tencent, DataSourceId.Sina, DataSourceId.CsIndex]),

        new(FetchActionId.RepairQfq, "重取前复权", "腾讯", QuotaGroup.Mixed,
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
            TimeSpan.FromSeconds(30), "每工作日",
            "抓龙虎榜的**买卖前五营业部明细**。\n"
            + "⚠ 跟已有的【龙虎榜】**不是一回事、不替换它**：那一项只有"
            + "「某天某股上榜了、原因是什么、成交额多少」，**没有营业部名单**。\n"
            + "而龙虎榜的全部价值就在于看**是谁在买**：机构专用席位、知名游资、还是深股通。"
            + "带营业部代码，能跨时间追踪同一个席位——自建游资库、算某个席位的历史胜率都靠它；"
            + "接口还直接给了该营业部近期上榜后的 3 日胜率。\n"
            + "**按交易日抓、整日替换**（2026-09-17 改）：一天是买卖两个接口，两侧都跟接口自报的"
            + "行数对上才删掉那天重写；只对上一侧就整天跳过、记成残缺日。所以反复跑结果都一样。\n"
            + "⚠ 为什么非这么改不可：原来按月切片抓（一片 40~60 页），而排序键 "
            + "TRADE_DATE,SECURITY_CODE **不唯一**（一天一只股有 5~10 行）。东财翻页靠排序定序，"
            + "键不唯一时同键行跨页的先后不保证，**既会重复又会丢行**——2021-05 月片实测收到 6226 行里"
            + "重复 14 行、同时丢掉 14 行真数据。而主键末列 seq 是**位次**，多收一行整组编号就多一位，"
            + "上次落库的高位行没人覆盖得掉，副本就永久留下了。全表因此多了 3579 行副本、"
            + "缺了一样多的真行，年份铺满 2016~2026。\n"
            + "⚠ **别拿\"行数对上了\"当验收判据**：实测 12 天里库中行数全部等于接口 count，错的是内容。"
            + "看的是唯一指纹数（同榜同席位同金额的重复组应为 0）。\n"
            + "改按日之后 5204 个「交易日×买卖侧」里 4941 个只有一页、页边界根本不存在；"
            + "剩下 263 个多页的日侧靠排序键加长兜（加到 EXPLANATION,NET,OPERATEDEPT_CODE）。\n"
            + "日常增量回看 7 天（约 16 个请求，十几秒）——这张表**没有滞后字段**"
            + "（rise_prob_3day 是营业部的滚动统计、每天都在变，回看多久都追不平），"
            + "7 天只为兜住盘后陆续发布和交易所补录。\n"
            + "「首次整段回补」＝2016-01-04 至今，约 **5540 个请求、50~60 分钟**，"
            + "存量清理就用它（缺的那 3579 行只能真抓回来，本地算不出）。\n"
            + "⚠ 没有回退源——新浪/交易所都不提供结构化的营业部明细。",
            // 支持「只抓某一天」就必须要日期参数，否则界面上没地方填那一天、只能落到"默认今天"。
            Params: FetchActionParams.Date,
            SoftDependsOn: [FetchActionId.StepTradingCalendar],
            SupportedModes: FetchMode.Incremental | FetchMode.SpecificDay
                          | FetchMode.FirstBackfill | FetchMode.FillBacklog,
            SupportsPartialRun: true,
            Sources: [DataSourceId.EmDataCenter]),

        new(FetchActionId.FetchBlockTrade, "大宗交易", "东财", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(1), "每工作日",
            "大宗交易明细，带**买卖双方营业部**和折溢价率。大幅折价通常是股东减持套现，"
            + "溢价接盘可能是产业资本，平价对倒多为机构间调仓或换券商席位——"
            + "跟【龙虎榜席位】互补：那边是场内异动席位，这边是场外大额易手。\n"
            + "⚠ 这张表**只有一半是股票**（实测 A股 48%、可转债/债券 41%、基金 2%），用之前要按代码前缀筛，"
            + "否则折溢价分布被债券带偏；折溢价率存的是**小数**（-0.0628 = 折价 6.28%）。\n"
            + "**按交易日抓、整日替换**（2026-09-17 从【拉取市场事件】拆出来时改的）："
            + "抓完一天先跟接口自报的行数核对，对得上才删掉那天重写，对不上就跳过并记成残缺日。"
            + "所以反复跑多少次结果都一样，也不会拿半天的数据盖掉完整的一天。\n"
            + "⚠ 为什么非这么改不可：主键第三列原来存东财的 DAILY_RANK，而那个值**跨抓取会变**"
            + "（同一笔交易 09-15 抓到 rank 22、09-16 抓到 rank 1），UPSERT 认不出「同一笔」，"
            + "每次重抓都 INSERT 一份副本。全表曾因此多出 3087 行，最近一个月的笔数和金额普遍虚高一倍。\n"
            + "单日 100~600 笔、一页 500 行，所以**永远只有 1~2 页**，深分页的坑绕开了。"
            + "日常增量抓最近 30 天（约 35 个请求，一分钟）；整段回补 2016 年至今约 3000 个请求。\n"
            + "⚠ 没有回退源——新浪/腾讯/交易所都不提供结构化的营业部明细。",
            // 支持「只抓某一天」就必须要日期参数，否则界面上没地方填那一天、只能落到"默认今天"。
            Params: FetchActionParams.Date,
            SupportedModes: FetchMode.Incremental | FetchMode.SpecificDay
                          | FetchMode.FirstBackfill | FetchMode.FillBacklog,
            Sources: [DataSourceId.EmDataCenter]),

        new(FetchActionId.FetchMarketEvents, "拉取市场事件", "东财", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(6), "每工作日",
            "一次抓三份本地此前**完全没有**的数据：\n"
            + "**机构调研**——带参与调研的机构名单。一家公司突然被几十家机构集中调研，"
            + "常常早于股价异动。属软信号：它证明「有人在关注」，不证明「基本面变好」。"
            + "⚠ **接口只保留滚动一年**（实测自报 28.4 万行、最早只到一年前），跟别的几项能回溯到 2016 不同——"
            + "长历史只能靠每天抓、慢慢养。\n"
            + "**限售解禁**——⚠ **含未来的解禁计划**（实测有 2035 年的），是「日程表」不是「历史表」，"
            + "所以每次全量重取（3 万行、63 页）。解禁是次新股最明确的时间节点。\n"
            + "**股东增减持**——跟已有的十大股东表互补：那张说「季末谁持有多少」，"
            + "这张说「期间谁在买卖、多少、什么价」，是明确的内部人信号。\n"
            + "三项各自独立失败：一项挂了不影响其余（覆盖面和重要性本来就不一样）。\n"
            + "首次全量约 44 万行、6 分钟；之后增量每次几十页。\n"
            + "⚠ **大宗交易 2026-09-17 拆成独立的【大宗交易】一项**——它改成了按交易日整日替换"
            + "（东财的 DAILY_RANK 跨抓取会变，原来当主键用，重抓一次就多一份副本），"
            + "切片从十几片变成两千多片，跟这三张的节奏对不上。\n"
            + "⚠ 没有回退源——这三份数据新浪/腾讯/交易所/巨潮都不提供结构化版本。\n"
            + "**「首次整段回补」**（2026-09-19 开放）＝不看水位线、从 2016 抓起（机构调研实际只有"
            + "滚动一年）。用在**改了排序键之后**：排序键定不出唯一序时深分页会跨页重复 + 遗漏，"
            + "而遗漏那半没有任何告警，只能整段重取一遍才补得回来。跑完记得改回「增量」。\n"
            + "⏳ 机构调研有时效：接口只保留滚动一年，漏掉的行会逐日滚出窗口，那之后再补也补不回来。",
            SupportedModes: FetchMode.Incremental | FetchMode.FirstBackfill,
            SupportsPartialRun: false,
            Sources: [DataSourceId.EmDataCenter]),

        new(FetchActionId.FetchMoneyFlowSnapshot, "分档资金流快照", "东财", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(2), "每日",
            "抓**当日全市场**的分档资金流：超大单/大单/中单/小单各自的净额和净占比，共 10 个维度。\n"
            + "走东财 push2delay 的全市场排行接口，约 60 个请求、一两分钟把 5500 只全拿到——"
            + "日常增量全靠这一条通道。\n"
            + "⚠ **漏一天就永久没了**：这个接口只给「最近一个交易日」，当天收盘后没跑，"
            + "下一个交易日开盘一到就滚到新一天。2026-09-09 全市场整天缺失就是这么来的——"
            + "补回来要走逐股那条通道、5500 个请求只换回一天数据。所以它归日更，不归「空闲时补」。\n"
            + "⚠ **盘中不入库**：收盘清算前拿到的是半天的资金流，写进去会污染当天那一行、"
            + "事后完全看不出来。判据是接口自报的行情时间；盘中跑会记「本轮没开工」，当天稍后还会再来。\n"
            + "对账：服务端自报的总数减去停牌数就是该拿到的行数，差额会报出来——"
            + "翻页少翻一页、某页被限流截断，表现出来都只是「今天少几百只」，不报的话没人会发现。\n"
            + "历史缺口归【分档资金流·补历史】那一项，两项写的是同一张表（逐条比对过、零差异）。",
            SupportsPartialRun: false,
            // 软依赖**不复权**个股日K（2026-09-18 补登记）：收尾那次"当天齐没齐"的核对拿它当期望
            // （"有K线的那天就该有资金流"，判据见 SqliteMoneyFlowDayAudit）。排在它前面的必须是
            // 不复权那一套——前复权排在这一项之后，拿它当期望的话核对时它还没到位，
            // 判据只会说"无法核对"，而这一项的全部意义就是当晚发现缺口。
            SoftDependsOn: [FetchActionId.StepStockRawBars],
            Sources: [DataSourceId.EmPush2Delay]),

        new(FetchActionId.FetchTotalShares, "总股本", "东财", QuotaGroup.Mixed,
            TimeSpan.FromSeconds(10), "每日",
            "全市场当前总股本（单位股），一个请求、一两秒——东财条件选股接口 ps=10000 一页装得下 5562 只。\n"
            + "⚠ **这是 PE/PB 的股数来源**，此前一直用财报的 share_capital（「实收资本(或股本)」）代替。"
            + "那是**金额、单位元**，等于 总股本 × 每股面值；A 股绝大多数面值 1.00 元，两个数恰好相等，"
            + "于是这个巧合被当成了定义。\n"
            + "2026-09-14 逐只比对，5561 只里 373 只对不上，两个方向都有：报表偏小的 269 只"
            + "（面值 <1 元，紫金矿业差 10 倍、诺诚健华差 7.5 万倍），报表偏大的 104 只"
            + "（H 股会计口径含股本溢价，中国移动 4703.59 亿元 vs 216.91 亿股）。"
            + "重算后 277 只（7.1%）的 PE 变了：中国移动 348.8 → 16.1、分众传媒 0.5 → 20.1。\n"
            + "⚠ 偏大那一类**没有本地判据能发现**——「流通市值÷收盘价 > 报表股本」只抓得出偏小的，"
            + "所以这份数据只能取，不能靠本地校验兜住。\n"
            + "拿不到的票（退市/停牌居多）库里就是没有这一行，PE/PB 回退用报表股本并在界面上标识。\n"
            + "两道护栏：比库里在市个股少 5% 以上整轮不写库；一只北交所股票都没拿到也不写"
            + "（前缀规则漏掉 920 曾让 342 只票静默抓不到，而 6% 的缺口进不了 5% 的门槛）。",
            SupportsPartialRun: false,
            Sources: [DataSourceId.EmDataCenter]),

        new(FetchActionId.FetchMoneyFlowDetail, "分档资金流·补历史", "东财", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(6), "季度定期组·空闲时补（每轮 30 只）",
            "抓**分档**资金流：超大单/大单/中单/小单各自的净额和净占比，共 10 个维度。\n"
            + "⚠ 跟已有的【资金净流入】**不是替换、是同一件事的不同精度**：那张表 1077 万行，"
            + "但每行只存了一个「主力净额合计」。\n"
            + "为什么要拆开看：同样是「主力净流入 1 亿」，**超大单进、小单出**（机构在建仓）跟"
            + "**大单进、超大单出**（游资接力）含义完全相反，合计数把这个信息抹平了。"
            + "判断一波行情是谁在买，靠的就是这个结构。\n"
            + "⚠ **接口只给最近约 120 个交易日**，lmt=0 也突破不了——所以拿不到长历史，"
            + "历史深度只能靠定期抓取慢慢养。短期内做不了长周期回测，但看「当下这波是谁在买」够用。\n"
            + "这一项只做**逐股补历史**（push2his，一只票一个请求给 120 天，只有它补得了历史缺口）；"
            + "当日增量归【分档资金流快照】那一项（2026-09-12 拆开，两项写同一张表，已验零差异）。\n"
            + "补历史的排队判据：**期望行数按本地日K根数算**（有K线的那天就该有资金流），不是固定行数门槛——上市不足 100 个交易日的次新股永远凑不出 100 行，老口径让它们每轮重抓。\n"
            + "  · 「增量」＝窗口内缺 3 行以上才补。缺 1~2 行（多半是某天全市场快照漏了）"
            + "只在日志里报一句——全市场为一行各补一次是 5500 个请求、约 20 小时机时，不值当；\n"
            + "  · 「首次整段回补」＝缺一行就补，用来把那种整天缺失补回来（一只一个请求，会跨好几轮）。"
            + "\n⚠ 这一项的「整段」只能是那 120 个交易日的窗口（别的项是"
            + "从开市首日抓起）——接口给不了更早的，窗口内补齐了就是这一项能到的头，"
            + "界面上那句「历史已补齐」说的就是它（2026-09-11 从「彻底重查」改过来：这一项只补缺、"
            + "不动已有的票，那是回补不是重查；「彻底重查」＝不管有没有全部重来，只有【全库数据体检】是那个意思）。\n"
            + "请求由**真浏览器**发（2026-09-14）：本机公司网关按域名把 push2his 整个拦了——"
            + "程序直连 TCP/TLS 都通、一发请求就被切、收到 0 字节，等多久、换哪块网卡都没用；"
            + "同一时刻真 Chrome/Edge 能稳定取到（2026-09-13 实跑验证过一整份清单）。"
            + "所以这一项跑起来会**弹一个浏览器窗口**并一直开着，那是正常的。"
            + "打的 URL 跟直连那条一模一样，配置 MoneyFlowBackfillTransport 可以切回 http。\n"
            + "⚠ **每轮只问 20 只**（浏览器通道；直连通道 30 只）：累计 16~35 个请求就被切，"
            + "所以节奏是间隔 2 秒、每 10~15 只主动歇 2~5 分钟（都带随机），摊下来约 17~20 秒/只；"
            + "一轮不限量就是十几个小时、期间别的任务全得让路。这份数据没有时效压力（120 天窗口内随时补），\n"
            + "所以改成一轮一轮慢慢补——勾上「空闲时自动补」它自己会补完，也可以多点几次执行。\n"
            + "估时 6 分钟说的就是这一轮（空闲调度拿它判断空档塞不塞得下，写成全量耗时会让它永远排不上）。\n"
            + "按最久没抓的先抓，跑不完下轮接着来，连续失败到阈值（浏览器通道 25 只／直连 15 只）"
            + "会提前收尾并自动暂停一段时间。\n"
            + "⚠ 限流是按**出口 IP** 算的，板块那两项走的也是浏览器、共用同一个出口——"
            + "调度上两边声明的数据源不同、不会互相挡，所以别把它们排在同一个时段。",
            SupportedModes: FetchMode.Incremental | FetchMode.FirstBackfill | FetchMode.FillBacklog,
            SupportsPartialRun: true,
            // 10 分钟而不是默认 5 分钟：push2his 每 15 个请求主动歇 2 分钟，单只失败还要静默重试
            // （2s+10s，三次 25 秒超时摊下来近 90 秒）。5 分钟余量太紧，2026-09-11 就是这么被
            // 误判成卡死掐断的。任务侧已改成按时间报进度，这里是第二道保险。
            MaxQuiet: TimeSpan.FromMinutes(10),
            // 拆走快照之后这一项只碰 push2his——声明窄一点，才跟走 push2delay 的快照并行不冲突
            Sources: [DataSourceId.EmPush2His]),

        new(FetchActionId.FetchRawBars, "补不复权历史", "腾讯", QuotaGroup.Mixed,
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
            DependsOn: FetchActionId.StepStockRawBars, SupportsPartialRun: true,
            // 「首次整段回补」在这一项里的意思是**全量重算**：忽略五条判据，有不复权日线的票
            // 全部整段重来（2026-09-10 加）。改过复权算法之后需要它——原来只能靠手工删掉
            // day_adj 逼判据重新认出来，而那一步没有任何地方记着该怎么做。
            SupportedModes: FetchMode.Incremental | FetchMode.FirstBackfill),

        new(FetchActionId.FetchBoards, "拉取板块", "东财", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(5), "每工作日～每周",
            "概念/题材板块 + 行业板块的行情与成分股，拉完自动用本地K线重算板块指数（不联网）。\n"
            + "⚠ **已退役**（2026-09-02）：拆成了【板块行情与成分】+【板块指数合成】两项——"
            + "抓取失败不再连累合成，改了合成算法也能单独重算、不用重抓一遍板块。"
            + "老计划里排了它的，加载时会自动换成这两项。",
            Retired: true,
            Sources: [DataSourceId.EmPush2]),

        new(FetchActionId.FetchIndustry, "拉取行业分类", "东财 + 交易所", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(2), "季度",
            "证监会两级行业分类。行业极少变动，跟财报同频跑一次即可，**整表重写**、反复跑无副作用。\n"
            + "2026-09-10 换成东财（门类名+大类都由它给，门类字母仍来自两所）：有大类的票 3886 → 6006 只、"
            + "门类叫法从 32 种收敛到 19 个标准名（原来沪深两所各说各话），实测「库里有、东财无」为 0 只。\n"
            + "⚠ 整表重写不是「顺手」：两个源的大类名分属证监会分类的不同修订版，新旧名并存会把同一个行业"
            + "裂成两个中性化分组。要退回新浪：改 fetcher-settings.json 的 IndustrySource。",
            Sources: [DataSourceId.Exchange, DataSourceId.EmDataCenter]),

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
            "逐只抓股东户数 + 十大股东 + 十大流通股东的全部历史（一只票 2 个请求）。"
            + "十大流通股东里的「香港中央结算」就是北向资金的名义持有人。\n"
            + "**2026-09-18 迁到新任务框架**（ShareholderTask）：一批 30 只、抓一批存一批，"
            + "**中途停止再点执行会接着抓、不从头来**。\n"
            + "「要不要抓」按**报告期**判，不按「多久没抓」：库里这只票股东户数的最新报告期"
            + "落后于它**已披露**的最新一期才抓（披露日来自【拉取财报预约日】，查不到的退回"
            + "法定截止日）。所以半年报抓齐之后、三季报披露之前，这一项一个请求都不发。\n"
            + "另有两条：100 天没抓过的整只重刷一遍（兜那 4% 不定期的股东名单变动公告）；"
            + "一年多没成交的跳过（恢复交易会自动回到名单）。\n"
            + "⚠ 不设人为的每轮上限——季度高峰约 60 分钟一轮跑得完；真撞限流会在连续 3 批"
            + "全部失败时收工记成「跳过」，下轮接着抓剩下的。\n"
            + "**名单含退市股**（2026-09-18 纳入，口径跟分红一致）：回测要消除幸存者偏差，"
            + "而纳入前 337 只退市股里只有 5 只有股东数据——十大流通股东里的「香港中央结算」"
            + "是北向持股的唯一来源。退市股的时间兜底走 365 天一档（它们的数据是静态历史），"
            + "而「报告期永远落后」那一面靠「一年没成交就跳过」挡住。\n"
            + "⚠ 退市名单来自【补全退市名单】：那一项没跑过，这边就少抓那些票（不报错，只是少）。",
            SupportedModes: FetchMode.Incremental | FetchMode.FirstBackfill | FetchMode.FillBacklog,
            SupportsPartialRun: true,
            Sources: [DataSourceId.Sina],
            SoftDependsOn: [FetchActionId.FetchEarningsSchedule, FetchActionId.StepDelistedSupplement]),

        new(FetchActionId.FetchFinancials, "拉取财务报表", "东财 F10（保险走新浪）", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(90), "季度·跨天分轮",
            "三张表 52 个科目的全部报告期。⚠ 这个接口配额很严，已单独降速到约 10 请求/分钟、"
            + "每轮上限 300 只（约 90 分钟），全市场要跨几天补完。"
            + "**建议把重复规则设成「空闲时」**：程序空着就自己补一批，到点前自动收尾给定时任务让路，"
            + "跨几天慢慢啃完。设成「每月某天」只会跑一轮 300 只，全市场根本补不完。\n"
            + "「要不要抓」按每只票的**实际披露日**判断（来自【拉取财报预约日】），不是法定截止日——"
            + "所以那一项要是好几天没跑成，这边会以为没人披露而少取。\n"
            + "**2026-09-10 换成东财 F10**（固定英文列名、按 ORG_TYPE 选 G/B/S 三套表），"
            + "datacenter 的配额比新浪宽得多，所以上面那句「10 请求/分钟」只在退回新浪时成立。\n"
            + "⚠ **保险那 5 家仍走新浪**（东财整组不填赔付支出/退保金/保单红利/分保费用），"
            + "所以两个源都占——见 FinancialSourceRouter。要退回新浪：改 fetcher-settings.json 的 FinancialSource，"
            + "但要知道新浪那条路有两个老毛病：幽灵 0（银行的应付账款/存货被写成 0）、营业支出漏掉信用减值。",
            SupportsPartialRun: true,
            Sources: [DataSourceId.Sina, DataSourceId.EmDataCenter],
            SoftDependsOn: [FetchActionId.FetchEarningsSchedule]),

        new(FetchActionId.FetchDividend, "拉取分红送配", "新浪", QuotaGroup.Sina,
            TimeSpan.FromHours(2), "年度",
            "逐只抓历年全部分红方案，**同一次请求顺带抓配股**（源页面上分红和配股是两张表，不额外花请求）。\n"
            + "做股息率因子、核对除权除息日都靠分红这张表。\n"
            + "配股是A股第四类除权事件（前三类是现金分红/送股/转增），2026-09-01 起才抓——漏掉它，"
            + "回测序列 day_adj 会在配股除权日凭空多一根阴线：实测招商证券 2020 年那次 10配3@7.46 "
            + "让十年累计收益少算了 25 个百分点，中信证券少 12 个。配股集中在**银行和券商**，正是底仓的重点。\n"
            + "⚠ 抓完要再跑一次【重算回测序列】，配股才会体现到 day_adj 上。\n"
            + "**2026-09-18 迁到新任务框架**（DividendTask）：一批 30 只、抓一批存一批，"
            + "每只的抓取时刻记在 DividendFetchState 表里。于是——\n"
            + "· 【增量】只抓 25 天以内没抓过的（含从没抓过的），**中途停止再点执行会接着抓、不从头来**；\n"
            + "· 想强刷全市场用【整段回补】；\n"
            + "· 连续 3 批（90 只）全部因限流失败就收工记成「跳过」，限流过去今天还能再来。\n"
            + "**2026-09-18 加了公告索引**：先问东财最近 45 天谁出了分红公告（含预案、进度更新），"
            + "只抓这些 + 到期兜底的那批，一轮从 5902 个请求降到一两百，每轮上限 400 只。\n"
            + "⚠ 值仍然取新浪，东财只当索引——它当值源不合格（退市股全空、配股比例只在文本里），"
            + "实测还有约 1% 漏检，靠 90 天全量兜底捞回来（退市股 365 天一轮，它们的分红不会再变）。\n"
            + "索引拿不到就自动退回纯水位线并在日志里说一声；想彻底关掉：fetcher-settings.json 里"
            + "把 DividendNoticeIndex 设成 none。",
            SupportedModes: FetchMode.Incremental | FetchMode.FirstBackfill | FetchMode.FillBacklog,
            SupportsPartialRun: true,
            Sources: [DataSourceId.Sina, DataSourceId.EmDataCenter]),

        new(FetchActionId.FetchDividendReconcile, "分红对账", "东财", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(5), "季度",
            "拿东财 RPT_SHAREBONUS_DET 全量（按年切片，约 120 个请求、3 分钟）跟库里的分红比一遍，\n"
            + "**把新浪漏掉的除权记录补进来**。只加不删：库里有而东财没有的一律不动\n"
            + "（东财那张表退市股全空，2,516 条只有我们有），两边互补、谁也不是权威。\n"
            + "为什么要对账：分红是复权因子的输入，**缺一条除权记录，那只票的复权序列整段错且不报错**\n"
            + "——行都在、值也都对，只是少了一次事件，没有任何现成的体检查得出来。\n"
            + "2026-09-18 首次对账查出 1,050 条缺口，其中 1,043 条是北交所：新浪对北交所覆盖不全\n"
            + "（920061/920547/833171/430047 实测都返回「暂时没有数据」）。\n"
            + "⚠ 补完如果日志说有缺口落在 day_adj 区间内，要再跑一次【重算回测序列】。",
            Sources: [DataSourceId.EmDataCenter]),

        new(FetchActionId.BankRegulatory, "金融监管指标", "新浪(页面 + PDF文件)", QuotaGroup.Sina,
            TimeSpan.FromHours(1), "半年（年报/中报后）",
            "下载银行/券商/保险的年报和中报 PDF，解析三张报表里没有的监管指标"
            + "（不良率、拨备覆盖率、资本充足率、风险覆盖率、偿付能力充足率等）。"
            + "每次都会先用当前规则把本地已有 PDF 重解析一遍（不联网，几分钟）。"
            + "⚠ 前置：财务报表——机构类型是靠特征科目认出来的。\n"
            + "没披露的机构不会去翻公告列表（按实际披露日判断，来自【拉取财报预约日】）——"
            + "这一段限流很紧，披露季前期挨家去查全是空转。",
            DependsOn: FetchActionId.FetchFinancials,
            SoftDependsOn: [FetchActionId.FetchEarningsSchedule],
            // PDF 有几 MB，单个下载的 HttpClient 超时就设到 3 分钟（BankReportFetcher）；
            // 再叠上解析一个大 PDF 的时间，5 分钟的默认阈值太贴脸。
            // 2026-09-19 从 10 分钟抬到 30：实测（09-15 日志）它开工前**哑了 16 分 42 秒**——
            // 那是认机构类型要读的两次全库查询（GetLatestSnapshotByCode / GetFetchStateByCode，
            // 25GB 库上就是这么慢），一条 SQL 没有可切分的心跳单位，10 分钟照样掐。
            MaxQuiet: TimeSpan.FromMinutes(30)),

        new(FetchActionId.ImportManual, "导入手工数据", "本地文件", QuotaGroup.Local,
            TimeSpan.FromSeconds(5), "人填完 CSV 后",
            "把填好的「待手工回填清单.csv」写回库。文件没填就是空跑，无副作用。",
            DependsOn: FetchActionId.BankRegulatory),

        new(FetchActionId.BackfillDaily, "一键补齐每日历史", "交易所", QuotaGroup.Exchange,
            TimeSpan.FromMinutes(30), "按需补洞",
            "把融资余额、龙虎榜的历史从各自的数据起点（两融 2010-03-31、龙虎榜 2002-01-01）补到今天，"
            + "跳过本地已有的交易日。幂等、可反复跑。\n"
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
            + "⚠ 几类数据有各自的**数据起点**（两融 2010-03-31、龙虎榜 2002-01-01、资金净流入 2010-03-01），"
            + "填的年份比它早时会自动上提到起点、并在日志里说一句——那不是漏抓，是那几年源上根本没有。\n"
            + "⚠ 起点还会被钳到 A股开市首日 1990-12-19：填得比它更早会让\"本地已补齐就跳过\"的判断失效、每只标的都白发一次请求。\n"
            + "中标公告按**自然年切片**搜索（巨潮单次搜索有翻页上限，一次跨二十几年会翻满即停、剩下的静默丢掉）。\n"
            + "补完之后，期间除过权的票会自动记进【重取前复权】的待办名单——新补的那段用的是数据源当前基准，"
            + "跟库里较新那段的基准可能对不上，接缝处会有假跳空。\n"
            + "⚠ 这一项**故意保持复合**（2026-09-02 评估）：它内部各段共享同一次"
            + "\"每只标的本地最早是哪天\"的预取，拆开的话每段都要把这几 GB 的库各扫一遍；"
            + "而它本来就是一次性整批回补、几乎没有\"只补某一类\"的用法。要只补某一类历史时，"
            + "用对应项的「首次整段回补」模式更省。",
            FetchActionParams.YearRange | FetchActionParams.GlobalFetchOptions | FetchActionParams.Keywords),

        new(FetchActionId.OptimizeDatabase, "优化数据库", "本地", QuotaGroup.Local,
            TimeSpan.FromMinutes(5), "一次性",
            "给几张大表补建二级索引。不联网。建完就是持久对象，之后不用再跑。",
            // 唯一的真·黑盒：CREATE INDEX 和 ANALYZE 各是一次 ExecuteNonQuery，中间没有任何
            // 可以插进度的地方，而库现在 23GB，单条索引跑十几分钟很正常。它期间会定时播报
            // "仍在建 xxx"，但那条走 Liveness 通道、**不算心跳**（见 QuietWatchdog 类注释），
            // 所以这一项实际是按时长兜底的——明知的代价，好在它只在手动组里。
            MaxQuiet: TimeSpan.FromMinutes(30)),
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
        FetchActionId.FetchBlockTrade,
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
            or FetchActionId.FetchMarketEvents
            or FetchActionId.FetchBlockTrade => PlanGroupKind.Daily,

        // 【分档资金流快照】（2026-09-12 拆出来）归日更，而且是"漏一天就永久没了"的那一类：
        // push2delay 只给最近一个交易日的资金流，当晚没抓，下一个交易日开盘接口就滚到新一天，
        // 事后只能靠逐股那条通道 5500 个请求换回一天。59 个请求、一两分钟，放日更毫无负担。
        FetchActionId.FetchMoneyFlowSnapshot => PlanGroupKind.Daily,

        // 【补全退市名单】（2026-09-19 从季度组挪过来）：它的产出是后面所有"逐只抓"的项的输入
        // 名单，晚一个月认出一只退市票 = 那一个月里每天都在白抓它。成本是候选 0~3 只、28 秒。
        FetchActionId.StepDelistedSupplement => PlanGroupKind.Daily,

        // 【总股本】归日更：股本本身不常动（增发/回购/送转才变），但一动就直接改 PE/PB，
        // 而一个请求一两秒，放日更毫无负担。不放季度组是因为"季度才跑一次"意味着
        // 送转之后最长两三个月里 PE 都是错的，代价和收益完全不成比例。
        FetchActionId.FetchTotalShares => PlanGroupKind.Daily,

        // 【行业景气指标】（2026-09-07）归日更：116 个指标里 45 个是日频，每天都变。
        // 增量很轻——每个指标只拉水位线之后的那几行。
        FetchActionId.StepIndustryIndicator => PlanGroupKind.Daily,

        // 【ETF日K·不复权】跟着【ETF日K】走日更：稳态下它几乎不发请求（没除权的那 1336 只
        // 直接从 day 复制，判据纯本地），而漏一天就要在下一轮多翻一页补回来。
        FetchActionId.StepEtfRawBars => PlanGroupKind.Daily,

        // 【基金除权除息】归周期组：它读的是东财终端自己更新的本地文件，不定期变；
        // 而且一次 20 秒、不发请求，晚几天补上毫无代价。
        FetchActionId.ImportFundExDividend => PlanGroupKind.Periodic,

        // 【观察指标映射】（2026-09-11）跟着【行业景气指标】走日更。它本身变得很慢（规则改了才变），
        // 但重算是纯本地、毫秒级、幂等，每天白跑一次的成本可以忽略；而放到季度组的话，
        // 改完规则要等下一个季度才生效，或者每次都得手工点一下——那正是这套东西想省掉的事。
        FetchActionId.StepWatchIndicator => PlanGroupKind.Daily,

        // 【融券余额补算】（2026-09-18）归日更：它补的是**当天**那批两融数据里沪市缺的那一列
        // （上交所 rqylje 恒 null），跟着【融资余额】一起走才有意义。纯本地 UPDATE、不发请求，
        // 增量只补缺值、没新数据就空转，放日更没有任何负担。
        // 不归【按需启动】：那一组是"想起来才做的一次性活"，而这一项漏一天就有一天的沪市融券余额是空的。
        FetchActionId.StepFillShortBalance => PlanGroupKind.Daily,

        // 【回购公告进展】（2026-09-11）必须日更：首次回购是**次一交易日**披露的，
        // 晚一天就失去意义——那正是这一项唯一要等的信号。
        FetchActionId.StepPlanWatch => PlanGroupKind.Daily,

        // ── 按周期更新、晚几天没关系的 ──
        FetchActionId.FetchIndustry
            // 【个股行业与题材】（2026-09-03）跟证监会分类同组同频：行业归属变动很慢，季度一轮够了。
            or FetchActionId.FetchStockBoardMap
            or FetchActionId.StepIndexCons or FetchActionId.StepIndexWeight
            or FetchActionId.StepEtfIndexMap or FetchActionId.FetchShareholder
            or FetchActionId.FetchDividend or FetchActionId.BankRegulatory
            or FetchActionId.StepReparseBankPdf or FetchActionId.ImportManual
            or FetchActionId.FetchFinancials
            // 【分档资金流·补历史】归定期而不是日更（2026-09-12 订正理由）：日常增量早就不走它了——
            // 那是【分档资金流快照】的事（59 个请求、一两分钟，已归日更）。留在这一组的只有
            // 逐股补历史：一只票一个请求、全市场一轮 5500 个请求十几小时，而且 120 天窗口内
            // 随时补都来得及、没有时效压力。正是"空闲时补"的典型。
            // ⚠ 老注释说的"没有增量入口、每轮得把 5500 只重抓一遍"已经不成立（那是 09-03 只有
            //   逐股一条通道时的情形），别照着它再把整项挪回日更。
            or FetchActionId.FetchMoneyFlowDetail
            // 【板块成分股】(2026-09-04 拆出来）跟分档资金流同样的处境：请求量大（约 2500 个）、
            // 没有时效压力（7 天内抓过就算新鲜），正适合"空闲时补"，别占着日更那一段。
            or FetchActionId.StepBoardMembers
            // 【公司档案】(2026-09-08) 归定期：一个季度才动一次，14 页很轻，
            // 排在客户与供应商前面——后者做对手方还原时要用它的全称。
            or FetchActionId.StepCompanyProfile
            // 【客户与供应商】(2026-09-07) 归定期：数据源是年报/中报，一个季度才动一次，
            // 而首轮要把 2002 年至今 76.5 万行抓全（约 1531 个请求）。没有时效压力、量又大，
            // 正是"空闲时补"这一组的典型。
            or FetchActionId.StepCustomerSupplier
            // 【年报子公司名单】(2026-09-11) 紧跟【客户与供应商】：它产出的名单就是给
            // 那一项的对手方还原当第三档用的。纯本地计算、没有时效压力，没有新 PDF 时
            // 自己就判定无事可做（水位线按份记）。
            or FetchActionId.StepSubsidiaryExtract
            or FetchActionId.FetchIndexCons => PlanGroupKind.Periodic,   // 最后这个已退役

        // ── 按需启动：想起来才做的一次性活（往回补历史、全库体检、建索引）──
        _ => PlanGroupKind.OnDemand,
    };

    /// <summary>
    /// 计划行里那些参数格的**默认值**（2026-09-02）。
    ///
    /// 为什么要有它：这些格子原来是留空的、留空表示"用【手动】页那个框里的值"。可空着的格子
    /// 看不出会用什么值——人得知道那一行到底按几年回看、按什么关键词搜，才能判断要不要改。
    /// 所以建计划项时直接把默认值填进去。
    ///
    /// 2026-09-08【手动】页撤掉之后，这里同时也是**运行期的兜底**：用户手工清空了某个格子
    /// 又直接点【执行】时，回看年数和年份区间取这里的值（关键词是例外——清空就是"别抓"）。
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

            // 【龙虎榜·换源重抓】退役（2026-09-10），**没有等价项**——空列表就是"原地删掉"。
            // 它是一次性迁移工具：09-10 跑过一次把 2004 年至今 26.8 万行从新浪换成东财，
            // 之后没有再用的场合（重算派生列该走本地重算、不该重发 580 个请求）。
            [FetchActionId.StepLhbMigrate] = [],

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
    /// <summary>某一组的模板顺序。给"换过组的动作该插在第几位"用，见 FetchPlan.TemplateIndexIn。</summary>
    public static IReadOnlyList<FetchActionId> OrderOf(PlanGroupKind kind) => kind switch
    {
        PlanGroupKind.Daily => DailyOrder,
        PlanGroupKind.Periodic => PeriodicOrder,
        _ => OnDemandOrder,
    };

    public static readonly IReadOnlyList<FetchActionId> DailyOrder =
    [
        // ── K线族 ──
        FetchActionId.StepRoster,
        // 【总股本】紧跟名册（2026-09-14）：两者都是"全市场当前是什么样"的截面，而且它一两秒就完。
        // 排在名册之后是因为覆盖护栏要拿库里在市名册当基准——名册先刷新，护栏才不会因为
        // "接口有新股、本地名册还没有"而误报。
        FetchActionId.FetchTotalShares,
        // 【补全退市名单】紧跟名册（2026-09-19 从季度组挪进日更）：它要拿刚刷新的在市名册当输入，
        // 而它的产出——StockMeta.type='delisted'——是**后面所有"逐只抓"的项的输入名单**。
        // 排在K线族前面，新摘牌的票当晚就不再被白抓（K线/资金流/名册每天几百个必然落空的请求）。
        // 为什么值得每天跑：它平时候选 0~3 只、28 秒、一个巨潮请求打底；而搁在季度组的代价是
        // 一只票退市后最长要等一个月才被认出来，这期间所有日更项都在陪跑。
        FetchActionId.StepDelistedSupplement,
        FetchActionId.StepStockDayBars,
        FetchActionId.StepStockRawBars,
        FetchActionId.StepStockHfqBars,
        FetchActionId.StepEtfBars,
        FetchActionId.StepEtfRawBars,
        FetchActionId.StepIndexBars,
        // ── 资金与交易 ──
        FetchActionId.StepNetInflow,
        // 【分档资金流快照】紧跟在【资金净流入】后面（2026-09-12）：两者是同一件事的两个精度
        // （那边是主力净额合计，这边拆成超大/大/中/小四档），放一起看得出是一族。
        // 它必须当天跑成——接口只给最近一个交易日，隔一个开盘就永久取不回来了。
        FetchActionId.FetchMoneyFlowSnapshot,
        FetchActionId.StepMargin,
        // 【融券余额补算】紧跟【融资余额】（2026-09-18 挪进日更）：上交所的 rqylje 恒为 null，
        // 沪市那 1675 只票的融券余额**只能**靠这一项按「融券余量 × 当日收盘价」算出来——
        // 不跑就是天天留一列 0/NULL，而且是静默的（资金面诊断的「融券余额变化」对沪市算不出来，
        // 界面还显示成「融券余额 0」）。它原来待在【按需启动】组且不启用，等于每天都得人记着点一下。
        // 排在这个位置两个前置都满足：不复权日K 在前面的 K线族里、融资余额就是上一行——
        // 它要拿当天的余量乘当天的不复权收盘价，两样都落库了才算得出来。
        // 纯本地 UPDATE、一个请求都不发；日常增量只补缺值，没新数据时空转。
        FetchActionId.StepFillShortBalance,
        FetchActionId.StepLhb,
        // 【大宗交易】紧跟龙虎榜（2026-09-17 拆出来时排的）：两者是互补的两块筹码信息——
        // 龙虎榜是场内异动席位，大宗是场外大额易手。日常增量只抓最近 30 天、几十个请求。
        FetchActionId.FetchBlockTrade,
        // 【市场事件】机构调研/限售解禁/股东增减持，都是按日出的筹码面信息。
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

        // 【行业景气指标】（2026-09-07）排在补漏之后、龙虎榜席位之前。
        //
        // 为什么不排前面：它是**传统行业分析**的输入，不是"当天必须拿到"的核心数据——
        // 猪粮比、螺纹钢库存这些晚几小时落库没有任何影响，而前面那些（K线、资金、公告）
        // 晚了就影响当晚的选股。
        //
        // 为什么不排最末：最末那个位置是留给"首轮要跑几小时"的龙虎榜席位的。
        // 这一项首轮也就 120 个请求、4 分钟，排它后面等于白等几小时。
        FetchActionId.StepIndustryIndicator,

        // 【观察指标映射】**必须排在【行业景气指标】之后**（2026-09-11）：它要拿那一项写的
        // IndustryIndicator 指标字典去校验规则里的指标码。字典是空的时候这一项会整项跳过
        // （保留上一轮的映射，不会清空），但那样等于白跑一天。
        FetchActionId.StepPlanWatch,

        FetchActionId.StepWatchIndicator,

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
    /// ⚠ 顺序上有三条依赖，别按"快的排前面"随手挪：
    ///   · 【金融监管指标】要在【财务报表】之后——机构类型（银行/券商/保险）是靠财务特征科目
    ///     认出来的，没有财务数据它一家都识别不出；
    ///   · 【导入手工数据】要在【金融监管指标】之后——它填的正是监管指标里没解析出来的那些格子；
    ///   · 【补全退市名单】要在【拉取股东数据】和【拉取分红送配】之前（2026-09-18）——
    ///     那两项的名单都含退市股，而退市名单是它写进 StockMeta 的。
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
        // 【股东数据】【分红送配】的名单都含退市股（分红 09-06 纳入、股东 09-18 纳入），
        // 而退市名单是【补全退市名单】往 StockMeta 写的。那一项 2026-09-19 从这一组挪进了日更——
        // **这个依赖因此变强而不是变弱**：原来同组顺序只保证"这一轮里它先跑"，而这一组几天才转
        // 一轮；现在每个工作日都刷新一遍，这两项无论什么时候跑，用的名单至多隔一天。
        FetchActionId.FetchShareholder,
        FetchActionId.FetchDividend,
        // 【基金除权除息】紧跟个股分红（2026-09-17）：同一族数据（除权除息事件），
        // 只是一个抓个股、一个读东财终端的本地文件。它给【ETF日K·不复权】当前置——
        // 那一项在日更组，靠它区分"这只 ETF 除过权（必须抓）"还是"从没除过（可以复制）"。
        FetchActionId.ImportFundExDividend,
        FetchActionId.FetchFinancials,        // 监管指标要靠它认机构类型，所以排在前面
        // 【公司档案】+【客户与供应商】紧跟财务报表（2026-09-07/09-08）：
        // 它们是同一份年报里的东西，一起更新才不会出现"财务是新的、客户集中度还是去年的"错配。
        // ⚠ 这三项**必须挨着且按这个顺序**：客户与供应商跑对手方还原时，要用前两项的产出——
        //   档案给全称（前两档），子公司名单给归并关系（第三档）。任何一项排在后面，
        //   同一轮里还原用的就永远是上一轮的旧数据。
        FetchActionId.StepCompanyProfile,
        FetchActionId.StepSubsidiaryExtract,
        FetchActionId.StepCustomerSupplier,
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

    /// <summary>
    /// 板块成分股当前**真正生效**的取数通道（<c>fetcher-settings.json</c> 的
    /// <c>BoardMemberChannel</c>，规范化成小写）。默认跟那份配置的默认值一致。
    ///
    /// ════ 为什么目录要知道这个 ════
    /// 板块那两项占不占数据源，是**运行期**才定的：选 terminal 就只读东财终端落在本地的
    /// 那份文件，一个请求都不发；选 page/browser/http 才真去打东财。目录里那两个
    /// <c>Sources</c> 是按"走网络"写死的，于是配成 terminal 之后它们照样被记成占着
    /// EmQuote / EmPush2，被别的东财任务挡住、或者在计划里让路，界面报"数据源被占用"
    /// ——占的是它根本不会去打的源（2026-09-06 用户反馈）。
    ///
    /// ════ 为什么是这么窄的一个状态、而不是通用的"源覆盖"钩子 ════
    /// 全项目只有板块这一处存在"配置决定走不走网络"。开一个
    /// <c>Func&lt;FetchActionId, ...&gt;</c> 的钩子谁都能往里塞，而这个属性把判断连同理由
    /// 留在目录里（<see cref="IsLocalOnlyNow"/> 就在下面），改的人一眼看得到。
    ///
    /// ⚠ 写它的人**必须写"真正生效的值"，不是配置文件里的值**：换通道要重造 fetcher，
    /// 而重造可能因为有任务正占着 push2 而跳过（见 MainViewModel.ReloadConfig）。
    /// 那种时候写进来的话，界面说"不占源"、实际跑的还是老的 push2 通道。
    /// </summary>
    public static string BoardChannel { get; set; } = "page";

    /// <summary>
    /// 这一项在**当前配置下**是不是纯本地、不占任何数据源。
    ///
    /// 只有板块那两项会因配置而变，判据是 <see cref="BoardChannel"/> == terminal：
    ///   · 【板块成分股】——EastMoneyTerminalBoardFetcher 一次读盘拿全量，取不到就抛，
    ///     没有任何网络回退，实打实的本地项。
    ///   · 【概念和行业板块】——同一份本地文件里也有名单，优先读它（见
    ///     FetchOrchestrator.FetchBoardListCoreAsync）；本地文件不可用时才退回菜单 JSON
    ///     （1 个请求）、再不行退回 push2 分页（约 10 个）。
    ///     **这种退回不登记源占用**，是有意为之：要两层同时失效才会走到那儿，而 EmQuote
    ///     全项目只有这一项在用、冲突面为零；为这么小的概率把它常年挡在门外不值当。
    ///     真退回去的时候日志里会明说（见那段的"未登记数据源占用"）。
    /// </summary>
    public static bool IsLocalOnlyNow(FetchActionId id)
        => (id is FetchActionId.StepBoardMembers or FetchActionId.StepBoardList)
           && string.Equals(BoardChannel, "terminal", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<FetchActionId, FetchActionInfo> ById =
        All.ToDictionary(a => a.Id);

    public static FetchActionInfo Info(FetchActionId id) => ById[id];

    /// <summary>枚举名认不出来时返回 null——手改过 json、或读到旧版本写的名字时不能崩。</summary>
    public static FetchActionInfo? TryInfo(string? rawId) =>
        Enum.TryParse<FetchActionId>(rawId, out var id) && ById.TryGetValue(id, out var info) ? info : null;
}
