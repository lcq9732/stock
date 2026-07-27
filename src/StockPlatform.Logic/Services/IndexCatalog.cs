using System.Reflection;
using System.Text;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 指数全集清单（732 个，2026-07-16 从聚宽 index_stock_info 一次性导出）——供"拉取指数成分/权重"
/// 遍历要抓成分股的指数。作为嵌入资源（<c>Services/IndexCatalog.csv</c>，每行 <c>code,name</c>）随程序
/// 打包：聚宽网页在用户真实环境可达性不确定，不在运行时依赖它；要更新清单只需替换这个 csv 重新编译。
///
/// 跟 <see cref="MarketIndexCatalog"/> 互不影响：那 6 个大盘指数是"带前缀符号(sh000001)存进 Bar 表、
/// 抓日K"用的；这里是 6 位裸指数代码（000300），只用于向新浪/中证拉成分股名单与权重（存进
/// IndexCons/IndexWeight 表），不进 Bar、不进选股全集。
/// </summary>
public static class IndexCatalog
{
    /// <summary>全部要抓成分的指数（6 位代码 + 名称）。首次访问时从嵌入 csv 懒加载。</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> All = Load();

    private static List<(string Code, string Name)> Load()
    {
        var result = new List<(string, string)>();
        var asm = typeof(IndexCatalog).Assembly;
        var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("IndexCatalog.csv"));
        if (resName == null) return result;
        using var stream = asm.GetManifestResourceStream(resName);
        if (stream == null) return result;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int comma = line.IndexOf(',');
            if (comma <= 0) continue;
            var code = line[..comma].Trim();
            var name = line[(comma + 1)..].Trim();   // 名称即使含逗号也整段保留（第一个逗号后全是名称）
            if (code.Length == 6 && code.All(char.IsDigit))
                result.Add((code, name));
        }
        return result;
    }
}
