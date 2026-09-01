using System.Globalization;
using System.Text;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 「待手工回填清单」（2026-08-29 新增）——把解析不出来的指标变成一份**能照着干活的表**，
/// 而不是日志里一句"某某失败"。
///
/// ════ 为什么需要 ════
/// PDF 解析不可能 100%。实测 271 份里有十来份卡在 PdfPig/pdftotext 都读不动的字体上
/// （中国人保四期全军覆没），这些机构的体检表会一直显示"待接入"。与其让人对着"待接入"
/// 三个字发呆，不如直接告诉他：**这家、这一期、缺这几个数、翻年报的哪一章能找到**，
/// 填完再导回来。
///
/// ════ 闭环怎么走 ════
///   ① 抓取跑完自动生成 CSV（本类的 <see cref="Generate"/>）
///   ② 用 Excel 打开，只填「填这里」一列
///   ③ Fetcher 点【导入手工数据】（<see cref="Import"/>），写回库、来源标 'manual'
///   ④ 之后再怎么重解析都不会覆盖它——见 SqliteBankRegulatoryRepository.Upsert 的 ON CONFLICT
///
/// CSV 带 UTF-8 BOM，Excel 双击打开不乱码。
/// </summary>
public static class ManualFillWorklist
{
    public const string FileName = "待手工回填清单.csv";

    private static readonly string[] Header =
    [
        // 代码和报告期带 # 前缀，是为了不让 Excel 把 000001 变成 1、把日期改格式；
        // 导入时自动去掉，不用管它。表头直说，免得看着奇怪。
        "机构代码(#号是防Excel吃零的标记)", "机构类型", "报告期", "指标key", "指标名称",
        "去哪里找", "翻到第几页", "打开PDF",
        "OCR识别值(机器认的,可能有错)", "填这里(OCR对就留空;OCR错或没有OCR值就填)",
    ];

    /// <summary>「OCR识别值」列的下标。旧版清单没有这一列，导入时按列数分辨，见 <see cref="Import"/>。</summary>
    private const int ColOcr = 8;
    /// <summary>「填这里」列的下标（新版）。</summary>
    private const int ColFill = 9;
    /// <summary>旧版清单（9 列）里「填这里」的下标。</summary>
    private const int ColFillLegacy = 8;

    /// <summary>
    /// 扫一遍所有该有监管指标的机构，把缺的列成清单。返回写出的文件路径；无缺失则返回 null。
    /// </summary>
    /// <param name="targets">机构代码 → 类型（银行/券商/保险）。</param>
    public static string? Generate(string dbPath, string reportsDir, string outputDir,
        IReadOnlyList<(string Code, FinancialInstitutionKind Kind)> targets,
        Action<string>? progress = null)
    {
        var repo = new SqliteBankRegulatoryRepository(dbPath);
        var rows = new List<string[]>();
        int scanned = 0;

        foreach (var (code, kind) in targets)
        {
            // 这一步要把有缺失的 PDF 逐份读出文本来判断"披没披露"，几百份下来要几分钟，
            // 不报进度会像卡住。
            if (++scanned % 20 == 0)
                progress?.Invoke($"  生成回填清单… 已检查 {scanned}/{targets.Count} 家");

            var expected = RegulatoryMetricCatalog.ExpectedFor(kind);
            if (expected.Length == 0) continue;

            var have = repo.GetByCode(code);
            // 只针对**本地已经下载了 PDF**的那些报告期——没下过的谈不上"解析失败"，
            // 那是还没抓，不该混进手工清单。
            var dir = Path.Combine(reportsDir, code);
            if (!Directory.Exists(dir)) continue;

            foreach (var pdf in Directory.GetFiles(dir, "*.pdf").OrderByDescending(f => f))
            {
                if (!DateTime.TryParse(Path.GetFileNameWithoutExtension(pdf), out var date)) continue;

                // 「翻到第几页」：拿**同一份报告里已经解析成功**的指标的页码当路标。
                // 监管指标基本都挤在同一章（招行中报是第 6~9 页），所以哪怕缺的这个没解析出来，
                // 邻居的页码也足够把人直接带到那一片，不用从头翻三百页。
                var pagesSamePeriod = have
                    .Where(m => m.ReportDate == date && m.SourcePage > 0)
                    .Select(m => m.SourcePage)
                    .ToList();
                string pageHint = pagesSamePeriod.Count > 0
                    ? (pagesSamePeriod.Min() == pagesSamePeriod.Max()
                        ? $"第 {pagesSamePeriod.Min()} 页"
                        : $"第 {pagesSamePeriod.Min()}~{pagesSamePeriod.Max()} 页")
                    : "整份都没解析出来，从目录找";

                // ── 要列进清单的有两类 ────────────────────────────────────────────────
                // ① **缺失**：这一期这个指标压根没解析出来 → OCR 列空着，等人填。
                // ② **待核对**：值是 OCR 认出来的（source='ocr'）→ OCR 列填上机器认的数，
                //    人对着 PDF 看一眼：认对了就留空不填，认错了才填正确值。见 MetricSources。
                //    OCR 会把 "94.5%，" 认成 "94.59%,"，所以每个 OCR 值都得过一遍人眼。
                var missingKeys = expected
                    .Where(k => !have.Any(m => m.MetricKey == k && m.ReportDate == date))
                    .ToList();
                var ocrRows = have
                    .Where(m => m.ReportDate == date && m.Source == MetricSources.Ocr
                                && expected.Contains(m.MetricKey))
                    .ToList();
                if (missingKeys.Count == 0 && ocrRows.Count == 0) continue;

                // 只对**缺失**的那些判断"这份报告到底披没披露"——否则会让人去翻一个根本不存在的
                // 数字。两种真实情况：
                //   · 公司本来就没有这项：纯寿险公司（中国人寿、新华保险）没有"综合成本率"，
                //     那是财险指标；不少银行也不披露"单一最大客户贷款比例"。
                //   · 这一册里没有：瑞丰银行 2024 年报下到的是财务报表分册，"核心一级资本充足率"
                //     只在附注一句话里被提了个名字（"…均满足《商业银行资本管理办法》"），数在
                //     另一份《资本管理第三支柱信息披露报告》里。
                // 所以判据是「**标签后面得有数**」而不是「提到过这个名字」，见 DisclosedKeys。
                // 取不到文本、或数字被转曲的（返回 null）保守处理——照列，因为无从判断。
                // 待核对的 OCR 行不做这个过滤：它已经有值了，"披没披露"没有疑问。
                if (missingKeys.Count > 0)
                {
                    var disclosed = BankReportParser.DisclosedKeys(pdf);
                    if (disclosed != null)
                        missingKeys = missingKeys
                            // 没登记标签的一律保留，别因为映射不全就把该填的漏掉
                            .Where(k => disclosed.Contains(k) || BankReportParser.LabelsOf(k).Length == 0)
                            .ToList();
                }
                if (missingKeys.Count == 0 && ocrRows.Count == 0) continue;

                var fullPath = Path.GetFullPath(pdf);
                // Excel/WPS 会把 = 开头的单元格当公式，HYPERLINK 就变成可点的链接，
                // 点一下直接用系统默认阅读器打开这份 PDF——否则得手工去复制粘贴路径，太折腾。
                var link = $"=HYPERLINK(\"{fullPath}\",\"打开 {code} {date:yyyy-MM-dd}\")";

                string kindName = kind switch
                {
                    FinancialInstitutionKind.Bank => "银行",
                    FinancialInstitutionKind.Broker => "券商",
                    _ => "保险",
                };

                // ⚠ 代码和日期用 # 前缀强制 Excel 当**文本**处理。直接写 000001，Excel 会当成
                // 数字、把前导零吃掉变成 1，深市代码（000/002/003/300 开头）全军覆没，存盘后
                // 代码就废了。日期同理：不加保护会被转成 2026/6/30 甚至日期序列号。
                // 导入侧还有一层还原兜底（见 Import），两头都堵。
                void AddRow(string key, string ocrValue, string pageText) => rows.Add([
                    ExcelText(code), kindName, ExcelText(date.ToString("yyyy-MM-dd")),
                    key, RegulatoryMetricCatalog.NameOf(key), RegulatoryMetricCatalog.WhereOf(key),
                    pageText, link,
                    ocrValue,
                    "",   // 填这里
                ]);

                foreach (var key in missingKeys) AddRow(key, "", pageHint);

                // 待核对的 OCR 行：页码用它**自己**那一页，人点开 PDF 直接翻到那页对一眼就行。
                foreach (var m in ocrRows)
                    AddRow(m.MetricKey,
                           m.Value.ToString("0.####", CultureInfo.InvariantCulture),
                           m.SourcePage > 0 ? $"第 {m.SourcePage} 页" : pageHint);
            }
        }

        if (rows.Count == 0) return null;

        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, FileName);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Header.Select(Escape)));
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(Escape)));
        // UTF-8 **带 BOM**：Excel 靠它认出编码，否则中文全是乱码
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>
    /// 把填好的 CSV 导回库。返回 (写入条数, 确认条数, 跳过条数, 错误信息)。
    ///
    /// 每一行按「OCR识别值」和「填这里」两列的组合决定怎么处理：
    ///   填这里 有数              → 以人填的为准，写库标 'manual'（不管 OCR 有没有值）
    ///   填这里 空 + OCR 有值     → **人看过、认可 OCR**，把那条 ocr 升级成 'ocr_confirmed'
    ///   填这里 空 + OCR 也空     → 还没填，跳过（分批填是常态，不能当成"确认"）
    /// 第二条是这份清单的核心约定：**留空 = OCR 是对的**，让人只需要在错的地方动手。
    /// </summary>
    public static (int Imported, int Confirmed, int Skipped, List<string> Errors) Import(
        string dbPath, string csvPath)
    {
        var errors = new List<string>();
        if (!File.Exists(csvPath)) return (0, 0, 0, ["找不到文件：" + csvPath]);

        var lines = File.ReadAllLines(csvPath, Encoding.UTF8);
        if (lines.Length < 2) return (0, 0, 0, ["文件是空的（只有表头或什么都没有）"]);

        var metrics = new List<BankRegulatoryMetric>();
        var confirms = new List<(string Code, DateTime ReportDate, string MetricKey)>();
        int skipped = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            var f = ParseCsvLine(lines[i]);
            // 旧版清单只有 9 列、没有「OCR识别值」，最后一列就是人填的值。手上可能还有填了
            // 一半的旧文件，按列数分辨着读，别让它们导不进来。
            bool legacy = f.Count == 9;
            if (f.Count < 9) { skipped++; continue; }

            var fillRaw = (legacy ? f[ColFillLegacy] : f[ColFill]).Trim().TrimEnd('%');
            var ocrRaw = legacy ? "" : f[ColOcr].Trim().TrimEnd('%');

            // 报告期先剥掉可能残留的 ="…" 外壳；Excel 存回来的格式可能是 2026/6/30，
            // TryParse 能吃下这些变体。
            var dateRaw = Unwrap(f[2]);
            if (!DateTime.TryParse(dateRaw, out var date))
            {
                if (fillRaw.Length == 0 && ocrRaw.Length == 0) { skipped++; continue; }
                errors.Add($"第 {i + 1} 行报告期格式不对：{f[2]}");
                continue;
            }

            // ⚠ 代码列必须还原：Excel 把 000001 存成了 1、002958 存成了 2958。
            // 不还原的话导入的数据会挂到不存在的代码上，等于白填。
            var code = CleanCode(Unwrap(f[0]));
            if (code.All(c => c == '0'))
            {
                if (fillRaw.Length == 0 && ocrRaw.Length == 0) { skipped++; continue; }
                errors.Add($"第 {i + 1} 行的机构代码解析不出来：{f[0]}");
                continue;
            }

            if (fillRaw.Length == 0)
            {
                // 留空 + OCR 有值 = 人核对过、认可机器认的那个数
                if (ocrRaw.Length > 0) confirms.Add((code, date, f[3].Trim()));
                else skipped++;
                continue;
            }

            if (!double.TryParse(fillRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
            {
                errors.Add($"第 {i + 1} 行「{f[0]} {f[2]} {f[4]}」的值不是数字：{(legacy ? f[ColFillLegacy] : f[ColFill])}");
                continue;
            }

            metrics.Add(new BankRegulatoryMetric
            {
                Code = code, ReportDate = date, MetricKey = f[3].Trim(),
                // 资本充足率类统一按权重法存（横向可比的口径），其余为空
                Basis = f[3].Trim() is BankRegulatoryKeys.CoreTier1Car or BankRegulatoryKeys.Tier1Car
                                     or BankRegulatoryKeys.TotalCar
                        ? BankRegulatoryKeys.BasisWeighted : "",
                Value = val,
                SourcePage = 0,
                Source = MetricSources.Manual,
            });
        }

        int confirmed = 0;
        if (metrics.Count > 0 || confirms.Count > 0)
        {
            var repo = new SqliteBankRegulatoryRepository(dbPath);
            if (metrics.Count > 0) repo.UpsertManual(metrics);
            // 先写人填的、再标确认：同一行不可能两者都命中，顺序其实无关，这样读着顺
            if (confirms.Count > 0) confirmed = repo.MarkOcrConfirmed(confirms);
        }
        return (metrics.Count, confirmed, skipped, errors);
    }

    /// <summary>
    /// 防止 Excel 把内容当数字处理的前缀标记。
    ///
    /// ════ 为什么用前缀而不是 ="…" 公式 ════
    /// 两种写法打开时都能正确显示，但**存盘行为完全不同**：
    ///   ="000001"  Excel 存回的是**求值后的结果** 000001 → 下次再打开又被当数字、又掉零
    ///   #000001    这是数据本身，Excel 不会动它 → 反复打开保存都稳定
    /// 这份清单是拿来分几次慢慢填的，填一半存盘、明天接着填是常态，公式那套第二次打开就烂了。
    /// 前缀还有个附带好处：<see cref="CleanCode"/> 本来就是"只留数字"，天然吃得下它，
    /// 导入侧一行都不用改。
    /// </summary>
    private const char TextPrefix = '#';

    /// <summary>加前缀，让 Excel 老老实实当文本——否则 000001 会变成 1、日期会被重新格式化。</summary>
    private static string ExcelText(string s) => TextPrefix + s;

    /// <summary>
    /// 把代码列还原成规范的 6 位。要能吃下这几种形态：
    ///   000001（原样）、1（Excel 吃掉前导零）、="000001"（公式没被求值就存回来了）、
    ///   000001.0（被当成数字又格式化了一遍）
    /// 做法是只留数字再左补零——A股代码固定 6 位，这个还原是确定的。
    /// </summary>
    private static string CleanCode(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        // "000001.0" 这种会多出个 0，取前 6 位；不足 6 位左补零
        if (digits.Length > 6) digits = digits[..6];
        return digits.PadLeft(6, '0');
    }

    /// <summary>
    /// 剥掉防 Excel 转换的外壳，取里面的原文。要盖住这几种形态：
    ///   #2026-06-30    当前用的前缀写法
    ///   ="2026-06-30"  早期版本的公式写法（旧清单可能还在手上）
    ///   =2026-06-30    上面那种被 CSV 解析器吃掉引号后的样子
    ///   2026-06-30     Excel 求值后存回来的裸值
    /// 老写法一并留着兼容，免得手上填了一半的旧清单导不进来。
    /// </summary>
    private static string Unwrap(string raw)
    {
        var s = raw.Trim();
        if (s.Length > 0 && s[0] == TextPrefix) s = s[1..].Trim();
        if (s.StartsWith('=')) s = s[1..].Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return s.Trim();
    }

    private static string Escape(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    /// <summary>够用的 CSV 行解析：支持双引号包裹和 "" 转义。</summary>
    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }
}
