using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Tasks;

/// <summary>
/// 当前用哪条板块通道（2026-09-21）——跟 <see cref="BarSourceHolder"/> 同一个道理。
///
/// 板块通道是**运行期可变**的：`fetcher-settings.json` 里的 `BoardMemberChannel` 决定走
/// 东财终端本地文件 / 菜单 JSON / push2 / 浏览器，点【重新读取配置】会现造一个新的
/// （见 <c>MainViewModel</c> 里 `ReplaceBoardFetcher` 那一段）。任务是启动时注册的，
/// 注册时把 fetcher 定死的话，改完配置重载一次，任务手里还攥着旧通道。
///
/// ⚠ 通道**不只是快慢的差别**，判据也跟着变：<see cref="IBoardFetcher.MemberFreshFor"/>
/// 挂在通道上（读本地文件的那条不节流、每轮全量覆盖），而 push2 熔断只该拦走网络的那几条。
/// 所以任务每轮开跑必须读 <see cref="Current"/>，不能缓存。
/// </summary>
public sealed class BoardFetcherHolder(IBoardFetcher initial)
{
    public IBoardFetcher Current { get; set; } = initial;
}
