using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 「抓回来这一段K线，哪几根该插、哪几根该覆盖、算不算复权基准漂移」——纯判据，零 IO。
///
/// 2026-09-21 从 <c>FetchOrchestrator.ProcessOneStockAsync</c> 里抽出来，抽的理由在
/// doc/solution-class-map.md §0.1（分层职责原则）：**判据归 Logic，Sqlite 只读写，
/// 编排归任务/编排层**。落到这一段上，直接动因是K线六项要迁到新任务框架
/// （见 doc/bar-tasks-migration-design.md）——迁完之后老编排层和新任务会**各自编排**，
/// 但绝不能各判各的：这三条规则一旦分叉，症状全是静默的。
///
/// ════ 三条规则，每条都是拿事故换来的 ════
///
/// ① **「今天」那几根走覆盖写**。盘中抓到的行 OHLC 是瞬时价、量额是半天累计值；
///    走 InsertOrIgnore 的话收盘后再抓一次也进不去，那份半成品就被**永久固化**了
///    （2026-09-01 早盘那轮 4020 只票、2026-07-16 的 1330 个指数/ETF 都是这么污染的）。
///
/// ② **更早的日期只插不覆盖**（<see cref="BarWritePlan.ToInsert"/> → InsertOrRefreshUnconfirmed）。
///    过去的交易日不会再变，重复抓到的是同一个事实；直接覆盖反而会把不同批次的
///    复权基准混进同一段历史。
///
/// ③ **例外：基准漂移要覆盖**。该股分红/送转之后，数据源的前复权基准变了，
///    库里那段旧值跟新拿回来的对不上——这时只覆盖手上这一页，并把这只票记下来，
///    更早的历史交给【重取前复权】慢慢补（见 <see cref="QfqRepairPlanner"/>）。
/// </summary>
public static class BarWritePlanner
{
    /// <summary>
    /// 复权基准漂移的比对回看天数。数据源一页固定返回 640 根K线（≈2.5 年），不管请求几天——
    /// 所以把"确实要抓"的窗口向前放宽到这个天数**不增加任何网络请求**，却能拿回几百根
    /// "数据源当前基准下的正确值"用来跟库里比对。
    ///
    /// ⚠ 只在本来就要发请求时放宽，水位线的跳过判定不变；否则每只票每天都会发一次请求。
    /// </summary>
    public const int DriftCheckLookbackDays = 400;

    /// <summary>
    /// 某根K线是否发生了复权基准漂移：库里存的值与数据源当前给出的值对不上。
    /// 阈值取"相对 0.2% 与绝对 0.005 元的较大者"——足够小以捕捉几分钱的现金分红调整，
    /// 又不会被浮点噪声和低价股的分位误差误判。
    /// </summary>
    public static bool IsDrifted(double stored, double fresh) =>
        Math.Abs(stored - fresh) > Math.Max(0.005, Math.Abs(fresh) * 0.002);

    /// <summary>
    /// 排一次写入。
    /// </summary>
    /// <param name="fetched">这一次抓回来的行（可以是空的）。</param>
    /// <param name="today">哪一天算"今天"（传进来而不是读 <see cref="DateTime.Today"/>，
    /// 判据才可单测，也才不会在跨零点的长任务里中途改口）。</param>
    /// <param name="storedCloses">库里**同一口径**在这一段上已有的收盘价（日期 → 收盘价）。
    /// 只有开启漂移检测时才需要真填，否则传空字典即可——见 <paramref name="driftCheck"/>。</param>
    /// <param name="overwrite">"覆盖重抓"模式：整段以数据源当前基准为准，全部覆盖。
    /// 用来一次性抹平历史上分批入库造成的复权基准接缝。</param>
    /// <param name="driftCheck">开启漂移检测。只有前复权那一路开——后复权/不复权的基准
    /// 本来就不随分红变，比对纯属浪费。</param>
    public static BarWritePlan Plan(
        IReadOnlyList<Bar> fetched, DateTime today,
        IReadOnlyDictionary<DateTime, double> storedCloses,
        bool overwrite = false, bool driftCheck = false)
    {
        if (fetched.Count == 0) return BarWritePlan.Empty;

        var todays = new List<Bar>();
        var older = new List<Bar>();
        foreach (var b in fetched)
        {
            if (b.PeriodStart.Date == today.Date) todays.Add(b);
            else older.Add(b);
        }

        // 覆盖重抓：旧行整段覆盖，不做漂移比对（本来就是"全都以新基准为准"）。
        if (overwrite)
            return new BarWritePlan([], older, todays, Drifted: false);

        var toInsert = new List<Bar>();
        var toOverwrite = new List<Bar>();
        foreach (var b in older)
        {
            if (!storedCloses.TryGetValue(b.PeriodStart.Date, out var stored)) toInsert.Add(b);
            else if (driftCheck && IsDrifted(stored, b.Close)) toOverwrite.Add(b);
            // 库里已有、值也对得上 → 什么都不做（这是绝大多数行的归宿）
        }

        return new BarWritePlan(toInsert, toOverwrite, todays, Drifted: toOverwrite.Count > 0);
    }
}

/// <summary>
/// <see cref="BarWritePlanner.Plan"/> 的结论。三份行**互不相交**，调用方按各自的写法落库：
/// <see cref="ToInsert"/> 走 InsertOrRefreshUnconfirmed（只补库里没有的），
/// <see cref="ToOverwrite"/> 和 <see cref="Today"/> 走 Upsert（要压住已有的那一行）。
/// </summary>
/// <param name="ToInsert">库里还没有的历史行。</param>
/// <param name="ToOverwrite">基准漂移、要按新基准压掉的历史行（<c>overwrite</c> 模式下是整段旧行）。</param>
/// <param name="Today">"今天"那几根——必须覆盖写，见类注释规则 ①。</param>
/// <param name="Drifted">这只票是否发生了基准漂移（调用方据此记进待重取名单）。</param>
public sealed record BarWritePlan(
    IReadOnlyList<Bar> ToInsert,
    IReadOnlyList<Bar> ToOverwrite,
    IReadOnlyList<Bar> Today,
    bool Drifted)
{
    public static readonly BarWritePlan Empty = new([], [], [], false);

    /// <summary>这一次到底要不要动库（三份都空就别开事务）。</summary>
    public bool HasWork => ToInsert.Count > 0 || ToOverwrite.Count > 0 || Today.Count > 0;
}
