using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Tasks;

/// <summary>
/// 当前用哪个K线源（2026-09-21）。
///
/// K线源是**运行期可变**的：它由 `fetcher-settings.json` 的 `BarSource` 决定，而那份配置
/// 在界面上点【重新加载设置】就会重算一次（见 <c>MainViewModel.ResolveBarSource</c>）。
/// 而任务是在 `App.xaml.cs` 里注册的——注册时把源定死的话，改完配置重载一次，
/// 任务手里还攥着旧的那个。
///
/// 所以传给任务的是这个可变持有者，不是 <see cref="NamedBarSource"/> 本身：
/// 组合根建一个，任务每轮开跑时读 <see cref="Current"/>，界面在重算之后写回来。
/// </summary>
public sealed class BarSourceHolder(NamedBarSource initial)
{
    public NamedBarSource Current { get; set; } = initial;
}
