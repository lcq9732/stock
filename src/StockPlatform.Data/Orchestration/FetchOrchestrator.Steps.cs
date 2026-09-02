using System.Collections.Concurrent;
using System.Diagnostics;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 【拉取全部】拆出来的**单项入口**（2026-09-02 新增，设计见 doc/fetch-plan-atomic-tasks-design.md）。
///
/// ════ 这一步做了什么、没做什么 ════
/// 这里的每个方法都只是把 <see cref="FetchOrchestrator"/> 主体里对应的那一段包了个壳：
/// 自己建 errors/failedCodes/stats/计时器、自己收尾写 manifest。**抓取逻辑一行都没改**，
/// 所以【拉取全部】和"只跑这一步"走的是同一段代码，不会出现两套行为。
///
/// ════ 为什么每项要自己建状态 ════
/// 原来 13 步共享一份 errors/stats，最后汇总一次、写一次 manifest。拆开之后每项是独立的一轮，
/// 各自汇总、各自记录——这正是拆分想要的（一项失败不再拖累其它 12 项）。
///
/// ════ 步骤之间怎么传数据 ════
/// 原来第 1 步取到的股票列表是**内存**传给后面几步的。拆开后统一走库：名册项写 StockMeta，
/// 逐只项开跑时用 <see cref="LocalStockCodes"/> 读回来（<see cref="SqliteStockMetaUpsert.GetAll"/>
/// 只返回 type='stock'，指数/ETF/板块/退市股不会混进来）。
/// 库里还没有名册时直接抛异常提示先跑名册项——跟【补指定历史日】现在的做法一致。
/// </summary>
public partial class FetchOrchestrator
{
    /// <summary>每个单项开跑前的准备：打开库、建好本轮要用的收集器。</summary>
    private (SqliteBarRepository Repo, ConcurrentBag<string> Errors, ConcurrentBag<string> Failed,
             FetchStats Stats, Stopwatch Sw) BeginStep()
    {
        var repo = new SqliteBarRepository(_paths.CurrentDb);
        repo.EnsureSchema();
        return (repo, new ConcurrentBag<string>(), new ConcurrentBag<string>(), new FetchStats(), Stopwatch.StartNew());
    }

    /// <summary>本地已知的**个股**代码（不含指数/ETF/板块/退市股）。空库时抛异常，提示先跑名册项。</summary>
    private List<string> LocalStockCodes()
    {
        var codes = SqliteStockMetaUpsert.GetAll(_paths.CurrentDb).Select(s => s.Code).ToList();
        if (codes.Count == 0)
            throw new InvalidOperationException(
                "本地还没有股票名册，这一步没有可抓的标的——请先跑一次【股票名册与流通市值】");
        return codes;
    }

    // ───────────────────────────── 1. 股票名册与流通市值 ─────────────────────────────

    /// <summary>
    /// 刷新全市场名册（写 StockMeta）+ 当下的流通市值快照。
    ///
    /// ⚠ 现在会扫两遍新浪的列表接口：一遍取名册、一遍在 <see cref="IMarketCapFetcher"/> 里面取市值
    /// （<see cref="Remote.SinaListMarketCapFetcher"/> 内部自己会再分页扫一次）。这跟【拉取全部】
    /// 原来的行为一致，先照搬不动；要省掉那 ~55 个重复请求得改 IMarketCapFetcher 的接口
    /// （让它接受"已经取到的列表"），留给后续阶段做。
    ///
    /// 市值是**当下快照**、接口没有历史，所以这一项只有"增量"一种模式。
    /// </summary>
    public async Task<FetchResult> RunStepRosterAndMarketCapAsync(
        NamedBarSource source, IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, _) = BeginStep();
        void Forward(string m) => progress?.Report(m);
        _marketCapFetcher.OnStatus += Forward;
        try
        {
            progress?.Report("正在获取全市场股票列表...");
            var stocks = await source.StockListProvider.GetAllStocksAsync(progress, ct);
            SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, stocks.Select(s => (s.Code, s.Name)));
            progress?.Report($"名册写入完成，共 {stocks.Count} 只（数据源：{source.Name}）");

            await FetchMarketCapAsync(source, stocks.Select(s => s.Code).ToList(), progress, ct);
        }
        finally { _marketCapFetcher.OnStatus -= Forward; }

        // attempted 传空：K线失败名单跟这一步无关，别把它清了（市值有自己的失败名单，
        // 由 FetchMarketCapAsync 内部维护）。
        return FinishFetchRun(errors, "股票名册与流通市值", Array.Empty<string>(), failed, progress);
    }

    // ───────────────────────────── 2. 资金净流入 ─────────────────────────────

    /// <summary>逐只抓主力资金净流入（新浪）。失败名单由 FetchNetInflowAsync 内部维护。</summary>
    public async Task<FetchResult> RunStepNetInflowAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, _) = BeginStep();
        var codes = LocalStockCodes();
        void Forward(string m) => progress?.Report(m);
        _netInflowFetcher.OnStatus += Forward;
        try
        {
            await FetchNetInflowAsync(codes, DateTime.Today, exactDayOnly: false, progress, ct);
        }
        finally { _netInflowFetcher.OnStatus -= Forward; }
        return FinishFetchRun(errors, "资金净流入", Array.Empty<string>(), failed, progress);
    }

    // ───────────────────────────── 3. 中标/订单公告 ─────────────────────────────

    /// <summary>
    /// 按关键词抓中标/订单公告（巨潮检索 → 正文）。关键词为空就是空跑（跟原来一致）。
    /// 回看窗口默认沿用【拉取全部】的 AnnouncementLookbackDaysForFetchAll 天。
    /// </summary>
    public async Task<FetchResult> RunStepAnnouncementsAsync(
        IReadOnlyList<string> keywords, IProgress<string>? progress, CancellationToken ct = default,
        int? lookbackDays = null)
    {
        var (_, errors, failed, _, _) = BeginStep();
        if (keywords.Count == 0)
        {
            progress?.Report("（公告关键词为空，这一项跳过）");
            return new FetchResult { NothingToDo = true };
        }
        var today = DateTime.Today;
        var start = DateOnly.FromDateTime(today.AddDays(-(lookbackDays ?? AnnouncementLookbackDaysForFetchAll)));
        await FetchAnnouncementsAsync(keywords, start, DateOnly.FromDateTime(today), progress, ct);
        return FinishFetchRun(errors, "中标/订单公告", Array.Empty<string>(), failed, progress);
    }

    // ───────────────────────────── 4. 指数日K ─────────────────────────────

    /// <summary>大盘指数日K（水位线增量）。它同时是"最近一个已收盘交易日"的锚，很多快照类判断靠它。</summary>
    public async Task<FetchResult> RunStepIndexBarsAsync(
        NamedBarSource source, int lookbackYears, IProgress<string>? progress, CancellationToken ct = default)
    {
        var (repo, errors, failed, stats, sw) = BeginStep();
        void Forward(string m) => progress?.Report(m);
        source.Fetcher.OnStatus += Forward;
        try
        {
            await FetchIndexBarsAsync(source, DateTime.Today, lookbackYears, repo, errors, failed, stats, progress, sw, ct);
        }
        finally { source.Fetcher.OnStatus -= Forward; }
        progress?.Report($"本项汇总：{stats.Summarize()}");
        return FinishFetchRun(errors, "指数日K",
            MarketIndexCatalog.All.Select(i => i.Symbol).ToList(), failed, progress);
    }

    // ──────────────────── 5~7. 个股日K（前复权 / 后复权 / 不复权） ────────────────────

    /// <summary>个股日K·前复权：按水位线续抓，顺带重算周/月线、记录复权基准漂移名单。</summary>
    public async Task<FetchResult> RunStepStockDayBarsAsync(
        NamedBarSource source, int lookbackYears, IProgress<string>? progress, CancellationToken ct = default)
    {
        var (repo, errors, failed, stats, sw) = BeginStep();
        var codes = LocalStockCodes();
        void Forward(string m) => progress?.Report(m);
        source.Fetcher.OnStatus += Forward;
        try
        {
            await FetchStockDayBarsAsync(source, codes, DateTime.Today, lookbackYears,
                repo, errors, failed, stats, progress, sw, ct);
        }
        finally { source.Fetcher.OnStatus -= Forward; }
        progress?.Report($"本项汇总：{stats.Summarize()}");
        return FinishFetchRun(errors, "个股日K·前复权", codes, failed, progress);
    }

    /// <summary>个股日K·后复权（回测遗留口径，水位线独立于前复权）。</summary>
    public Task<FetchResult> RunStepStockHfqBarsAsync(
        NamedBarSource source, int lookbackYears, IProgress<string>? progress, CancellationToken ct = default) =>
        RunStepStockAdjustedBarsAsync(source, lookbackYears, Granularity.DayHfq, "个股日K·后复权", progress, ct);

    /// <summary>个股日K·不复权（原始成交价，day_adj 的输入）。日常增量走这里；首次整段回补用
    /// <see cref="RunFetchRawBarsAsync"/>。</summary>
    public Task<FetchResult> RunStepStockRawBarsAsync(
        NamedBarSource source, int lookbackYears, IProgress<string>? progress, CancellationToken ct = default) =>
        RunStepStockAdjustedBarsAsync(source, lookbackYears, Granularity.DayRaw, "个股日K·不复权", progress, ct);

    private async Task<FetchResult> RunStepStockAdjustedBarsAsync(
        NamedBarSource source, int lookbackYears, string gran, string kind,
        IProgress<string>? progress, CancellationToken ct)
    {
        var (repo, errors, failed, stats, sw) = BeginStep();
        var codes = LocalStockCodes();
        var today = DateTime.Today;
        void Forward(string m) => progress?.Report(m);
        source.Fetcher.OnStatus += Forward;
        try
        {
            await FetchHfqBarsAsync(source, codes,
                code => HfqWatermarkWindow(repo, code, today, lookbackYears, gran),
                repo, errors, failed, stats, progress, sw, ct, gran: gran);
        }
        finally { source.Fetcher.OnStatus -= Forward; }
        progress?.Report($"本项汇总：{stats.Summarize()}");
        return FinishFetchRun(errors, kind, codes, failed, progress);
    }

    // ───────────────────────────── 8. ETF 日K ─────────────────────────────

    /// <summary>全市场 ETF 日K（新浪名单 + 所选K线源，水位线增量）。</summary>
    public async Task<FetchResult> RunStepEtfBarsAsync(
        NamedBarSource source, int lookbackYears, IProgress<string>? progress, CancellationToken ct = default)
    {
        var (repo, errors, failed, stats, sw) = BeginStep();
        void Forward(string m) => progress?.Report(m);
        source.Fetcher.OnStatus += Forward;
        List<string> etfCodes;
        try
        {
            etfCodes = await FetchEtfBarsAsync(source, DateTime.Today, lookbackYears, repo, errors, failed, stats, progress, sw, ct);
        }
        finally { source.Fetcher.OnStatus -= Forward; }
        progress?.Report($"本项汇总：{stats.Summarize()}");
        return FinishFetchRun(errors, "ETF日K", etfCodes, failed, progress);
    }

    // ───────────────────────────── 9. 退市股收尾 ─────────────────────────────

    /// <summary>刷新退市名单，并给"本地跟踪过、但最后一根K线还早于终止日"的票补完最后那几天。</summary>
    public async Task<FetchResult> RunStepDelistedTailsAsync(
        NamedBarSource source, IProgress<string>? progress, CancellationToken ct = default)
    {
        var (repo, errors, failed, stats, sw) = BeginStep();
        void Forward(string m) => progress?.Report(m);
        source.Fetcher.OnStatus += Forward;
        List<string> codes;
        try
        {
            codes = await CatchUpDelistedTailsAsync(source, repo, errors, failed, stats, progress, sw, ct);
        }
        finally { source.Fetcher.OnStatus -= Forward; }
        progress?.Report($"本项汇总：{stats.Summarize()}");
        return FinishFetchRun(errors, "退市股收尾", codes, failed, progress);
    }

    // ─────────────────────── 10. 板块指数合成（本地计算） ───────────────────────

    /// <summary>
    /// 用本地成分股 + 个股日K 等权合成板块指数日K。**不联网、纯 CPU**，所以推到线程池上跑，
    /// 免得在 UI 线程上把界面冻住（同 RunFetchBankRegulatoryAsync 的理由）。
    /// </summary>
    public async Task<FetchResult> RunStepSynthesizeBoardIndexAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var (repo, errors, failed, _, _) = BeginStep();
        await Task.Run(() => SynthesizeBoardIndexCore(repo, errors, progress, ct), ct);
        return FinishFetchRun(errors, "板块指数合成", Array.Empty<string>(), failed, progress);
    }

    // ─────────────────────── 11~12. 融资余额 / 龙虎榜 ───────────────────────

    /// <summary>融资余额（交易所）——以指定日为终点回看最近几个交易日、跳过本地已有的。默认今天。</summary>
    public async Task<FetchResult> RunStepMarginRecentAsync(
        DateTime? day, IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, _) = BeginStep();
        void Forward(string m) => progress?.Report(m);
        _marginProvider.OnStatus += Forward;
        try { await FetchMarginRecentAsync(day ?? DateTime.Today, errors, progress, ct); }
        finally { _marginProvider.OnStatus -= Forward; }
        return FinishFetchRun(errors, "融资余额", Array.Empty<string>(), failed, progress);
    }

    /// <summary>龙虎榜（新浪）——只抓指定那一天，默认今天。</summary>
    public async Task<FetchResult> RunStepLhbDayAsync(
        DateTime? day, IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, _) = BeginStep();
        void Forward(string m) => progress?.Report(m);
        _lhbProvider.OnStatus += Forward;
        try { await FetchLhbOneDayAsync(day ?? DateTime.Today, errors, progress, ct); }
        finally { _lhbProvider.OnStatus -= Forward; }
        return FinishFetchRun(errors, "龙虎榜", Array.Empty<string>(), failed, progress);
    }

    // ─────────────────── 13. 当日覆盖率体检（本地查库） ───────────────────

    /// <summary>
    /// 以上证指数最新一根日线当交易日锚，查出"上一个交易日有、这一天没有"的个股，写进待重试名单。
    /// 纯查库，可能要扫几 GB，所以推到线程池上跑。
    /// </summary>
    public async Task<FetchResult> RunStepDayCoverageCheckAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, _) = BeginStep();
        var (_, missing) = await Task.Run(() => CheckLatestDayCoverage(progress), ct);
        var result = FinishFetchRun(errors, "当日覆盖率体检", Array.Empty<string>(), failed, progress);
        result.NothingToDo = missing == 0;
        return result;
    }
}
