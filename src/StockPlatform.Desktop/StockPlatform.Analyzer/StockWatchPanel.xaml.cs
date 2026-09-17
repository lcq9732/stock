using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using StockPlatform.Analyzer.ViewModels;
using StockPlatform.Analyzer.Watchlist;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer;

/// <summary>
/// 单只票的观察项面板（2026-09-14），嵌在【分析详情】窗口右上角。
///
/// 原来这是个独立窗口 <c>StockWatchWindow</c>。用户的要求是"个股的观察项和财务分析
/// 放一个页面，一看就有了，也可对比数据"——**只是布局合并，内容各自不变**。
/// 于是窗口拆成这个 UserControl，塞进财务分析窗右栏的上半部分。
///
/// ⚠ 这里**只读不写**任何观察项：没跟踪的票临时算一遍就显示（见
/// <see cref="WatchService.ReadEvents"/>）。点一下详情就悄悄多出一堆观察项，
/// 那份清单很快就没人信了。唯一会写文件的是【分析笔记】，那是人主动写的。
/// </summary>
public partial class StockWatchPanel : UserControl
{
    private WatchService? _service;
    private StockNoteStore? _notes;
    private string _code = "";
    private string _name = "";

    private readonly ObservableCollection<WatchEventLine> _events = [];

    public StockWatchPanel()
    {
        InitializeComponent();
        EventsGrid.ItemsSource = _events;
    }

    /// <summary>
    /// 装上一只票的"轻"那一半：记住是谁、把【分析笔记】按钮的状态点出来、先显示"读取中"。
    /// 真正的事件由调用方在后台读完后交给 <see cref="ApplyEvents"/>——
    /// 读一只票的事件要打十来条 SQL，其中按代码查 Lhb 是全表扫，不能在 UI 线程上做
    /// （2026-09-17 拆开；原先是一个同步的 <c>Load</c>）。
    /// </summary>
    public void BeginLoad(WatchService service, StockNoteStore notes, string code, string name)
    {
        _service = service;
        _notes = notes;
        _code = code;
        _name = name;

        _events.Clear();
        EventsGrid.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Visible;
        EmptyText.Text = "读取中…";
        CountText.Text = "";

        // 这一句只查文件在不在，是本地小文件、毫秒级，不值得为它转异步
        NoteButton.Content = notes.Exists(code) ? "分析笔记 ●" : "分析笔记";
    }

    /// <summary>
    /// 把后台读好的事件填进来（UI 线程调）。<see cref="BeginLoad"/> 之后调一次。
    /// </summary>
    public void ApplyEvents(IReadOnlyList<WatchEvent> events)
    {
        _events.Clear();
        foreach (var e in events)
            _events.Add(new WatchEventLine
            {
                Date = e.Date.ToString("yyyy-MM-dd"),
                Text = e.Text,
                Tip = e.Tip ?? "",
                // 缩进 12px（窗口里只占右栏，比独立窗口窄，20px 太吃宽度）
                Margin = new Thickness(e.Indent * 12, 0, 0, 0),
                IsFuture = e.IsFuture,
            });

        bool empty = _events.Count == 0;
        EventsGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        // 说清是"库里没有"而不是"这票没事"——数据缺失和确实没动静是两回事
        EmptyText.Text = empty ? "库里没有这只票的事件（可能还没抓到，或它确实没有可跟踪的事项）。" : "";
        CountText.Text = empty ? "" : $"{_events.Count} 条";
    }

    private void Note_Click(object sender, RoutedEventArgs e)
    {
        if (_notes is null) return;
        new StockNoteWindow(_notes, _code, _name) { Owner = Window.GetWindow(this) }.ShowDialog();
        NoteButton.Content = _notes.Exists(_code) ? "分析笔记 ●" : "分析笔记";
    }
}
