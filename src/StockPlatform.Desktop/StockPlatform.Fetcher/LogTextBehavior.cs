using System.Collections;
using System.Collections.Specialized;
using System.Windows;
// 本项目 UseWindowsForms=true 且未移除 WinForms 隐式 using，TextBox 会与 WinForms.TextBox 撞名——
// 别名指向 WPF 的 TextBox（日志框是 WPF 控件）。
using TextBox = System.Windows.Controls.TextBox;

namespace StockPlatform.Fetcher;

/// <summary>
/// 把一个日志集合（ObservableCollection&lt;string&gt;）实时同步进只读 <see cref="TextBox"/> 的
/// Text，让"运行日志"可以用鼠标拖选 / Ctrl+C / Ctrl+A 复制——原来用 ListBox 显示，列表项里的文字
/// 选不中、没法复制。用法：<c>&lt;TextBox local:LogText.Lines="{Binding LogLines}" IsReadOnly="True"/&gt;</c>。
/// 集合按"新在前"(Insert 0) 累积，这里原样 join，最新一行在最上面，跟原 ListBox 一致。
/// </summary>
public static class LogText
{
    public static readonly DependencyProperty LinesProperty = DependencyProperty.RegisterAttached(
        "Lines", typeof(IEnumerable), typeof(LogText), new PropertyMetadata(null, OnLinesChanged));

    public static void SetLines(DependencyObject o, IEnumerable value) => o.SetValue(LinesProperty, value);
    public static IEnumerable GetLines(DependencyObject o) => (IEnumerable)o.GetValue(LinesProperty);

    private static void OnLinesChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBox tb) return;

        void Update()
        {
            tb.Text = GetLines(tb) is IEnumerable en
                ? string.Join(Environment.NewLine, en.Cast<object?>().Select(x => x?.ToString() ?? ""))
                : "";
        }

        // 集合内容变化(Insert/Clear)时重刷。日志集合是 get-only、只绑定一次，这里不解绑旧订阅
        // （对象活到程序退出，不会泄漏成问题）。用 Dispatcher 兜底非UI线程的意外来源。
        //
        // ⚠ 合并成一次重刷（2026-09-29）：一批日志是逐行 Insert 的，原来每插一行就排一次 Update，
        //    而 Update 要把**全部行**重新拼接——一批 50 行就是 50 次整段重拼。行数一多，
        //    UI 线程排队排到永远追不上（实测一夜 11 万行，窗口卡死 5 小时）。
        //    现在一批改动只排一次，等这批 Insert/RemoveAt 都做完再拼。
        bool pending = false;
        void Schedule()
        {
            if (pending) return;
            pending = true;
            tb.Dispatcher.BeginInvoke(new Action(() => { pending = false; Update(); }),
                                      System.Windows.Threading.DispatcherPriority.Background);
        }

        if (e.NewValue is INotifyCollectionChanged ncc)
            ncc.CollectionChanged += (_, _) => Schedule();
        Update();
    }
}
