using System.Runtime.CompilerServices;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
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
/// 从年报 PDF 里解析**子公司名单**（2026-09-11，2026-09-15 加下载）。
///
/// ⚠ **它不再是纯本地任务**：本地缺年报时会按「被别人写进前五大客户/供应商的次数」
///   排出前 N 家自己去下（只下年报——半年报附注是简版，没有完整的合并报表范围）。
///   但**零请求自愈仍然成立**：目标报告期从日历算、查文件在列公告之前，
///   所以 ParserVersion 改版触发的全量重跑照样一个请求都不发。
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
    private readonly string _reportsDir;
    private readonly SubsidiaryParser _parser;

    /// <summary>
    /// 缺年报时去下载。<b>为 null 就退回纯本地模式</b>——只解析目录里已有的，一个请求都不发。
    /// </summary>
    private readonly SinaReportIndex? _index;

    /// <summary>
    /// 优先下载谁：按"被别人写进前五大客户/供应商的次数"排出来的清单。
    ///
    /// 为什么是这个判据：子公司名单的用处是把「中国建筑第八工程局有限公司」还原成 601668，
    /// 而一家公司被点名越多，它的子公司出现在别人名单里的概率越大。实测前 25 名里 18 家
    /// 还没有名单，中国石油被点名 620 次、中国石化 347 次。
    ///
    /// ⚠ 它是**代理指标**：量的是"已经被认出多少次"，不是"有多少没认出的名字属于它"。
    ///   直接量只能靠"名字里含简称"那种启发式，而那个假阳性太多（连云港、太阳能、机器人
    ///   都是上市公司简称，会撞上一堆无关公司）。
    /// </summary>
    private readonly Func<int, IReadOnlyList<string>>? _priorityCodes;

    /// <summary>一轮最多下几家。年报中位 2 MB、最大 22 MB，别一次铺太开。</summary>
    private readonly int _maxDownloads;

    /// <summary>
    /// 取全市场最新财务快照，用来认出金融机构。<b>为 null 就不做行业过滤</b>（老行为）。
    ///
    /// 为什么要过滤掉金融股（2026-09-16，有实测支撑）：
    ///   · 金融股 184 份年报 → 认不出 53%，产出 567 条名单，最终 subsidiary 匹配 **0 条**
    ///   · 非金融 63 份年报 → 认不出 14%，产出 1184 条名单，匹配 229 条
    /// 银行券商在别人年报里是以**分行**形态出现的（「浦发银行股份有限公司普陀支行」），
    /// 那条路由 PartnerNameMatcher 的 qualified 档接住，而且比子公司归并更准——
    /// 分行不是独立法人、就是总行本身，不是"两个法人有控制关系"的假设。
    ///
    /// 省掉的不只是时间：43% 这个"认不出率"里混着金融股，掩盖了非金融那条线的真实质量（14%）。
    /// </summary>
    private readonly Func<IReadOnlyDictionary<string, FinancialSnapshot>>? _latestFinancials;

    /// <summary>
    /// 当前在市个股代码（<c>StockMeta.type = 'stock'</c>）。用来把母公司代码归一到正主——
    /// 见 <see cref="NormalizeParent"/>。为 null 就不归一（老行为）。
    /// </summary>
    private readonly Func<IReadOnlySet<string>>? _currentStockCodes;

    public SubsidiaryExtractTask(ICompanySubsidiaryRepository repository,
                                 ICompanyProfileRepository profiles,
                                 string reportsDir,
                                 SubsidiaryParser? parser = null,
                                 SinaReportIndex? index = null,
                                 Func<int, IReadOnlyList<string>>? priorityCodes = null,
                                 int maxDownloads = 50,
                                 Func<IReadOnlyDictionary<string, FinancialSnapshot>>? latestFinancials = null,
                                 Func<IReadOnlySet<string>>? currentStockCodes = null)
    {
        _repository = repository;
        _profiles = profiles;
        _reportsDir = reportsDir;
        _parser = parser ?? SubsidiaryParser.Default;
        _index = index;
        _priorityCodes = priorityCodes;
        _maxDownloads = maxDownloads;
        _latestFinancials = latestFinancials;
        _currentStockCodes = currentStockCodes;
    }

    /// <summary>
    /// 把母公司代码归一到**当前个股**。
    ///
    /// ════ 为什么需要 ════
    /// PDF 存在 <c>reports/&lt;code&gt;/</c>，目录名就是代码，而这个代码来自下载清单——
    /// 清单里可能混着废代码。实测：「上海医药集团股份有限公司」这个全称同时挂在
    /// 600849（上药转换，不是个股）和 601607（上海医药）上，老的 Preferred 判给了 600849，
    /// 于是年报下到 reports/600849/、48 条子公司名单也记成了 600849。
    ///
    /// PartnerNameMatcher 那边 v3 的修复**救不了这一半**：subsidiary 档的母公司代码
    /// 直接来自 CompanySubsidiary 表，不经过 Preferred。所以要在**落库这一步**再判一次。
    ///
    /// ⚠ 只在"同全称的当前个股**唯一**"时才改。找不到、或找到多个（A/B 股同全称）都原样返回——
    ///   拿不准就别动数据，这跟"匹配不上就不猜"是同一条。
    /// </summary>
    /// <summary>给用例直接调——判据类的东西必须能单测，不然只能靠跑真库碰运气。</summary>
    internal static string NormalizeParentForTest(
        string code, IReadOnlySet<string>? currentStocks,
        IReadOnlyDictionary<string, List<string>> currentByFullName,
        IReadOnlyDictionary<string, string> fullNames)
        => NormalizeParent(code, currentStocks, currentByFullName, fullNames);

    private static string NormalizeParent(
        string code,
        IReadOnlySet<string>? currentStocks,
        IReadOnlyDictionary<string, List<string>> currentByFullName,
        IReadOnlyDictionary<string, string> fullNames)
    {
        if (currentStocks == null || currentStocks.Contains(code)) return code;
        if (!fullNames.TryGetValue(code, out var full) || string.IsNullOrWhiteSpace(full)) return code;
        return currentByFullName.TryGetValue(full, out var same) && same.Count == 1 ? same[0] : code;
    }

    /// <summary>
    /// 本地该有哪一期年报——**从日历算出来，不问服务器**。
    ///
    /// 这一条是"零请求重解析"的关键：<see cref="SubsidiaryParser.ParserVersion"/> 改版触发全量
    /// 重跑时，只要文件都在就一个请求都不发。要是靠列公告才知道最新一期是哪期，50 家就是
    /// 50 个请求，自愈路径的零请求保证就没了。
    ///
    /// 判据：年报法定披露截止是 4 月 30 日，所以 5 月起"上一个完整年度"的年报应该有了。
    /// </summary>
    internal static DateTime LatestAnnualPeriod(DateTime today)
        => new(today.Month >= 5 ? today.Year - 1 : today.Year - 2, 12, 31);

    /// <summary>
    /// 缺年报的就去下。**查文件在列公告之前**，理由见 <see cref="LatestAnnualPeriod"/>。
    /// </summary>
    private async Task EnsureReportsAsync(CancellationToken ct)
    {
        if (_index == null || _priorityCodes == null) return;

        var want = LatestAnnualPeriod(DateTime.Today);
        var missing = _priorityCodes(_maxDownloads)
            .Where(c => !File.Exists(SinaReportIndex.PathOf(_reportsDir, c, want))
                        || new FileInfo(SinaReportIndex.PathOf(_reportsDir, c, want)).Length
                           < SinaReportIndex.MinPdfBytes)
            .ToList();

        if (missing.Count == 0)
        {
            Report($"优先清单里的 {want:yyyy} 年报本地都有，不发请求。", phase: "下载");
            return;
        }

        Report($"优先清单里有 {missing.Count} 家缺 {want:yyyy} 年报，开始下载"
             + "（只下年报，中报没有完整的合并报表范围）。", 0, missing.Count, "下载");

        int ok = 0, fail = 0;
        for (int i = 0; i < missing.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var code = missing[i];
            try
            {
                // 只列年报那一路，省掉一半列表页请求
                var byDate = await _index.ListCandidatesAsync(
                    code, maxPerKind: 1, kinds: [SinaReportIndex.KindAnnual], ct);
                if (!byDate.TryGetValue(want, out var candidates) || candidates.Count == 0)
                {
                    fail++;
                    Report($"  [{i + 1}/{missing.Count}] {code} 没找到 {want:yyyy} 年报公告");
                    continue;
                }

                // 候选按标题可信度排过序，取最可信那条。
                // ⚠ 这里**不像金融那条链逐个试**：那边的终判是"解析不解析得出指标"，
                //   而这边解析不出子公司名单是常态（实测 32 份里 6 份版式认不出），
                //   拿它当判据会把好文件反复删了重下。
                var dl = await _index.DownloadPdfAsync(candidates[0], _reportsDir, ct);
                if (dl.Ok) { ok++; _downloaded++; Report($"  [{i + 1}/{missing.Count}] {code} {candidates[0].Title}"); }
                else { fail++; Report($"  [{i + 1}/{missing.Count}] ⚠ {code} {dl.Status}：{dl.Message}"); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                fail++;
                Report($"  [{i + 1}/{missing.Count}] ⚠ {code} 下载失败：{ex.Message}");
            }
        }
        Report($"年报下载完成：成功 {ok} 家、失败 {fail} 家。", phase: "下载");
    }

    public override FetchActionId Id => FetchActionId.StepSubsidiaryExtract;

    private int _found, _empty;

    /// <summary>本轮有几份的母公司代码被归一到了正主。</summary>
    private int _renamed;

    /// <summary>本轮实际下载成功的年报份数——决定日志里说不说"联网了"。</summary>
    private int _downloaded;

    protected override async IAsyncEnumerable<IReadOnlyList<ParsedReport>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _repository.EnsureSchema();
        _found = _empty = _downloaded = _renamed = 0;

        // ⚠ 下载**在扫目录之前**：刚下回来的那几份要在这一轮就被解析掉，
        //   否则得等下一轮，用户点一次只完成一半。
        await EnsureReportsAsync(ct);

        if (!Directory.Exists(_reportsDir))
        {
            Report($"报告目录还不存在（{_reportsDir}），本轮无事可做。");
            yield break;
        }

        // 规则版本变了的、和从没处理过的，都要跑
        var done = _repository.GetParsed(SubsidiaryParser.ParserVersion);

        // 母公司全称：用来把"自己"从自己的子公司名单里排掉（全称会出现在表头和正文里）
        var profileNames = _profiles.GetAllNames();
        var fullNames = profileNames
                        .GroupBy(x => x.Code, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.First().FullName,
                                      StringComparer.Ordinal);

        // 同全称的**当前个股**有哪些——归一母公司代码要用，见 NormalizeParent
        var currentStocks = _currentStockCodes?.Invoke();
        var currentByFullName = profileNames
            .Where(x => currentStocks != null && currentStocks.Contains(x.Code)
                        && !string.IsNullOrWhiteSpace(x.FullName))
            .GroupBy(x => x.FullName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Code).Distinct().ToList(),
                          StringComparer.Ordinal);

        // 认金融机构。判据跟【金融监管指标】共用同一处，别各写一份。
        var latestFin = _latestFinancials?.Invoke();
        int skippedFinancial = 0;

        var todo = new List<(string Code, DateTime Date, string Path)>();
        foreach (var dir in Directory.GetDirectories(_reportsDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var code = Path.GetFileName(dir);

            // ⚠ 跳过银行/券商/保险——解析它们的年报是纯粹的无用功，理由见 _latestFinancials 的注释。
            if (latestFin != null && FinancialInstitutionRoster.OwnsPdfOf(code, latestFin))
            {
                skippedFinancial++;
                continue;
            }
            foreach (var pdf in Directory.GetFiles(dir, "*.pdf").OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!DateTime.TryParse(Path.GetFileNameWithoutExtension(pdf), out var d)) continue;
                // ⚠ **只吃年报**。两个 PDF 目录 2026-09-15 合并之后，金融股的中报也躺在这儿，
                //   而半年报附注是未经审计的简版，通常只披露「合并范围变更」、不重列全表——
                //   拿去解析只会白跑一遍再记一条 found_count=0，把"版式认不出"的统计污染掉。
                if (d.Month != 12 || d.Day != 31) continue;
                if (done.Contains((code, d))) continue;
                todo.Add((code, d, pdf));
            }
        }

        // 跳过了多少要说出来——不报的话，目录里躺着一批它压根没碰的东西，而没人知道。
        if (skippedFinancial > 0)
            Report($"  跳过 {skippedFinancial} 个金融机构目录"
                 + "（银行券商在别人年报里是分行形态，走 qualified 档，不需要子公司名单）。");

        if (todo.Count == 0)
        {
            Report($"年报子公司：{done.Count} 份都按 v{SubsidiaryParser.ParserVersion} 规则解析过了，本轮无事可做。");
            yield break;
        }

        // ⚠ 这句以前写死"纯本地计算，不联网"，2026-09-15 加了下载前置之后就不准了——
        //   实测日志里它紧跟在「年报下载完成：成功 30 家」后面，自相矛盾。
        //   现在按这一轮**实际有没有下载**来说，别再写死。
        Report($"年报子公司：{todo.Count} 份待解析"
             + (done.Count > 0 ? $"（另有 {done.Count} 份已按当前规则处理过，跳过）" : "")
             + (_downloaded > 0 ? $"。本轮下了 {_downloaded} 份新年报，" : "。")
             + "解析是纯本地计算。");

        var batch = new List<ParsedReport>(BatchSize);
        int n = 0;
        foreach (var (dirCode, date, path) in todo)
        {
            ct.ThrowIfCancellationRequested();

            // ⚠ **归一必须在 Parse 之前**。目录名可能是废代码（见 NormalizeParent），
            //   而 Parse 会把传进去的 code 填进**每一条** CompanySubsidiary.Code——
            //   落库写的是条目里那个，不是 ParsedReport 外壳上那个。
            //   2026-09-16 栽过一次：只改了外壳，结果 SubsidiaryParseState 记 601607、
            //   CompanySubsidiary 记 600849，两张表对不上。
            var code = NormalizeParent(dirCode, currentStocks, currentByFullName, fullNames);
            if (code != dirCode)
            {
                _renamed++;
                Report($"  {dirCode} 不是当前个股，同全称的正主是 {code}，本份名单记到 {code} 名下");
            }

            // 单份解析失败不该让整轮报废——版式千奇百怪，认不出来是常态。
            // 失败也当成"处理过、0 家"记下来，否则每轮都会重试同一份。
            IReadOnlyList<CompanySubsidiary> items;
            try
            {
                // 全称按**归一后**的代码查——两者全称相同是归一的前提，查哪个都一样，
                // 但用归一后的更不容易在以后放宽判据时出错。
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
                Report($"  {dirCode} {date:yyyy-MM-dd} 解析出错（记为 0 家，不再重试）：{ex.GetType().Name}");
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
        var summary = $"年报子公司："
                    + (_downloaded > 0 ? $"下载 {_downloaded} 份新年报，" : "")
                    + (_renamed > 0 ? $"{_renamed} 份归一到正主代码，" : "")
                    + $"本轮解析 {stats.Items} 份，提出 {_found} 家"
                    + (_empty > 0 ? $"（{_empty} 份版式认不出来）" : "")
                    + $"；库里累计 {rows} 行 / {parents} 家母公司，已处理 {parsed} 份、其中 {empty} 份为空。";
        Report(summary);
        return Task.FromResult<TaskRunResult?>(
            TaskRunResult.Ok(nothingToDo: stats.Items == 0, progress: summary));
    }
}
