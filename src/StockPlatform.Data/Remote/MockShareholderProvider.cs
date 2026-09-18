using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 股东数据的**离线模拟**源（2026-09-18）——一个请求都不发，凭空造出近四个报告期的
/// 户数 + 十大股东 + 十大流通股东。跟 <see cref="MockBarFetcher"/> 同一个用途和同一道闸：
/// 只在 DEBUG 构建里、且 <c>OfflineMock</c> 开关打开时才会被装上。
///
/// ════ 为什么要它 ════
/// 【拉取股东数据】2026-09-18 迁成新式任务（<c>ShareholderTask</c>）。单测能覆盖判据和
/// 落库，但覆盖不了"界面点下去之后整条链对不对"——而那恰恰是这个项目栽过的地方
/// （单测测不出调用环路和 UI 链）。真跑一轮又是 11130 个新浪请求、一小时，
/// 还得看新浪当天让不让抓（2026-09-18 上午整域 456）。
/// 选上这个源，Debug 实例里就能把整条链真跑一遍，网络那层换成本地构造。
///
/// ════ 造出来的数据长什么样 ════
/// 最新一期取 <see cref="FinancialFetchPlanner.LatestExpectedReportPeriod"/>（跟判据同口径，
/// 所以"抓完就该判定为最新"这件事测得出来），再往前推三期，共四期。
/// 户数给递减的假数（模拟筹码集中），十大股东/十大流通股东各 10 名。
///
/// ════ 两个标记代码 ════
///   · 代码含 <see cref="FailMarker"/> ⇒ 抛 <see cref="RateLimitedException"/>，
///     用来验失败名单、熔断、以及"失败不算抓过"；
///   · 代码含 <see cref="EmptyMarker"/> ⇒ 返回空聚合，用来验"空结果不删库里已有数据"
///     （<c>ReplaceByCode</c> 是先删后插，这条判据错一次就会把那只票的历史清空）。
/// </summary>
public sealed class MockShareholderProvider : IShareholderProvider
{
    /// <summary>含这个片段的代码一律按限流失败。</summary>
    public const string FailMarker = "999";

    /// <summary>含这个片段的代码返回空聚合（模拟"数据源这只票就是没有"）。</summary>
    public const string EmptyMarker = "888";

    /// <summary>造几期。</summary>
    private const int Periods = 4;

    public event Action<string>? OnStatus;

    public Task<ShareholderData> GetAsync(string code, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (code.Contains(FailMarker, StringComparison.Ordinal))
            throw new RateLimitedException($"[离线模拟] {code} 按 FailMarker 规则失败");

        var data = new ShareholderData();
        if (code.Contains(EmptyMarker, StringComparison.Ordinal))
        {
            OnStatus?.Invoke($"[离线模拟] {code} 按 EmptyMarker 规则返回空聚合");
            return Task.FromResult(data);
        }

        var now = DateTime.Now;
        var latest = FinancialFetchPlanner.LatestExpectedReportPeriod(DateTime.Today);
        for (int k = 0; k < Periods; k++)
        {
            var period = latest.AddMonths(-3 * k);
            data.Counts.Add(new ShareholderCountRow
            {
                Code = code,
                ReportDate = period,
                // 户数越近越少：模拟筹码集中，也让"这一期是新抓的"在界面上看得出来
                HolderNum = 50000 + k * 1000,
                AvgShares = 1111,
                FetchedAt = now,
            });
            for (int rank = 1; rank <= 10; rank++)
            {
                data.TopHolders.Add(Holder(code, period, TopShareholderRow.KindTotal, rank, now));
                data.TopHolders.Add(Holder(code, period, TopShareholderRow.KindFloat, rank, now));
            }
        }
        return Task.FromResult(data);
    }

    /// <summary>
    /// 一行十大股东。第 1 名故意叫「香港中央结算有限公司」——北向持股就是从这个名字认出来的
    /// （见 project_northbound_data），造假数据时留着它，下游那条链也就顺带能跑。
    /// </summary>
    private static TopShareholderRow Holder(string code, DateTime period, string kind, int rank, DateTime now)
        => new()
        {
            Code = code,
            ReportDate = period,
            Kind = kind,
            Rank = rank,
            HolderName = rank == 1 ? "香港中央结算有限公司" : $"[离线模拟]股东{rank}",
            Shares = 1_000_000d / rank,
            Ratio = 10d / rank,
            ShareType = "流通A股",
            ChangeDirection = rank % 2 == 0 ? "增" : null,
            FetchedAt = now,
        };
}
