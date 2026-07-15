using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockPlatform.Logic.Models;
using StockPlatform.Mobile.Services;

namespace StockPlatform.Mobile.ViewModels;

public interface IStockRow { string Code { get; } string Name { get; } }
public record ScreenRow(string Code, string Name, string Metric) : IStockRow;
public record QueryRow(string Code, string Name) : IStockRow;
public record BoardListRow(string BoardCode, string Name, string ChangeText, int MemberCount);
public record BoardMemberRow(string Code, string Name, string ChangeText) : IStockRow;
public record WatchRow(string Code, string Name, string DataDate, string PickPrice, string Latest, string Change) : IStockRow;

/// <summary>手机端主视图：数据更新 + 选股(7种方法) + 板块热度 + 查询 + K线图。整个 App 一个壳，
/// 没在看K线时显示三个 Tab；点某只股票的『K线』切到图，返回回来。</summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly DataService _data = new();

    // ── 数据状态 / 更新 ──
    [ObservableProperty] private string _dbStatus = "";
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _isUpdating;

    // ── 选股（7种方法） ──
    public IReadOnlyList<ScreeningMethod> Methods { get; } = ScreeningMethods.All;
    [ObservableProperty] private ScreeningMethod _selectedMethod = ScreeningMethods.All[1];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _screenSummary = "";
    public ObservableCollection<ScreenRow> ScreenResults { get; } = new();
    /// <summary>当前所选方法的可调参数（跟桌面版各方法可调项一致），界面上每个参数一个数字输入框。</summary>
    public ObservableCollection<ParamVM> CurrentParams { get; } = new();

    partial void OnSelectedMethodChanged(ScreeningMethod value) => RebuildParams();

    private void RebuildParams()
    {
        CurrentParams.Clear();
        foreach (var p in SelectedMethod.Params) CurrentParams.Add(new ParamVM(p));
    }

    // ── 板块热度 ──
    [ObservableProperty] private bool _showConcept = true;
    [ObservableProperty] private string _boardSummary = "";
    [ObservableProperty] private string _boardMembersHeader = "点某个板块看成分股";
    public ObservableCollection<BoardListRow> Boards { get; } = new();
    public ObservableCollection<BoardMemberRow> BoardMembers { get; } = new();

    private BoardListRow? _selectedBoard;
    public BoardListRow? SelectedBoard
    {
        get => _selectedBoard;
        set { _selectedBoard = value; OnPropertyChanged(); LoadBoardMembers(); }
    }

    // ── 查询 ──
    [ObservableProperty] private string _queryText = "";
    [ObservableProperty] private string _querySummary = "";
    public ObservableCollection<QueryRow> QueryResults { get; } = new();

    // ── 自选股 ──
    private readonly WatchlistStore _watch;
    [ObservableProperty] private string _watchSummary = "";
    public ObservableCollection<WatchRow> Watchlist { get; } = new();

    // ── K线导航 ──
    [ObservableProperty] private bool _showChart;
    [ObservableProperty] private string _chartTitle = "";
    [ObservableProperty] private IReadOnlyList<Bar>? _chartBars;
    private string _chartCode = "", _chartName = "";

    public MainViewModel()
    {
        _watch = new WatchlistStore(_data.DataDir);
        RebuildParams();
        RefreshDbStatus();
        LoadWatchlist();
    }

    private void RefreshDbStatus() =>
        DbStatus = _data.DbExists
            ? $"数据：已就绪（最新到 {_data.LatestDay:yyyy-MM-dd}）"
            : "数据：本地还没有，请点『从服务端更新数据』。";

    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (IsUpdating) return;
        IsUpdating = true;
        var progress = new Progress<string>(s => UpdateStatus = s);
        try { await _data.UpdateFromServerAsync(progress); }
        catch (Exception ex) { UpdateStatus = "更新失败：" + ex.Message; }
        finally { IsUpdating = false; RefreshDbStatus(); }
    }

    // ── 选股 ──
    [RelayCommand]
    private async Task RunScreenAsync()
    {
        if (!_data.DbExists) { ScreenSummary = "没有本地数据库，请先更新数据。"; return; }
        var method = SelectedMethod;
        var values = CurrentParams.Select(x => x.AsDouble).ToList();
        IsBusy = true;
        ScreenResults.Clear();
        ScreenSummary = $"{method.Name} 分析中…";

        var passed = await Task.Run(() =>
        {
            var bar = _data.BarRepository;
            var names = _data.StockNames();
            var analyze = method.Factory(_data, values);
            var list = new List<StockScreenResult>();
            foreach (var code in bar.GetAllCodes())
            {
                StockScreenResult r;
                try { r = analyze(code, names.GetValueOrDefault(code, code)); }
                catch { continue; }
                if (r.Error == null && r.Passed) list.Add(r);
            }
            return list.OrderByDescending(r => r.SortScore ?? r.Criteria.Count(c => c.Satisfied)).ToList();
        });

        foreach (var r in passed)
        {
            string metric = r.SortScore.HasValue
                ? r.SortScore.Value.ToString("F0")
                : $"{r.Criteria.Count(c => c.Satisfied)}/{r.Criteria.Count}";
            ScreenResults.Add(new ScreenRow(r.Code, r.Name, metric));
        }
        ScreenSummary = $"{method.Name}：入选 {passed.Count} 只";
        IsBusy = false;
    }

    // ── 板块热度 ──
    partial void OnShowConceptChanged(bool value) => LoadBoards();

    [RelayCommand]
    private void LoadBoards()
    {
        Boards.Clear();
        BoardMembers.Clear();
        BoardMembersHeader = "点某个板块看成分股";
        if (!_data.DbExists) { BoardSummary = "没有本地数据库，请先更新数据。"; return; }

        var asOf = _data.BoardRepository.GetLatestAsOf();
        if (asOf == null) { BoardSummary = "本地没有板块数据（需要电脑端 Fetcher 先『拉取板块』并上传）。"; return; }

        var type = ShowConcept ? BoardType.Concept : BoardType.Industry;
        foreach (var b in _data.BoardRepository.QueryBoards(type))
            Boards.Add(new BoardListRow(b.BoardCode, b.Name,
                $"{(b.ChangePct >= 0 ? "+" : "")}{b.ChangePct:F2}%", b.MemberCount));
        BoardSummary = $"{(ShowConcept ? "概念/题材" : "行业")}板块 {Boards.Count} 个（按涨幅降序）；截至 {asOf:yyyy-MM-dd HH:mm}";
    }

    private void LoadBoardMembers()
    {
        BoardMembers.Clear();
        if (_selectedBoard == null || !_data.DbExists) { BoardMembersHeader = "点某个板块看成分股"; return; }
        var names = _data.StockNames();
        var bar = _data.BarRepository;
        var rows = new List<(BoardMemberRow Row, double Pct, bool Has)>();
        foreach (var code in _data.BoardRepository.QueryMembers(_selectedBoard.BoardCode))
        {
            var bars = bar.Query(code, Granularity.Day);
            bool has = bars.Count > 0;
            // 用最近两天收盘价现算——库里 pct_chg 列对单日/增量入库的行常年是0（见桌面版 BoardTabViewModel 说明）。
            double pct = bars.Count >= 2 && bars[^2].Close > 0 ? (bars[^1].Close - bars[^2].Close) / bars[^2].Close * 100 : 0;
            var text = has ? $"{(pct >= 0 ? "+" : "")}{pct:F2}%" : "—";
            rows.Add((new BoardMemberRow(code, names.GetValueOrDefault(code, code), text), pct, has));
        }
        foreach (var x in rows.OrderByDescending(x => x.Has).ThenByDescending(x => x.Pct))
            BoardMembers.Add(x.Row);
        BoardMembersHeader = $"「{_selectedBoard.Name}」成分股 {BoardMembers.Count} 只";
    }

    // ── 查询 ──
    [RelayCommand]
    private void Search()
    {
        QueryResults.Clear();
        if (!_data.DbExists) { QuerySummary = "没有本地数据库，请先更新数据。"; return; }
        var q = (QueryText ?? "").Trim();
        if (q.Length == 0) { QuerySummary = "请输入代码或名称"; return; }

        var universe = _data.StockNames();
        var matches = universe
            .Where(kv => kv.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
                      || (!string.IsNullOrEmpty(kv.Value) && kv.Value.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(kv => kv.Key.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0
                         : kv.Key.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(kv => kv.Key).Take(300).ToList();
        foreach (var kv in matches)
            QueryResults.Add(new QueryRow(kv.Key, string.IsNullOrEmpty(kv.Value) ? kv.Key : kv.Value));
        QuerySummary = matches.Count == 0 ? $"没有匹配\"{q}\"的股票" : $"匹配 {matches.Count} 只";
    }

    // ── 自选股 ──
    private void LoadWatchlist()
    {
        Watchlist.Clear();
        var entries = _watch.Load();
        var bar = _data.DbExists ? _data.BarRepository : null;
        foreach (var e in entries.OrderByDescending(x => x.AddedAt))
        {
            string latest = "—", change = "—";
            if (bar != null && DateTime.TryParse(e.DataDate, out var pick))
            {
                var bars = bar.Query(e.Code, Granularity.Day, start: pick);
                if (bars.Count > 0 && e.PriceAtPick > 0)
                {
                    var last = bars[^1];
                    latest = $"{last.Close:F2}({last.PeriodStart:MM-dd})";
                    var pct = (last.Close - e.PriceAtPick) / e.PriceAtPick * 100;
                    change = $"{(pct >= 0 ? "+" : "")}{pct:F2}%";
                }
            }
            Watchlist.Add(new WatchRow(e.Code, e.Name, e.DataDate, e.PriceAtPick.ToString("F2"), latest, change));
        }
        WatchSummary = Watchlist.Count == 0 ? "还没有自选（看某只K线时点『加自选』）" : $"自选 {Watchlist.Count} 只";
    }

    [RelayCommand]
    private void AddToWatchlist()
    {
        if (ChartBars == null || ChartBars.Count == 0 || _chartCode.Length == 0) return;
        var last = ChartBars[^1];
        var ok = _watch.Add(new WatchEntry
        {
            Code = _chartCode,
            Name = _chartName,
            DataDate = last.PeriodStart.ToString("yyyy-MM-dd"),
            PriceAtPick = last.Close,
            AddedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        });
        WatchSummary = ok ? $"已加入自选：{_chartCode} {_chartName}" : "已经在自选里了";
        LoadWatchlist();
    }

    [RelayCommand]
    private void RemoveWatch(WatchRow? row)
    {
        if (row == null) return;
        _watch.Remove(row.Code, row.DataDate);
        LoadWatchlist();
    }

    // ── K线 ──
    [RelayCommand]
    private void OpenChart(IStockRow? row)
    {
        if (row == null || !_data.DbExists) return;
        var bars = _data.BarRepository.Query(row.Code, Granularity.Day);
        if (bars.Count == 0) return;
        ChartBars = bars;
        _chartCode = row.Code;
        _chartName = row.Name;
        ChartTitle = $"{row.Code} {row.Name}";
        ShowChart = true;
    }

    [RelayCommand]
    private void CloseChart()
    {
        ShowChart = false;
        ChartBars = null;
    }
}
