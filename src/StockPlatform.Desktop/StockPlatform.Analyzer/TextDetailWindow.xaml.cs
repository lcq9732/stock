using System.Windows;
using System.Windows.Controls;

namespace StockPlatform.Analyzer;

/// <summary>
/// 长文本详情窗口（2026-08-20 新增）——短线法的"条件详情"和全部方法的 ⓘ"条件说明"都用它。
///
/// 为什么不用 MessageBox：那些文本动辄几十行、里面还有靠空格和 ├└ 对齐的表格，而 MessageBox
/// 的**宽度和字体都改不了**，挤在系统对话框里既看不全也对不齐（用户 2026-08-20 反馈"太窄、
/// 字体小，毕竟打开就是为了看详情的"）。
///
/// 2026-08-20 二次调整（用户要求"开大、不要出现滚动条"）三件事：
/// ① 窗口开到屏幕九成大；
/// ② 字号自动挑一个能装下的最大值（<see cref="TextFitter"/>）；
/// ③ 单列还装不下就**自动分两列**——这些文本最长的一行也就 90 来个半角宽，而全屏窗口横向能放
///    200 多个，单列时右边半屏是空的、内容却在纵向溢出，把条目挪一半过去正好。
///
/// 手动调过字号之后就不再自动改了：用户既然自己定了大小，跟着窗口变化去覆盖它会很烦人。
/// </summary>
public partial class TextDetailWindow : Window
{
    /// <summary>条目之间的分隔（各处生成条件文字时都是用空行隔开每一条）。分列时按这个切，
    /// 保证不会把一个条目劈成两半。</summary>
    private const string ItemSeparator = "\n\n";

    private readonly string _body;

    /// <summary>用户手动点过 +/− 之后就停止自动适配，见类注释。</summary>
    private bool _fontManuallySet;

    /// <summary>已经排过版了——避免 SizeChanged 反复触发时来回抖动（分两列会改变尺寸、
    /// 又触发 SizeChanged，不设这个闸就可能在一列/两列之间来回跳）。</summary>
    private bool _laidOut;

    /// <summary>正在排版中，挡住重入。排版里要调 UpdateLayout()，而那可能同步触发 SizeChanged，
    /// 那个处理器又会调回排版——窗口尺寸没变时其实不会触发，但这条路径不值得赌。</summary>
    private bool _layingOut;

    public TextDetailWindow(string title, string header, string body)
    {
        InitializeComponent();
        Title = title;
        HeaderText.Text = header;
        _body = body;
        BodyText.Text = body;

        TextFitter.SizeToScreen(this);
        Loaded += (_, _) => LayoutBody();
        SizeChanged += (_, _) =>
        {
            // 用户手动拉窗口大小时重新排一次（可能从"要两列"变成"一列就够"）
            _laidOut = false;
            LayoutBody();
        };
    }

    /// <summary>
    /// 先按单列试着装；装不下就把条目分到第二列再试。两种情况都用 <see cref="TextFitter.Fit"/>
    /// 挑能装下的最大字号。
    /// </summary>
    private void LayoutBody()
    {
        if (_laidOut || _fontManuallySet || _layingOut) return;
        if (BodyText.ViewportHeight <= 0) return;

        _laidOut = true;
        _layingOut = true;
        try
        {
            // ① 单列
            UseSingleColumn();
            TextFitter.Fit(BodyText);
            if (!Overflows(BodyText)) return;

            // ② 单列在最小字号下仍然溢出 → 分两列
            var (left, right) = SplitInHalf(_body);
            if (right.Length == 0) return;      // 只有一个条目，劈不开，只能让它滚

            BodyText.Text = left;
            BodyText2.Text = right;
            SecondColumn.Width = new GridLength(1, GridUnitType.Star);
            SecondBorder.Visibility = Visibility.Visible;
            UpdateLayout();

            TextFitter.Fit(BodyText, BodyText2);
        }
        finally
        {
            _layingOut = false;
        }
    }

    private void UseSingleColumn()
    {
        BodyText.Text = _body;
        BodyText2.Text = "";
        SecondColumn.Width = new GridLength(0);
        SecondBorder.Visibility = Visibility.Collapsed;
        UpdateLayout();
    }

    private static bool Overflows(TextBox box) => box.ExtentHeight > box.ViewportHeight + 2;

    /// <summary>按条目把文本分成两半——以**行数**均分（不是按条目个数），否则一条 20 行的和一条
    /// 2 行的会把两列拉得一长一短。</summary>
    private static (string Left, string Right) SplitInHalf(string body)
    {
        var items = body.Split(ItemSeparator);
        if (items.Length < 2) return (body, "");

        var lineCounts = items.Select(x => x.Count(c => c == '\n') + 1).ToArray();
        int total = lineCounts.Sum();

        int running = 0, cut = 0;
        for (int i = 0; i < items.Length; i++)
        {
            running += lineCounts[i];
            // 一旦累计过半就在这里切；至少给左列留一条、也至少给右列留一条
            if (running >= total / 2.0 && i < items.Length - 1)
            {
                cut = i + 1;
                break;
            }
        }
        if (cut == 0) cut = items.Length - 1;

        return (string.Join(ItemSeparator, items.Take(cut)),
                string.Join(ItemSeparator, items.Skip(cut)));
    }

    private void SetFontSize(double size)
    {
        _fontManuallySet = true;
        double clamped = Math.Clamp(size, TextFitter.MinFontSize, TextFitter.MaxFontSize);
        BodyText.FontSize = clamped;
        BodyText2.FontSize = clamped;
    }

    private void SmallerFont_Click(object sender, RoutedEventArgs e) => SetFontSize(BodyText.FontSize - 1);

    private void LargerFont_Click(object sender, RoutedEventArgs e) => SetFontSize(BodyText.FontSize + 1);

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 复制的始终是完整原文，跟当前分几列无关
            Clipboard.SetText(_body);
        }
        catch (Exception)
        {
            // 剪贴板被别的进程占用时 SetText 会抛——静默算了，用户还能手动拖选复制。
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>各处调用的统一入口：<paramref name="owner"/> 可以为 null（ViewModel 里拿不到窗口时
    /// 退回用主窗口当 Owner，保证不会跑到主窗口后面去）。</summary>
    public static void Show(string title, string header, string body, Window? owner = null)
    {
        var window = new TextDetailWindow(title, header, body)
        {
            Owner = owner ?? Application.Current?.MainWindow,
        };
        window.ShowDialog();
    }
}
