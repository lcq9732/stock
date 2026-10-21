using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 【金融监管指标】的开工检查（2026-10-21，随这一项挪进日更新增）。**只查本地库、不联网。**
///
/// ════ 为什么要有它 ════
/// 这一项每跑一次都有两段重活，不管有没有新东西：读全市场财务快照认机构（09-15 实测开工前静默
/// 16 分 42 秒）、把本地全部 PDF 重解析一遍（十几分钟）。放在每月一次时无所谓，挪进日更就是
/// 每个工作日白花一刻钟。而银行/券商/保险的年报、中报一年只集中在 3–4 月和 8 月那二三十个工作日——
/// 其余日子开工前问一句"有没有活"，没有就几秒结束。
///
/// ════ 认机构不扫全市场 ════
/// 先按证监会门类圈候选：金融业（J）一百来只，加上**还没有行业归属**的票（新股，实测几十只）——
/// 新上市的银行在行业数据跟上之前也落在这一类里，漏不掉。再只对这些票读最新一期快照，
/// 用跟任务同一个判据（<see cref="FinancialInstitutionRoster.Classify"/>）认类型。
/// 认出来之后再去掉已退市的（名单标了退市且 30 天没成交，见 <see cref="DelistedNoTrading"/>）。
/// 不拿 BankReportFetchState 里的代码当名单：那张表里混着非金融股（002594 比亚迪、600998 九州通，
/// 是早年重解析把子公司年报当成下错的文件留下的 wrong_file 行）。
///
/// ════ 一家算"有活"的条件 ════
///   ① 最近一期**已实际披露的年报或中报**（只认 06-30 / 12-31：一季报三季报的 PDF 里没有监管指标）
///      还没解析成功——从没试过的立刻算；上次是网络报错 / 新浪还没挂出 PDF 的，**不是今天试的**才算，
///      披露超过一周还没成的改成每周一试（不然一只卡住的票会让重解析天天跑一刻钟）。
///      查不到披露记录的退回法定截止日——兜底方向只能是"多查"。
///   ② 老期次的 PDF 文件丢了（任务里原来那条 needRefill），每周一试。
///   PDF 下到了却解析不出指标的（no_match / no_text）**不自动重试**：再下一遍结果一样，
///   它们在「待手工回填清单」里；解析规则改了靠重解析自愈。
/// </summary>
public class BankRegulatoryPlanner(FetchPaths paths)
{
    /// <summary>披露后这么多天内，没成的每天再试；过了就改成每周一试。</summary>
    internal static readonly TimeSpan DailyRetryWindow = TimeSpan.FromDays(7);

    /// <summary>每周一试的间隔。</summary>
    internal static readonly TimeSpan WeeklyRetry = TimeSpan.FromDays(7);

    /// <summary>
    /// 下载/解析状态里"再试一遍结果一样"的那几种——不自动重试。
    /// <see cref="NotListed"/>：补丢失文件时公告列表里根本找不到那一期（老期次，新浪下架了），
    /// 不记成它的话，这一期每周都会把整套重解析拖出来跑一遍。
    /// </summary>
    private static readonly HashSet<string> Deterministic = new(StringComparer.Ordinal) { "no_match", "no_text", NotListed };

    /// <summary>老期次补文件时公告列表里找不到——见 <see cref="Deterministic"/>。</summary>
    public const string NotListed = "not_listed";

    /// <summary>
    /// 标了退市、而且这么久没有成交，才算真退市（2026-10-21）。
    /// 只看退市名单不行：名单误标过（上交所"只 B 股退市"的行也填了 A 股代码，600801 被误标停更过），
    /// 一只误标、其实还在交易的银行券商会被静默跳过，新年报永远不解析。还在交易就照样检查。
    /// </summary>
    internal static readonly TimeSpan DelistedNoTrading = TimeSpan.FromDays(30);

    public BankRegulatoryPlan Plan(DateTime today)
    {
        today = today.Date;
        var industry = new SqliteIndustryRepository(paths.CurrentDb).GetClassCodeByCode();
        // 全新库（行业数据都还没有）：圈不出候选，退回任务原来的全市场认法
        if (industry.Count == 0) return BankRegulatoryPlan.Full("本地还没有行业分类数据");

        var finRepo = new SqliteFinancialRepository(paths.CurrentDb);
        var candidates = industry.Where(kv => kv.Value == "J").Select(kv => kv.Key)
            .Concat(finRepo.GetFetchStateByCode().Keys.Where(c => !industry.ContainsKey(c)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var institutions = FinancialInstitutionRoster.Classify(finRepo.GetLatestSnapshotByCodes(candidates));
        // 一家都认不出（财务报表还没抓过）：让任务走原来那条路，它会把"请先拉取财务报表"报出来
        if (institutions.Count == 0) return BankRegulatoryPlan.Full("候选里一家金融机构都认不出来");

        // 已退市的不进检查（2026-10-21）：600837 海通证券 2025-03 被合并退市，财务还在库里、照样认得出是券商，
        // 而它不会再出新报告——"公告列表里没找到中报"就每周被拉进来一次，每次连带一刻钟的重解析。
        // 判据见 DelistedNoTrading：名单标了退市 **且** 30 天没有成交。成交日期只对这一百来家逐只查（走索引）。
        var delistedMarked = SqliteStockMetaUpsert.GetByTypes(paths.CurrentDb, SqliteStockMetaUpsert.TypeDelisted)
            .Select(x => x.Code)
            .Concat(new SqliteDelistedRepository(paths.CurrentDb).GetAll().Select(r => r.Code))
            .ToHashSet(StringComparer.Ordinal);
        var bars = new SqliteBarRepository(paths.CurrentDb);
        var delisted = institutions
            .Where(x => delistedMarked.Contains(x.Code)
                        && (bars.GetLatestPeriodStart(x.Code, Granularity.DayRaw) is not { } last
                            || today - last.Date > DelistedNoTrading))
            .Select(x => x.Code)
            .ToHashSet(StringComparer.Ordinal);
        institutions = institutions.Where(x => !delisted.Contains(x.Code)).ToList();

        var states = new SqliteBankRegulatoryRepository(paths.CurrentDb).GetAllStates();
        var halfYear = new SqliteEarningsScheduleRepository(paths.CurrentDb).GetLatestHalfYearDisclosureByCode(today);
        var expected = LatestExpectedHalfYearPeriod(today);

        var work = new Dictionary<string, List<DateTime>>(StringComparer.Ordinal);
        var refillKeys = new HashSet<(string, DateTime)>();
        int fresh = 0, retry = 0, refill = 0;
        void Add(string code, DateTime period)
        {
            if (!work.TryGetValue(code, out var list)) work[code] = list = [];
            if (!list.Contains(period)) list.Add(period);
        }

        foreach (var (code, _) in institutions)
        {
            // ① 最近一期已披露的年报/中报
            var (period, disclosedOn) = halfYear.TryGetValue(code, out var d)
                ? (d.Period, (DateTime?)d.ActualDate)
                : (expected, null);
            if (!states.TryGetValue((code, period), out var st)) { Add(code, period); fresh++; }
            else if (st.Status != "ok" && !Deterministic.Contains(st.Status)
                     && IsDue(st.FetchedAt, today,
                              disclosedOn is { } on && today - on <= DailyRetryWindow ? TimeSpan.FromDays(1) : WeeklyRetry))
            { Add(code, period); retry++; }

            // ② 老期次的文件丢了
            foreach (var ((c, date), s) in states)
            {
                if (c != code || date == period || s.Status == "ok" || Deterministic.Contains(s.Status)) continue;
                if (File.Exists(SinaReportIndex.PathOf(paths.ReportsDir, code, date))) continue;
                if (!IsDue(s.FetchedAt, today, WeeklyRetry)) continue;
                Add(code, date);
                refillKeys.Add((code, date));
                refill++;
            }
        }

        return new BankRegulatoryPlan(false, null, institutions.Count, work, fresh, retry, refill)
        {
            RefillKeys = refillKeys,
            Delisted = delisted.Count,
        };
    }

    /// <summary>上次尝试离今天够不够 <paramref name="interval"/>（没试过＝够）。按日历日算，跟日更一天一轮对齐。</summary>
    private static bool IsDue(DateTime? lastAttempt, DateTime today, TimeSpan interval)
        => lastAttempt is not { } at || today - at.Date >= interval;

    /// <summary>今天应该已经能拿到的最新一期年报/中报——按法定截止日：中报 8-31、年报次年 4-30。</summary>
    internal static DateTime LatestExpectedHalfYearPeriod(DateTime today)
    {
        var interim = new DateTime(today.Year, 6, 30);
        if (today >= new DateTime(today.Year, 8, 31)) return interim;
        var lastAnnual = new DateTime(today.Year - 1, 12, 31);
        if (today >= new DateTime(today.Year, 4, 30)) return lastAnnual;
        return new DateTime(today.Year - 1, 6, 30);
    }
}

/// <summary>
/// 开工检查的结果。<see cref="FullRun"/> 为真时检查做不了（全新库），任务照原来的方式全量跑；
/// 否则只处理 <see cref="Work"/> 里的那几家、那几期，为空就整轮不做。
/// </summary>
public record BankRegulatoryPlan(
    bool FullRun,
    string? FullRunReason,
    int InstitutionCount,
    IReadOnlyDictionary<string, List<DateTime>> Work,
    int Fresh,
    int Retry,
    int Refill)
{
    public static BankRegulatoryPlan Full(string why)
        => new(true, why, 0, new Dictionary<string, List<DateTime>>(), 0, 0, 0);

    public bool NothingToDo => !FullRun && Work.Count == 0;

    /// <summary>
    /// 哪些 (代码, 期) 是"补丢失的老文件"——任务在公告列表里找不到它们时记 <see cref="BankRegulatoryPlanner.NotListed"/>
    /// （不再自动试）；找不到的若是最新一期则记 no_pdf（按披露后的节奏再试）。
    /// </summary>
    public IReadOnlySet<(string Code, DateTime Period)> RefillKeys { get; init; } = new HashSet<(string, DateTime)>();

    /// <summary>认得出是金融机构、但已退市而不检查的家数（2026-10-21，见 BankRegulatoryPlanner.DelistedNoTrading）。</summary>
    public int Delisted { get; init; }

    public string Describe() =>
        (FullRun
            ? $"金融监管指标：{FullRunReason}，按全市场认机构、逐家检查。"
            : Work.Count == 0
                ? $"金融监管指标：{InstitutionCount} 家机构都没有新披露的年报/中报要解析，也没有要补的文件，无需执行。"
                : $"金融监管指标：{InstitutionCount} 家机构里有 {Work.Count} 家要处理"
                  + $"（新披露 {Fresh} 期、上次没成再试 {Retry} 期、补丢失的文件 {Refill} 期）。")
        + (Delisted > 0 ? $"另有 {Delisted} 家已退市，不检查。" : "");
}
