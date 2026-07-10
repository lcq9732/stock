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
public class QueryRowViewModel
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Board { get; init; } = "";
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

    public ObservableCollection<QueryRowViewModel> Results { get; } = new();

    private string _queryText = "";
    public string QueryText { get => _queryText; set => Set(ref _queryText, value); }

    private string _statusText = "输入股票代码或名称后点击\"查询\"（支持部分匹配）";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public RelayCommand SearchCommand { get; }

    public QueryTabViewModel(AnalyzerPaths paths, IBarRepository barRepository)
    {
        _paths = paths;
        _barRepository = barRepository;
        SearchCommand = new RelayCommand(_ => Search());
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

        // 每次查询都重新读一遍本地股票清单——StockMeta 只有几千行，读一次很快，也能反映用户中途
        // 替换过的数据文件。StockMeta 为空（老数据文件没写过名称）时退回到 Bar 表里的代码清单。
        List<(string Code, string Name)> universe;
        try
        {
            universe = SqliteStockMetaUpsert.GetAll(_paths.TotalDb);
            if (universe.Count == 0)
                universe = _barRepository.GetAllCodes().Select(c => (c, c)).ToList();
        }
        catch (Exception ex)
        {
            StatusText = $"读取本地股票清单失败：{ex.Message}";
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
                Board = IndustryClassifier.GetIndustry(s.Code),
            });

        StatusText = matches.Count == 0
            ? $"没有找到匹配\"{q}\"的股票"
            : matches.Count > MaxResults
                ? $"匹配到 {matches.Count} 只，只显示前 {MaxResults} 只，请输入更精确的代码或名称"
                : $"匹配到 {matches.Count} 只";
    }
}
