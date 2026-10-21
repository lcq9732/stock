using System.Text.Json;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 「待手工回填清单」旁边那个 <c>待手工回填清单.state.json</c> 的读写（2026-10-21）。
/// 判据在 <see cref="ManualFillImportRule"/>，这里只管文件。
///
/// 放在清单旁边、不进 manifest：两者同生同灭——人把清单删了，记录留着也只会让判据先撞上"清单不存在"。
/// </summary>
public static class ManualFillStateStore
{
    public const string FileName = "待手工回填清单.state.json";

    public static string PathIn(string reportsDir) => Path.Combine(reportsDir, FileName);

    /// <summary>读记录；没有或读不了都返回 null（判据会当成"没有生成记录"，往保守的方向走）。</summary>
    public static ManualFillState? Load(string reportsDir)
    {
        try
        {
            var p = PathIn(reportsDir);
            return File.Exists(p) ? JsonSerializer.Deserialize<ManualFillState>(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    public static void Save(string reportsDir, ManualFillState state)
        => File.WriteAllText(PathIn(reportsDir),
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>清单这一版的修改时间（UTC）；不存在时给 MinValue，配合 fileExists=false 用。</summary>
    public static DateTime FileTimeUtc(string csvPath)
        => File.Exists(csvPath) ? File.GetLastWriteTimeUtc(csvPath) : DateTime.MinValue;
}
