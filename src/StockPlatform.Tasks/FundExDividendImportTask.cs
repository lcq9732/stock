using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【导入基金除权除息】（2026-09-17）——从**东财终端自己落盘的** <c>fund_cqcx.db</c> 把 ETF 的
/// 分红/份额折算事件导进 <c>Dividend</c> 表。**纯本地读文件，一个请求都不发。**
///
/// ════ 为什么需要它 ════
/// ETF 至今只有 <c>day</c>（源给的**减法式前复权**），没有 <c>day_raw</c>/<c>day_adj</c>，
/// ⇒ **ETF 完全不能回测**。而 <c>day_adj</c> ＝ 不复权价 × 本地算的乘法式因子，
/// 因子要靠除权事件——没有事件就没有因子。这一项补的就是事件那一半
/// （另一半 <c>day_raw</c> 由【ETF日K·不复权】抓，见 <c>EtfRawBarTask</c>）。
///
/// ════ 数据源：东财终端的本地 SQLite ════
/// <c>C:\eastmoney\dfcf\config\DataBackUp\fund_cqcx.db</c>，标准 SQLite：
/// <code>
/// fund_cqcx(RecordID, Code, Market, UpdateDate, UpdateTime, ExDivType, NoticeDate,
///           NvcvtDate, Diviratioa, Diviratiob, Remark, IsValid)
/// </code>
/// 5845 条 / 1017 只基金，1999-04-06 ~ 2026-08-26。<c>ExDivType=1</c> 现金分红（3500 条，
/// 金额在 <c>Diviratioa</c>）、<c>=2</c> 份额折算（2345 条，比例在 <c>Diviratiob</c>）。
///
/// ⚠ 别跟 <c>full_cqcx_hs_V3.dat</c> 搞混——那份 105,117 条的沪深除权除息里**一条 ETF 都没有**，
/// 全是个股和新三板。
///
/// ════ 两个字段的单位都验过（恒等式，不是推测）════
/// **<c>Diviratioa</c> = 每 10 份现金分红**，跟个股 <c>DividendYuan</c> 同口径。验法是
/// <c>raw_t − qfq_t == (此后累计 Diviratioa) / 10</c>，510300/510050 逐点精确吻合。
///
/// **<c>Diviratiob</c> = 每 10 份折算后的份数**，价格乘数 = <c>10 / Diviratiob</c>。推导：
/// 除权参考价的分母是 <c>1 + ShareRatio</c>，要等于 <c>Diviratiob/10</c>
/// ⇒ **<c>ShareRatio = Diviratiob/10 − 1</c>** ⇒ 存进表里就是
/// <c>TransferShares = Diviratiob − 10</c>（表是每 10 股口径）。
/// 510500 那两次折算实测：比例 2.80325 ⇒ 理论跳空 +256.7%、实际 +261.1%；
/// 11.4539 ⇒ 理论 −12.70%、实际 −14.13%，都落在价格校验的容差内。
///
/// ⚠ 份额合并时 <c>TransferShares</c> 是**负数**，这是合法输入——
/// <see cref="StockPlatform.Logic.Services.AdjustFactorCalculator.ExDividend.IsEmpty"/>
/// 2026-09-17 专门为此从 <c>&lt;= 0</c> 改成 <c>== 0</c>，否则这类事件会被当空事件静默跳过。
///
/// ════ 代码一律带前缀，且前缀取库里存的 ════
/// ETF 在 <c>Bar</c>/<c>StockMeta</c> 里是 <c>sh510300</c> 这种 8 位符号，写进 <c>Dividend</c>
/// 也必须带前缀——个股是 6 位裸码，两个 key 空间天然不相交，股息率/连续分红年数那些因子
/// 一行都不会变（<c>GetAllCodes</c> 写死了 <c>GLOB '[0-9]×6'</c>）。
/// 前缀**不按 <c>Market</c> 字段自己算**，而是查库里存的是 <c>sh</c> 还是 <c>sz</c>
/// （自写前缀规则的代价见 feedback_market_prefix_via_classifier）；<c>Market</c> 只用来交叉校验，
/// 对不上就报一条，不改判断。
///
/// ════ 主键冲突的处理 ════
/// <c>Dividend</c> 主键是 <c>(code, announce_date)</c>，而同一只基金同一 <c>NoticeDate</c>
/// 有多条的情况确实存在（实测 1 组：166012 在 20150416 有两条折算，除权日差一天）。
/// 所以按 <c>(Code, NvcvtDate)</c> 聚合之后，**若某只基金的 announce_date 仍不唯一，
/// 整只退回用除权日当 announce_date**。这一列下游只作主键和排序用
/// （复权读 <c>ex_date</c>、股息率读 <c>dividend_yuan</c>），不影响语义。
/// </summary>
public sealed class FundExDividendImportTask(
    FetchPaths paths,
    IDividendRepository dividends,
    string? fundCqcxPath = null) : FetchTaskBase<DividendRow>
{
    /// <summary>东财终端的落盘位置。装了终端就有，没装就整项跳过（不算失败）。</summary>
    public const string DefaultPath = @"C:\eastmoney\dfcf\config\DataBackUp\fund_cqcx.db";

    private readonly string _path = fundCqcxPath ?? DefaultPath;

    public override FetchActionId Id => FetchActionId.ImportFundExDividend;

    private int _funds, _events, _cash, _split, _marketMismatch, _fallbackKey;
    private bool _fileMissing;

    protected override async IAsyncEnumerable<IReadOnlyList<DividendRow>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _funds = _events = _cash = _split = _marketMismatch = _fallbackKey = 0;
        _fileMissing = false;

        if (!File.Exists(_path))
        {
            _fileMissing = true;
            Report($"没找到东财终端的基金除权文件（{_path}），本项跳过——没装终端就没有这份数据。");
            yield break;
        }

        // ⚠ 读库 + 读文件 + 分组全是同步重活，**首个 await 之前必须自己推线程池**，
        // 否则 UI 会整段冻死（feedback_task_must_offload_heavy_sync）。
        var batches = await Task.Run(() => Build(ct), ct);

        foreach (var b in batches)
        {
            ct.ThrowIfCancellationRequested();
            yield return b;
        }
    }

    /// <summary>读文件 + 按基金分组 + 映射成 <see cref="DividendRow"/>。全同步，跑在线程池上。</summary>
    private List<List<DividendRow>> Build(CancellationToken ct)
    {
        // 库里的 ETF：带前缀的 8 位符号 → 裸码索引，用来决定"这只基金我们有没有、前缀是什么"
        var prefixByBare = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, _) in SqliteStockMetaUpsert.GetByTypes(paths.CurrentDb, SqliteStockMetaUpsert.TypeEtf))
        {
            if (code.Length != 8) continue;                       // 只认带前缀的；裸码的 ETF 是脏数据
            var bare = code[2..];
            if (!prefixByBare.ContainsKey(bare)) prefixByBare[bare] = code;
        }
        Report($"库里 ETF {prefixByBare.Count} 只（带前缀存），开始读 {Path.GetFileName(_path)}");

        var rowsByFund = new Dictionary<string, List<(DateTime Ex, int NoticeYmd, int Type, double A, double B)>>(StringComparer.Ordinal);
        using (var conn = new SqliteConnection($"Data Source={_path};Mode=ReadOnly"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Code, Market, NoticeDate, NvcvtDate, ExDivType, Diviratioa, Diviratiob
                FROM fund_cqcx
                WHERE IsValid = 1 AND NvcvtDate > 0
                ORDER BY Code, NvcvtDate;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                var bare = r.GetString(0).Trim();
                if (!prefixByBare.TryGetValue(bare, out var full)) continue;   // 不是我们库里的 ETF

                // Market 只做交叉校验：0=深 1=沪。前缀仍以库里存的为准。
                int market = r.IsDBNull(1) ? -1 : r.GetInt32(1);
                var expect = market == 1 ? "sh" : market == 0 ? "sz" : null;
                if (expect != null && !full.StartsWith(expect, StringComparison.Ordinal)) _marketMismatch++;

                if (!TryYmd(r.GetInt32(3), out var ex)) continue;
                int notice = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                if (!rowsByFund.TryGetValue(full, out var list)) rowsByFund[full] = list = [];
                list.Add((ex, notice, r.GetInt32(4),
                          r.IsDBNull(5) ? 0 : r.GetDouble(5),
                          r.IsDBNull(6) ? 0 : r.GetDouble(6)));
            }
        }

        var now = DateTime.Now;
        var result = new List<List<DividendRow>>();
        foreach (var (full, raw) in rowsByFund.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            // 同一除权日的多条合并成一行——复权算法本来也按 ExDate 分组合并（实测只有 4 组）
            var merged = raw.GroupBy(x => x.Ex).Select(g =>
            {
                double cash = g.Where(x => x.Type == 1).Sum(x => x.A);
                // 折算同日多条极罕见；真撞上就取第一条，比例不能相加
                var split = g.FirstOrDefault(x => x.Type == 2 && x.B > 0);
                int notice = g.Select(x => x.NoticeYmd).FirstOrDefault(v => v > 0);
                return (Ex: g.Key, Notice: notice, Cash: cash,
                        Transfer: split.B > 0 ? split.B - 10.0 : 0.0);
            }).OrderBy(x => x.Ex).ToList();

            // announce_date 要在这只基金内唯一，否则整只退回用除权日
            bool noticeUsable = merged.All(x => x.Notice > 0)
                                && merged.Select(x => x.Notice).Distinct().Count() == merged.Count;
            if (!noticeUsable) _fallbackKey++;

            var rows = new List<DividendRow>(merged.Count);
            foreach (var m in merged)
            {
                var announce = noticeUsable && TryYmd(m.Notice, out var nd) ? nd : m.Ex;
                rows.Add(new DividendRow
                {
                    Code = full,                        // ⚠ 带前缀，见类注释
                    AnnounceDate = announce,
                    BonusShares = 0,
                    TransferShares = m.Transfer,        // 负数＝份额合并，合法
                    DividendYuan = m.Cash,
                    Progress = "实施",                   // fund_cqcx 的 IsValid=1 就是已实施
                    RecordDate = null,                  // 源里没有股权登记日
                    ExDate = m.Ex,
                });
                if (m.Cash > 0) _cash++;
                if (m.Transfer != 0) _split++;
            }
            _funds++;
            _events += rows.Count;
            result.Add(rows);
        }
        return result;
    }

    protected override Task SaveBatchAsync(IReadOnlyList<DividendRow> batch, CancellationToken ct)
    {
        if (batch.Count > 0) dividends.ReplaceByCode(batch[0].Code, batch.ToList());
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_fileMissing)
            return Task.FromResult<TaskRunResult?>(
                TaskRunResult.Ok(nothingToDo: true, progress: "没装东财终端，没有这份数据，跳过"));

        Report($"导入完成：{_funds} 只 ETF、{_events} 条除权事件"
               + $"（现金分红 {_cash} 条、份额折算 {_split} 条）");
        if (_fallbackKey > 0)
            Report($"　其中 {_fallbackKey} 只的公告日不唯一，已退回用除权日当主键（不影响复权）");
        if (_marketMismatch > 0)
            Report($"　⚠ {_marketMismatch} 条的 Market 字段跟库里存的前缀对不上——前缀以库里为准，"
                   + "但这说明两边有一边的市场归属是错的，值得查");
        Report("下一步：抓 ETF 的 day_raw，然后点【重算回测序列】就有 ETF 的 day_adj 了。");
        return Task.FromResult<TaskRunResult?>(null);
    }

    /// <summary>东财的日期是 <c>yyyyMMdd</c> 的整数。</summary>
    private static bool TryYmd(int ymd, out DateTime date)
    {
        date = default;
        if (ymd < 19900101 || ymd > 21001231) return false;
        return DateTime.TryParseExact(ymd.ToString(CultureInfo.InvariantCulture), "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
