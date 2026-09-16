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
    /// 语义 → 画刷。跟同窗口的财务分析共用一套（Ok/Danger/Warn/Gray），不另起配色——
    /// 三列并排时两套红绿会让人以为含义不同。
    ///
    /// ⚠ <see cref="CellTone.Positive"/>/<see cref="CellTone.Negative"/> 指的是**数本身的正负**，
    /// 不是"好/坏"：资金流出对空头是好消息，这个功能不替用户判断立场。用绿/红只是因为
    /// "正数绿、负数红"在这份报告里全程一致，扫一眼就知道符号。
    /// </summary>
    private static Brush BrushOf(CellTone tone) => tone switch
    {
        CellTone.Positive => ThemeBrushes.Ok,
        CellTone.Negative => ThemeBrushes.Danger,
        CellTone.Muted => ThemeBrushes.Gray,
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
