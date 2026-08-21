using System.Windows;
using System.Windows.Controls;

namespace StockPlatform.Analyzer;

/// <summary>
/// 把一段文字"塞进"给定的 TextBox 里而不出滚动条（2026-08-20 新增）——条件详情/条件说明那几个
/// 窗口共用。
///
/// 用户 2026-08-20 的要求是两件看着矛盾的事：**字要大**、**又不要出滚动条**。固定字号做不到，
/// 因为各方法的条件文字长短差很多（短线法8条几十行、底仓法带多行 Basis 更长）。所以改成
/// 打开时自动挑一个"能装下的最大字号"：从偏大的字号往下试，直到内容不再溢出。
///
/// 为什么用"试到不溢出"而不是按行数算：TextBox 开了自动折行后，实际显示行数跟文本里的 \n 行数
/// 不是一回事（一行长文字会占好几行），按行数算会算错。直接读 ExtentHeight/ViewportHeight
/// 最省事也最准，代价是每次要 UpdateLayout 一下——最多十几次迭代，感觉不出来。
/// </summary>
public static class TextFitter
{
    public const double MinFontSize = 11;
    public const double MaxFontSize = 26;

    /// <summary>自动适配时允许缩到的下限。比 <see cref="MinFontSize"/> 高一点——为了不出滚动条
    /// 把字缩成蚂蚁就违背初衷了，宁可留一点滚动。</summary>
    private const double MinAutoFitSize = 12;

    /// <summary>自动适配的起点（也是上限）。再大就算装得下也没必要，一行放不了几个字反而难读。</summary>
    private const double MaxAutoFitSize = 17;

    /// <summary>
    /// 从 <see cref="MaxAutoFitSize"/> 往下找第一个让**所有**给定 TextBox 都不出纵向滚动条的
    /// 字号并应用（多个框时取共同能装下的那个，否则分栏后两列字号会不一致）。控件还没完成布局
    /// （ViewportHeight 为 0）时什么都不做——调用方应该在 Loaded/SizeChanged 里调。
    /// </summary>
    public static void Fit(params TextBox[] boxes)
    {
        var live = boxes.Where(b => b.ViewportHeight > 0).ToArray();
        if (live.Length == 0) return;

        for (double size = MaxAutoFitSize; size >= MinAutoFitSize; size -= 0.5)
        {
            foreach (var b in live) b.FontSize = size;
            foreach (var b in live) b.UpdateLayout();
            // 留 2px 余量：ExtentHeight 正好等于 ViewportHeight 时某些 DPI 下仍会冒出滚动条
            if (live.All(b => b.ExtentHeight <= b.ViewportHeight + 2)) return;
        }
        foreach (var b in live) b.FontSize = MinAutoFitSize;
    }

    /// <summary>窗口初始尺寸 = 屏幕工作区的 <paramref name="ratio"/>，并居中。
    /// 详情窗口就是为了"看得全"才打开的，默认给到九成屏幕（用户 2026-08-20 要求开大）。</summary>
    public static void SizeToScreen(Window window, double ratio = 0.92)
    {
        var area = SystemParameters.WorkArea;
        window.Width = Math.Max(window.MinWidth, area.Width * ratio);
        window.Height = Math.Max(window.MinHeight, area.Height * ratio);
        window.Left = area.Left + (area.Width - window.Width) / 2;
        window.Top = area.Top + (area.Height - window.Height) / 2;
    }
}
