using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using StockPlatform.Analyzer.Watchlist;

namespace StockPlatform.Analyzer;

/// <summary>
/// 个股分析笔记窗口（2026-08-27 新增）——见 <see cref="StockNoteStore"/> 和
/// <see cref="Data.Orchestration.AnalyzerPaths.NotesDir"/>：**库里存数据，这里存判断**。
///
/// 关窗前有未保存改动会拦一下问。这个拦截是必要的：笔记是手打的，误关一次就白写了，
/// 而数据类窗口关掉无所谓（随时重算）。
/// </summary>
public partial class StockNoteWindow : Window
{
    private readonly StockNoteStore _store;
    private readonly string _code;
    private readonly string _name;

    /// <summary>编辑器里的内容跟磁盘上是否已经不一致。</summary>
    private bool _dirty;

    /// <summary>初始化期间不把 TextChanged 当成用户改动。</summary>
    private bool _loading = true;

    public StockNoteWindow(StockNoteStore store, string code, string name)
    {
        InitializeComponent();
        _store = store;
        _code = code;
        _name = name;

        Title = $"分析笔记 — {code} {name}".TrimEnd();
        HeaderText.Text = $"{code} {name}".TrimEnd();

        var existing = _store.Read(code);
        Editor.Text = existing ?? StockNoteStore.NewTemplate(code, name, DateTime.Today);
        // 新建的模板算"未保存"——不点保存就不该在磁盘上留下一个只有模板的空笔记
        _dirty = existing == null;

        UpdatePathText(existing != null);
        _loading = false;

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Loaded += (_, _) => Editor.Focus();
    }

    private void UpdatePathText(bool onDisk)
    {
        var path = _store.PathOf(_code);
        PathText.Text = onDisk
            ? $"{path}　（最后修改 {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}）"
            : $"{path}　（还没有这份笔记，保存后创建）";
    }

    private void Editor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_loading) return;
        _dirty = true;
        StatusText.Text = "未保存";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+S 保存。放在 PreviewKeyDown 而不是 XAML 的 InputBindings：那需要一个
        // ICommand，为一个快捷键在窗口上挂命令属性不划算。
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SaveNote();
            e.Handled = true;
        }
    }

    private void SaveNote()
    {
        try
        {
            _store.Save(_code, Editor.Text);
            _dirty = false;
            bool stillThere = _store.Exists(_code);
            UpdatePathText(stillThere);
            StatusText.Text = stillThere
                ? $"已保存 {DateTime.Now:HH:mm:ss}"
                : "内容为空，已删除这份笔记";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "分析笔记",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveNote();

    private void InsertToday_Click(object sender, RoutedEventArgs e)
    {
        var updated = StockNoteStore.InsertTodaySection(Editor.Text, DateTime.Today);
        if (ReferenceEquals(updated, Editor.Text) || updated == Editor.Text)
        {
            StatusText.Text = $"{DateTime.Today:yyyy-MM-dd} 的小节已经有了";
            return;
        }
        Editor.Text = updated;
        Editor.CaretIndex = 0;
        StatusText.Text = "已插入今日小节（未保存）";
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _store.PathOf(_code);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 文件还没保存过时就只打开目录，不然 explorer /select 会报找不到
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打不开文件夹：{ex.Message}", "分析笔记",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_dirty) return;
        var r = MessageBox.Show(this, "笔记有未保存的改动，要保存吗？", "分析笔记",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (r == MessageBoxResult.Yes) SaveNote();
    }
}
