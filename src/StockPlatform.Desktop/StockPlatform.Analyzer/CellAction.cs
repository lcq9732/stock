using System.Windows;

namespace StockPlatform.Analyzer;

/// <summary>
/// 点这一列的单元格会打开什么窗口（2026-09-15）。
///
/// 原来每个列表页最右边都挂一条"操作"列，里面塞着【分析详情▾】【条件详情】【交易记录】…
/// 一排按钮。按钮多了以后那一列比数据还宽，而且每页塞的按钮都不一样，找起来还得先横向滚。
/// 现在改成**点数据本身**：点代码看分析详情、点名称看K线、点判据列看条件详情——
/// 要看哪一项就点哪一项，操作列整条去掉。
/// </summary>
public enum CellActionKind
{
    None,
    /// <summary>分析详情（左财务 / 右上观察项 / 右下趋势）——挂在"代码"列。</summary>
    Detail,
    /// <summary>行情详情（K线）——挂在"名称"列。</summary>
    Quote,
    /// <summary>条件详情——挂在各页的判据列（满足数 / 低于MA20% / 低位分 / 股息率 / 方法）。</summary>
    Criteria,
    /// <summary>交易记录 / 录成交（同一个窗口）——挂在买入、卖出、建仓进度列。</summary>
    Lots,
    /// <summary>仓位计算器——挂在"持仓股数"列。</summary>
    Sizing,
    /// <summary>分红历史柱状图——挂在底仓页的"连续分红"列。</summary>
    Dividend,
    /// <summary>板块指数K线——挂在板块榜的"板块"列（那一行不是个股，没有代码/名称两列）。</summary>
    BoardQuote,
}

/// <summary>
/// 把 <see cref="CellActionKind"/> 挂到 <see cref="System.Windows.Controls.DataGridColumn"/> 上：
/// XAML 里写一句 <c>local:CellAction.Kind="Detail"</c>，这一列的单元格就自动变成手型+链接色，
/// 点下去由 <c>MainWindow.DataGridCell_PreviewMouseLeftButtonDown</c> 统一分发。
///
/// 为什么还带一个只读的 <see cref="IsActionableProperty"/>：WPF 的 DataTrigger 只能比"等于某个值"，
/// 没法表达"Kind 不是 None"。与其给七个枚举值各写一条触发器，不如在 Kind 变化时顺手算出这个 bool，
/// 样式里只判它一条。
/// </summary>
public static class CellAction
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.RegisterAttached(
            "Kind", typeof(CellActionKind), typeof(CellAction),
            new PropertyMetadata(CellActionKind.None, OnKindChanged));

    private static readonly DependencyPropertyKey IsActionablePropertyKey =
        DependencyProperty.RegisterAttachedReadOnly(
            "IsActionable", typeof(bool), typeof(CellAction), new PropertyMetadata(false));

    public static readonly DependencyProperty IsActionableProperty = IsActionablePropertyKey.DependencyProperty;

    public static void SetKind(DependencyObject element, CellActionKind value) => element.SetValue(KindProperty, value);

    public static CellActionKind GetKind(DependencyObject element) => (CellActionKind)element.GetValue(KindProperty);

    public static bool GetIsActionable(DependencyObject element) => (bool)element.GetValue(IsActionableProperty);

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => d.SetValue(IsActionablePropertyKey, (CellActionKind)e.NewValue != CellActionKind.None);
}
