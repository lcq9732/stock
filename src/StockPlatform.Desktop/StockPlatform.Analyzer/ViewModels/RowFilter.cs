using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>
/// 自选股 / 主动仓 / 底仓三页共用的查询框（2026-09-23新增，票多了找不到）——边打边筛，
/// <c>fields</c> 给出的任一字段包含关键字即显示，不分大小写。
///
/// 过滤挂在集合的默认视图上，所以集合本身始终是全量、表格显示的是视图；Reload 之后过滤条件保留。
/// 凡是"对勾选的行做操作"的按钮（删除/移出/加入主动仓/导出）都要走 <see cref="Visible"/>：
/// 被筛掉的行看不见，筛之前勾过也不能被一起删掉/移走。各页顶部的汇总/准确率统计仍按全量算。
/// </summary>
public class RowFilter<T> : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private readonly ObservableCollection<T> _all;
    private readonly Func<T, string?[]> _fields;
    private readonly ICollectionView _view;

    public RowFilter(ObservableCollection<T> all, Func<T, string?[]> fields)
    {
        _all = all;
        _fields = fields;
        _view = System.Windows.Data.CollectionViewSource.GetDefaultView(all);
        _view.Filter = Matches;
    }

    private string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            Raise();
            _view.Refresh();
            RaiseCounts();
        }
    }

    /// <summary>当前显示出来的行。</summary>
    public List<T> Visible => _view.Cast<T>().ToList();

    public string StatusText => _text.Trim().Length == 0
        ? $"共 {_all.Count} 只"
        : $"命中 {Visible.Count}/{_all.Count} 只";

    /// <summary>集合重新加载后调一下，刷新"共 N 只"。</summary>
    public void RaiseCounts() => Raise(nameof(StatusText));

    private bool Matches(object o)
    {
        var q = _text.Trim();
        if (q.Length == 0) return true;
        return o is T row && _fields(row).Any(s => s != null && s.Contains(q, StringComparison.OrdinalIgnoreCase));
    }
}
