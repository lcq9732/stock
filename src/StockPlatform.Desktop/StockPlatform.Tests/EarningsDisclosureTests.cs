using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 「这只票到底披露了没有」——财务报表和金融监管指标改用**实际披露日**判断要不要抓，
/// 这个查询就是判据的来源（2026-09-03）。
///
/// 为什么换掉原来的法定截止日：2026 年半年报法定截止 8/31，而 66% 的公司挤在 8/25–8/29
/// 那五天披露；等截止日过完才认这一期，5478 只会同时涌进待补队列，按每轮 300 只要补三四天，
/// 数据到 9 月上旬才可用。按各自的实际披露日算，8/25 起就边披露边抓，8/31 基本就齐了。
/// </summary>
public class EarningsDisclosureTests
{
    private static DateTime D(string s) => DateTime.Parse(s);

    private static EarningsScheduleRow Row(string code, string period, string? actual)
        => new(code, D(period), null, null, null, null, actual is null ? null : D(actual));

    /// <summary>建一个临时库，塞几条预约日记录。</summary>
    private static (SqliteEarningsScheduleRepository Repo, string Dir) NewRepo(params EarningsScheduleRow[] rows)
    {
        var dir = Path.Combine(Path.GetTempPath(), "earnsched-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var repo = new SqliteEarningsScheduleRepository(Path.Combine(dir, "test.sqlite"));
        repo.EnsureSchema();
        if (rows.Length > 0) repo.Upsert(rows);
        return (repo, dir);
    }

    [Fact]
    public void 只认已经披露的_没披露的不出现()
    {
        var (repo, dir) = NewRepo(
            Row("000001", "2026-06-30", "2026-08-20"),   // 披露了
            Row("000002", "2026-06-30", null));          // 还没披露
        try
        {
            var map = repo.GetLatestDisclosedPeriodByCode(D("2026-09-03"));

            Assert.Equal(D("2026-06-30"), map["000001"]);
            // ⚠ 没披露的**不在字典里**，不是返回一个更早的期——调用方靠"查不到"退回兜底判据
            Assert.False(map.ContainsKey("000002"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 取已披露里最新的那一期()
    {
        var (repo, dir) = NewRepo(
            Row("000001", "2025-12-31", "2026-03-28"),
            Row("000001", "2026-03-31", "2026-04-25"),
            Row("000001", "2026-06-30", "2026-08-20"));
        try
        {
            Assert.Equal(D("2026-06-30"),
                repo.GetLatestDisclosedPeriodByCode(D("2026-09-03"))["000001"]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// 判据要按"当时"算，不能拿今天的披露状态去回答过去。
    /// 8/20 才披露的半年报，8/19 那天问必须还看不到——否则回补历史时会误判成"早该有了"。
    /// </summary>
    [Fact]
    public void 披露日之后才算数()
    {
        var (repo, dir) = NewRepo(
            Row("000001", "2026-03-31", "2026-04-25"),
            Row("000001", "2026-06-30", "2026-08-20"));
        try
        {
            Assert.Equal(D("2026-03-31"), repo.GetLatestDisclosedPeriodByCode(D("2026-08-19"))["000001"]);
            Assert.Equal(D("2026-06-30"), repo.GetLatestDisclosedPeriodByCode(D("2026-08-20"))["000001"]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// **安全底线**：查不到披露记录的票（新股、B股、老退市股）必须落到空结果上，
    /// 好让调用方退回法定截止日那套老判据。少取比多取危险得多——漏一只是静默的，
    /// 多取一只只是多花几个请求。
    /// </summary>
    [Fact]
    public void 库里没有的票查不到_由调用方兜底()
    {
        var (repo, dir) = NewRepo(Row("000001", "2026-06-30", "2026-08-20"));
        try
        {
            var map = repo.GetLatestDisclosedPeriodByCode(D("2026-09-03"));
            Assert.False(map.ContainsKey("688999"));   // 从来没进过预约日表
            Assert.Single(map);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 空库不炸()
    {
        var (repo, dir) = NewRepo();
        try { Assert.Empty(repo.GetLatestDisclosedPeriodByCode(D("2026-09-03"))); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
