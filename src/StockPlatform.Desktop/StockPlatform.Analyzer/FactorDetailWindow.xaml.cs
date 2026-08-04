using System.Windows;

namespace StockPlatform.Analyzer;

/// <summary>
/// "因子法"里点某个因子的"说明"按钮弹出的窗口——显示该因子的构造公式、方向含义、作用说明
/// （经济学逻辑）以及本次评估的逐年IC等。2026-08-03 改成按钮弹窗：原来是选中行时在右侧固定
/// 面板显示，那个面板要占掉一整列宽度，而两张表改成左右排列后已经没有这个空间了。
/// 内容来自 FactorRowViewModel.DetailText，最终源头是 IFactor 的元数据（单一事实来源）。
/// </summary>
public partial class FactorDetailWindow : Window
{
    public FactorDetailWindow(string title, string body)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
