using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Color = System.Windows.Media.Color;

namespace StockPlatform.Desktop.Shared.Theme;

/// <summary>
/// 代码里（ViewModel、窗口 code-behind）用的语义画刷，替代 <c>Brushes.Firebrick</c> 这类固定色
/// ——那些在深色底上要么发暗、要么（<c>Brushes.Black</c>）根本看不见。
///
/// 关键点：每个属性是**同一个 SolidColorBrush 实例**，换主题时改的是它的 Color。WPF 里画刷是
/// Freezable，改 Color 会让所有绑定到它的元素自动重画——所以主题一切换，表格里那些"红涨绿跌"的
/// 文字立刻跟着变，不需要 ViewModel 重新发 PropertyChanged、更不用重新跑一遍分析。
/// （如果这里每次 new 一个画刷返回，切换主题就只有新算出来的行会变色，已经显示的不会。）
///
/// 浅色下的取值一律等于原来写死的那个颜色（Firebrick 还是 Firebrick），所以浅色主题下界面跟
/// 做主题之前一模一样；深色下统一提亮到在 #1E1E1E 上读得清。
/// </summary>
public static class ThemeBrushes
{
    /// <summary>涨（A股口径红涨绿跌）。</summary>
    public static SolidColorBrush Red { get; } = new();
    /// <summary>跌。</summary>
    public static SolidColorBrush Green { get; } = new();
    /// <summary>提醒/持仓/风险偏红的那一类。</summary>
    public static SolidColorBrush Firebrick { get; } = new();
    /// <summary>安全/正常的那一类绿。</summary>
    public static SolidColorBrush SeaGreen { get; } = new();
    /// <summary>次要说明文字。</summary>
    public static SolidColorBrush Gray { get; } = new();
    /// <summary>需要留意但不是坏消息（橙）。</summary>
    public static SolidColorBrush DarkOrange { get; } = new();
    /// <summary>中性提示（蓝）。</summary>
    public static SolidColorBrush SteelBlue { get; } = new();
    /// <summary>普通正文色——原来写死的 <c>Brushes.Black</c> 换成它。</summary>
    public static SolidColorBrush Foreground { get; } = new();

    // 财务分析那种"结论"配色：好/需留意/不好。跟上面的涨跌红绿是两回事——那是行情方向，
    // 这是判断结论，所以红取暗红一档、绿取偏正的绿，两套在同一屏里也不会串味。
    /// <summary>结论：好。</summary>
    public static SolidColorBrush Ok { get; } = new();
    /// <summary>结论：需要留意。</summary>
    public static SolidColorBrush Warn { get; } = new();
    /// <summary>结论：不好。</summary>
    public static SolidColorBrush Danger { get; } = new();

    static ThemeBrushes()
    {
        Refresh();
        ThemeManager.ThemeChanged += (_, _) => Refresh();
    }

    private static void Refresh()
    {
        bool dark = ThemeManager.IsDark;
        Set(Red, dark ? 0xFF6B6B : 0xFF0000);
        Set(Green, dark ? 0x4FC98A : 0x008000);
        Set(Firebrick, dark ? 0xE97070 : 0xB22222);
        Set(SeaGreen, dark ? 0x57C08A : 0x2E8B57);
        Set(Gray, dark ? 0x9AA0A6 : 0x808080);
        Set(DarkOrange, dark ? 0xF0A952 : 0xFF8C00);
        Set(SteelBlue, dark ? 0x6FB3E8 : 0x4682B4);
        Set(Foreground, dark ? 0xE4E4E4 : 0x000000);
        Set(Ok, dark ? 0x5FC98A : 0x1E7A33);
        Set(Warn, dark ? 0xE0A860 : 0xB86E00);
        Set(Danger, dark ? 0xF08A8A : 0xB02020);
    }

    private static void Set(SolidColorBrush brush, int rgb)
        => brush.Color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
