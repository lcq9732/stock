namespace StockPlatform.Logic.Models;

/// <summary>
/// Small piece of cross-run state for the Fetcher UI, persisted next to the local database
/// (see doc/data-platform-design.md §3.9 for the 2026-07-09 removal of the earlier master/daily
/// output-file bookkeeping this class used to also carry — that scheme was a holdover from an
/// abandoned multi-machine-via-netdisk sharing design and never actually served a purpose here).
/// </summary>
public class Manifest
{
    /// <summary>When the last "拉取全部"/"拉取当天" run actually completed (wall-clock, not a
    /// trading day) — paired with the Bar table's own earliest/latest period_start so the Fetcher
    /// UI can show both "数据覆盖到哪天" and "上次真的抓取是什么时候"，让用户判断该不该再抓一次，
    /// 不用凭感觉重复点。Updated every time a run reaches completion, even if it found nothing new
    /// (a clean "刚检查过，没有新数据" is still worth recording as a last-checked time).</summary>
    public DateTime? LastFetchAt { get; set; }
    public string? LastFetchKind { get; set; } // "拉取全部" / "拉取当天" / "重新拉取失败股票"

    /// <summary>K线抓取失败、还没成功补上的股票代码（2026-07-08新增）——每次"拉取全部"/"拉取
    /// 当天"/"重新拉取失败股票"结束后都会更新：本轮尝试过且这次成功了的代码会被移出，这次还是
    /// 失败的会被加入/保留。用户可以在Fetcher界面点"重新拉取失败股票"只针对这份名单重试，不用
    /// 重新跑一遍全市场；只要这份名单不为空，就可以一直重复点这个按钮。</summary>
    public List<string> FailedCodes { get; set; } = new();

    /// <summary>流通市值抓取失败、还没成功补上的股票代码（2026-07-09新增）。跟FailedCodes不完全
    /// 一样——流通市值现在是"新浪股票列表整体扫一遍"（SinaListMarketCapFetcher），不是逐只单独
    /// 查，所以"失败"的粒度是"这一整轮扫描失败了"：扫描失败时把这一轮请求的全部代码都记进来，
    /// 扫描成功时把这一轮请求的代码全部移出（某只股票本来就没有市值数据不算失败，不会被记录）。
    /// "重新拉取失败股票"点击时会用这份名单重新扫一遍——注意扫描本身还是全市场性质的，重试并不会
    /// 比正常跑一次更快，但至少能正确清零失败名单、把之前没写进去的值补上。</summary>
    public List<string> FailedMarketCapCodes { get; set; } = new();

    /// <summary>资金净流入抓取失败、还没成功补上的股票代码（2026-07-09新增）——这个是逐只股票
    /// 单独查的，跟FailedCodes（K线）同样的按股票精确追踪逻辑，"重新拉取失败股票"点击时只会
    /// 针对名单里这些代码重新查，不用等一轮全市场扫描。</summary>
    public List<string> FailedNetInflowCodes { get; set; } = new();

    /// <summary>指数成分权重（中证指数官网 closeweight.xls）抓取失败、还没补上的指数代码
    /// （2026-07-16新增）——中证源不稳、失败率较高，逐指数精确记录，"重新拉取失败股票"会一并重试。
    /// 注意非中证系指数本来就没有权重文件，不算失败、不会被记进来（见 CsindexWeightProvider）。</summary>
    public List<string> FailedIndexWeightCodes { get; set; } = new();

    /// <summary>指数成分名单（新浪指数成分接口）抓取失败、还没补上的指数代码（2026-07-16新增）——
    /// 逐指数精确记录，跟权重共用"重新拉取失败股票"重试。新浪对某些老指数本来就无成分数据，返回空
    /// 不算失败；只有请求本身失败（网络/限流）才记进来。</summary>
    public List<string> FailedIndexConsCodes { get; set; } = new();

    /// <summary>股东数据（新浪股本股东页：户数+十大股东+十大流通股东）抓取失败、还没补上的股票代码
    /// （2026-07-16新增）——逐只精确记录，共用"重新拉取失败股票"重试。全市场逐只抓、量大易有零星失败。</summary>
    public List<string> FailedShareholderCodes { get; set; } = new();

    /// <summary>分红送配（新浪分红派息页 vISSUE_ShareBonus）抓取失败、还没补上的股票代码
    /// （2026-07-31新增）——逐只精确记录，共用"重新拉取失败股票"重试。全市场逐只抓、量大易有零星失败。</summary>
    public List<string> FailedDividendCodes { get; set; } = new();

    /// <summary>
    /// 一轮抓取跑完之后，**本该有最新交易日的日线、库里却还是没有**的股票代码（2026-08-21新增），
    /// 以及那个交易日是哪天（<see cref="MissingDayDate"/>）。
    ///
    /// 为什么它跟上面几份"失败名单"是两回事：这些股票的请求**根本没失败**。数据源盘后是逐步更新
    /// 的，请求发过去时那只股票的当天K线还没出来，接口正常返回、只是里面没有那一天——代码把这种
    /// 情况算作"请求成功但无新数据"（FetchStats.FetchedButEmpty），既不报错也不记名单、更不会重试。
    /// 2026-08-20 那轮"拉取全部"19:00 开跑，个股前复权只拿到 1773/5539 只，而 21:00 之后才抓的
    /// 后复权和 ETF 一个不缺——全是这个原因，而当时用户在界面上完全看不出漏了数据。
    ///
    /// 所以改成：一轮跑完后直接查库体检（见 FetchOrchestrator.CheckLatestDayCoverage），把"上一个
    /// 交易日有、最新交易日没有"的股票记在这里，并入"重新拉取失败股票"按钮一起重试。名单每次体检
    /// 都是**重建**而不是累加。当天临时停牌的股票会误入一次（数据源确实没有它那天的数据），重试
    /// 两轮拿不到就由自动重试的收敛判断停下来，不会无限重试。
    /// </summary>
    public List<string> MissingDayCodes { get; set; } = new();

    /// <summary>上面那份名单缺的是哪个交易日（体检时以本地上证指数日线的最新一根为锚）。</summary>
    public DateTime? MissingDayDate { get; set; }

    /// <summary>
    /// **全库体检**查出来的日线空洞（2026-09-02 新增，见 FetchOrchestrator.RunStepFullAuditAsync）。
    ///
    /// 跟上面那份 <see cref="MissingDayCodes"/> 的分工：那个只管**最新一个交易日**、每天日更末尾自动查；
    /// 这个是手动触发的全库体检，查的是每只票在自己存续期内的所有空洞。
    ///
    /// 存的是**区间**不是一堆散日期：抓一只票的一段跟抓一天成本几乎一样（数据源一页固定返回 640 根K线），
    /// 所以补的时候一次请求就能把中间的洞全填上。
    ///
    /// 2026-09-04 起同一只票可能有**多条**记录——每条对应一个口径（前复权/后复权/不复权，
    /// 见 <see cref="MissingBarRange.Granularity"/>），另外名单里还会混进 ETF 和指数的代码。
    /// 补的时候按口径分组、把口径传给数据源；板块指数和 day_adj 不会进这份名单，它们靠本地重算。
    ///
    /// <see cref="MissingBarRange.Tries"/> 是补过几轮——停牌那种"补也补不到"的，
    /// 连补两轮拿不到就移出这份名单、写进库里的 MissingBarConfirmed 白名单，往后体检跳过它，
    /// 否则每次体检都报一遍、每次都白抓一遍，永远收敛不了。
    /// </summary>
    public List<MissingBarRange> MissingBars { get; set; } = new();

    /// <summary>
    /// **等着重取前复权全历史**的股票（2026-08-31 新增）。
    ///
    /// 数据源的前复权是"原价 − 之后累计分红送配"，基准随抓取时点变化：某只股票一分红，
    /// 它**全部历史**的前复权值就都变了。日常抓取时顺带比对最近 400 天（那几百根本来就在
    /// 返回里，不多花请求），对不上就说明这只票除权了——手上那一页就地覆盖，更早的历史
    /// 记进这份名单，等着重取。
    ///
    /// 为什么是名单而不是当场重抓（这是 2026-08-31 的改动）：分红季一天上百只，每只都要
    /// 从本地最早一根重抓十年，混在日常那两小时里既拖慢当轮、又只能看到一个数字、还得
    /// 限量 200 只/轮。改成记名单之后，由计划里的【重取前复权】任务在**空闲时**慢慢补
    /// （跟"拉取财务报表"一样的路子），界面上能看见还剩多少只。
    ///
    /// 名单是**累加**的（去重），重取成功一只就移走一只；没取完不要紧，日常比对还会把它
    /// 重新检出来——这个机制本身是自愈的。
    /// </summary>
    public List<string> PendingQfqRepairCodes { get; set; } = new();

    /// <summary>
    /// **确认没有权重文件**的指数（2026-09-02 新增）：指数代码 → 上次确认 404 的时间。
    ///
    /// 内置指数全集有 732 个，而 closeweight.xls **只有中证系才有**，其余一律 404。
    /// 每次跑都把这四五百个 404 重敲一遍，是最容易把中证那边反爬撞醒的原因——
    /// 而它们一条数据都拿不到。记下来之后，一个月内不再问；一个月后重试一次
    /// （中证偶尔会给新指数补上文件，所以不能永久拉黑）。
    ///
    /// 跟【全库体检】那份"确认数据源没有"的白名单是同一个思路：**抓过、确认拿不到，才记**。
    /// </summary>
    public Dictionary<string, DateTime> IndexWeightMissing { get; set; } = new();

    /// <summary>
    /// **每个任务**上一次跑完的时间和结果（2026-09-02 新增，随【拉取全部】拆成 13 个原子项）。
    ///
    /// 为什么需要它：<see cref="LastFetchKind"/> 记的是"最后一次抓取是哪一种"，在只有
    /// 拉取全部/当天/重试三个入口时够用；拆细之后一天会有十几项各跑各的，那个字段就变成
    /// "今天最后收尾的那一项"，看不出别的项跑没跑、什么时候跑的。
    ///
    /// key 用任务的中文名（就是 FetchOrchestrator.FinishFetchRun 的 fetchKind 参数，
    /// 也是目录里的 Name）——不用枚举名，是因为编排层不认识计划层的 FetchActionId。
    /// 改名会丢历史记录，但这只是"给人看的最近运行时间"，丢了下次跑完就重新有了。
    /// </summary>
    public Dictionary<string, TaskRunRecord> LastRunByTask { get; set; } = new();

    /// <summary>
    /// **资金净流入整天缺失**的那些交易日（2026-09-06 新增，由全库体检写入）。
    ///
    /// 为什么只有这一张日频表进待补名单、别的表都只报不补：新浪那个源的响应是**整只票的全部
    /// 历史**（窗口是客户端裁的，见 SinaNetInflowFetcher 的类注释），所以"补 1 天"和"补 5 天"
    /// 的请求数完全一样——一轮全市场就能把所有缺日一起补掉。而用现成的【补指定历史日】是
    /// 一天一轮、每轮 1 小时 45 分，补 5 天要 9 小时，纯属浪费。
    ///
    /// <see cref="MissingDayRetry.Tries"/> 的收敛跟 K 线那套一致：补两轮还拿不到就移进
    /// <see cref="ConfirmedNetInflowDays"/>，往后体检不再报，否则每次体检都报一遍、每次都白抓一轮。
    /// </summary>
    public List<MissingDayRetry> MissingNetInflowDays { get; set; } = new();

    /// <summary>
    /// 补满两轮仍然拿不到、判定"数据源确实没有"的资金净流入交易日（2026-09-06 新增）。
    /// 体检时跳过这些天。「彻底体检」会连这份名单一起清空、全部重查——跟库里那张
    /// MissingBarConfirmed 白名单是一个道理，只是量小（十年才几天），没必要单开一张表。
    /// </summary>
    public List<DateTime> ConfirmedNetInflowDays { get; set; } = new();

    /// <summary>
    /// 补满两轮仍然补不齐、判定"数据源那天就是只有这些"的**残缺日**（2026-09-16 新增）。
    /// 键是任务 id（<see cref="RetryTaskIds"/> 的取值），值是那一项已定案的日子。
    ///
    /// 为什么按任务分开存、不复用 <see cref="ConfirmedNetInflowDays"/>：那张是资金净流入专用的，
    /// 两融的残缺日塞进去会互相污染——一边定案了另一边就不查了。
    /// 「彻底体检」会连这份一起清空重查，跟上面那张一致。
    /// </summary>
    public Dictionary<string, List<DateTime>> ConfirmedPartialDays { get; set; } = new();

    /// <summary>
    /// 统一的待办清单（2026-09-13）——**这是"还有什么没补上"的唯一权威**，
    /// 上面那九个名单是它的历史前身，只在 <see cref="MigrateLegacyTodos"/> 里读一次就清空。
    ///
    /// 换成它的原因见 <see cref="RetryTodo"/> 的类注释：九个各自为政的名单让"写的地方"和
    /// "用的地方"没法协调，体检写进来的两类待办漏报了两周，而且重试拿到代码也不知道归谁补。
    /// </summary>
    public List<RetryTodo> Todos { get; set; } = new();

    /// <summary>
    /// 把九个老名单并进 <see cref="Todos"/> 然后清空它们——<c>JsonManifestStore.Load</c> 每次读完就调，
    /// 所以进到内存里的 manifest 永远是新格式，全程序不用再有第二种读法。
    ///
    /// ⚠ **这是"manifest 上哪些东西算待办"的唯一登记表**。以前这份知识在代码里抄了四遍
    /// （摘要文字一遍、按钮可点判定一遍、重试入口的 early-return 一遍、执行链一遍），
    /// 加新待办的人不知道要去抄第五遍——2026-09-02 和 09-06 加的两类就是这么漏的。
    /// 现在新增一类待办只要在这里登记，显示、按钮、分派全都自动跟上。
    ///
    /// 幂等：老名单清空之后再调不会重复并入；已经在 Todos 里的同 (TaskId, Kind) 不覆盖
    /// （新格式写进来的是权威，老字段只是还没清干净的残留）。
    /// </summary>
    public void MigrateLegacyTodos()
    {
        void Take(string taskId, string kind, IEnumerable<RetryTarget> targets, DateTime? day = null)
        {
            var list = targets.ToList();
            if (list.Count == 0) return;
            if (Todos.Any(t => t.TaskId == taskId && t.Kind == kind)) return;
            Todos.Add(new RetryTodo { TaskId = taskId, Kind = kind, Day = day, Targets = list });
        }
        static IEnumerable<RetryTarget> Codes(IEnumerable<string> codes)
            => codes.Select(c => new RetryTarget { Code = c });

        Take(RetryTaskIds.StockDayBars, RetryTodoKind.Failed, Codes(FailedCodes));
        Take(RetryTaskIds.StockDayBars, RetryTodoKind.MissingDay, Codes(MissingDayCodes), MissingDayDate);
        Take(RetryTaskIds.Roster, RetryTodoKind.Round, Codes(FailedMarketCapCodes));
        Take(RetryTaskIds.NetInflow, RetryTodoKind.Failed, Codes(FailedNetInflowCodes));
        Take(RetryTaskIds.IndexCons, RetryTodoKind.Failed, Codes(FailedIndexConsCodes));
        Take(RetryTaskIds.IndexWeight, RetryTodoKind.Failed, Codes(FailedIndexWeightCodes));
        Take(RetryTaskIds.Shareholder, RetryTodoKind.Failed, Codes(FailedShareholderCodes));
        Take(RetryTaskIds.Dividend, RetryTodoKind.Failed, Codes(FailedDividendCodes));
        Take(RetryTaskIds.NetInflow, RetryTodoKind.MissingDays,
            MissingNetInflowDays.Select(d => new RetryTarget { Day = d.Day, Tries = d.Tries }));

        // 历史空洞/值问题：按口径分给三个个股日K任务——它们是三个独立任务，各补各的。
        // 缺行和值问题分成两条 Kind：复查方式不一样（缺行看"行在不在"，值错要按 Reason 重查判据），
        // 混成一条的话值问题会被 FindGaps 一律判成"已补齐"划掉，而且划得很安静。
        foreach (var g in MissingBars.GroupBy(r => (
                     Task: RetryTaskIds.ForGranularity(r.Granularity),
                     Kind: r.IsValueIssue ? RetryTodoKind.ValueIssue : RetryTodoKind.Gap)))
        {
            Take(g.Key.Task, g.Key.Kind, g.Select(r => new RetryTarget
            {
                Code = r.Code,
                Gran = string.IsNullOrEmpty(r.Granularity) ? Granularity.Day : r.Granularity,
                From = r.From, To = r.To, Days = r.Days, Tries = r.Tries,
                Reason = r.IsValueIssue ? r.EffectiveReason : null,
            }));
        }

        FailedCodes.Clear();
        MissingDayCodes.Clear();
        MissingDayDate = null;
        FailedMarketCapCodes.Clear();
        FailedNetInflowCodes.Clear();
        FailedIndexConsCodes.Clear();
        FailedIndexWeightCodes.Clear();
        FailedShareholderCodes.Clear();
        FailedDividendCodes.Clear();
        MissingNetInflowDays.Clear();
        MissingBars.Clear();
    }

    /// <summary>取某一条待办（没有就是 null）。(TaskId, Kind) 是主键。</summary>
    public RetryTodo? Todo(string taskId, string kind)
        => Todos.FirstOrDefault(t => t.TaskId == taskId && t.Kind == kind);

    /// <summary>写回某一条待办：空名单直接移除（留个空壳只会让"还有没有活"的判断变复杂）。</summary>
    public void SetTodo(string taskId, string kind, IReadOnlyList<RetryTarget> targets, DateTime? day = null)
    {
        Todos.RemoveAll(t => t.TaskId == taskId && t.Kind == kind);
        if (targets.Count > 0)
            Todos.Add(new RetryTodo { TaskId = taskId, Kind = kind, Day = day, Targets = targets.ToList() });
    }
}

/// <summary>
/// 某个整天缺失、等着重抓的交易日（见 <see cref="Manifest.MissingNetInflowDays"/>）。
/// </summary>
public class MissingDayRetry
{
    public DateTime Day { get; set; }

    /// <summary>补过几轮。补两轮还拿不到就判定"数据源确实没有"。</summary>
    public int Tries { get; set; }
}

/// <summary>
/// 一只标的缺日线的那一段（见 <see cref="Manifest.MissingBars"/>）。
/// 空洞不连续时取**包络区间**：数据源一页返回 640 根，一次请求覆盖整段比逐日请求划算得多。
/// </summary>
public class MissingBarRange
{
    public string Code { get; set; } = "";

    /// <summary>
    /// 缺的是**哪一套日线**（2026-09-04 新增）：<see cref="Models.Granularity.Day"/> 前复权 /
    /// <see cref="Models.Granularity.DayHfq"/> 后复权 / <see cref="Models.Granularity.DayRaw"/> 不复权。
    ///
    /// 同一只票的不同口径是**各自独立的一条记录**——它们的水位线、历史起点、能不能抓（后复权和
    /// 不复权只有腾讯给）全都不一样，合成一条就没法分别计 <see cref="Tries"/>、也没法分别补。
    ///
    /// 老 manifest 里没有这个字段，反序列化出来是 null/空串，一律按前复权处理
    /// （2026-09-04 之前体检只查前复权，那些记录本来就都是前复权的）。
    /// </summary>
    public string Granularity { get; set; } = StockPlatform.Logic.Models.Granularity.Day;
    /// <summary>最早缺的那个交易日。</summary>
    public DateTime From { get; set; }
    /// <summary>最晚缺的那个交易日。</summary>
    public DateTime To { get; set; }
    /// <summary>缺了几个交易日（给人看的，判断这只票是停牌还是真漏抓）。</summary>
    public int Days { get; set; }
    /// <summary>补过几轮。补两轮还拿不到就判定"数据源确实没有"，移进白名单表。</summary>
    public int Tries { get; set; }

    /// <summary>
    /// 这一段**为什么**要重抓（2026-09-09 新增，取值见 <see cref="AuditFindingKind"/>）。
    /// 原来只有一种情形——缺行；现在"行在但值错"也走同一条管道，于是必须记下原因。
    ///
    /// ⚠ **复查时一定要按它分派回对应判据**：值错的行**一直都在**，拿"行在不在"（<c>FindGaps</c>）
    /// 去复查会一律判成"已补齐"划掉，哪怕值根本没被覆盖（比如又在盘中跑了一次）。
    /// 见 doc/bar-value-audit-design.md §5。
    ///
    /// 老 manifest 没有这个字段，反序列化出来是 null/空串，一律按 <see cref="AuditFindingKind.Gap"/>
    /// 处理——那些记录本来就都是缺行。
    /// </summary>
    public string Reason { get; set; } = AuditFindingKind.Gap;

    /// <summary>空/null 一律当"缺行"（老记录兼容）。</summary>
    public string EffectiveReason =>
        string.IsNullOrEmpty(Reason) ? AuditFindingKind.Gap : Reason;

    /// <summary>是不是"行在但值错"那一类（跟缺行相对）——复查方式和白名单策略都不同。</summary>
    public bool IsValueIssue => EffectiveReason != AuditFindingKind.Gap;
}

/// <summary>某个任务最后一次跑完的记录（见 <see cref="Manifest.LastRunByTask"/>）。</summary>
public class TaskRunRecord
{
    public DateTime At { get; set; }

    /// <summary>那一轮记了多少条错误（0 = 干净跑完）。</summary>
    public int ErrorCount { get; set; }
}
