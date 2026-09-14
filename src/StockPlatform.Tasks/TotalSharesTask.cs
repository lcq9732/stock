using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【拉取总股本】（2026-09-14 新增）。全市场当前总股本，一个请求、一两秒。
///
/// ════ 为什么要这份数据 ════
/// PE/PB 一直拿财报的 <c>share_capital</c>（实收资本，**金额**）当股数用，只有面值 1 元的票
/// 才碰巧对。实测 5561 只里 373 只对不上，重算后 277 只（7.1%）的 PE 会变：中国移动
/// 348.8 → 16.1、分众传媒 0.5 → 20.1。完整口径说明见 <see cref="ITotalSharesProvider"/>。
///
/// ════ 一批＝整个市场 ════
/// 跟 <see cref="IndustryTask"/> 同一个理由：这份数据的语义是"全市场当前股本的完整快照"，
/// 按页落账的话中途停下来库里就只剩半个市场，而缺的那一半会**静默**回退到报表股本——
/// 正是这次要修的那个错，还更难发现。所以整批 yield 一次，
/// <see cref="TaskRunArgs.MaxItems"/>/<c>Deadline</c> 对本任务没有意义。
///
/// ════ 两道护栏 ════
/// ① **北交所专项**：库里有 920 开头的票、这轮却一只都没拿到就放弃。单独列一条是因为
///    项目里正好栽过：provider 自写前缀规则把 920 漏掉，**342 只票静默抓不到、一个错都不报**。
///    它排在总数护栏**之前**：北交所占全市场约 6%，整块丢会把两条护栏一起触发，
///    而"疑似半截名单"远不如"市场范围变了"有诊断价值。
/// ② **半截名单**：比库里在市个股少 5% 以上就整轮放弃（沿用 IndustryTask 的判据）。
/// </summary>
public sealed class TotalSharesTask(
    FetchPaths paths,
    IFundamentalMetricRepository repository,
    ITotalSharesProvider provider,
    ITradingDayRepository tradingDays) : FetchTaskBase<FundamentalMetric>
{
    /// <summary>比库里在市个股少这个比例以上就判定为"半截名单"，整轮放弃。</summary>
    private const double MinKeepRatio = 0.95;

    public override FetchActionId Id => FetchActionId.FetchTotalShares;

    /// <summary>非空＝这一轮没写库，<see cref="OnCompletedAsync"/> 据此报 Failed。</summary>
    private string? _abortReason;

    private int _rowsSaved, _localLive, _bjs;
    private DateTime _asOf;
    private string? _dateNote;

    protected override async IAsyncEnumerable<IReadOnlyList<FundamentalMetric>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        tradingDays.EnsureSchema();
        _abortReason = null;
        _rowsSaved = _bjs = 0;
        _dateNote = null;

        var live = SqliteStockMetaUpsert.GetAll(paths.CurrentDb).Select(s => s.Code).ToHashSet();
        _localLive = live.Count;
        int localBjs = live.Count(IsBeijing);

        void Forward(string s) => Report(s);
        provider.OnStatus += Forward;
        TotalSharesSnapshot snap;
        try
        {
            Report($"开始抓取全市场总股本（来源 {provider.SourceName}，库里在市个股 {_localLive} 只）...");
            snap = await provider.GetAllAsync(ct);
        }
        finally
        {
            provider.OnStatus -= Forward;
        }

        if (snap.Rows.Count == 0)
        {
            _abortReason = "总股本返回空——接口可能变了，本轮不写库（库里保留上一版）";
            yield break;
        }

        // 护栏①：北交所专项。**排在半截名单之前**——北交所占全市场约 6%，整块丢会同时
        // 触发下面那条总数护栏，而"疑似半截名单"远不如"市场范围变了"有诊断价值。
        // 先判具体的那个，报出来的才是真原因。
        _bjs = snap.Rows.Count(r => IsBeijing(r.Code));
        if (localBjs > 0 && _bjs == 0)
        {
            _abortReason = $"总股本一只北交所股票都没拿到（库里有 {localBjs} 只）——"
                         + "接口的市场范围变了，本轮不写库";
            yield break;
        }

        // 护栏②：半截名单。⚠ 只在库里本来就有名册时才判，空库首次抓取要放行。
        if (_localLive > 0 && snap.Rows.Count < _localLive * MinKeepRatio)
        {
            _abortReason = $"总股本只拿到 {snap.Rows.Count} 只、库里在市 {_localLive} 只（少了 "
                         + $"{(1 - (double)snap.Rows.Count / _localLive) * 100:F1}%），疑似半截名单，本轮不写库";
            yield break;
        }

        _asOf = ResolveAsOfDate(snap.TradeDate);
        var fetchedAt = DateTime.Now;
        yield return snap.Rows.Select(r => new FundamentalMetric
        {
            Code = r.Code,
            MetricKey = MetricKeys.TotalShares,
            AsOfDate = _asOf,
            Value = r.TotalShares,
            Source = provider.SourceName,
            FetchedAt = fetchedAt,
        }).ToList();
    }

    /// <summary>
    /// 定这批值属于哪个交易日。
    ///
    /// ⚠ **不直接信接口自报的日期**：它是数据源说的，而本地已经有权威日历（深交所官网那份）。
    /// 对不上时回退到日历里 ≤ 它的最近一个交易日并记一句——记的是"值所属交易日"，不是"哪天跑的"，
    /// 这跟流通市值那一列踩过的坑是同一条纪律（早期一律写 DateTime.Today，周末跑到的值被记到
    /// 周末名下，事后要按交易日归位）。
    /// </summary>
    private DateTime ResolveAsOfDate(DateTime? reported)
    {
        var target = (reported ?? DateTime.Today).Date;
        var from = DateOnly.FromDateTime(target.AddDays(-30));
        var known = tradingDays.GetBetween(from, DateOnly.FromDateTime(target));

        if (known.Count == 0)
        {
            _dateNote = $"本地交易日历在 {target:yyyy-MM-dd} 前后没有数据，直接采用接口自报的日期"
                      + "（先跑一次【交易日历】能让这个判断变准）";
            return target;
        }

        if (known.Contains(DateOnly.FromDateTime(target))) return target;

        var latest = known.Max();
        _dateNote = $"接口自报 {target:yyyy-MM-dd} 不是交易日，按日历归到 {latest:yyyy-MM-dd}";
        return latest.ToDateTime(TimeOnly.MinValue);
    }

    private static bool IsBeijing(string code) => code.StartsWith("92") || code.StartsWith("8");

    protected override Task SaveBatchAsync(IReadOnlyList<FundamentalMetric> batch, CancellationToken ct)
    {
        repository.Upsert(batch);
        _rowsSaved = batch.Count;
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_abortReason != null)
        {
            Report("⚠ " + _abortReason);
            return Task.FromResult<TaskRunResult?>(new TaskRunResult(TaskState.Failed, [_abortReason]));
        }

        if (_dateNote != null) Report("⚠ " + _dateNote);

        // 缺的那些会静默回退到报表股本（面值不是 1 元的票就此算错），所以差额要报出来。
        int missing = Math.Max(0, _localLive - _rowsSaved);
        Report($"总股本完成：{_rowsSaved} 只（北交所 {_bjs} 只），归到交易日 {_asOf:yyyy-MM-dd}"
             + (missing > 0 ? $"；库里另有 {missing} 只没拿到（退市/停牌居多，这些票的 PE 会回退用报表股本）" : ""));
        return Task.FromResult<TaskRunResult?>(null);
    }
}
