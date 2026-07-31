using System.Diagnostics;
using System.Text;
using StockPlatform.FactorLab.Core;
using StockPlatform.FactorLab.Factors;

namespace StockPlatform.FactorLab;

/// <summary>用法：FactorLab [数据库路径] [输出目录]。缺省时向上查找 publish/data/local/total.sqlite。</summary>
public static class Program
{
    /// <summary>因子清单在 <see cref="FactorRegistry"/> 登记（与 Analyzer 因子Tab共用）。</summary>
    static List<IFactor> BuildFactors() => FactorRegistry.BuildAll();

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? dbPath = args.Length > 0 ? args[0] : ProbeDb();
        if (dbPath is null || !File.Exists(dbPath))
        {
            Console.Error.WriteLine("找不到数据库。用法：FactorLab <total.sqlite路径> [输出目录]");
            return 1;
        }
        // 运行时结果统一放 publish 目录下（跟数据库一样，见 doc/data-platform-design.md）。默认
        // publish/factorlab-output；命令行传的输出目录若是相对路径也落到 publish 下（如归档快照
        // "factorlab-output-日期-说明" → publish/factorlab-output-日期-说明），绝对路径则原样用。
        var publishDir = ProbePublishDir();
        string outDir = args.Length > 1
            ? (Path.IsPathRooted(args[1]) ? args[1] : Path.Combine(publishDir ?? ".", args[1]))
            : Path.Combine(publishDir ?? ".", "factorlab-output");

        var sw = Stopwatch.StartNew();
        Console.WriteLine($"数据库：{dbPath}");
        var md = MarketData.Load(dbPath, msg => Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,6:0.0}s] {msg}"));

        Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,6:0.0}s] 构建共享评估数据（每期收益/可交易掩码）…");
        var shared = Evaluator.BuildShared(md);
        Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,6:0.0}s] 共 {shared.Periods.Count} 个调仓期");

        var results = new List<FactorResult>();
        foreach (var f in BuildFactors())
        {
            results.Add(Evaluator.Evaluate(f, md, shared));
            Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,6:0.0}s] 因子完成：{f.Name}");
        }

        // 去重（基于单因子相关性）→ 合成 → 合成因子走同一评估管线
        Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,6:0.0}s] 单因子相关性与去重…");
        var baseCorr = Evaluator.CorrelationMatrix(results, shared);
        var dedup = Evaluator.Deduplicate(results, baseCorr);

        var compEq = CompositeFactor.Build("合成-等权", icirWeighted: false, results, dedup, md, shared);
        var compIcir = CompositeFactor.Build("合成-ICIR加权", icirWeighted: true, results, dedup, md, shared);
        results.Add(Evaluator.Evaluate(compEq, md, shared));
        var compIcirResult = Evaluator.Evaluate(compIcir, md, shared);
        results.Add(compIcirResult);
        Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,6:0.0}s] 合成因子完成（成分 {compIcir.Components.Count} 个）");

        Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,6:0.0}s] 全量相关性矩阵…");
        var corr = Evaluator.CorrelationMatrix(results, shared);

        // 组合回测：ICIR加权合成为主策略，叠加 MA60 指数择时
        var portfolios = new List<PortfolioResult>
        {
            Portfolio.Run(results[^2], md, shared), // 合成-等权
            Portfolio.Run(compIcirResult, md, shared),
        };

        Report.WriteAll(outDir, md, shared, results, corr, dedup, portfolios, (compIcir, compIcirResult));
        Console.WriteLine($"\n[{sw.Elapsed.TotalSeconds:0.0}s] 完成。明细与因子手册见：{Path.GetFullPath(outDir)}");
        return 0;
    }

    /// <summary>从当前目录和程序目录逐级向上找 publish/data/local/total.sqlite。</summary>
    static string? ProbeDb()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "publish", "data", "local", "total.sqlite");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>从当前目录和程序目录逐级向上找 publish 目录——运行时结果数据（数据库、因子实验输出等）
    /// 都放这里，跟 ProbeDb 找 total.sqlite 同一套向上查找。找不到返回 null（退回当前目录）。</summary>
    static string? ProbePublishDir()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "publish");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
