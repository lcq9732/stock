using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using StockPlatform.Analyzer.Watchlist;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>界面上的一行观察项。</summary>
public class WatchItemRow
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Layer { get; init; } = "";
    public string Origin { get; init; } = "";
    public string Position { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Expr { get; init; } = "";
    public string Priority { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Thesis { get; init; } = "";
}

/// <summary>界面上的一条触发。</summary>
public class WatchHitRow
{
    public string Date { get; init; } = "";
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Priority { get; init; } = "";
    public string Message { get; init; } = "";
}

/// <summary>
/// 【观察项】页（2026-09-11），见 doc/watch-item-design.md M3。
///
/// 这一页的产出是**触发**，不是清单——清单只是让人能看见"现在在盯什么、为什么盯"。
/// 点【重算并求值】＝跑一遍 <see cref="WatchService.Run"/>：
/// 重算派生项（挂/摘）→ 逐条求值 → 记录新触发。
///
/// ⚠ 手写项（Origin=手写）在这里**只读**——它们由人直接编辑 <c>watch/items.json</c>，
/// 界面不提供编辑入口是有意的：观察项要满足"可判定/能改变动作/有归属层"三条准入
/// （设计文档 §1），做成随手能加的输入框就会变成许愿池。
/// </summary>
public class WatchTabViewModel : INotifyPropertyChanged
{
    private readonly WatchService _service;

    public ObservableCollection<WatchItemRow> Items { get; } = [];
    public ObservableCollection<WatchHitRow> Hits { get; } = [];

    private string _status = "点【重算并求值】开始。";
    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    private string _summary = "";
    public string Summary
    {
        get => _summary;
        private set { _summary = value; OnPropertyChanged(); }
    }

    public ICommand RunCommand { get; }
    public ICommand RefreshCommand { get; }

    public WatchTabViewModel(WatchService service)
    {
        _service = service;
        RunCommand = new RelayCommand(_ => Run());
        RefreshCommand = new RelayCommand(_ => Refresh());
    }

    /// <summary>只读当前状态，不重算——启动时和切页时用。</summary>
    public void Refresh()
    {
        try
        {
            FillItems(_service.LoadItems());
            FillHits(_service.LoadHits(DateTime.Today.Year));
            Status = $"当前 {Items.Count} 条观察项，{Hits.Count} 条触发记录（本年）。";
        }
        catch (Exception ex)
        {
            Status = $"读取失败：{ex.Message}";
        }
    }

    private void Run()
    {
        try
        {
            Status = "重算中…";
            var r = _service.Run();

            FillItems(_service.LoadItems());
            FillHits(_service.LoadHits(DateTime.Today.Year));

            Summary = $"观察项 {r.ItemCount} 条（新挂 {r.Added}、摘掉 {r.Expired}）；"
                      + $"求值 {r.Evaluated} 条，命中 {r.Hits} 条，其中新记录 {r.NewHits} 条。";
            Status = r.Hits == 0
                ? "跑完了：没有任何观察项触发。"
                : $"跑完了：{r.Hits} 条触发，A 档 {r.TopHits.Count(h => h.Priority == "A")} 条。";
        }
        catch (Exception ex)
        {
            Status = $"重算失败：{ex.Message}";
        }
    }

    private void FillItems(List<WatchItem> items)
    {
        Items.Clear();
        foreach (var i in items
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.Code))
        {
            Items.Add(new WatchItemRow
            {
                Code = i.Code, Name = i.Name, Layer = i.Layer, Origin = i.Origin,
                Position = i.Position, Kind = i.Kind, Expr = i.Expr,
                Priority = i.Priority, Reason = i.Reason, Thesis = i.Thesis ?? "",
            });
        }
    }

    private void FillHits(List<WatchHit> hits)
    {
        Hits.Clear();
        foreach (var h in hits
            .OrderByDescending(h => h.TriggerTradeDate)
            .ThenBy(h => h.Priority)
            .Take(300))
        {
            Hits.Add(new WatchHitRow
            {
                Date = h.TriggerTradeDate.ToString("yyyy-MM-dd"),
                Code = h.Code, Name = h.Name, Priority = h.Priority, Message = h.Message,
            });
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
