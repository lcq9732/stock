using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockPlatform.Scheduling;

/// <summary>
/// 计划的存取（<c>data/fetch-plan.json</c>，2026-08-31 新增）。
///
/// ════ 几条刻意的取舍 ════
/// ① **枚举存字符串**：json 里写 "FetchAll" / "EveryWorkday" 而不是 0/1。数字在插入新枚举成员时
///    会整体错位，把用户排好的计划变成另一堆动作；字符串顶多是认不出来（下面会跳过）。
/// ② **认不出来的项跳过、不报错**：手改坏了 json、或读到更新版本写的动作名时，加载要能继续，
///    不能让程序开不起来。
/// ③ **运行记录跟计划存在同一个文件**：重开程序要知道"今天哪几项已经跑过了"才能接着跑，
///    而不是从头再来一遍（拉取全部跑两遍就是白白两小时）。
/// ④ **先写临时文件再替换**：这个文件在每项跑完后都会写一次，正写着断电会留下半个文件，
///    下次启动整份计划就没了。
/// </summary>
public sealed class FetchPlanStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>读计划；文件不存在或读不动时返回默认计划（不抛异常）。</summary>
    public FetchPlan Load()
    {
        try
        {
            if (!File.Exists(path)) return FetchPlan.CreateDefault();
            var plan = JsonSerializer.Deserialize<FetchPlan>(File.ReadAllText(path), Options);
            if (plan == null) return FetchPlan.CreateDefault();
            // 认不出来的动作直接丢掉——json 被手改坏、或降级运行时读到新版本的动作名
            plan.Items = plan.Items.Where(i => Enum.IsDefined(i.Action)).ToList();
            if (plan.Items.Count == 0) return FetchPlan.CreateDefault();
            plan.Normalize();   // 补齐动作全集 + 让"手动"的项保持未启用，见 FetchPlan.Normalize
            return plan;
        }
        catch
        {
            return FetchPlan.CreateDefault();
        }
    }

    /// <summary>存计划。存不下来只影响下次启动，不打扰用户。</summary>
    public void Save(FetchPlan plan)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(plan, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // 忽略：写盘失败不该中断正在跑的计划
        }
    }
}
