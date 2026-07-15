using CommunityToolkit.Mvvm.ComponentModel;
using StockPlatform.Mobile.Services;

namespace StockPlatform.Mobile.ViewModels;

/// <summary>选股方法的一个可调参数（绑定到界面上的一个数字输入框）。用 decimal 是为了跟 Avalonia
/// NumericUpDown 的类型对齐；读取时转回 double 给分析引擎。</summary>
public partial class ParamVM : ObservableObject
{
    public string Label { get; }
    public decimal Min { get; }
    public decimal Max { get; }
    public decimal Increment { get; }

    [ObservableProperty] private decimal _value;

    public double AsDouble => (double)Value;

    public ParamVM(MethodParam p)
    {
        Label = p.Label;
        Min = (decimal)p.Min;
        Max = (decimal)p.Max;
        Increment = (decimal)p.Increment;
        _value = (decimal)p.Default;
    }
}
