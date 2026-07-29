using System.Diagnostics;
using System.Text;
using StockPlatform.FactorLab.Core;
using StockPlatform.FactorLab.Factors;

namespace StockPlatform.FactorLab;

/// <summary>用法：FactorLab [数据库路径] [输出目录]。缺省时向上查找 publish/data/local/total.sqlite。</summary>
public static class Program
{
    /// <summary>因子注册表（M2：27个）。新因子在此登记即可进入评估和因子手册。</summary>
    static List<IFactor> BuildFactors() =>
    [
        // 趋势
        new Momentum(20),
        new Momentum(60, skip: 5),
        new Momentum(120),
        new NewHighDistance60(),
        // 反转
        new Reversal(5),   // 阳性对照
        new Reversal(10),
        new Reversal(20),
        new MaDeviation(20),
        new MaDeviation(60),
        // 波动
        new LowVolatility20(),
        new LowAmplitude20(),
        new LowMax20(),
        new AmplitudeShrink(),
        // 量价
        new LowTurnover20(),
        new VolumeShrink(5, 60),
        new VolumeShrink(20, 120),
        new PriceVolumeDiverge(20),
        new PriceVolumeDiverge(60),
        new StableTurnover20(),
        new Amihud20(),
        new OvernightReversal20(),
        new IntradayMomentum20(),
        new LowUpperShadow20(),
        // 规模
        new SmallSize(),
        // 资金
        new MarginChange20(),  // 阴性对照
        new LhbCold20(),
        // 筹码
        new HolderShrink(),    // 阴性对照
    ];

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? dbPath = args.Length > 0 ? args[0] : ProbeDb();
        if (dbPath is null || !File.Exists(dbPath))
        {
            Console.Error.WriteLine("找不到数据库。用法：FactorLab <total.sqlite路径> [输出目录]");
            return 1;
        }
        string outDir = args.Length > 1 ? args[1] : "factorlab-output";

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
}
