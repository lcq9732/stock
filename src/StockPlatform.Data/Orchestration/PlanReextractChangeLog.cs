using System.Globalization;
using System.Text;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 回购公告重抽的变化清单（2026-10-23），路径见 <see cref="FetchPaths.PlanReextractChangesPath"/>。
/// 判据（哪些字段算变了）在 <c>PlanAnnouncementExtractor.ChangedValues</c>，这里只管文件。
///
/// 只追加：一轮重抽 300 条、分好几轮做完，清单要跨轮攒在一起看。文件不存在就先写表头。
/// 带 BOM 的 UTF-8，Excel 直接打开中文不乱码。
/// </summary>
public static class PlanReextractChangeLog
{
    private const string Header = "记录时间,代码,名称,公告日,阶段,字段,旧值,新值,正文来源";

    public static void Append(string path, IReadOnlyList<PlanReextractChange> changes)
    {
        if (changes.Count == 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var sb = new StringBuilder();
        var isNew = !File.Exists(path);
        if (isNew) sb.AppendLine(Header);
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        foreach (var c in changes)
            sb.AppendLine(string.Join(",",
                now, c.Code, Csv(c.Name), c.AnnounceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                c.Stage, c.Field, c.Old, c.New, c.Source));
        // BOM 只在新建时写一次；追加时用不带 BOM 的编码，否则每轮都会在文件中间插一个
        if (isNew) File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        else File.AppendAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>名称里可能有逗号/引号（极少），按 CSV 规矩包起来。</summary>
    private static string Csv(string s)
        => s.IndexOfAny([',', '"', '\n']) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
}
