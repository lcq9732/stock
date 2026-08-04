using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using StockPlatform.Analyzer.Export;
using StockPlatform.Analyzer.Watchlist;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.ViewModels;

public class WatchlistRowViewModel : ISelectableRow, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public WatchlistEntry Entry { get; }
    private readonly JsonWatchlistStore _store;
    private double? _latestClose;

    public WatchlistRowViewModel(WatchlistEntry entry, IBarRepository barRepository, string conceptBoards, JsonWatchlistStore store)
    {
        Entry = entry;
        Board = conceptBoards;
        _store = store;
        ComputeTracking(barRepository);
    }

    public string Code => Entry.Code;
    public string Name => Entry.Name;
    /// <summary>该股所属的概念/题材板块（来自"板块热度"抓取的数据，一只票可能属于多个，用顿号连接）；
    /// 没有板块数据或不属于任何概念板块时显示"—"。由 WatchlistTabViewModel 一次性反查后传入。</summary>
    public string Board { get; }
    /// <summary>申万行业（跟其它结果表"板块"列同一个口径，来自本地静态映射，见 IndustryClassifier）。</summary>
    public string Industry => IndustryClassifier.GetIndustry(Entry.Code);
    public string Method => Entry.Method;
    public string DataDate => Entry.DataDate.ToString("yyyy-MM-dd");
    public double PriceAtPick => Entry.PriceAtPick;
    public string AddedAt => Entry.AddedAt.ToString("yyyy-MM-dd HH:mm");
    public string SatisfiedText => $"{Entry.SatisfiedCount}/{Entry.TotalCount}";

    /// <summary>"选中后表现"——用本地已有的K线历史，从选中当天(DataDate)开始查到最新一根日线，
    /// 拿最新收盘价相对PriceAtPick（选中当天收盘价）算涨跌幅。故意不持久化任何跟踪数据：本地
    /// K线库本来就有选中日之后的完整历史，每次刷新这个Tab直接现算即可，没必要额外存一份、
    /// 也没有"数据过期"的问题。start用DataDate本身（不是DataDate+1）——如果选中日之后还没有更新的
    /// 交易日数据，会查到DataDate自己那根，涨跌幅显示为0%，这正确反映"还没有新的一天可比较"。</summary>
    public string LatestCloseText { get; private set; } = "—";
    public string ChangeText { get; private set; } = "本地无该日期之后的K线数据";
    public Brush ChangeColor { get; private set; } = Brushes.Gray;

    /// <summary>"选中后涨跌幅"的数值形式——给"自选股"页统计各方法准确率用（平均涨跌/胜率）。</summary>
    public double? SincePickPct { get; private set; }

    /// <summary>是否在"我的交易"池里（显式勾入，或已填买入价）——"自选股"页用一列标出来，让人一眼看出
    /// 哪些样本自己真的下手了。</summary>
    public string TradePoolText => Entry.IsInTradePool ? (Entry.BuyPrice is > 0 ? "✔持仓" : "✔已加入") : "";
    public Brush TradePoolColor => Entry.BuyPrice is > 0 ? Brushes.Firebrick : Brushes.SeaGreen;

    private void ComputeTracking(IBarRepository barRepository)
    {
        var bars = barRepository.Query(Entry.Code, Granularity.Day, start: Entry.DataDate);
        if (bars.Count == 0 || Entry.PriceAtPick <= 0) return;

        var latest = bars[^1];
        _latestClose = latest.Close;
        LatestCloseText = $"{latest.Close:F2}（{latest.PeriodStart:yyyy-MM-dd}）";
        var pct = (latest.Close - Entry.PriceAtPick) / Entry.PriceAtPick * 100;
        SincePickPct = pct;
        ChangeText = $"{(pct >= 0 ? "+" : "")}{pct:F2}%";
        ChangeColor = pct >= 0 ? Brushes.Red : Brushes.Green; // 国内看盘习惯：涨红跌绿
    }

    // ── 手动持仓信息（买入日期/买入价/股数，2026-07-29新增）——单元格里直接编辑，提交时解析并
    //    立即持久化；解析不了的输入丢弃（Raise让界面回显旧值）。三个都空=观察中、没买。 ──

    public string BuyDateText
    {
        get => Entry.BuyDate?.ToString("yyyy-MM-dd") ?? "";
        set
        {
            var t = (value ?? "").Trim();
            if (t.Length == 0) Entry.BuyDate = null;
            else if (DateTime.TryParse(t, out var d)) Entry.BuyDate = d.Date;
            PersistTradeInfo();
        }
    }

    public string BuyPriceText
    {
        get => Entry.BuyPrice?.ToString("F2") ?? "";
        set
        {
            var t = (value ?? "").Trim();
            if (t.Length == 0) Entry.BuyPrice = null;
            else if (double.TryParse(t, out var p) && p > 0) Entry.BuyPrice = p;
            PersistTradeInfo();
        }
    }

    public string SharesText
    {
        get => Entry.Shares?.ToString() ?? "";
        set
        {
            var t = (value ?? "").Trim();
            if (t.Length == 0) Entry.Shares = null;
            else if (int.TryParse(t, out var n) && n > 0) Entry.Shares = n;
            PersistTradeInfo();
        }
    }

    public string SellDateText
    {
        get => Entry.SellDate?.ToString("yyyy-MM-dd") ?? "";
        set
        {
            var t = (value ?? "").Trim();
            if (t.Length == 0) Entry.SellDate = null;
            else if (DateTime.TryParse(t, out var d)) Entry.SellDate = d.Date;
            PersistTradeInfo();
        }
    }

    public string SellPriceText
    {
        get => Entry.SellPrice?.ToString("F2") ?? "";
        set
        {
            var t = (value ?? "").Trim();
            if (t.Length == 0) Entry.SellPrice = null;
            else if (double.TryParse(t, out var p) && p > 0) Entry.SellPrice = p;
            PersistTradeInfo();
        }
    }

    private void PersistTradeInfo()
    {
        _store.UpdateTradeInfo(Entry.Id, Entry.BuyDate, Entry.BuyPrice, Entry.Shares, Entry.SellDate, Entry.SellPrice);
        Raise(nameof(BuyDateText));
        Raise(nameof(BuyPriceText));
        Raise(nameof(SharesText));
        Raise(nameof(SellDateText));
        Raise(nameof(SellPriceText));
        Raise(nameof(HoldingText));
        Raise(nameof(HoldingColor));
    }

    /// <summary>持仓盈亏——三种状态：没填买入价=观察中；填了买入价没填卖出价=持仓（较买入价的浮动
    /// 盈亏，有股数带金额）；买入卖出都填了=已平仓（按卖出价算最终已实现盈亏，留痕复盘）。</summary>
    public string HoldingText
    {
        get
        {
            if (Entry.BuyPrice is not (> 0)) return "观察中";

            if (Entry.SellPrice is > 0)
            {
                var spct = (Entry.SellPrice.Value - Entry.BuyPrice.Value) / Entry.BuyPrice.Value * 100;
                if (Entry.Shares is > 0)
                {
                    var spnl = (Entry.SellPrice.Value - Entry.BuyPrice.Value) * Entry.Shares.Value;
                    return $"已平仓 {(spnl >= 0 ? "+" : "")}{spnl:N0}元（{(spct >= 0 ? "+" : "")}{spct:F2}%）";
                }
                return $"已平仓 {(spct >= 0 ? "+" : "")}{spct:F2}%";
            }

            if (_latestClose is not (> 0)) return "无最新价";
            var pct = (_latestClose.Value - Entry.BuyPrice.Value) / Entry.BuyPrice.Value * 100;
            var text = $"{(pct >= 0 ? "+" : "")}{pct:F2}%";
            if (Entry.Shares is > 0)
            {
                var pnl = (_latestClose.Value - Entry.BuyPrice.Value) * Entry.Shares.Value;
                text += $"（{(pnl >= 0 ? "+" : "")}{pnl:N0}元）";
            }
            return text;
        }
    }

    public Brush HoldingColor
    {
        get
        {
            if (Entry.BuyPrice is not (> 0)) return Brushes.Gray;
            if (Entry.SellPrice is > 0)
                return Entry.SellPrice >= Entry.BuyPrice ? Brushes.Red : Brushes.Green;
            if (_latestClose is not (> 0)) return Brushes.Gray;
            return _latestClose >= Entry.BuyPrice ? Brushes.Red : Brushes.Green;
        }
    }

    /// <summary>Plain mutable property, same reasoning as ResultRowViewModel.IsSelected — only
    /// read when "移除勾选" is clicked.</summary>
    public bool IsSelected { get; set; }
}

/// <summary>
/// "自选股（算法验证）" tab — 各选股方法丢进来的**样本池**，用途只有一个：跟踪这些票后来涨跌如何，
/// 统计各方法的准确率（见下方 MethodStatsText）。2026-07-31 起这里**不再录买卖信息**——那是"我的交易"
/// Tab 的事（<see cref="TradePoolTabViewModel"/>）；本页只看"选中后涨跌幅"。
///
/// 关键设计：一只票加入交易池后**仍然留在本页**。否则"你挑走的正好是自己看好的那些"，剩下的样本
/// 就有了选择偏差，方法准确率会被系统性低估/高估——验证样本必须包含方法选出的全部票。本页因此显示
/// 的是全部自选记录，交易池只是叠加在上面的一个标记（"在交易池"列）。
///
/// Reads/writes JsonWatchlistStore, not total.sqlite — this is the Analyzer's own state, not
/// Fetcher's shared read-only data.
/// </summary>
public class WatchlistTabViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly JsonWatchlistStore _store;
    private readonly IBarRepository _barRepository;
    private readonly IBoardRepository _boardRepository;

    public ObservableCollection<WatchlistRowViewModel> Entries { get; } = new();

    private string _methodStatsText = "";
    /// <summary>各方法的准确率速览（只数 / 平均涨跌 / 上涨占比）——本页的核心产出。</summary>
    public string MethodStatsText
    {
        get => _methodStatsText;
        private set { _methodStatsText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MethodStatsText))); }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand AddToTradePoolCommand { get; }

    /// <summary>加入交易池后要通知"我的交易"页和晨检页重新加载——由 MainViewModel 注入。</summary>
    public Action? TradePoolChanged { get; set; }

    public WatchlistTabViewModel(JsonWatchlistStore store, IBarRepository barRepository, IBoardRepository boardRepository)
    {
        _store = store;
        _barRepository = barRepository;
        _boardRepository = boardRepository;
        RefreshCommand = new RelayCommand(_ => Reload());
        RemoveSelectedCommand = new RelayCommand(_ => RemoveSelected());
        ExportCommand = new RelayCommand(_ => GridExporter.ExportWatchlist(Entries));
        AddToTradePoolCommand = new RelayCommand(_ => AddSelectedToTradePool());
        Reload();
    }

    /// <summary>Public so MainWindow can call it when the user switches to this tab — entries
    /// added from another tab in the same session otherwise wouldn't show up until "刷新" is
    /// clicked manually. Also re-triggers each row's "选中后表现"计算 with whatever is currently
    /// the latest local K线 data.</summary>
    public void Reload()
    {
        Entries.Clear();
        // 一次性反查"股票→所属概念板块"，每行直接取（没有板块数据时 map 为空，各行显示"—"）。
        var conceptMap = _boardRepository.GetConceptBoardsByStock();
        foreach (var e in _store.Load().OrderByDescending(e => e.AddedAt))
        {
            var boards = conceptMap.TryGetValue(e.Code, out var list) && list.Count > 0
                ? string.Join("、", list)
                : "—";
            Entries.Add(new WatchlistRowViewModel(e, _barRepository, boards, _store));
        }
        BuildMethodStats();
    }

    /// <summary>按"来源方法"统计准确率——只数、平均"选中后涨跌幅"、上涨占比，按平均涨跌从高到低排。
    /// 口径说明：各方法的自选日期不同、持有时长不可比，这是粗对比；严格评测要固定同一时间窗口。</summary>
    private void BuildMethodStats()
    {
        var groups = Entries
            .Where(r => r.SincePickPct.HasValue)
            .GroupBy(r => r.Method)
            .Select(g => new
            {
                Method = g.Key,
                Count = g.Count(),
                Avg = g.Average(r => r.SincePickPct!.Value),
                Up = g.Count(r => r.SincePickPct!.Value >= 0),
            })
            .OrderByDescending(x => x.Avg)
            .ToList();

        if (groups.Count == 0)
        {
            MethodStatsText = "各方法准确率：还没有可统计的自选记录（从各选股方法勾选\"加入自选\"后这里会自动统计）";
            return;
        }

        var parts = groups.Select(x =>
            $"{x.Method} {x.Count}只 平均{(x.Avg >= 0 ? "+" : "")}{x.Avg:F1}% 胜率{(double)x.Up / x.Count * 100:F0}%");
        MethodStatsText = "各方法准确率（按平均涨跌排序）：" + string.Join("  ｜  ", parts);
    }

    private void RemoveSelected()
    {
        var toRemove = Entries.Where(e => e.IsSelected).Select(e => e.Entry.Id).ToList();
        if (toRemove.Count == 0) return;
        _store.Remove(toRemove);
        Reload();
        TradePoolChanged?.Invoke();   // 删掉的可能正在交易池里
    }

    /// <summary>把勾选的票加进"我的交易"页——买卖信息去那边录。本页仍然保留这些票（验证样本不能被挑走，
    /// 理由见类注释）。</summary>
    private void AddSelectedToTradePool()
    {
        var ids = Entries.Where(e => e.IsSelected).Select(e => e.Entry.Id).ToList();
        if (ids.Count == 0) return;
        _store.SetTradePool(ids, true);
        foreach (var e in Entries) e.IsSelected = false;
        Reload();
        TradePoolChanged?.Invoke();
    }
}

/// <summary>
/// "我的交易" tab（2026-07-31新增）——**我打算买卖、要每天盯的那一小撮票**，跟"自选股（算法验证）"
/// 分开：买入日期/买入价/股数/卖出日期/卖出价只在这里录，每日晨检也只体检这里的票。
///
/// 数据上不是另一份清单，而是同一个 watchlist.json 里 <see cref="WatchlistEntry.IsInTradePool"/>
/// 为真的那些记录（显式勾进来的 + 已经填了买入价的）——这样一只票"既是算法样本又是我的持仓"不需要
/// 存两份、也不会两边不同步。
///
/// 进入方式：①"自选股"页勾选后点"加入交易池"；②"查询"页搜到后直接"加入交易池"（手工看好的票）。
/// </summary>
public class TradePoolTabViewModel
{
    private readonly JsonWatchlistStore _store;
    private readonly IBarRepository _barRepository;
    private readonly IBoardRepository _boardRepository;

    public ObservableCollection<WatchlistRowViewModel> Entries { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RemoveFromPoolCommand { get; }
    public RelayCommand ExportCommand { get; }

    /// <summary>移出交易池后要通知晨检页重新加载——由 MainViewModel 注入。</summary>
    public Action? TradePoolChanged { get; set; }

    public TradePoolTabViewModel(JsonWatchlistStore store, IBarRepository barRepository, IBoardRepository boardRepository)
    {
        _store = store;
        _barRepository = barRepository;
        _boardRepository = boardRepository;
        RefreshCommand = new RelayCommand(_ => Reload());
        RemoveFromPoolCommand = new RelayCommand(_ => RemoveFromPool());
        ExportCommand = new RelayCommand(_ => GridExporter.ExportWatchlist(Entries));
        Reload();
    }

    public void Reload()
    {
        Entries.Clear();
        var conceptMap = _boardRepository.GetConceptBoardsByStock();
        // 持仓中的排最前（真金白银的先看），然后已平仓，最后只是打算买的；同组按加入时间倒序。
        foreach (var e in _store.Load().Where(e => e.IsInTradePool)
                     .OrderByDescending(e => e.BuyPrice is > 0 && e.SellPrice is not (> 0))
                     .ThenByDescending(e => e.BuyPrice is > 0)
                     .ThenByDescending(e => e.AddedAt))
        {
            var boards = conceptMap.TryGetValue(e.Code, out var list) && list.Count > 0
                ? string.Join("、", list)
                : "—";
            Entries.Add(new WatchlistRowViewModel(e, _barRepository, boards, _store));
        }
    }

    /// <summary>把勾选的票移出交易池（不删除记录，它仍留在"自选股"页作为算法样本，交易记录也不清空）。
    /// 只有**未平仓的持仓**移不出去——钱还在里面就必须每天盯；已平仓的可以移出（这笔交易已经结束，
    /// 没道理继续占着每天要看的清单）。有挡下的就如实提示，不静默失败。</summary>
    private void RemoveFromPool()
    {
        var selected = Entries.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0) return;

        // 未平仓持仓 = 填了买入价、还没填卖出价（跟 WatchlistEntry.IsInTradePool 的第①条同一判定）
        var held = selected.Where(e => e.Entry.BuyPrice is > 0 && e.Entry.SellPrice is not (> 0)).Select(e => e.Name).ToList();
        var movable = selected.Where(e => !(e.Entry.BuyPrice is > 0 && e.Entry.SellPrice is not (> 0))).Select(e => e.Entry.Id).ToList();
        if (movable.Count > 0) _store.SetTradePool(movable, false);
        Reload();
        TradePoolChanged?.Invoke();

        if (held.Count > 0)
            System.Windows.MessageBox.Show(
                $"已移出 {movable.Count} 只。\n\n以下 {held.Count} 只是未平仓的持仓，仍留在交易池：\n{string.Join("、", held)}\n\n" +
                "钱还在里面就必须每天盯，所以不允许移出。要么先把卖出日期/卖出价填上（平仓后就能移出了），" +
                "要么把买入信息清空（表示这笔其实没买）。",
                "部分未移出", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }
}
