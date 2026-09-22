using System.IO;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.Watchlist;

/// <summary>
/// Reads/writes the user's watchlist as a flat JSON array — plenty for what's realistically a few
/// dozen-to-hundred hand-picked entries, not worth a SQLite table (and keeping it out of
/// the market database matters more than the storage format — see AnalyzerPaths.WatchlistPath).
/// </summary>
public class JsonWatchlistStore
{
    private readonly string _filePath;
    private readonly object _fileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public JsonWatchlistStore(string filePath)
    {
        _filePath = filePath;
    }

    public List<WatchlistEntry> Load()
    {
        lock (_fileLock)
        {
            return LoadUnlocked();
        }
    }

    private void Save(List<WatchlistEntry> entries)
    {
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    /// <summary>
    /// 加入自选——**一只股票在自选里只留一条记录**：只要这个代码已经在自选里（不管当初是哪个方法、
    /// 哪一天加进来的），再加就直接跳过。返回真正新增的条数，供调用方回报给用户。
    ///
    /// 2026-08-12 从"按 (代码, 方法, 数据日期) 去重"改成"只按代码去重"（用户要求）：原来同一只票被
    /// 不同方法选中、或同一方法隔天再选中，都会各自多出一行，自选列表里于是出现成片的重复股票
    /// （中国长城、南天信息、广电运通都曾各占两行）。
    ///
    /// ⚠️ 附带影响：自选股页的"各方法准确率"是按记录分组统计的，所以一只票只会计入**第一个**把它
    /// 加进来的方法；后来也选中它的方法就少一个样本。要让每个方法都留下样本、同时列表里不重复，
    /// 得改成"已存在就把新方法名并进那条记录"——需要的话再做，当前按用户明确要求走简单跳过。
    /// </summary>
    public int Add(IEnumerable<WatchlistEntry> newEntries)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var existingCodes = all.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
            int added = 0;
            foreach (var entry in newEntries)
            {
                if (!existingCodes.Add(entry.Code)) continue;   // 已在自选里（含本批里重复勾选的同一只）
                all.Add(entry);
                added++;
            }
            if (added > 0) Save(all);
            return added;
        }
    }

    /// <summary>整体替换某条自选的成交明细（"主动仓"Tab的【交易记录】窗口里录入的多笔买入/卖出）——
    /// 按 Id 定位，整份覆盖（窗口里本来就是"改完一起保存"，逐笔增删改反而要处理更多中间状态），
    /// 顺带把汇总写回兼容字段。加载-修改-保存都在锁内完成，跟 Add/Remove 一样保持单写者语义。</summary>
    public void UpdateLots(Guid id, IEnumerable<TradeLot> lots)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var entry = all.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;
            entry.Lots = lots.OrderBy(l => l.Date).ToList();
            entry.SyncLegacyFromLots();
            Save(all);
        }
    }

    /// <summary>更新手填的财报披露日（2026-08-17新增，"主动仓"页那一列）——按 Id 定位、只改这一个
    /// 字段。传 null 表示清空。用途见 <see cref="WatchlistEntry.EarningsDate"/>。</summary>
    public void UpdateEarningsDate(Guid id, DateTime? earningsDate)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var entry = all.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;
            entry.EarningsDate = earningsDate;
            Save(all);
        }
    }

    /// <summary>更新手填的"我的分类"（2026-09-22新增，"主动仓"页那一列）——按 Id 定位、只改这一个
    /// 字段。空/空白一律存成空字符串（不存 null），见 <see cref="WatchlistEntry.UserTag"/>。</summary>
    public void UpdateUserTag(Guid id, string? userTag)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var entry = all.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;
            entry.UserTag = (userTag ?? "").Trim();
            Save(all);
        }
    }

    /// <summary>把若干条自选加入/移出"主动仓"（2026-07-31新增）——主动仓是"我打算买卖、要每天盯"的
    /// 那一小撮，跟"算法验证样本"分开（见 <see cref="WatchlistEntry.InTradePool"/>）。返回实际改动的条数。
    /// 移出时不清空买卖信息（交易记录要留痕）：**未平仓的持仓**移不出去（<see cref="WatchlistEntry.IsInTradePool"/>
    /// 恒为真），要移出得先清掉买入信息；已平仓的可以正常移出。</summary>
    public int SetTradePool(IEnumerable<Guid> ids, bool inPool)
    {
        lock (_fileLock)
        {
            var idSet = ids.ToHashSet();
            var all = LoadUnlocked();
            int changed = 0;
            foreach (var e in all.Where(e => idSet.Contains(e.Id)))
            {
                // 两个标记一起写，保证"加入↔移出"可以来回切：加入=显式加入+清掉移出标记；
                // 移出=打上移出标记+清掉加入标记（见 WatchlistEntry.IsInTradePool 的判定）。
                var before = e.IsInTradePool;
                e.InTradePool = inPool;
                e.RemovedFromPool = !inPool;
                if (e.IsInTradePool != before) changed++;
            }
            Save(all);
            return changed;
        }
    }

    public void Remove(IEnumerable<Guid> ids)
    {
        lock (_fileLock)
        {
            var idSet = ids.ToHashSet();
            var all = LoadUnlocked();
            all.RemoveAll(e => idSet.Contains(e.Id));
            Save(all);
        }
    }

    private List<WatchlistEntry> LoadUnlocked()
    {
        if (!File.Exists(_filePath)) return new List<WatchlistEntry>();
        var json = File.ReadAllText(_filePath);
        if (string.IsNullOrWhiteSpace(json)) return new List<WatchlistEntry>();
        var entries = JsonSerializer.Deserialize<List<WatchlistEntry>>(json) ?? new List<WatchlistEntry>();
        // 老记录（2026-08-11之前只有单笔买卖字段）补成成交明细，之后全程按明细算。幂等，
        // 不在这里回写文件——下次任何一次保存会顺带把迁移结果落盘。
        foreach (var e in entries) e.MigrateLegacyLots();
        return entries;
    }

    // 原来这里有个 EntryKey(代码, 方法, 数据日期) 做去重键，2026-08-12 起改成只按代码去重
    // （见 Add 的注释），这个键就没用了，一并删掉避免留着误导。
}
