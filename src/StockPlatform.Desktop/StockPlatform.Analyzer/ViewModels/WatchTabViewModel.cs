using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using StockPlatform.Analyzer.Watchlist;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>
/// 叙述里的**一行事件**，给界面绑定用。
/// 缩进做成 <see cref="Margin"/>（而不是在文字里塞空格）——文字里塞空格在复制、
/// 排序、搜索时都会跟着走，而缩进是纯粹的视觉层级。
/// </summary>
public class WatchEventLine
{
    public string Date { get; init; } = "";
    public string Text { get; init; } = "";

    /// <summary>附注，挂 ToolTip 不占正文。见 <see cref="WatchEvent.Tip"/>。</summary>
    public string Tip { get; init; } = "";

    /// <summary>0 级＝主行（回购方案、解禁），1 级＝它的后续进展。</summary>
    public Thickness Margin { get; init; }

    /// <summary>还没发生（解禁日期在将来）。界面上淡一点显示，跟已成事实的分开。</summary>
    public bool IsFuture { get; init; }
}

/// <summary>
/// 界面上的**一只票一行**（2026-09-14 重构）。
///
/// 以前是左右两张表按事项组织：左边"要发生什么"、右边"发生了什么"。
/// 用户的原话是：从左边看到事件，还得去右边找是否成了事实——**一件事被拆在两处**。
/// 现在一只票占一行，身上发生过什么按时间连成一条线，回购那种跨几个月的过程读起来才通顺：
/// <code>
/// 2026-07-30  回购方案  计划 0~1 亿，价格上限 14 元
///    2026-07-31  首次回购  0.03 亿
///    2026-09-11  回购完毕  累计 0.6 亿
/// </code>
/// </summary>
public class StockEventRow
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>个人观点，来自 <c>notes/{code}.md</c> 里「观点：」开头那行。</summary>
    public string Opinion { get; init; } = "";

    public IReadOnlyList<WatchEventLine> Lines { get; init; } = [];
}

/// <summary>市场观察区的一行。</summary>
public class MarketWatchRow
{
    public string Date { get; init; } = "";
    public string Text { get; init; } = "";
}

/// <summary>
/// 【观察项】页（2026-09-11 建，2026-09-14 改成叙述式），见 doc/watch-item-design.md M3。
///
/// 这一页回答的是"**我持有的票身上，最近发生了什么**"。
///
/// ⚠ 内容**现读库**（<see cref="WatchService.BuildEvents"/>），没有"先重算再看"这一步：
/// 抓取程序刚落库的新公告，下次打开就看得到。所以按钮叫【刷新】而不是【重算并求值】
/// ——那套重算（规则引擎 + 求值器 + 触发落库）于 2026-09-15 整体退休，见 WatchService 类注释。
///
/// ════ 左边个股、右边市场 ════
/// "跌破 MA20"每只票都会有，熊市里几千只同时触发，逐票列在个股行里只会把真正个股独有的事
/// （回购买了没、解禁多少）淹掉。所以广度类的信息单独放右边，个股行里**仍然保留**自己那条数。
///
/// ════ 「个人观点」写在个股笔记里 ════
/// 那一列的内容来自 <c>notes/{code}.md</c> 里以「观点：」开头的那一行，点行尾的 📝 就能编辑。
/// 为什么不做成表格里直接填：
///   · 笔记本来就是判断的去处——"数据能重算，判断不能"（见 AnalyzerPaths.NotesDir）；
///   · "要写成一段话存进文件"这件事本身是个门槛，能拦住随手许愿。观察项要过
///     "可判定／能改变动作／有归属层"三条准入（设计文档 §1），做成随手能填的输入框就成许愿池了。
/// </summary>
public class WatchTabViewModel : INotifyPropertyChanged
{
    private readonly WatchService _service;

    /// <summary>这一轮算出来的**全部**票。<see cref="Stocks"/> 是它过滤后的视图。</summary>
    private List<StockEventRow> _allStocks = [];

    /// <summary>一股一行的叙述（已按 <see cref="Filter"/> 过滤）。</summary>
    public ObservableCollection<StockEventRow> Stocks { get; } = [];

    /// <summary>市场普遍现象——不属于任何一只票的那些。</summary>
    public ObservableCollection<MarketWatchRow> Market { get; } = [];

    private bool _loaded;

    private string _status = "还没读取。";
    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    private bool _trackedScope;
    /// <summary>
    /// 勾上＝看**全部跟踪**（主动仓全部＋底仓），不勾＝只看**持仓**（默认）。
    ///
    /// 默认只看持仓，是因为实测这三个清单里真正有钱在里面的只有 9 只，而原来一律盯 67 只
    /// （见 <see cref="WatchScope"/>）。没建仓的票，建仓前用【分析详情】查一次就够，
    /// 不需要天天摆在眼前。
    /// </summary>
    public bool TrackedScope
    {
        get => _trackedScope;
        set { _trackedScope = value; OnPropertyChanged(); Run(); }
    }

    private string _filter = "";
    /// <summary>
    /// 按代码或名称找票（2026-09-14 加）。盯的票多了以后一页翻不完，
    /// 想确认某一只的情况得拿眼睛扫——输入框比滚动快。
    ///
    /// 输入即筛，不用回车：这是个"找"的动作，不是"提交"的动作。
    /// 清空就回到全部。
    /// </summary>
    public string Filter
    {
        get => _filter;
        set { _filter = value ?? ""; OnPropertyChanged(); ApplyFilter(); }
    }

    public ICommand RunCommand { get; }
    public ICommand RefreshCommand { get; }

    /// <summary>
    /// <paramref name="notes"/> 只为保持调用点不变而留着——「个人观点」现在由
    /// <see cref="WatchService.BuildEvents"/> 一并读出来，跟事件在同一趟里组装好。
    /// </summary>
    public WatchTabViewModel(WatchService service, StockNoteStore notes)
    {
        _ = notes;
        _service = service;
        RunCommand = new RelayCommand(_ => Run());
        RefreshCommand = new RelayCommand(_ => Run());
    }

    /// <summary>
    /// 切到这一页时调：**只在第一次自动读**，之后保持上次结果。
    ///
    /// ⚠ 不是每次切页都读（2026-09-15）：读一轮要扫 9 只票各 7 张表、再算一次全市场均线广度
    /// （实测那一项单独就 630ms），切来切去都顿一下很烦。这跟【每日晨检】当初改成全手动
    /// 是同一个理由，只是这页轻得多，首次自动读还担得起。要最新的点【刷新】。
    /// </summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Run();
    }

    /// <summary>重读一遍库。</summary>
    public void Refresh() => Run();

    private void Run()
    {
        try
        {
            Status = "读取中…";
            var (stocks, market) = _service.BuildEvents(
                _trackedScope ? WatchScope.Tracked : WatchScope.Holding);

            _allStocks = stocks.Select(ToRow).ToList();
            ApplyFilter();

            Market.Clear();
            foreach (var m in market)
                Market.Add(new MarketWatchRow { Date = m.Date.ToString("yyyy-MM-dd"), Text = m.Text });

            // 一行说完。原来后面还跟着一长串"观察项 N 条（新挂/摘掉）、求值 N 条、命中 N 条"，
            // 那是已退休的重算机器的计数——它没有任何界面下游，报出来的数没人能据此做任何事。
            var scopeName = _trackedScope ? "全部跟踪" : "持仓";
            var events = _allStocks.Sum(x => x.Lines.Count);
            Status = _allStocks.Count == 0
                // 全清仓时这页会空。**得说清是"没持仓"而不是"坏了"**，并指一下出口。
                ? (_trackedScope
                    ? "主动仓和底仓里都没有票。"
                    : "当前没有持仓——勾上【全部跟踪】可以看主动仓里还没买的那些。")
                : $"{scopeName} {_allStocks.Count} 只有动静，共 {events} 条事件。" + FilterNote();
        }
        catch (Exception ex)
        {
            Status = $"重算失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 按代码或名称筛。两者都认——记得住代码的输 300750，记得住名字的输"宁德"。
    ///
    /// ⚠ 只匹配代码和名称，**不匹配叙述正文**：输入框是用来"找某只票"的。
    /// 连正文一起匹配的话，输"宁"会把叙述里碰巧带这个字的票也捞出来，
    /// 那就不是找票而是全文检索了，两种意图混在一个框里谁都用不顺。
    /// </summary>
    private void ApplyFilter()
    {
        var q = _filter.Trim();
        Stocks.Clear();
        foreach (var row in _allStocks)
        {
            if (q.Length > 0
                && !row.Code.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !row.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;
            Stocks.Add(row);
        }
        OnPropertyChanged(nameof(FilterHint));
    }

    /// <summary>筛完剩几只，显示在输入框右边。没筛的时候不占地方。</summary>
    public string FilterHint
        => _filter.Trim().Length == 0 ? ""
           : Stocks.Count == 0 ? "没有匹配的票"
           : $"{Stocks.Count} / {_allStocks.Count}";

    /// <summary>重算完了要在状态里说清"现在看到的是筛过的"，否则会以为票丢了。</summary>
    private string FilterNote()
        => _filter.Trim().Length == 0 ? "" : $"（当前筛「{_filter.Trim()}」，显示 {Stocks.Count} 只）";

    private static StockEventRow ToRow(StockWatchEvents s) => new()
    {
        Code = s.Code, Name = s.Name, Opinion = s.Opinion,
        Lines = s.Events.Select(e => new WatchEventLine
        {
            Date = e.Date.ToString("yyyy-MM-dd"),
            Text = e.Text,
            Tip = e.Tip ?? "",
            // 一级缩进 20px：够看出层级，又不会把长句子挤出显示宽度
            Margin = new Thickness(e.Indent * 20, 0, 0, 0),
            IsFuture = e.IsFuture,
        }).ToList(),
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
