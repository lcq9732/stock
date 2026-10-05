using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 【停复牌】三路官网接口的真实联网冒烟（2026-09-30）：
///   dotnet test --filter "FullyQualifiedName~SuspensionSmokeTests" -e SUSP_SMOKE=1
///
/// 需要设 SUSP_SMOKE=1 才真跑，否则直接判过。每一路只问一个月（2026-09），一共 3 个请求。
/// 盯的是解析单测盯不到的那一半：URL 拼对没有、翻页收没收齐、Referer 带没带（不带会被拦）。
/// ⚠ 在用户本机抓数据时别跑（本机不发请求，见 feedback_no_local_network_probing）。
/// </summary>
public class SuspensionSmokeTests
{
    private readonly ITestOutputHelper _out;
    public SuspensionSmokeTests(ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("SUSP_SMOKE") == "1";

    private static RateLimiter Limiter() => new(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(1));

    private async Task<List<SuspensionRow>> Month(ISuspensionProvider p)
    {
        var rows = await p.GetMonthAsync(new DateOnly(2026, 9, 1));
        _out.WriteLine($"{p.Label} 2026-09：{rows.Count} 条，前几条：");
        foreach (var r in rows.Take(3))
            _out.WriteLine($"  {r.Market}{r.Code} {r.Name} {r.StartDay} {r.StartTime} → {r.EndDay} {r.EndTime} [{r.Kind}/{r.StopTime}] {r.Reason}");
        return rows;
    }

    [Fact]
    public async Task 上交所股票_9月有600293和600363()
    {
        if (!Enabled) { _out.WriteLine("跳过（设 SUSP_SMOKE=1 才联网跑）"); return; }
        var rows = await Month(new SseStockSuspensionProvider(Limiter()));
        Assert.Contains(rows, r => r.Code == "600293" && r.Kind == "LXTP");
        Assert.Contains(rows, r => r.Code == "600363" && r.StopTime == "WH");
        Assert.All(rows, r => Assert.Equal("sh", r.Market));
    }

    [Fact]
    public async Task 上交所基金_9月有513100()
    {
        if (!Enabled) { _out.WriteLine("跳过（设 SUSP_SMOKE=1 才联网跑）"); return; }
        var rows = await Month(new SseFundSuspensionProvider(Limiter()));
        Assert.Contains(rows, r => r.Code == "513100");
    }

    [Fact]
    public async Task 深交所_9月有300527那条1天()
    {
        if (!Enabled) { _out.WriteLine("跳过（设 SUSP_SMOKE=1 才联网跑）"); return; }
        var rows = await Month(new SzseSuspensionProvider(Limiter()));
        Assert.True(rows.Count > 100, $"只有 {rows.Count} 条——9 月实测 152 条");
        Assert.Contains(rows, r => r.Code == "300527" && r.Kind == "1天"
                                   && r.StartDay == new DateOnly(2026, 9, 29) && r.EndDay == new DateOnly(2026, 9, 30));
    }
}
