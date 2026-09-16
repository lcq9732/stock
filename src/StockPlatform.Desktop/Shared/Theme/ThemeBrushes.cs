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

    // 财务分析那种"结论"配色：好/需留意/不好。**按 A 股口径上色——好=红、不好=绿**，
    // 跟上面的涨跌红绿同向，不是欧美那套"绿=正数红=负数"。
    //
    // 2026-09-16 翻转过一次。原来是好=绿坏=红（国际惯例），理由是"这是结论不是方向，两套
    // 色值岔开一档就不会串味"——实际不成立：深色下 Ok #5FC98A 跟 Green #4FC98A 色相几乎
    // 一样，肉眼分不出，而且"归母净利 +1.08 亿"配绿色在 A 股用户眼里就是跌，看一眼就别扭。
    // 代价是知道的：✗ 配绿色警示力不如红，所以异常项另外靠加粗 + 顶部"需要留意的 N 项"汇总兜底。
    /// <summary>结论：好（红）。</summary>
    public static SolidColorBrush Ok { get; } = new();
    /// <summary>结论：需要留意。</summary>
    public static SolidColorBrush Warn { get; } = new();
    /// <summary>结论：不好（绿）。</summary>
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
        Set(Ok, dark ? 0xF08A8A : 0xB02020);
        Set(Warn, dark ? 0xE0A860 : 0xB86E00);
        Set(Danger, dark ? 0x5FC98A : 0x1E7A33);
    }

    private static void Set(SolidColorBrush brush, int rgb)
        => brush.Color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
