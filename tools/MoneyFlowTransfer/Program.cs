using System.Globalization;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

// ════════════════════════════════════════════════════════════════════════════
// 分档资金流「搬运」工具（2026-09-12）
//
// 为什么存在：分档资金流的历史只有 push2his.eastmoney.com 的 fflow/daykline 给得出来
// （最近约 120 个交易日），而这台机器所在的公司网把那个域名按域名拦了——TCP/TLS 都通，
// 一发 HTTP 请求就被切、收到 0 字节。所以历史得拿到另一条网络上抓，再搬回来。
//
// 三步：
//   ① 本机导出清单：  dotnet run --project tools/MoneyFlowTransfer -- export-codes
//   ② 外机抓成 CSV：  tools/moneyflow-fetch.ps1（跟 codes.txt 一起拷过去）
//   ③ 本机导入：      dotnet run --project tools/MoneyFlowTransfer -- import out/moneyflow-*.csv
//
// 入库走的是产品代码里那个 SqliteNetInflowDetailRepository.Upsert（INSERT OR REPLACE），
// 所以重复导入安全、跟程序自己抓的数据完全同构。
// ════════════════════════════════════════════════════════════════════════════

const string CsvHeader =
    "code,trade_date,main_net,small_net,mid_net,big_net,super_net," +
    "main_ratio,small_ratio,mid_ratio,big_ratio,super_ratio,close_price,change_rate,fetched_at";

if (args.Length == 0) { Usage(); return 1; }

var rest = args.Skip(1).ToList();
switch (args[0].ToLowerInvariant())
{
    case "export-codes": return ExportCodes(rest);
    case "import": return Import(rest);
    default: Usage(); return 1;
}

void Usage()
{
    Console.WriteLine("""
        分档资金流搬运工具

          export-codes [--db <库文件>] [--out <codes.txt>] [--all]
              导出要抓的代码清单（每行「代码,secid」），拷到外机给 moneyflow-fetch.ps1 用。
              默认只导「窗口内还缺行」的票；--all 导全部个股名册。
              不指定 --out 时写到 publish/moneyflow-transfer/，并把抓取脚本一并复制过去。
              --date 2026-09-09  只导那天「有日K但缺分档资金流」的票（最准，停牌的不会混进来）
              --split 6          分成 6 份，各自一个 part 目录、自带脚本；抓回的 CSV 带份号不重名
              ⚠ secid 在这里算好（用 MarketClassifier），外机脚本不自己猜——
                北交所 920xxx 要用 "0." 前缀，猜错了东财返回空且不报错。

          import <csv> [<csv> ...] [--db <库文件>] [--dry-run]
              把外机抓回来的 CSV 导进库。INSERT OR REPLACE，重复导入安全。
              --dry-run 只解析和校验、不写库。

          公共参数：
            --db   库文件路径，默认 publish/data/local/current.sqlite（相对当前目录）
        """);
}

string ResolveDb(List<string> a)
{
    var db = Opt(a, "--db") ?? Path.Combine("publish", "data", "local", "current.sqlite");
    db = Path.GetFullPath(db);
    if (!File.Exists(db))
        throw new FileNotFoundException($"找不到库文件：{db}（用 --db 指一个）");
    return db;
}

static string? Opt(List<string> a, string name)
{
    int i = a.IndexOf(name);
    if (i < 0 || i + 1 >= a.Count) return null;
    var v = a[i + 1];
    a.RemoveAt(i + 1); a.RemoveAt(i);
    return v;
}

static bool Flag(List<string> a, string name)
{
    int i = a.IndexOf(name);
    if (i < 0) return false;
    a.RemoveAt(i);
    return true;
}

// ── export-codes ───────────────────────────────────────────────────────────
int ExportCodes(List<string> a)
{
    bool all = Flag(a, "--all");
    var dateOpt = Opt(a, "--date");
    int split = int.TryParse(Opt(a, "--split"), out var sp) && sp > 1 ? sp : 1;
    // 默认写进 publish 那边：运行时产物统一放那儿（已 gitignore），别散落在仓库根。
    var outPath = Path.GetFullPath(Opt(a, "--out")
                                   ?? Path.Combine("publish", "moneyflow-transfer", "codes.txt"));
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    var db = ResolveDb(a);

    List<string> todo;
    string note;

    if (dateOpt is { } dateText)
    {
        // ── 按某一天精确排（2026-09-12 加）────────────────────────────────────
        // 判据是用户定的，而且实测成立：**那天有日K才可能有分档资金流**——
        // 库里 09-09 那天「有资金流却没有日K」的票是 0 只。按这个筛比"窗口内缺 N 行"准：
        // 当天停牌的票根本不会有数据，混进清单就是白发请求（那天 5561 只个股里有 13 只这样）。
        if (!DateTime.TryParse(dateText, out var day))
        { Console.Error.WriteLine($"✗ 日期读不了：{dateText}（要 yyyy-MM-dd）"); return 1; }

        todo = CodesMissingOn(db, day);
        note = $"{day:yyyy-MM-dd} 那天有日K、但缺分档资金流的个股";
    }
    else if (all)
    {
        todo = SqliteStockMetaUpsert.GetAll(db).Select(s => s.Code).ToList();
        note = "全部个股名册（--all）";
    }
    else
    {
        var codes = SqliteStockMetaUpsert.GetAll(db).Select(s => s.Code).ToList();
        Console.WriteLine($"本地个股名册：{codes.Count} 只");
        var repo = new SqliteNetInflowDetailRepository(db);
        repo.EnsureSchema();
        Console.WriteLine("正在按本地日K根数算缺口（大库要等一会儿）…");
        var q = MoneyFlowBackfillPlan.Build(db, codes, repo, MoneyFlowBackfillPlan.BackfillGapThreshold);
        if (q.Unavailable is { } why) { Console.Error.WriteLine("✗ " + why); return 1; }
        todo = q.Todo;
        note = $"窗口 {q.From:yyyy-MM-dd}~{q.To:yyyy-MM-dd}（{MoneyFlowBackfillPlan.WindowTradingDays} 个交易日）"
             + $"内缺 {MoneyFlowBackfillPlan.BackfillGapThreshold} 行以上；其中 {q.Never} 只窗口内一行都没有";
    }

    if (todo.Count == 0)
    {
        Console.WriteLine("该有的都有了，不用抓——没生成清单。");
        return 0;
    }

    var script = Path.GetFullPath(Path.Combine("tools", "moneyflow-fetch.ps1"));
    // 网页版一并发过去（2026-09-13）：ps1 只能在 Windows 上跑，而这活要的是"多个不同出口"，
    // Mac 也该能当一个。两个版本抓法、节奏、CSV 格式完全一致，用哪个看手边是什么机器。
    var webPage = Path.GetFullPath(Path.Combine("tools", "moneyflow-fetch.html"));
    var baseDir = Path.GetDirectoryName(outPath)!;

    if (split == 1)
    {
        WriteList(outPath, todo, note, part: null, total: 1);
        Console.WriteLine($"✓ 已写出 {todo.Count} 只 → {outPath}");
        Console.WriteLine($"  {note}");
        CopyScript(script, baseDir);
        CopyScript(webPage, baseDir);
        Console.WriteLine($"  把整个 {baseDir} 拷到能访问 push2his 的机器上，在那边跑 moneyflow-fetch.ps1。");
        return 0;
    }

    // ── 分成 N 份：**程序只放一份，数据分成 N 个清单文件**（2026-09-13 按用户要求改）──
    //
    // 原来是每份一个目录、各自复制一套脚本，六个目录里六份一模一样的程序。改成平铺之后
    // 一个目录拷到哪台机器都行，在那边挑 codes-p<N>.txt 上传就是了——脚本和网页都会从清单
    // 头部的「# part: N/总数」认出自己在跑第几份，抓回的 CSV 文件名带份号，拷到一处不重名。
    //
    // 轮流发牌而不是切段：代码是排好序的，切段会让某一份全是 60xxxx、另一份全是 000xxx，
    // 万一某个市场段整体出问题（限流、secid 规则变了），受影响的是一整份而不是均摊。
    var parts = Enumerable.Range(0, split).Select(_ => new List<string>()).ToList();
    for (int i = 0; i < todo.Count; i++) parts[i % split].Add(todo[i]);

    Console.WriteLine($"✓ {todo.Count} 只分成 {split} 份（{note}）：");
    for (int i = 0; i < split; i++)
    {
        var f = Path.Combine(baseDir, $"codes-p{i + 1}.txt");
        WriteList(f, parts[i], note, part: i + 1, total: split);
        Console.WriteLine($"   codes-p{i + 1}.txt: {parts[i].Count} 只");
    }
    CopyScript(script, baseDir);
    CopyScript(webPage, baseDir);
    Console.WriteLine($"  程序各一份放在 {baseDir}：");
    Console.WriteLine("    · moneyflow-fetch.ps1   Windows 用，跑之前把要抓的那份改名成 codes.txt，");
    Console.WriteLine("      或者 -Codes codes-p3.txt 指给它");
    Console.WriteLine("    · moneyflow-fetch.html  哪个系统都行，页面上直接选 codes-p<N>.txt");
    Console.WriteLine("  两边都会从清单头部认出份号，抓回的 CSV 自带 -p<份号>，拷到一处不会重名。");
    return 0;
}

void WriteList(string path, List<string> codes, string note, int? part, int total)
{
    using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
    w.WriteLine($"# 分档资金流待抓清单 —— {DateTime.Now:yyyy-MM-dd HH:mm}");
    w.WriteLine($"# {note}");
    // 这一行抓取脚本会读：CSV 文件名带上份号，六台机器的产物拷到一处才不会互相覆盖
    if (part is { } p) w.WriteLine($"# part: {p}/{total}");
    w.WriteLine($"# 共 {codes.Count} 只；格式：代码,secid");
    foreach (var code in codes)
        w.WriteLine($"{code},{MarketClassifier.EastMoneySecIdPrefix(code)}{code}");
}

static void CopyScript(string script, string dir)
{
    if (!File.Exists(script)) return;
    var copy = Path.Combine(dir, Path.GetFileName(script));
    if (!string.Equals(copy, script, StringComparison.OrdinalIgnoreCase))
        File.Copy(script, copy, overwrite: true);
}

static void CopyIfExists(string src, string dir)
{
    if (File.Exists(src)) File.Copy(src, Path.Combine(dir, Path.GetFileName(src)), overwrite: true);
}

/// <summary>
/// 某一天「有日K、但没有分档资金流」的个股。
///
/// 为什么用日K当期望：那天没开盘交易（停牌/未上市）就不会有资金流，问了也是空——
/// 实测 09-09 那天「有资金流却没日K」的票是 0 只，反过来成立。
/// ⚠ 只算 type='stock'（含老库 type IS NULL）：指数/ETF/板块/退市股不在这条通道里。
/// </summary>
static List<string> CodesMissingOn(string db, DateTime day)
{
    var d = day.ToString("yyyy-MM-dd");
    using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT b.code
          FROM Bar b
          JOIN StockMeta m ON m.code = b.code AND (m.type = 'stock' OR m.type IS NULL)
         WHERE b.granularity = 'day' AND b.period_start = $d || ' 00:00:00'
           AND NOT EXISTS (SELECT 1 FROM NetInflowDetail n
                            WHERE n.code = b.code AND n.trade_date = $d)
         ORDER BY b.code;
        """;
    var p = cmd.CreateParameter(); p.ParameterName = "$d"; p.Value = d; cmd.Parameters.Add(p);
    var list = new List<string>();
    using var r = cmd.ExecuteReader();
    while (r.Read()) list.Add(r.GetString(0));
    return list;
}

// ── import ─────────────────────────────────────────────────────────────────
int Import(List<string> a)
{
    bool dry = Flag(a, "--dry-run");
    var db = ResolveDb(a);
    var files = a.Where(x => !x.StartsWith("--")).SelectMany(Expand).Distinct().ToList();
    if (files.Count == 0) { Console.Error.WriteLine("✗ 没指定 CSV 文件"); return 1; }

    var repo = new SqliteNetInflowDetailRepository(db);
    repo.EnsureSchema();
    int beforeRows = repo.Count(), beforeCodes = repo.CountCodes();
    Console.WriteLine($"库：{db}");
    Console.WriteLine($"导入前：{beforeCodes} 只 / {beforeRows} 行");

    int totalParsed = 0, totalBad = 0, totalCodes = 0, totalWritten = 0;
    DateTime? minDate = null, maxDate = null;

    foreach (var file in files)
    {
        Console.WriteLine($"── {file}");
        int lineNo = 0, parsed = 0, bad = 0, codesInFile = 0, written = 0;
        string? curCode = null;
        var buf = new List<NetInflowDetail>(140);

        void FlushBuf()
        {
            if (buf.Count == 0) return;
            if (!dry) written += repo.Upsert(buf);
            else written += buf.Count;
            codesInFile++;
            buf.Clear();
        }

        foreach (var line in File.ReadLines(file))
        {
            lineNo++;
            if (line.Length == 0) continue;
            if (lineNo == 1)
            {
                // 表头必须一字不差：列顺序错位是这类搬运最容易犯又最难发现的错——
                // 净额和净占比全是数字，串了列谁也看不出来，只有回测结果会莫名其妙。
                var head = line.TrimStart('﻿').Trim();
                if (!string.Equals(head, CsvHeader, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("✗ 表头对不上，拒绝导入。");
                    Console.Error.WriteLine("  期望：" + CsvHeader);
                    Console.Error.WriteLine("  实际：" + head);
                    return 1;
                }
                continue;
            }

            var p = line.Split(',');
            if (p.Length < 15 || !DateTime.TryParseExact(p[1], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                                         DateTimeStyles.None, out var day))
            {
                bad++;
                if (bad <= 5) Console.Error.WriteLine($"  ⚠ 第 {lineNo} 行读不了，跳过：{Trim80(line)}");
                continue;
            }

            var code = p[0].Trim();
            if (curCode != null && code != curCode) FlushBuf();
            curCode = code;

            buf.Add(new NetInflowDetail
            {
                Code = code,
                TradeDate = day,
                MainNet = D(p[2]), SmallNet = D(p[3]), MidNet = D(p[4]), BigNet = D(p[5]), SuperNet = D(p[6]),
                MainRatio = D(p[7]), SmallRatio = D(p[8]), MidRatio = D(p[9]), BigRatio = D(p[10]), SuperRatio = D(p[11]),
                ClosePrice = D(p[12]), ChangeRate = D(p[13]),
                FetchedAt = DateTime.TryParseExact(p[14].Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                                                   DateTimeStyles.None, out var f) ? f : DateTime.Now,
            });
            parsed++;
            if (minDate == null || day < minDate) minDate = day;
            if (maxDate == null || day > maxDate) maxDate = day;

            // 一只票约 120 行，攒满一只就写一次——不把 70 万行全堆进内存
            if (buf.Count >= 500) { var c = buf.Count; if (!dry) written += repo.Upsert(buf); else written += c; buf.Clear(); }
        }
        FlushBuf();

        Console.WriteLine($"   {parsed} 行、{codesInFile} 只" + (bad > 0 ? $"、{bad} 行读不了" : "")
                        + (dry ? "（--dry-run，没写库）" : $"，写入 {written} 行"));
        totalParsed += parsed; totalBad += bad; totalCodes += codesInFile; totalWritten += written;
    }

    Console.WriteLine();
    Console.WriteLine($"合计：解析 {totalParsed} 行 / {totalCodes} 只"
                    + (totalBad > 0 ? $"，{totalBad} 行读不了" : "")
                    + $"，日期 {minDate:yyyy-MM-dd}~{maxDate:yyyy-MM-dd}");
    if (dry) { Console.WriteLine("--dry-run：一行都没写库。"); return 0; }

    int afterRows = repo.Count(), afterCodes = repo.CountCodes();
    Console.WriteLine($"导入后：{afterCodes} 只 / {afterRows} 行"
                    + $"（净增 {afterCodes - beforeCodes} 只 / {afterRows - beforeRows} 行；"
                    + "差额是已有行被覆盖，正常）");
    return 0;
}

static IEnumerable<string> Expand(string pattern)
{
    if (File.Exists(pattern)) return [Path.GetFullPath(pattern)];
    var dir = Path.GetDirectoryName(pattern);
    if (string.IsNullOrEmpty(dir)) dir = ".";
    var name = Path.GetFileName(pattern);
    if (!Directory.Exists(dir)) return [];
    return Directory.GetFiles(dir, name).OrderBy(x => x).Select(Path.GetFullPath);
}

static double? D(string s) =>
    double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

static string Trim80(string s) => s.Length <= 80 ? s : s[..80] + "…";
