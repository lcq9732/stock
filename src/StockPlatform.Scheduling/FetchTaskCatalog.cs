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
    /// 这个动作能不能"只跑一部分"——给「空闲时」那类触发用（见 RepeatKind.WhenIdle）。
    ///
    /// true 的动作接受一个"本轮最多做多少"的上限，所以哪怕离下一个定时任务只剩半小时，
    /// 也能塞进去补一小批、到点前干净收尾。目前只有财务报表是这样（它本来就按"每轮 300 只"
    /// 分批跑）。false 的动作只能整轮跑，空闲窗口装不下它的预计耗时时就不启动，
    /// 免得跑到一半被定时任务打断。
    /// </summary>
    bool SupportsPartialRun = false);

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
        new(FetchActionId.FetchAll, "拉取全部", "腾讯K线 + 新浪 + 交易所 + 巨潮", QuotaGroup.Mixed,
            TimeSpan.FromHours(2), "每工作日",
            "日常收盘后的主任务：个股/指数/ETF日K、流通市值、资金净流入、中标公告、融资余额、龙虎榜。"
            + "每只标的从自己上次抓到那天续抓，所以会自动补齐前几天的断档。",
            FetchActionParams.GlobalFetchOptions | FetchActionParams.LookbackYears),

        new(FetchActionId.FetchDay, "补指定历史日", "同「拉取全部」", QuotaGroup.Mixed,
            TimeSpan.FromHours(2), "按需",
            "只抓指定那一天，不看每只股票上次抓到哪天、不补断档，耗时却和「拉取全部」一样。"
            + "日常请用「拉取全部」，这项只在要补某个过去的具体日期时才排。",
            FetchActionParams.Date),

        new(FetchActionId.RetryFailed, "重新拉取失败", "跟随各自的源", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(10), "抓取之后",
            "重试失败名单（K线/市值/资金流/指数成分/权重/股东/分红各自独立记录）。"
            + "名单为空时秒过。注意：后台还有一套自动重试（跑完 1 小时后、且不早于当天 21:00，"
            + "最多 3 轮），它跟计划不冲突，这一项是给你想在固定时刻强制重试一次时用的。",
            FetchActionParams.GlobalFetchOptions),

        new(FetchActionId.RepairQfq, "重取前复权", "跟随K线的源", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(30), "空闲时",
            "把**除权后基准变了**的股票，前复权历史整段按新基准重取。\n"
            + "数据源的前复权是「原价 − 之后累计分红送配」，某只票一分红，它全部历史的前复权值就都变了；"
            + "而本地历史是分批入库的，不重取就会在接缝处出现假跳空（实测有股票虚增 50%）。\n"
            + "日常抓取会自动比对最近 400 天、把除权了的票记进待重取名单（参数格里显示还剩多少只）。"
            + "**建议重复规则设成「空闲时」**：分红季一天可能上百只，每只要重抓十年，让它在空档里慢慢补。\n"
            + "取过的不会重取；没取完不要紧，下一轮日常比对还会把它检出来。后复权不受除权影响，不用重取。",
            SupportsPartialRun: true),

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
            SupportsPartialRun: false),

        new(FetchActionId.FetchRawBars, "补不复权历史", "跟随K线的源", QuotaGroup.Mixed,
            TimeSpan.FromHours(2), "一次性（补完就不用再跑了）",
            "抓**不复权**日线（原始成交价）。\n"
            + "它是全库唯一**不随分红变化**的价格序列——抓一次永远有效，不会像前复权那样一除权就得整段重取。\n"
            + "用途：算回测专用的复权序列（下面那一项），以及回答「某天实际成交价是多少」。\n"
            + "**这是个一次性任务**：日常那一根增量已经并进【拉取全部】和【拉取当天】了，跟后复权一样。\n"
            + "这里只负责首次回补——把每只个股补到跟前复权一样长，实测约 24700 个请求、2 小时出头（腾讯源约 3 请求/秒，不像新浪报表接口那样卡配额）。\n"
            + "右边的待办量正常应该是 0；哪天不是 0（比如刚拉过区间历史、或新增了退市股），点一次执行补上即可。",
            SupportsPartialRun: true),

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
            DependsOn: FetchActionId.FetchRawBars, SupportsPartialRun: true),

        new(FetchActionId.FetchBoards, "拉取板块", "新浪", QuotaGroup.Sina,
            TimeSpan.FromMinutes(5), "每工作日～每周",
            "概念/题材板块 + 行业板块的行情与成分股，拉完自动用本地K线重算板块指数（不联网）。"),

        new(FetchActionId.FetchIndustry, "拉取行业分类", "交易所 + 新浪", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(2), "季度",
            "证监会两级行业分类。行业极少变动，跟财报同频跑一次即可，整体覆盖写入、反复跑无副作用。"),

        new(FetchActionId.FetchIndexCons, "指数成分/权重", "新浪 + 中证", QuotaGroup.Mixed,
            TimeSpan.FromMinutes(15), "季度",
            "指数成分名单（新浪）和成分权重（中证 OSS）。中证那侧偏不稳，失败进重试名单。"),

        new(FetchActionId.FetchShareholder, "拉取股东数据", "新浪", QuotaGroup.Sina,
            TimeSpan.FromHours(1.5), "季度",
            "逐只抓股东户数 + 十大股东 + 十大流通股东的全部历史。"
            + "十大流通股东里的「香港中央结算」就是北向资金的名义持有人。"),

        new(FetchActionId.FetchFinancials, "拉取财务报表", "新浪(报表接口·配额最严)", QuotaGroup.Sina,
            TimeSpan.FromMinutes(90), "季度·跨天分轮",
            "三张表 52 个科目的全部报告期。⚠ 这个接口配额很严，已单独降速到约 10 请求/分钟、"
            + "每轮上限 300 只（约 90 分钟），全市场要跨几天补完。"
            + "**建议把重复规则设成「空闲时」**：程序空着就自己补一批，到点前自动收尾给定时任务让路，"
            + "跨几天慢慢啃完。设成「每月某天」只会跑一轮 300 只，全市场根本补不完。",
            SupportsPartialRun: true),

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
            + "⚠ 前置：财务报表——机构类型是靠特征科目认出来的。",
            DependsOn: FetchActionId.FetchFinancials),

        new(FetchActionId.ImportManual, "导入手工数据", "本地文件", QuotaGroup.Local,
            TimeSpan.FromSeconds(5), "人填完 CSV 后",
            "把填好的「待手工回填清单.csv」写回库。文件没填就是空跑，无副作用。",
            DependsOn: FetchActionId.BankRegulatory),

        new(FetchActionId.BackfillDaily, "一键补齐每日历史", "交易所", QuotaGroup.Exchange,
            TimeSpan.FromMinutes(30), "按需补洞",
            "把融资余额、龙虎榜的历史从K线最早那天补到今天，跳过本地已有的交易日。幂等、可反复跑。"),

        new(FetchActionId.FetchYear, "拉取区间数据", "腾讯 + 新浪 + 交易所", QuotaGroup.Mixed,
            TimeSpan.FromHours(1.7), "一次性回补",
            "按年份区间往回补历史（K线/退市股/资金流/融资/龙虎/公告）。只补本地还缺的部分。",
            FetchActionParams.YearRange | FetchActionParams.GlobalFetchOptions),

        new(FetchActionId.OptimizeDatabase, "优化数据库", "本地", QuotaGroup.Local,
            TimeSpan.FromMinutes(5), "一次性",
            "给几张大表补建二级索引。不联网。建完就是持久对象，之后不用再跑。"),
    ];

    private static readonly Dictionary<FetchActionId, FetchActionInfo> ById =
        All.ToDictionary(a => a.Id);

    public static FetchActionInfo Info(FetchActionId id) => ById[id];

    /// <summary>枚举名认不出来时返回 null——手改过 json、或读到旧版本写的名字时不能崩。</summary>
    public static FetchActionInfo? TryInfo(string? rawId) =>
        Enum.TryParse<FetchActionId>(rawId, out var id) && ById.TryGetValue(id, out var info) ? info : null;
}
