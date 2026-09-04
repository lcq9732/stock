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
            // 老格式（2026-09-02 之前：一串平铺的 Items、每项自带重复规则）**不迁移**：
            // 新的三组划分（每日 / 周期 / 按需）更合理，硬映射只会带进一堆别扭的组合。
            // 直接备份原文件、重建默认计划——用户改过的勾选和时刻会丢，所以备份必须留住。
            if (plan.LegacyItems is { Count: > 0 } || plan.Groups.Count == 0)
                return RebuildFromDefault("这份计划是老格式（平铺的任务清单）",
                    // 结构按新的来，但"上次跑没跑、跑了多久"这些**事实**要承接
                    carryOver: plan.LegacyItems?.Where(i => Enum.IsDefined(i.Action)));

            // 认不出来的动作直接丢掉（手改坏的 json、或降级运行时读到新版本的动作名）
            foreach (var g in plan.Groups)
                g.Items = g.Items.Where(i => Enum.IsDefined(i.Action)).ToList();

            // 退役的复合动作原地换成等价的原子项（2026-09-02）
            var notes = plan.MigrateRetired();
            plan.Normalize();   // 补齐组和动作全集、回填 Owner，见 FetchPlan.Normalize
            // 说明文本存在 plan 上，调用方（Fetcher 的 MainViewModel）加载完打进日志，
            // 让人知道计划为什么变了。
            plan.MigrationNotes = notes;
            return plan;
        }
        catch (Exception ex)
        {
            // 读不动（半个文件、手改坏、某个字段格式不合）也走重建那条路：
            // 至少把原文件留一份，人想找回自己排过什么还有得看。
            // 把原因带出来——不然"计划怎么变回默认了"永远查不清。
            // 整份读不出来时再**尽力捞一次运行记录**：结构可以重建，但"今天跑没跑过"
            // 丢了就要白跑一遍。
            return RebuildFromDefault($"这份计划读不出来（{ex.Message}）", RescueRunRecords());
        }
    }

    /// <summary>
    /// 备份现有文件、重建默认计划（2026-09-02）。
    ///
    /// 什么时候会走到这儿：读到 2026-09-02 之前的老格式（一串平铺的任务），或者文件损坏/认不出来。
    /// **老格式不做映射迁移**——新的三组划分是按"数据更新节奏"重新想过的，硬把老计划套过来
    /// 只会带进一堆别扭的组合（比如被人设成「空闲时」的日更项）。
    /// 但用户排过的东西不能无声无息地没了，所以原文件一定先备份成 <c>*.bak.json</c>，
    /// 并通过 MigrationNotes 告诉他备份在哪。
    ///
    /// ⚠ **重建的是结构，不是事实**：老计划里每一项"上次什么时候跑的、成没成、跑了多久"
    /// 会按动作搬到新计划上（<paramref name="carryOver"/>）。不搬的话，当天已经跑完的任务
    /// 会被当成没跑过再来一遍（白花两小时），耗时自学也要从头学起。
    /// 各类**失败名单**不在计划文件里、而在 manifest.json，重建根本不碰它，天然承接。
    /// </summary>
    private FetchPlan RebuildFromDefault(string why, IEnumerable<FetchPlanItem>? carryOver = null)
    {
        var plan = FetchPlan.CreateDefault();
        var note = $"{why}，已按新的分组重建了一份默认计划。";

        var old = carryOver?.ToList();
        if (old is { Count: > 0 })
        {
            plan.CarryOverRunRecords(old);
            note += $"（{old.Count} 项的运行记录——上次跑的时间、结果、实测耗时——已按任务承接过来；"
                  + "各类失败名单本来就存在 manifest.json 里，不受影响。）";
        }
        try
        {
            if (File.Exists(path))
            {
                var backup = Path.ChangeExtension(path, ".bak.json");
                File.Copy(path, backup, overwrite: true);
                note += $"原来那份备份在 {backup}，想对照自己排过什么可以打开看。";
            }
        }
        catch
        {
            note += "（原文件没能备份成功）";
        }
        plan.MigrationNotes = [note];
        return plan;
    }

    /// <summary>
    /// 整份文件反序列化失败时，**逐项手工捞运行记录**（2026-09-02）。
    ///
    /// 为什么要有这一层：强类型反序列化是"一处不合、全份作废"——老文件里某个字段格式对不上
    /// （比如 <c>"NotBefore": "18:00"</c> 少了秒），整份计划连同"今天哪几项已经跑过"一起没了，
    /// 于是当天已经跑完的又被跑一遍（【个股日K】那种就是白花半小时）。
    ///
    /// 这里改用 JsonDocument 一项项读，**每一项独立 try**：读得出来的就捞回来，读不出的跳过。
    /// 只捞运行记录（动作 + 上次跑的时间/结果/耗时样本），不碰结构——结构本来就要重建。
    /// 老格式的 <c>Items</c> 和新格式的 <c>Groups[].Items</c> 都认。
    /// </summary>
    private List<FetchPlanItem> RescueRunRecords()
    {
        var rescued = new List<FetchPlanItem>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("Items", out var flat) && flat.ValueKind == JsonValueKind.Array)
                foreach (var e in flat.EnumerateArray()) Rescue(e, rescued);

            if (root.TryGetProperty("Groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                foreach (var g in groups.EnumerateArray())
                    if (g.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var e in items.EnumerateArray()) Rescue(e, rescued);
        }
        catch
        {
            // 连 JsonDocument 都解析不了（文件被截断/不是 json）——那就真没得捞了
        }
        return rescued;

        static void Rescue(JsonElement e, List<FetchPlanItem> into)
        {
            try
            {
                if (!e.TryGetProperty("Action", out var a) || a.ValueKind != JsonValueKind.String) return;
                if (!Enum.TryParse<FetchActionId>(a.GetString(), out var action)) return;

                var item = new FetchPlanItem { Action = action };
                if (e.TryGetProperty("LastStart", out var s) && s.TryGetDateTime(out var start)) item.LastStart = start;
                if (e.TryGetProperty("LastEnd", out var en) && en.TryGetDateTime(out var end)) item.LastEnd = end;
                if (e.TryGetProperty("LastOutcome", out var o) && o.ValueKind == JsonValueKind.String
                    && Enum.TryParse<RunOutcome>(o.GetString(), out var outcome)) item.LastOutcome = outcome;
                if (e.TryGetProperty("LastErrorCount", out var c) && c.TryGetInt32(out var errCount)) item.LastErrorCount = errCount;
                if (e.TryGetProperty("LastMessage", out var m) && m.ValueKind == JsonValueKind.String) item.LastMessage = m.GetString();
                if (e.TryGetProperty("LastNothingToDo", out var n) && n.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    item.LastNothingToDo = n.GetBoolean();
                if (e.TryGetProperty("RecentDurationsSec", out var d) && d.ValueKind == JsonValueKind.Array)
                    foreach (var x in d.EnumerateArray())
                        if (x.TryGetInt32(out var sec)) item.RecentDurationsSec.Add(sec);

                into.Add(item);
            }
            catch
            {
                // 这一项捞不出来就算了，不影响别的项
            }
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
