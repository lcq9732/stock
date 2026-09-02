using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>One matched stock in the 查询 tab — just enough to show a row and open its K线详情
/// (行情详情 / QuoteDetailWindow). Not tied to any analysis method, so it doesn't reuse
/// ResultRowViewModel (which carries 满足数/收敛质量 etc.).</summary>
public class QueryRowViewModel : ISelectableRow
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Board { get; init; } = "";
    /// <summary>标的类型显示：个股/大盘指数/ETF/板块——2026-07-15 起查询页也能搜到指数/ETF/板块，
    /// 用这列区分（分析仍只跑个股）。</summary>
    public string Type { get; init; } = "";
    /// <summary>StockMeta 里的原始 type 值——"加入主动仓"只对个股放行（指数/ETF/板块没有股东户数
    /// 等跟踪数据、也不是"选股"语义），用它判断而不是拿显示文本反推。</summary>
    public string TypeRaw { get; init; } = "";

    /// <summary>是不是个股——财务分析只对个股有意义（指数/ETF/板块没有财务报表）。
    /// 放在行上而不是让界面层去比 <see cref="TypeRaw"/>，是为了不让 Analyzer 为了一个常量
    /// 去引 Data 层的 SqliteStockMetaUpsert。</summary>
    public bool IsStock => TypeRaw == SqliteStockMetaUpsert.TypeStock;
    /// <summary>Plain mutable, same reasoning as ResultRowViewModel.IsSelected — only read when
    /// "加入主动仓" is clicked.</summary>
    public bool IsSelected { get; set; }
}

/// <summary>"查询" tab — type a code or name, list the matching stocks, and open any of them in the
/// K线行情详情窗口 (QuoteDetailWindow, the same pure-quote chart the other tabs' "行情详情" button
/// opens). Searches the local StockMeta (code+name); if StockMeta is empty (older data file) it
/// falls back to the codes present in the Bar table so at least code search still works.</summary>
public class QueryTabViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private const int MaxResults = 500;

    private readonly AnalyzerPaths _paths;
    private readonly IBarRepository _barRepository;
    private readonly Watchlist.JsonWatchlistStore _watchlistStore;
    /// <summary>底仓是**另一份存储**（core-positions.json），不是带标记的 WatchlistEntry——
    /// 见 <see cref="Watchlist.CorePosition"/>：字段不重叠，混一份会让晨检的短线纪律误伤底仓。</summary>
    private readonly Watchlist.JsonCorePositionStore _corePositionStore;

    public ObservableCollection<QueryRowViewModel> Results { get; } = new();

    private string _queryText = "";
    public string QueryText { get => _queryText; set => Set(ref _queryText, value); }

    private string _statusText = "输入股票代码或名称后点击\"查询\"（支持部分匹配）";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public RelayCommand SearchCommand { get; }

    // ── 勾中的票加到哪一层（2026-09-01 起给三个去处，之前只能进主动仓）──
    // 三层不是同一份数据加个标记，语义也不一样，所以给三个命令而不是一个带参数的：
    //   主动仓 = WatchlistEntry + InTradePool，每天进晨检、吃止损止盈纪律；
    //   底仓   = CorePosition（另一份 json），不设价格止损、按股息倒推仓位；
    //   自选股 = WatchlistEntry 不进池，纯算法验证样本，不进晨检。
    // 界面上是一个分裂按钮：主按钮=主动仓（最常用），另外两个收在 ▾ 里。
    public RelayCommand AddToTradePoolCommand { get; }
    public RelayCommand AddToCorePositionCommand { get; }
    public RelayCommand AddToWatchlistOnlyCommand { get; }

    /// <summary>加进去之后要通知"主动仓"/"自选股"/晨检三页刷新——由 MainViewModel 注入。</summary>
    public Action? TradePoolChanged { get; set; }

    /// <summary>加进底仓后要通知【底仓】页刷新——同上，由 MainViewModel 注入。</summary>
    public Action? CorePositionsChanged { get; set; }

    public QueryTabViewModel(AnalyzerPaths paths, IBarRepository barRepository,
        Watchlist.JsonWatchlistStore watchlistStore, Watchlist.JsonCorePositionStore corePositionStore)
    {
        _paths = paths;
        _barRepository = barRepository;
        _watchlistStore = watchlistStore;
        _corePositionStore = corePositionStore;
        SearchCommand = new RelayCommand(_ => Search());
        AddToTradePoolCommand = new RelayCommand(_ => AddSelectedToWatchlist(intoTradePool: true));
        AddToWatchlistOnlyCommand = new RelayCommand(_ => AddSelectedToWatchlist(intoTradePool: false));
        AddToCorePositionCommand = new RelayCommand(_ => AddSelectedToCorePosition());
    }

    /// <summary>勾中的行里挑出**能加进跟踪列表的个股**：指数/ETF/板块没有股东户数等跟踪数据、
    /// 也不是"选股"语义，直接跳过；没有K线的（新上市还没抓到）也跳过——三个去处的界面都要拿最新
    /// 收盘价算东西，没有价就只会显示成一行"—"。</summary>
    private List<(QueryRowViewModel Row, Bar Last)> TakeSelectedStocks(out int skippedType, out int skippedNoBar)
    {
        skippedType = skippedNoBar = 0;
        var result = new List<(QueryRowViewModel, Bar)>();
        foreach (var r in Results.Where(x => x.IsSelected))
        {
            if (r.TypeRaw != SqliteStockMetaUpsert.TypeStock) { skippedType++; continue; }
            var bars = _barRepository.Query(r.Code, Granularity.Day);
            if (bars.Count == 0) { skippedNoBar++; continue; }
            result.Add((r, bars[^1]));
        }
        return result;
    }

    private void ClearSelection()
    {
        foreach (var r in Results) r.IsSelected = false;
    }

    /// <summary>没勾任何行时给一句提示并返回 true（三个入口共用同一句话）。</summary>
    private bool NothingSelected()
    {
        if (Results.Any(r => r.IsSelected)) return false;
        StatusText = "先勾选要加入的行";
        return true;
    }

    /// <summary>
    /// 查询Tab的"加入主动仓 / 只加自选股"——跟各选股方法的 WatchlistAdder 语义一致（按
    /// Code+Method+DataDate 去重），但没有分析条件可存：Method 固定"查询"，DataDate/PriceAtPick
    /// 用该股最新一根日线，Criteria 为空。
    /// </summary>
    /// <param name="intoTradePool">
    /// true = 进【主动仓】：手工搜出来加的本来就是"我看好、想买卖"的票，纳入每日晨检体检。
    /// 这也是 2026-07-31 起这个按钮的默认行为。
    /// false = 只进【自选股】：2026-09-01 按用户要求加的第二条路——有时候只是想先挂着看一阵，
    /// 还没到要每天盯止损的程度。这条路**不会**动已存在记录的主动仓状态（否则跟上面那条没区别）。
    /// </param>
    private void AddSelectedToWatchlist(bool intoTradePool)
    {
        if (NothingSelected()) return;
        var picked = TakeSelectedStocks(out int skippedType, out int skippedNoBar);

        var entries = picked.Select(p => new Watchlist.WatchlistEntry
        {
            Code = p.Row.Code,
            Name = p.Row.Name,
            Method = "查询",
            Granularity = Granularity.Day,
            DataDate = p.Last.PeriodStart,
            PriceAtPick = p.Last.Close,
            AddedAt = DateTime.Now,
            InTradePool = intoTradePool,
            SatisfiedCount = 0,
            TotalCount = 0,
        }).ToList();

        int added = _watchlistStore.Add(entries);

        // 只在"进主动仓"这条路上补标已存在的记录：这只票可能之前是某个方法丢进自选的算法样本，
        // 现在用户从查询页手工加了一次，意思就是"我要买它"，不能因为记录已存在就静默什么都不做。
        var existingIds = new List<Guid>();
        if (intoTradePool)
        {
            existingIds = _watchlistStore.Load()
                .Where(e => entries.Any(n => n.Code == e.Code) && !e.IsInTradePool)
                .Select(e => e.Id).ToList();
            if (existingIds.Count > 0) _watchlistStore.SetTradePool(existingIds, true);
        }

        ClearSelection();
        if (added > 0 || existingIds.Count > 0) TradePoolChanged?.Invoke();

        string target = intoTradePool ? "主动仓" : "自选股";
        var parts = new List<string>
        {
            added > 0 ? $"已加入{target} {added} 只"
            : existingIds.Count > 0 ? $"已把 {existingIds.Count} 只已在自选里的票放进主动仓"
            : $"勾选的个股都已经在{target}里了",
        };
        if (skippedType > 0) parts.Add($"跳过 {skippedType} 个非个股（指数/ETF/板块不支持自选跟踪）");
        if (skippedNoBar > 0) parts.Add($"跳过 {skippedNoBar} 个无K线数据的");
        if (entries.Count > 0 && added < entries.Count)
            parts.Add(intoTradePool
                ? $"{entries.Count - added} 只本来就在自选里（已确保在主动仓中）"
                : $"{entries.Count - added} 只本来就在自选里（主动仓状态没动）");
        StatusText = string.Join("；", parts);
    }

    /// <summary>
    /// 查询Tab的"加入底仓"（2026-09-01 新增）——写的是 core-positions.json 这份**独立存储**，
    /// 跟上面那条路不共用记录。
    ///
    /// 只建记录，不代填任何买入，也不设目标年化股息（底仓法那页是按勾选只数摊分总目标，查询页
    /// 没有"总目标"这个输入，留 0 表示"还没设目标"，到【底仓】页用【录成交】时再填）。
    /// </summary>
    private void AddSelectedToCorePosition()
    {
        if (NothingSelected()) return;
        var picked = TakeSelectedStocks(out int skippedType, out int skippedNoBar);

        int added = _corePositionStore.Add(picked.Select(p => new Watchlist.CorePosition
        {
            Code = p.Row.Code,
            Name = p.Row.Name,
            Note = $"{DateTime.Today:yyyy-MM-dd} 从查询页手工加入",
        }));

        ClearSelection();
        if (added > 0) CorePositionsChanged?.Invoke();

        var parts = new List<string>
        {
            added > 0
                ? $"已加入底仓 {added} 只——成交明细到【底仓】页用【录成交】逐档录，目标年化股息也在那里设"
                : "勾选的个股都已经在底仓里了",
        };
        if (skippedType > 0) parts.Add($"跳过 {skippedType} 个非个股");
        if (skippedNoBar > 0) parts.Add($"跳过 {skippedNoBar} 个无K线数据的");
        StatusText = string.Join("；", parts);
    }

    private void Search()
    {
        Results.Clear();
        var q = (QueryText ?? "").Trim();
        if (q.Length == 0)
        {
            StatusText = "请输入股票代码或名称";
            return;
        }

        // 每次查询都重新读一遍本地标的清单——StockMeta 只有几千行，读一次很快，也能反映用户中途替换
        // 过的数据文件。2026-07-15 起用 GetAllInstruments（含个股+指数+ETF+板块）。StockMeta 为空（老数据
        // 文件没写过名称）时退回到 Bar 表里的6位个股代码清单。
        List<(string Code, string Name, string Type)> universe;
        try
        {
            universe = SqliteStockMetaUpsert.GetAllInstruments(_paths.CurrentDb);
            if (universe.Count == 0)
                universe = _barRepository.GetAllCodes().Select(c => (c, c, SqliteStockMetaUpsert.TypeStock)).ToList();
        }
        catch (Exception ex)
        {
            StatusText = $"读取本地标的清单失败：{ex.Message}";
            return;
        }

        var matches = universe
            .Where(s => s.Code.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrEmpty(s.Name) && s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            // 精确代码 > 代码前缀 > 其它（名称匹配等），同档内按代码排序
            .OrderBy(s => s.Code.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0
                        : s.Code.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(s => s.Code)
            .ToList();

        foreach (var s in matches.Take(MaxResults))
            Results.Add(new QueryRowViewModel
            {
                Code = s.Code,
                Name = string.IsNullOrEmpty(s.Name) ? s.Code : s.Name,
                // 行业分类只对个股有意义（按6位代码段判定）；指数/ETF/板块留空
                Board = s.Type == SqliteStockMetaUpsert.TypeStock ? IndustryClassifier.GetIndustry(s.Code) : "",
                Type = TypeDisplay(s.Type),
                TypeRaw = s.Type,
            });

        StatusText = matches.Count == 0
            ? $"没有找到匹配\"{q}\"的标的"
            : matches.Count > MaxResults
                ? $"匹配到 {matches.Count} 个，只显示前 {MaxResults} 个，请输入更精确的代码或名称"
                : $"匹配到 {matches.Count} 个";
    }

    private static string TypeDisplay(string type) => type switch
    {
        SqliteStockMetaUpsert.TypeIndex => "大盘指数",
        SqliteStockMetaUpsert.TypeEtf => "ETF",
        SqliteStockMetaUpsert.TypeBoard => "板块",
        _ => "个股",
    };
}
