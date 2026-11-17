using System.Windows;

namespace StockPlatform.Analyzer;

/// <summary>
/// 把 DataContext 递给不在可视树里、拿不到它的地方（如 DataGrid 列头里的控件）。
/// 放进 DataGrid.Resources：<c>&lt;local:BindingProxy x:Key="Proxy" Data="{Binding}" /&gt;</c>，
/// 用的地方写 <c>{Binding Data.Xxx, Source={StaticResource Proxy}}</c>。
/// Freezable 放在资源里能继承宿主的 DataContext，这是它能工作的原因。
///
/// 每日晨检"方法"列头的过滤下拉框原来用 RelativeSource 往上找 DataGrid，实机里绑不上
/// （下拉是空的、点开没东西，2026-11-17 用户反馈"点不了"），改走这个。
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
