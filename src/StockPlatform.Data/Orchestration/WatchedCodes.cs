using System.Text.Json;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// "用户真正关注的票"——自选股 + 底仓 + 主动仓（2026-09-18 从
/// <see cref="FinancialFetchPlanner"/> 提出来共用）。抓取项拿它做优先级排序：
/// 被 <c>Deadline</c> 截断时，先保住真会被拿来分析的那几十只。
///
/// 提出来的理由：【拉取股东数据】迁成新任务时也要这份名单，而"关注的票从哪几个文件读、
/// 键名怎么拼"抄第二遍就会出现两处不一致——哪天自选存储格式变了，只改一处，
/// 另一处静默退化成纯代码序、谁也不会发现。
///
/// 为什么 Fetcher 能读到 Analyzer 的状态文件：两个 exe 装在同一个目录，
/// <see cref="FetchPaths.BaseDir"/> 和 AnalyzerPaths.BaseDir 算出来是同一个 data 文件夹。
/// 这里只读 code 字段、不反序列化成完整模型（那些模型在 Analyzer 项目里，Data 层引用不到）。
///
/// 文件不存在（Fetcher 单独部署、或用户还没建过自选）就返回空集合，排序退化成纯代码序。
/// </summary>
internal static class WatchedCodes
{
    private static readonly string[] Files = ["watchlist.json", "core-positions.json"];

    public static HashSet<string> Read(FetchPaths paths)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            var path = Path.Combine(paths.BaseDir, file);
            try
            {
                if (!File.Exists(path)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var el in doc.RootElement.EnumerateArray())
                    if (el.TryGetProperty("Code", out var c) || el.TryGetProperty("code", out c))
                    {
                        var code = c.GetString();
                        if (!string.IsNullOrWhiteSpace(code)) result.Add(code);
                    }
            }
            catch (Exception)
            {
                // 读不了/格式坏了不影响抓取，只是失去优先级排序
            }
        }
        return result;
    }
}
