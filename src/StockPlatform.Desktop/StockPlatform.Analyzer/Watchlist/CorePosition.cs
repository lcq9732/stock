using System.IO;
using System.Text.Json;

namespace StockPlatform.Analyzer.Watchlist;

/// <summary>
/// 一只**底仓**股票（2026-08-20 新增）——用来长期拿着吃分红的那部分持仓，跟"主动仓"页记录的
/// 主动仓是两套东西。
///
/// 为什么不复用 <see cref="WatchlistEntry"/>：见 AnalyzerPaths.CorePositionPath 的注释（字段不
/// 重叠、标的不该重叠、以及最关键的——混一份数据会让晨检的短线纪律误伤底仓）。
///
/// 复用的部分是 <see cref="TradeLot"/>：底仓是**分档建仓**的，一条记录下面挂多笔买入，这跟主动仓
/// 的金字塔建仓结构完全一样，没必要另造一套。
///
/// ⚠ 底仓和主动仓的**税务**隔不开：红利税按先进先出认定持股期限（财税[2012]85号），同一证券账户
/// 同一只票，卖主动仓那笔会被认定成卖掉了最早买入的底仓份额。在这里给成交打标记改变不了券商和
/// 中登的认定——要真隔开只能"同一只票不两头做"，或者底仓和主动仓分开用两个证券账户。
/// </summary>
public class CorePosition
{
    /// <summary>稳定标识——同 <see cref="WatchlistEntry.Id"/>：反序列化后是新实例，不能靠引用相等。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Code { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>加入底仓的时刻。</summary>
    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 这只票的目标年化股息（元，税前）——底仓的仓位是按**现金流**倒推的，不是按凯利。
    /// 凯利要有胜率和赔率两个输入，而底仓压根不做胜负判断，做的是"每年收多少分红"。
    /// 目标股数 = 目标年化股息 ÷ 每股股息，见 CorePositionRowViewModel 的建仓进度。
    /// 0 表示还没设目标，此时进度列显示"—"。
    /// </summary>
    public double TargetAnnualDividend { get; set; }

    /// <summary>各笔成交（买/卖），分档建仓就靠它。结构同主动仓，见 <see cref="TradeLot"/>。</summary>
    public List<TradeLot> Lots { get; set; } = new();

    /// <summary>建仓理由 / 退出条件备注——底仓没有价格止损，退出条件是"分红中断"或"基本面变坏"
    /// 这类事件，是要写下来的，不然几年后自己都想不起当初为什么买它。</summary>
    public string Note { get; set; } = "";

    // ── 汇总（跟 WatchlistEntry 的同名属性同口径，都不含费；含费的走 TradeCostSummary）──

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<TradeLot> BuyLots => Lots.Where(l => l.Side == TradeSide.Buy && l.Price > 0).ToList();

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<TradeLot> SellLots => Lots.Where(l => l.Side == TradeSide.Sell && l.Price > 0).ToList();

    [System.Text.Json.Serialization.JsonIgnore]
    public int TotalBuyShares => BuyLots.Sum(l => l.Shares);

    [System.Text.Json.Serialization.JsonIgnore]
    public int TotalSellShares => SellLots.Sum(l => l.Shares);

    [System.Text.Json.Serialization.JsonIgnore]
    public int RemainingShares => Math.Max(0, TotalBuyShares - TotalSellShares);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasBought => BuyLots.Count > 0;

    /// <summary>已经买了几笔 = 分了几档建仓。
    /// 原来还有个"计划档数"配它显示成"2/4 档"，2026-08-20 按用户要求删掉了——那个字段除了当分母
    /// 之外没有任何作用（不参与计算、不做提醒），而真正有信息量的是下面那个按股数算的百分比。
    /// 老的 core-positions.json 里可能还留着 plannedTranches 字段，反序列化会自动忽略。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int FilledTranches => BuyLots.Count;

    /// <summary>最早那笔买入的日期——免税时钟从这里算起（先进先出，最早的那批先被认定卖出）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? FirstBuyDate => BuyLots.Count == 0 ? null : BuyLots.Min(l => l.Date);

    /// <summary>最晚那笔买入的日期——整个底仓要全部满一年免税，看的是这个。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? LastBuyDate => BuyLots.Count == 0 ? null : BuyLots.Max(l => l.Date);
}

/// <summary>
/// 底仓记录的持久化（<c>data\core-positions.json</c>）——写法照
/// <see cref="JsonWatchlistStore"/>：一个扁平 JSON 数组，改完立刻整份落盘，文件不存在或坏了
/// 就当空列表。底仓realistically 就几只到十几只，不值得上 SQLite。
/// </summary>
public class JsonCorePositionStore
{
    private readonly string _filePath;
    private readonly object _fileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public JsonCorePositionStore(string filePath)
    {
        _filePath = filePath;
    }

    public List<CorePosition> Load()
    {
        lock (_fileLock) return LoadUnlocked();
    }

    private List<CorePosition> LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_filePath)) return new List<CorePosition>();
            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json)) return new List<CorePosition>();
            return JsonSerializer.Deserialize<List<CorePosition>>(json) ?? new List<CorePosition>();
        }
        catch (Exception)
        {
            // 同 JsonWatchlistStore：文件坏了不能让整个程序起不来，退回空列表。
            return new List<CorePosition>();
        }
    }

    private void Save(List<CorePosition> items)
        => File.WriteAllText(_filePath, JsonSerializer.Serialize(items, JsonOptions));

    /// <summary>加入底仓——**一只股票只留一条记录**（同 JsonWatchlistStore.Add 的去重口径）。
    /// 返回真正新增的条数。</summary>
    public int Add(IEnumerable<CorePosition> newItems)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var existing = all.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
            int added = 0;
            foreach (var item in newItems)
            {
                if (!existing.Add(item.Code)) continue;
                all.Add(item);
                added++;
            }
            if (added > 0) Save(all);
            return added;
        }
    }

    /// <summary>整份覆盖某条记录的成交明细（同 JsonWatchlistStore.UpdateLots：录成交窗口点保存后
    /// 把改完的整份 Lots 写回）。</summary>
    public void UpdateLots(Guid id, IEnumerable<TradeLot> lots)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var target = all.FirstOrDefault(e => e.Id == id);
            if (target == null) return;
            target.Lots = lots.ToList();
            Save(all);
        }
    }

    /// <summary>改目标年化股息（在【录成交】窗口里填，2026-08-20 从表格挪进去的）。</summary>
    public void UpdateTargetDividend(Guid id, double targetAnnualDividend)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var target = all.FirstOrDefault(e => e.Id == id);
            if (target == null) return;
            target.TargetAnnualDividend = targetAnnualDividend;
            Save(all);
        }
    }

    /// <summary>改建仓理由/退出条件备注（表格里直接编辑）。</summary>
    public void UpdateNote(Guid id, string note)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var target = all.FirstOrDefault(e => e.Id == id);
            if (target == null) return;
            target.Note = note ?? "";
            Save(all);
        }
    }

    public void Remove(IEnumerable<Guid> ids)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var set = ids.ToHashSet();
            int before = all.Count;
            all.RemoveAll(e => set.Contains(e.Id));
            if (all.Count != before) Save(all);
        }
    }
}
