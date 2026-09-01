using System.Globalization;
using System.Windows;
using System.Windows.Data;
using StockPlatform.Fetcher.Planning;

namespace StockPlatform.Fetcher.ViewModels;

/// <summary>
/// 重复规则是不是「手动」→ 显示/隐藏（2026-08-31 新增）。
///
/// ════ 为什么不直接绑 CanEnable ════
/// 计划表的"启用"格要跟着"重复"格变：选了「手动」就把勾选框换成一个"—"。原先绑的是
/// <c>PlanItemViewModel.CanEnable</c>（一个由 Repeat 算出来的只读属性），实际用下来**换了下拉
/// 之后那一格不刷新**——改成手动了勾选框还在、改回定期了又还是"—"。
///
/// 现在直接绑 <c>Repeat</c> 本身：那个属性是下拉框双向绑着的，值一变必然发通知（否则下拉框
/// 自己的显示也不会跟着变），依赖链最短、不会漏。
///
/// <c>ConverterParameter=invert</c> 时反过来——同一个格子里"勾选框"和"—"正好互为反面。
/// </summary>
public sealed class ManualRepeatToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isManual = value is RepeatKind.Manual;
        bool invert = parameter as string == "invert";
        // 不带参数：手动才显示（那个"—"）；带 invert：手动之外才显示（勾选框）。
        return isManual != invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
