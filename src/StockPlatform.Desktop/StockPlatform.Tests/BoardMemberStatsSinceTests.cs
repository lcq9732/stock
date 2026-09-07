using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 板块成分股「抓得怎么样了」的统计口径（2026-09-07，实战中踩出来的）。
///
/// 现场：09-07 走 terminal 通道（读东财终端本地文件）跑完成分股，日志写着
/// 「成分股：1031/1031（成功 1031、失败 0）」，紧接着下一行却是
/// 「全库成分股状态：最新 0 个、待重试 1031 个、从未抓过 0 个。（再跑一次会从没抓到的接着来）」——
/// 照着它再跑一轮纯属白跑。
///
/// 根因不在仓储，在编排层复用了同一个时间界：<c>BoardMemberFreshSince</c> 回答的是
/// "要不要重抓"，不节流的通道下它是 <see cref="DateTime.MaxValue"/>（含义：谁都不新鲜、全部重抓），
/// 对抓取决策完全正确；但拿它去统计，<c>fetched_at &gt;= MaxValue</c> 恒为假，
/// 刚抓成功的全部落进"待重试"。界面上的待抓计数是同一个毛病。
/// </summary>
public class BoardMemberStatsSinceTests
{
    private static readonly DateTime Today = new(2026, 9, 7);

    [Fact]
    public void 不节流的通道_统计界是今天_不是MaxValue()
    {
        // terminal 通道（EastMoneyTerminalBoardFetcher.MemberFreshFor => TimeSpan.Zero）
        var since = FetchOrchestrator.BoardMemberStatsSince(TimeSpan.Zero, Today);

        Assert.Equal(Today, since);
        Assert.NotEqual(DateTime.MaxValue, since);   // 回归锁：就是这个值把 1031 个全算成待重试
    }

    [Fact]
    public void 负的新鲜期也当作不节流()
    {
        // MemberFreshFor 的语义是"小于等于 0 表示不节流"，负值不能掉进减法分支
        // （today - (-7天) 会算出未来时间，比 MaxValue 更隐蔽：统计恒为 0，但不报错）
        Assert.Equal(Today, FetchOrchestrator.BoardMemberStatsSince(TimeSpan.FromDays(-7), Today));
    }

    [Fact]
    public void 节流的通道_统计界跟抓取判据保持一致()
    {
        // 走网络的通道（东财 7 天、新浪 7 天）。这两者必须一致，否则会出现
        // "界面说还剩 300 个、跑起来说 0 个要抓"
        var fresh = TimeSpan.FromDays(7);

        Assert.Equal(Today - fresh, FetchOrchestrator.BoardMemberStatsSince(fresh, Today));
    }

    [Fact]
    public void 现场复现_刚抓成功的板块在旧口径下会被算成待重试()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"boardstats_{Guid.NewGuid():N}.sqlite");
        var repo = new SqliteBoardRepository(dbPath);
        try
        {
            repo.EnsureSchema();
            repo.UpsertBoards(new[] { Make("BK1"), Make("BK2") });
            repo.ReplaceMembers("BK1", new[] { "600000" });   // 本轮刚抓成功
            repo.ReplaceMembers("BK2", new[] { "600001" });   // 本轮刚抓成功

            // 旧口径：不节流通道拿 BoardMemberFreshSince(=MaxValue) 统计
            var (okOld, failedOld, _) = repo.GetMemberFetchProgress(DateTime.MaxValue);
            Assert.Equal(0, okOld);        // ← 现场那句"最新 0 个"
            Assert.Equal(2, failedOld);    // ← 现场那句"待重试 1031 个"

            // 新口径
            var (okNew, failedNew, neverNew) =
                repo.GetMemberFetchProgress(FetchOrchestrator.BoardMemberStatsSince(TimeSpan.Zero, DateTime.Today));
            Assert.Equal(2, okNew);
            Assert.Equal(0, failedNew);
            Assert.Equal(0, neverNew);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { /* 临时文件 */ }
        }
    }

    private static Board Make(string code) => new()
    {
        BoardCode = code,
        Type = BoardType.Concept,
        Name = "板块" + code,
        MemberCount = 1,
        ChangePct = 1.5,
        AsOf = new DateTime(2026, 9, 7, 10, 0, 0),
        MemberCodes = new List<string>(),
    };
}
