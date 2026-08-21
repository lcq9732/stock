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
    /// <summary>StockMeta 里的原始 type 值——"加入交易池"只对个股放行（指数/ETF/板块没有股东户数
    /// 等跟踪数据、也不是"选股"语义），用它判断而不是拿显示文本反推。</summary>
    public string TypeRaw { get; init; } = "";
    /// <summary>Plain mutable, same reasoning as ResultRowViewModel.IsSelected — only read when
    /// "加入交易池" is clicked.</summary>
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

    public ObservableCollection<QueryRowViewModel> Results { get; } = new();

    private string _queryText = "";
    public string QueryText { get => _queryText; set => Set(ref _queryText, value); }

    private string _statusText = "输入股票代码或名称后点击\"查询\"（支持部分匹配）";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public RelayCommand SearchCommand { get; }
    public RelayCommand AddToWatchlistCommand { get; }

    /// <summary>加进交易池后要通知"主动仓"/"自选股"/晨检三页刷新——由 MainViewModel 注入。</summary>
    public Action? TradePoolChanged { get; set; }

    public QueryTabViewModel(AnalyzerPaths paths, IBarRepository barRepository, Watchlist.JsonWatchlistStore watchlistStore)
    {
        _paths = paths;
        _barRepository = barRepository;
        _watchlistStore = watchlistStore;
        SearchCommand = new RelayCommand(_ => Search());
        AddToWatchlistCommand = new RelayCommand(_ => AddSelectedToWatchlist());
    }

    /// <summary>查询Tab的"加入交易池"——跟各选股方法的 WatchlistAdder 语义一致（按 Code+Method+DataDate
    /// 去重），但没有分析条件可存：Method 固定"查询"，DataDate/PriceAtPick 用该股最新一根日线，
    /// Criteria 为空。只放行个股；指数/ETF/板块没有股东户数等跟踪数据、也不是"选股"，直接跳过并提示。
    ///
    /// 2026-07-31起直接标记 InTradePool=true：从这里手工搜出来加进去的票，本来就是"我看好、想买卖"的
    /// （不像各选股方法丢进来的那些只是算法验证样本），所以直接进"主动仓"页、纳入每日晨检体检。</summary>
    private void AddSelectedToWatchlist()
    {
        var selected = Results.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText = "先勾选要加入交易池的行";
            return;
        }

        int skippedType = 0, skippedNoBar = 0;
        var entries = new List<Watchlist.WatchlistEntry>();
        foreach (var r in selected)
        {
            if (r.TypeRaw != SqliteStockMetaUpsert.TypeStock) { skippedType++; continue; }
            var bars = _barRepository.Query(r.Code, Granularity.Day);
            if (bars.Count == 0) { skippedNoBar++; continue; }
            var last = bars[^1];
            entries.Add(new Watchlist.WatchlistEntry
            {
                Code = r.Code,
                Name = r.Name,
                Method = "查询",
                Granularity = Granularity.Day,
                DataDate = last.PeriodStart,
                PriceAtPick = last.Close,
                AddedAt = DateTime.Now,
                InTradePool = true,   // 手工搜出来加的 = 我看好想买卖的，直接进"主动仓"
                SatisfiedCount = 0,
                TotalCount = 0,
            });
        }

        int added = _watchlistStore.Add(entries);
        // 已存在的记录（去重挡下的）也要确保在交易池里——比如这只票之前是某个方法丢进自选的算法样本，
        // 现在用户从查询页手工加了一次，意思就是"我要买它"，不能因为记录已存在就静默什么都不做。
        var existingIds = _watchlistStore.Load()
            .Where(e => entries.Any(n => n.Code == e.Code) && !e.IsInTradePool)
            .Select(e => e.Id).ToList();
        if (existingIds.Count > 0) _watchlistStore.SetTradePool(existingIds, true);
        foreach (var r in selected) r.IsSelected = false;
        if (added > 0 || existingIds.Count > 0) TradePoolChanged?.Invoke();

        var parts = new List<string>
        {
            added > 0 ? $"已加入交易池 {added} 只"
            : existingIds.Count > 0 ? $"已把 {existingIds.Count} 只已在自选里的票放进交易池"
            : "勾选的个股都已经在交易池里了",
        };
        if (skippedType > 0) parts.Add($"跳过 {skippedType} 个非个股（指数/ETF/板块不支持自选跟踪）");
        if (skippedNoBar > 0) parts.Add($"跳过 {skippedNoBar} 个无K线数据的");
        if (entries.Count > 0 && added < entries.Count) parts.Add($"{entries.Count - added} 只本来就在自选里（已确保在交易池中）");
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
