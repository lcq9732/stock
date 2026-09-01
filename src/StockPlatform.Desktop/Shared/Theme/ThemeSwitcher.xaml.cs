using System.Windows.Controls;

namespace StockPlatform.Desktop.Shared.Theme;

/// <summary>主题选择下拉的行为，见同名 xaml 的注释。基类写在 XAML 生成的 partial 里
/// （两个项目都开了 UseWindowsForms，这里再写一次 UserControl 会跟 WinForms 的同名类型撞上）。</summary>
public partial class ThemeSwitcher
{
    private bool _ready;

    public ThemeSwitcher()
    {
        InitializeComponent();

        // 先按当前主题把下拉选上，再放行 SelectionChanged——否则这一次程序性的选中会被
        // 当成用户操作，白白触发一次 Apply（还会把"跟随系统"覆盖存成同一个值）。
        ModeCombo.SelectedIndex = ThemeManager.Mode switch
        {
            ThemeMode.Dark => 1,
            ThemeMode.System => 2,
            _ => 0,
        };
        _ready = true;
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (ModeCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (!Enum.TryParse<ThemeMode>(tag, out var mode)) return;

        ThemeManager.Apply(mode);
    }
}
