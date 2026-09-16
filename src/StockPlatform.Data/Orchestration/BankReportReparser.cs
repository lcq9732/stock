using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 把本地已缓存的 PDF 用**当前**解析规则重跑一遍（2026-09-15 抽出来）。<b>不联网。</b>
///
/// ════ 为什么要有这一步 ════
/// 幂等自愈：解析规则改进后（各家版式差异会不断暴露新问题），已经下载过的报告不需要重新
/// 下载就能用新规则重跑，旧的错值被 INSERT OR REPLACE 覆盖掉。这正是"PDF 要留在本地"的
/// 意义所在——真实修过的坑：注释角标「（注3）」没清干净，平安银行的拨备覆盖率被存成了 3.0；
/// 目录页"七、资本充足率分析 42"的页码被当成资本充足率。
///
/// ════ 为什么抽成一个类 ════
/// 两个入口都要它，而且**必须是同一份逻辑**：
///   · 【金融监管指标】开头先跑一遍，再去下新的
///   · 【重解析已有PDF】只跑这一步，一个请求都不发
/// 抄成两份的话，改了一边忘了另一边，两个入口会给出不同的结果而没人发现。
/// </summary>
public sealed class BankReportReparser
{
    private readonly SqliteBankRegulatoryRepository _repo;
    private readonly string _reportsDir;
    private readonly BankReportParser _parser;

    /// <summary>写库要不要加锁——编排层那边跟别的步骤共用一把锁，任务那边独占，传 null 即可。</summary>
    private readonly object? _dbLock;

    public BankReportReparser(SqliteBankRegulatoryRepository repo, string reportsDir,
                              object? dbLock = null, BankReportParser? parser = null)
    {
        _repo = repo;
        _reportsDir = reportsDir;
        _dbLock = dbLock;
        _parser = parser ?? BankReportParser.Default;
    }

    /// <param name="Reparsed">重解析成功的报告份数。</param>
    /// <param name="MetricsRefreshed">刷新的指标个数。</param>
    /// <param name="SkippedNonFinancial">跳过的非金融股目录数——**必须报出来**，理由见 <see cref="Run"/>。</param>
    /// <param name="NoMatch">解析不出任何指标的份数——记 no_match 进手工回填清单，**文件不删**。</param>
    public readonly record struct Result(
        int Reparsed, int MetricsRefreshed, int SkippedNonFinancial, int NoMatch);

    /// <summary>
    /// 扫 <c>reportsDir</c> 下的每个代码目录，重解析里面的 PDF。
    ///
    /// ════ ⚠ 2026-09-16：不再删文件 ════
    /// 这里原来先用 <c>LooksLikeReport</c>（看前 3 页有没有年报的结构关键词）挡一道，
    /// 判 false 就 <c>File.Delete</c> + 记 wrong_file。那是为拦「问询函回复」写的
    /// （353 份里有 9 份）。实测下来这个判据已经**弊大于利**：
    ///
    ///   · 拿全库 377 份金融报告扫一遍，它判 false 的 13 份**全部**在库里有从它自己
    ///     解析出来的指标（7~15 个）——**100% 假阳性**，一份真的坏文件都没抓到。
    ///     而且是按公司聚集的：兰州银行 4 份全中、国信证券 4 份全中、招商证券 3 份，
    ///     跟报告类型无关，就是这几家的版式它认不出。
    ///   · 它要拦的东西**已经在源头拦掉了**：SinaReportIndex.TitleBlockers 现在把
    ///     「问询/回复/专项报告/摘要/英文/H股」全挡在下载之前，同一件事不必做两遍。
    ///   · 删了还补不回来：实测 11 份被删的 PDF 标着"已删除待重下"，最早的挂了半个月
    ///     都没补——下载循环那道"最新一期已有就整只票跳过"的优化把它们挡在门外了。
    ///
    /// 所以判据统一成跟下载路径一致的那个：**解析得出指标才算数**
    /// （下载那边是 FetchBestAsync 逐候选试到 metrics.Count > 0）。解析不出来就记
    /// no_match、进手工回填清单——**看得见**，比静默删掉强。真有漏网的坏文件也是如此。
    /// </summary>
    /// <param name="latest">全市场最新财务快照——认机构类型用，也是"这个目录归不归我管"的唯一依据。</param>
    public Result Run(IReadOnlyDictionary<string, FinancialSnapshot> latest,
                      Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(_reportsDir)) return default;

        int reparsed = 0, refreshed = 0, skippedNonFinancial = 0, noMatch = 0;
        progress?.Invoke("正在用当前解析规则重跑本地已缓存的 PDF（不联网）...");

        foreach (var dir in Directory.GetDirectories(_reportsDir))
        {
            ct.ThrowIfCancellationRequested();
            var code = Path.GetFileName(dir);

            // ⚠ **只碰金融股**（2026-09-11 补）。下面那段判定失败时会 File.Delete，
            //   而删文件这种事，判据必须收紧到"我确定这是我该管的文件"。
            //
            //   踩过的坑：为子公司解析下载的非金融年报也放在这个目录里，被这里扫到，
            //   LooksLikeReport（判据是前 3 页有没有年报的结构关键词，为拦截问询函而写）
            //   对它们一律返回 false —— 非金融年报前几页是封面和图片 —— 于是当成"下错的
            //   文件"删掉，删了 2 份（002594 比亚迪、600998 九州通）。
            //
            //   2026-09-15 起两个 PDF 目录合并，**这是唯一防线**。判据在
            //   FinancialInstitutionRoster.OwnsPdfOf，那边有一组用例钉着。
            if (!FinancialInstitutionRoster.OwnsPdfOf(code, latest))
            {
                skippedNonFinancial++;
                continue;
            }

            // 这家已经存下的指标，用来判断哪几期不用再 OCR（见下面 fullyApproved）。
            List<BankRegulatoryMetric> existing;
            if (_dbLock != null) lock (_dbLock) existing = _repo.GetByCode(code);
            else existing = _repo.GetByCode(code);

            foreach (var pdf in Directory.GetFiles(dir, "*.pdf"))
            {
                if (!DateTime.TryParse(Path.GetFileNameWithoutExtension(pdf), out var d)) continue;

                // ⚠ 这里以前有一道 LooksLikeReport + File.Delete，2026-09-16 去掉了，
                //   理由见方法注释（100% 假阳性、源头已经拦了、删了还补不回来）。
                //   现在一律走解析，出不来指标记 no_match，文件留着。
                try
                {
                    // 重解析也要按机构类型选标签集，否则会拿银行的标签去解析券商的报表。
                    var kind = latest.TryGetValue(code, out var snap)
                        ? BankHealthCheckBuilder.ClassifyInstitution(snap)
                        : FinancialInstitutionKind.Bank;
                    // 这一期的核心指标要是全都被人核对/回填过了，就别再跑 OCR 了——
                    // 一份要一分钟，而跑出来的值按 Upsert 的规则本来也覆盖不了人拍板的。
                    var expected = RegulatoryMetricCatalog.ExpectedFor(kind);
                    bool fullyApproved = expected.Length > 0 && expected.All(k =>
                        existing.Any(m => m.ReportDate == d && m.MetricKey == k
                                          && MetricSources.HumanApproved.Contains(m.Source)));
                    var ms = _parser.Parse(pdf, code, d, kind, progress, ct,
                                           allowOcr: !fullyApproved);
                    if (ms.Count == 0)
                    {
                        // **留痕**：静默 continue 的话，这一期在界面上跟"没抓过"长得一模一样。
                        // 记成 no_match 之后它会进手工回填清单，人看得见。
                        noMatch++;
                        Write(() => _repo.UpsertState(new BankReportFetchState
                        {
                            Code = code, ReportDate = d, Status = "no_match", MetricCount = 0,
                            Message = "PDF 有文本但没匹配到任何指标（版式可能变了）", PdfPath = pdf,
                        }));
                        continue;
                    }
                    Write(() =>
                    {
                        _repo.Upsert(ms);
                        _repo.UpsertState(new BankReportFetchState
                        {
                            Code = code, ReportDate = d, Status = "ok",
                            MetricCount = ms.Count, PdfPath = pdf,
                        });
                    });
                    reparsed++; refreshed += ms.Count;
                }
                catch { /* 解析不了的交给下载流程当成没抓过重新处理 */ }
            }
        }

        if (reparsed > 0)
            progress?.Invoke($"  本地重解析完成：{reparsed} 份报告、{refreshed} 个指标已按新规则刷新。");
        // 跳过了多少要说出来。不报的话，目录里躺着一批它压根没碰的东西，而没人知道。
        if (skippedNonFinancial > 0)
            progress?.Invoke($"  跳过 {skippedNonFinancial} 个非金融股目录（这一项只管银行/券商/保险）。");

        if (noMatch > 0)
            progress?.Invoke($"  {noMatch} 份解析不出指标，已记 no_match（文件留着，进手工回填清单）。");
        return new Result(reparsed, refreshed, skippedNonFinancial, noMatch);
    }

    private void Write(Action write)
    {
        if (_dbLock != null) lock (_dbLock) write();
        else write();
    }
}
