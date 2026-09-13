namespace StockPlatform.Logic.Models;

/// <summary>
/// 待办的归属任务 id（<c>FetchActionId</c> 的枚举名）。
///
/// 为什么是字符串常量而不是那个枚举：Logic 层不引用 Scheduling 层，而且这是要落进
/// manifest.json 的存储格式——跟 fetch-plan.json 存动作名同一个理由，枚举值可以重排、
/// 名字不能改。拼写有测试兜着（RetryBacklogTests 会核对每个都解析得成 FetchActionId）。
/// </summary>
public static class RetryTaskIds
{
    public const string StockDayBars = "StepStockDayBars";
    public const string StockHfqBars = "StepStockHfqBars";
    public const string StockRawBars = "StepStockRawBars";
    public const string EtfBars = "StepEtfBars";
    public const string IndexBars = "StepIndexBars";
    public const string DelistedTails = "StepDelistedTails";
    public const string Roster = "StepRoster";
    public const string NetInflow = "StepNetInflow";
    public const string IndexCons = "StepIndexCons";
    public const string IndexWeight = "StepIndexWeight";
    public const string Shareholder = "FetchShareholder";
    public const string Dividend = "FetchDividend";

    /// <summary>口径 → 该补它的个股日K任务。三个口径是三个独立任务（2026-09-02 拆的），
    /// 各自的水位线、历史起点、能不能抓都不一样。</summary>
    public static string ForGranularity(string? gran) => gran switch
    {
        Granularity.DayHfq => StockHfqBars,
        Granularity.DayRaw => StockRawBars,
        _ => StockDayBars,     // 空/null 也走这里：2026-09-04 之前的记录本来就都是前复权
    };
}

/// <summary>待办的类别。存进 manifest 的是这个枚举的**名字**，改名字等于改存储格式。</summary>
public static class RetryTodoKind
{
    /// <summary>抓取时失败的标的（逐只重试）。</summary>
    public const string Failed = "failed";

    /// <summary>整轮扫描失败（流通市值那种一次请求拿全市场的）——量词是"轮"不是"只"。</summary>
    public const string Round = "round";

    /// <summary>该有最新交易日的数据、却还缺着（不是失败，是数据源当时还没更新到）。</summary>
    public const string MissingDay = "missing_day";

    /// <summary>全库体检查出的历史空洞（行不在）。</summary>
    public const string Gap = "gap";

    /// <summary>全库体检查出的值问题（行在但值错）——补法和复查都跟缺行不一样。</summary>
    public const string ValueIssue = "value_issue";

    /// <summary>整天缺失的日子（资金净流入那种按天补的）。</summary>
    public const string MissingDays = "missing_days";
}

/// <summary>
/// 一件待办：**归谁补**（<see cref="TaskId"/>）+ **补什么**（<see cref="Targets"/>）。
///
/// ════ 为什么要有它（2026-09-13）════
/// 原来 manifest 上是九个各自为政的名单（FailedCodes、MissingBars、MissingNetInflowDays…），
/// 谁写谁知道、别人不知道。于是：
///   ① 体检 2026-09-02 写进来的历史空洞，界面上的待办摘要漏抄了整整两周——
///      显示"09-11日线 1 只"，点下去实际跑 1909 段、20.8 万个交易日，几个小时；
///   ② 【重新拉取失败】拿到一堆代码却不知道它们是哪个任务失败的，只能一律按前复权重抓，
///      于是后复权/不复权任务失败的票走这条路补不回自己的口径（要绕一轮体检才补得上）。
///
/// 归属信息其实一直都在写入方手里——<c>FinishFetchRun</c> 有 <c>fetchKind</c>（任务名）、
/// <c>FullAuditTask.CommitScope</c> 有 <c>scope</c>（类型×口径），
/// 只是落盘时被扔掉了。这个类就是把它留住。
///
/// ════ 谁来补 ════
/// 【重新拉取失败】不再自己抓，它读这份清单、按 <see cref="TaskId"/> 用
/// <c>FetchMode.FillBacklog</c> 启动对应任务；任务认领属于自己的那条，
/// 自己补、自己复查、自己移除（复查方式各类不同，见 doc/retry-backlog-design.md §3.5）。
/// </summary>
public class RetryTodo
{
    /// <summary>归属任务，取值是 <c>FetchActionId</c> 的枚举名（如 "StepStockRawBars"）。
    /// 用字符串而不是枚举：Logic 层不认识 Scheduling 层的枚举，而且这是要进 json 的存储格式，
    /// 跟 fetch-plan.json 存动作名是同一个理由。</summary>
    public string TaskId { get; set; } = "";

    /// <summary>哪一类待办，取值见 <see cref="RetryTodoKind"/>。同一个任务可以有多条
    /// （比如个股日K既有"抓取失败"又有"历史空洞"），所以 (TaskId, Kind) 才是主键。</summary>
    public string Kind { get; set; } = "";

    /// <summary>这一类待办涉及的日期（目前只有 <see cref="RetryTodoKind.MissingDay"/> 用：
    /// 缺的是哪个交易日。按钮文字里带上它，"当天日线"才不至于含义模糊）。</summary>
    public DateTime? Day { get; set; }

    public List<RetryTarget> Targets { get; set; } = new();
}

/// <summary>
/// 待办里的一条：补哪只、补哪一段。
/// 各类待办用到的字段不同（失败名单只用 <see cref="Code"/>；历史空洞用区间和
/// <see cref="Tries"/>；整天缺失只用 <see cref="Day"/>），没用到的留空——
/// 为每一类单开一个类型的话，"统一格式"就不存在了，分派也就无从谈起。
/// </summary>
public class RetryTarget
{
    public string Code { get; set; } = "";

    /// <summary>缺的是哪一套日线（<see cref="Granularity"/> 的取值）。空＝按任务自己的默认口径。</summary>
    public string? Gran { get; set; }

    /// <summary>要补的区间（含）。两个都为空＝整只按任务自己的规则补（水位线或全量）。</summary>
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    /// <summary>单日类待办（整天缺失）的那一天。</summary>
    public DateTime? Day { get; set; }

    /// <summary>缺了几个交易日——给人看的（判断这只票是停牌还是真漏抓），也是界面上
    /// "1907 段/20.8万交易日"里的后半个数字：段数≈请求数、决定要跑多久，交易日数才是数据量。</summary>
    public int Days { get; set; }

    /// <summary>补过几轮。补两轮还拿不到就判定"数据源确实没有"（多半是停牌），
    /// 移进白名单、以后体检不再报——不这么收敛的话，停牌的票会年复一年地每次都被报、每次都白抓。</summary>
    public int Tries { get; set; }

    /// <summary>值问题专用：**为什么**要重抓（取值见 <c>AuditFindingKind</c>）。
    /// ⚠ 复查时一定要按它分派回对应判据：值错的行一直都在，拿"行在不在"去复查会一律判成
    /// "已补齐"划掉，哪怕值根本没被覆盖。见 doc/bar-value-audit-design.md §5。</summary>
    public string? Reason { get; set; }
}
