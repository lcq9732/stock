using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StockPlatform.Desktop.Shared.Theme;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer;

/// <summary>
/// 资金面诊断面板（2026-09-16）——把 <see cref="CapitalDiagnosis"/> 画出来。
///
/// 跟 <see cref="FinancialAnalysisWindow"/> 里 AnalysisLineVm 一样的做法：**颜色和字重在 VM 里
/// 预先算好**，XAML 那边不写 converter。Logic 层只给语义（<see cref="CellTone"/>），
/// 映射成画刷是展示层的事——Logic 不认识 WPF 的 Brush。
/// </summary>
public partial class CapitalDiagnosisPanel : UserControl
{
    public CapitalDiagnosisPanel()
    {
        InitializeComponent();
    }

    /// <summary>诊断结果 → 界面。没有结果（Error）时清空，由调用方另行显示提示。</summary>
    public void Load(CapitalDiagnosis diagnosis)
    {
        DimensionList.ItemsSource = diagnosis.Error != null
            ? null
            : diagnosis.Dimensions.Select(ToVm).ToList();
    }

    private static DiagnosisDimensionVm ToVm(DiagnosisDimension d) => new()
    {
        Title = d.Title,
        Tooltip = d.Tooltip,
        Warnings = d.Warnings.ToList(),
        Unavailable = d.Unavailable ?? "",
        Conclusions = d.Conclusions.Select((c, i) => new DiagnosisConclusionVm
        {
            Text = c,
            // 第一句是主结论，加重；⚠ 开头的（背离）用警示色——这类结论信息量最高
            Brush = c.StartsWith("⚠") ? ThemeBrushes.Warn : ThemeBrushes.Foreground,
            Weight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal,
        }).ToList(),
        Tables = d.Tables.Select(ToVm).ToList(),
    };

    private static DiagnosisTableVm ToVm(DiagnosisTable t)
    {
        var rows = new List<DiagnosisRowVm>();

        // 表头当成普通一行画（淡色）——这些表只有三五行，为表头另起一套模板不值当
        if (t.Columns.Any(c => c.Header.Length > 0))
            rows.Add(new DiagnosisRowVm
            {
                Cells = t.Columns.Select(c => new DiagnosisCellVm
                {
                    Text = c.Header,
                    Width = c.Width > 0 ? c.Width : double.NaN,
                    Align = c.RightAlign ? TextAlignment.Right : TextAlignment.Left,
                    Brush = ThemeBrushes.Gray,
                    Font = UiFont,
                }).ToList(),
            });

        foreach (var row in t.Rows)
            rows.Add(new DiagnosisRowVm
            {
                Cells = row.Select((cell, i) =>
                {
                    var col = i < t.Columns.Count ? t.Columns[i] : new DiagnosisColumn("");
                    return new DiagnosisCellVm
                    {
                        Text = cell.Text,
                        Width = col.Width > 0 ? col.Width : double.NaN,
                        Align = col.RightAlign ? TextAlignment.Right : TextAlignment.Left,
                        Brush = BrushOf(cell.Tone),
                        // 数值列用等宽字体，小数点才对得齐（跟财务分析那几列一致）
                        Font = col.RightAlign ? MonoFont : UiFont,
                        Weight = cell.Tone == CellTone.Alert ? FontWeights.Bold : FontWeights.Normal,
                    };
                }).ToList(),
            });

        return new DiagnosisTableVm { Caption = t.Caption, Rows = rows };
    }

    private static readonly FontFamily MonoFont = new("Consolas");
    private static readonly FontFamily UiFont = new("Microsoft YaHei");

    /// <summary>
    /// 语义 → 画刷。
    ///
    /// ⚠ **正负数必须用 <see cref="ThemeBrushes.Red"/>/<see cref="ThemeBrushes.Green"/>，
    /// 不能用 Ok/Danger**（2026-09-16 用户指出：颜色弄反了）。这两套画刷是**故意分开**的，
    /// <see cref="ThemeBrushes"/> 的注释里写得很清楚：
    ///   · Red/Green —— **行情方向**，A股口径**红涨绿跌**
    ///   · Ok/Warn/Danger —— **判断结论**（好/需留意/不好），红取暗红、绿取正绿
    /// 我原先拿"结论配色"去表示正负数，于是涨跌幅变成了"正数绿、负数红"，跟同一个程序里
    /// 的行情图（涨红跌青，见 ChartTheme.Up/Down）和全市场看盘习惯全都反着。
    ///
    /// 这里用红/绿而不是行情图那套红/青：那套是黑底专业看盘图的配色（青在黑底上比深绿清楚），
    /// 而这个面板是普通文字表格，走主题资源的 Theme.Up/Theme.Down 那一档（浅色纯红纯绿、
    /// 深色提亮版），跟表格里其它"红涨绿跌"的文字一致。
    ///
    /// 至于"正负"本身不带褒贬——资金流出对空头是好消息，这个功能不替用户判断立场。
    /// 红绿只表示**符号**，扫一眼知道方向而已。
    /// </summary>
    private static Brush BrushOf(CellTone tone) => tone switch
    {
        CellTone.Positive => ThemeBrushes.Red,     // 涨 / 净流入 —— A股口径红
        CellTone.Negative => ThemeBrushes.Green,   // 跌 / 净流出 —— A股口径绿
        CellTone.Muted => ThemeBrushes.Gray,
        // Alert 是"需要注意"（放量下跌、背离），属于结论语义，用橙不用红绿——
        // 免得跟上面的涨跌符号色串味
        CellTone.Alert => ThemeBrushes.Warn,
        _ => ThemeBrushes.Foreground,
    };
}

public class DiagnosisDimensionVm
{
    public string Title { get; init; } = "";
    public string Tooltip { get; init; } = "";

    public List<string> Warnings { get; init; } = new();
    public Visibility WarningVisibility => Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Unavailable { get; init; } = "";
    public Visibility UnavailableVisibility =>
        Unavailable.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public List<DiagnosisConclusionVm> Conclusions { get; init; } = new();
    public Visibility ConclusionVisibility =>
        Conclusions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public List<DiagnosisTableVm> Tables { get; init; } = new();
}

public class DiagnosisConclusionVm
{
    public string Text { get; init; } = "";
    public Brush Brush { get; init; } = ThemeBrushes.Foreground;
    public FontWeight Weight { get; init; } = FontWeights.Normal;
}

public class DiagnosisTableVm
{
    public string Caption { get; init; } = "";
    public Visibility CaptionVisibility => Caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public List<DiagnosisRowVm> Rows { get; init; } = new();
}

public class DiagnosisRowVm
{
    public List<DiagnosisCellVm> Cells { get; init; } = new();
}

public class DiagnosisCellVm
{
    public string Text { get; init; } = "";

    /// <summary>NaN = 自适应内容宽度（XAML 的 Width="Auto" 在绑定里就是 NaN）。</summary>
    public double Width { get; init; } = double.NaN;

    public TextAlignment Align { get; init; } = TextAlignment.Left;
    public FontFamily Font { get; init; } = new("Microsoft YaHei");
    public Brush Brush { get; init; } = ThemeBrushes.Foreground;
    public FontWeight Weight { get; init; } = FontWeights.Normal;
}
