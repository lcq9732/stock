using System.Runtime.CompilerServices;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 一份年报 PDF 的解析结果。
///
/// ⚠ **这个类型存在的唯一理由，是让"认不出来"也能落库。**
/// 骨架只在 <c>batch.Count > 0</c> 时才调 SaveBatchAsync。如果批里装的是 CompanySubsidiary，
/// 那么版式认不出来的 PDF（0 家）就永远进不了保存 → 水位线写不进去 → 每轮重试那几份，
/// 而且毫无征兆。装成"一份 PDF 的结果"之后，Items 为空它自己仍是一个元素，照样会被保存。
///
/// 顺带满足那条一般原则：**水位线粒度（一份 PDF）细于截断粒度（一批 = 若干份）**。
/// </summary>
public sealed record ParsedReport(string Code, DateTime ReportDate,
                                  IReadOnlyList<CompanySubsidiary> Items);

/// <summary>
/// 从本地已下载的年报 PDF 里解析**子公司名单**（2026-09-11）。纯本地计算，一个网络请求都不发。
///
/// ════ 干什么用 ════
/// 给【客户与供应商】的对手方还原补第三档：年报里的客户写的是"中国建筑第六工程局有限公司"，
/// 本地股票池里只有母公司"中国建筑 601668"。实测库里 10.9 万个未还原的对手名中有相当一部分
/// 是上市公司的子公司。
///
/// ════ 读哪个目录 ════
/// <c>FetchPaths.AnnualReportsDir</c>（publish/data/annual-reports）—— **不是** reports/。那个目录是
/// 【金融监管指标】的 PDF 缓存，把年报放进去会被【重解析已有PDF】当成"下错的文件"删掉
/// （实测删过 2 份，见 FetchPaths.AnnualReportsDir 的注释）。
///
/// ════ 重解析靠 parser_version ════
/// 解析规则改了就把 <see cref="SubsidiaryParser.ParserVersion"/> +1，这里据此重跑已处理过的
/// PDF——跟财务报表"科目集版本 v4→v5"同一套路。不改版本就只处理没见过的。
/// </summary>
public sealed class SubsidiaryExtractTask : FetchTaskBase<ParsedReport>
{
    /// <summary>一批几份。批是截断粒度，不能太大——太大的话中途停会丢掉一整批的进度。</summary>
    private const int BatchSize = 20;

    private readonly ICompanySubsidiaryRepository _repository;
    private readonly ICompanyProfileRepository _profiles;
    private readonly string _annualReportsDir;
    private readonly SubsidiaryParser _parser;

    public SubsidiaryExtractTask(ICompanySubsidiaryRepository repository,
                                 ICompanyProfileRepository profiles,
                                 string annualReportsDir,
                                 SubsidiaryParser? parser = null)
    {
        _repository = repository;
        _profiles = profiles;
        _annualReportsDir = annualReportsDir;
        _parser = parser ?? SubsidiaryParser.Default;
    }

    public override FetchActionId Id => FetchActionId.StepSubsidiaryExtract;

    private int _found, _empty;

    protected override async IAsyncEnumerable<IReadOnlyList<ParsedReport>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _repository.EnsureSchema();
        _found = _empty = 0;

        if (!Directory.Exists(_annualReportsDir))
        {
            Report($"年报目录还不存在（{_annualReportsDir}），本轮无事可做。");
            yield break;
        }

        // 规则版本变了的、和从没处理过的，都要跑
        var done = _repository.GetParsed(SubsidiaryParser.ParserVersion);

        // 母公司全称：用来把"自己"从自己的子公司名单里排掉（全称会出现在表头和正文里）
        var fullNames = _profiles.GetAllNames()
                                 .GroupBy(x => x.Code, StringComparer.Ordinal)
                                 .ToDictionary(g => g.Key, g => g.First().FullName,
                                               StringComparer.Ordinal);

        var todo = new List<(string Code, DateTime Date, string Path)>();
        foreach (var dir in Directory.GetDirectories(_annualReportsDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var code = Path.GetFileName(dir);
            foreach (var pdf in Directory.GetFiles(dir, "*.pdf").OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!DateTime.TryParse(Path.GetFileNameWithoutExtension(pdf), out var d)) continue;
                if (done.Contains((code, d))) continue;
                todo.Add((code, d, pdf));
            }
        }

        if (todo.Count == 0)
        {
            Report($"年报子公司：{done.Count} 份都按 v{SubsidiaryParser.ParserVersion} 规则解析过了，本轮无事可做。");
            yield break;
        }

        Report($"年报子公司：{todo.Count} 份待解析"
             + (done.Count > 0 ? $"（另有 {done.Count} 份已按当前规则处理过，跳过）" : "")
             + "。纯本地计算，不联网。");

        var batch = new List<ParsedReport>(BatchSize);
        int n = 0;
        foreach (var (code, date, path) in todo)
        {
            ct.ThrowIfCancellationRequested();

            // 单份解析失败不该让整轮报废——版式千奇百怪，认不出来是常态。
            // 失败也当成"处理过、0 家"记下来，否则每轮都会重试同一份。
            IReadOnlyList<CompanySubsidiary> items;
            try
            {
                fullNames.TryGetValue(code, out var self);
                // ⚠ **必须包 Task.Run**。解析一份年报要读几百页 PDF，是纯 CPU 的同步活，
                //   直接调会占着调用线程不放——实测 32 份 27 秒，界面全程卡死。
                //   网络任务天然有 await 会让出线程，本地计算任务没有，得自己让。
                //   同样的坑 AdjSeriesRebuildTask 的类注释里记过："丢了就是 UI 卡死几十分钟"。
                items = await Task.Run(() => _parser.Parse(path, code, date, self, ct), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Report($"  {code} {date:yyyy-MM-dd} 解析出错（记为 0 家，不再重试）：{ex.GetType().Name}");
                items = [];
            }

            if (items.Count > 0) _found += items.Count; else _empty++;
            batch.Add(new ParsedReport(code, date, items));

            if (++n % 20 == 0)
                Report($"  已解析 {n}/{todo.Count} 份，累计 {_found} 家子公司");

            if (batch.Count >= BatchSize)
            {
                yield return batch;
                batch = new List<ParsedReport>(BatchSize);
            }
        }
        if (batch.Count > 0) yield return batch;
    }

    /// <summary>
    /// 一份 PDF 一次 Save——名单和水位线在同一个事务里，空名单也写。
    /// 同样包 Task.Run：SQLite 写是同步的，一批 20 份、每份一个事务，别占着调用线程。
    /// </summary>
    protected override Task SaveBatchAsync(IReadOnlyList<ParsedReport> batch, CancellationToken ct)
        => Task.Run(() =>
        {
            foreach (var r in batch)
                _repository.Save(r.Code, r.ReportDate, SubsidiaryParser.ParserVersion, r.Items);
        }, ct);

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        var (rows, parents, parsed, empty) = _repository.GetStats();
        var summary = $"年报子公司：本轮解析 {stats.Items} 份，提出 {_found} 家"
                    + (_empty > 0 ? $"（{_empty} 份版式认不出来）" : "")
                    + $"；库里累计 {rows} 行 / {parents} 家母公司，已处理 {parsed} 份、其中 {empty} 份为空。";
        Report(summary);
        return Task.FromResult<TaskRunResult?>(
            TaskRunResult.Ok(nothingToDo: stats.Items == 0, progress: summary));
    }
}
