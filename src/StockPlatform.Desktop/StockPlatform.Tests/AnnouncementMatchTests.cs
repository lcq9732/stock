using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 巨潮公告 ↔ 东财正文的日期对位（2026-10-22）。
///
/// 原来的规则"±3 天、标题对得上取第一条"在东财列表（新的在前）里取到的是窗口里**最新**那篇：
/// 600636 退市国化 06-16～06-19 每天一份同名「股份回购进展公告」，四天全配到 06-18 那篇；
/// 库里回购 19 行、中标订单 4 行就是这么错的。新规则只认东财"早一天或同一天"、优先早一天——
/// 依据是 2614 行回购公告的日期差分布：早一天 1950、同一天 650，正常的没有一行更晚。
/// </summary>
public class AnnouncementMatchTests : IDisposable
{
    // ── 规则本身 ──────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-06-15", 0)]   // 东财早一天：最常见
    [InlineData("2026-06-16", 1)]   // 同一天
    [InlineData("2026-06-17", null)] // 更晚：正常的从来没有
    [InlineData("2026-06-14", null)] // 早两天：也没有
    public void 只认早一天或同一天(string eastMoneyDate, int? expected)
        => Assert.Equal(expected, AnnouncementMatchRule.DatePreference(DateOnly.Parse(eastMoneyDate), new DateOnly(2026, 6, 16)));

    [Theory]
    [InlineData("AN202608061827713711", "2026-08-06")]
    [InlineData("AN001", null)]
    [InlineData("", null)]
    [InlineData("cninfo:1225461458", null)]
    public void art_code里的日期(string art, string? expected)
        => Assert.Equal(expected is null ? null : DateOnly.Parse(expected), AnnouncementMatchRule.ArtCodeDate(art));

    [Fact]
    public void 连发几天同名公告_共用一篇的只留真主()
    {
        // 600636 的真实情形：06-16..06-19 四行全配到 AN20260618...
        const string art = "AN202606181823672615";
        var rows = new List<(string, DateOnly, string)>
        {
            ("600636", new(2026, 6, 16), art), ("600636", new(2026, 6, 17), art),
            ("600636", new(2026, 6, 18), art), ("600636", new(2026, 6, 19), art),
        };
        var wrong = AnnouncementMatchRule.FindMismatched(rows);
        Assert.Equal([0, 1, 2], wrong.OrderBy(i => i));   // 06-19（东财早一天）是真主
    }

    [Fact]
    public void 单独一行但东财日期更晚_算配错_正常的不动()
    {
        var rows = new List<(string, DateOnly, string)>
        {
            ("002475", new(2026, 6, 30), "AN202607011826636778"),   // 晚一天：配错
            ("600000", new(2026, 9, 2), "AN202609011800000001"),     // 早一天：正常
            ("600001", new(2026, 9, 2), "AN202609021800000002"),     // 同一天：正常
            ("600002", new(2026, 9, 2), "AN001"),                     // 认不出日期：不判
        };
        Assert.Equal([0], AnnouncementMatchRule.FindMismatched(rows).ToList());
    }

    // ── 东财匹配：同名公告在相邻几天都有时选对那一篇 ───────────────

    private sealed class SameTitleHandler(params (string Date, string Art)[] items) : HttpMessageHandler
    {
        public readonly List<string> ContentArts = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            string json;
            if (url.Contains("/api/security/ann"))
                json = JsonSerializer.Serialize(new
                {
                    data = new
                    {
                        // 东财列表新的在前
                        list = items.OrderByDescending(i => i.Date).Select(i => new
                        {
                            title = "退市国化：国新文化控股股份有限公司关于股份回购进展公告",
                            notice_date = i.Date + " 00:00:00",
                            art_code = i.Art,
                        }),
                    },
                });
            else
            {
                ContentArts.Add(url.Split("art_code=")[1].Split('&')[0]);
                json = JsonSerializer.Serialize(new { data = new { notice_content = "正文" } });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private static async Task<string?> Match(DateOnly cninfoDate, params (string, string)[] items)
    {
        var fetcher = new EastMoneyAnnouncementDetailFetcher(
            new RateLimiter(delayBetweenRequests: TimeSpan.Zero), new HttpClient(new SameTitleHandler(items)));
        var got = await fetcher.FetchDetailAsync("600636", "关于股份回购进展公告", cninfoDate);
        return got?.ArtCode;
    }

    private static readonly (string, string)[] DailySeries =
    [
        ("2026-06-15", "AN0615"), ("2026-06-16", "AN0616"), ("2026-06-17", "AN0617"), ("2026-06-18", "AN0618"),
    ];

    [Fact]
    public async Task 每天一份同名公告_各自配到早一天那篇()
    {
        Assert.Equal("AN0615", await Match(new(2026, 6, 16), DailySeries));   // 原来配到 AN0618
        Assert.Equal("AN0616", await Match(new(2026, 6, 17), DailySeries));
        Assert.Equal("AN0618", await Match(new(2026, 6, 19), DailySeries));
    }

    [Fact]
    public async Task 只有同一天的那篇_也认()
        => Assert.Equal("AN0616", await Match(new(2026, 6, 16), ("2026-06-16", "AN0616"), ("2026-06-18", "AN0618")));

    [Fact]
    public async Task 只有更晚的同名公告_宁可不配()
        => Assert.Null(await Match(new(2026, 6, 16), ("2026-06-17", "AN0617"), ("2026-06-18", "AN0618")));

    // ── 一次性清理：打在真 SQLite 上 ─────────────────────────────

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"annMatch_{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        // 不调 SqliteConnection.ClearAllPools()——那是进程级的，会崩掉别的测试类
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath) + "*"))
            try { File.Delete(f); } catch { /* 临时文件 */ }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        c.Open();
        return c;
    }

    private void Exec(string sql)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void 清理_配错的行变回空壳_对的行和标题不动_只跑一次()
    {
        using (var c = Open()) SqliteSchema.EnsureSchema(c);
        Exec($"DELETE FROM DataMigration WHERE id = '{SqliteDataMigrations.AnnouncementMismatchCleanup}';");
        Exec("""
            INSERT INTO PlanAnnouncement (code, kind, announce_date, stage, title, art_code, cum_shares, cum_amount, as_of_date)
            VALUES ('600636','回购','2026-06-18','进展','进展公告','AN202606181823672615', 6382330, 12103739.59, '2026-06-18'),
                   ('600636','回购','2026-06-19','进展','进展公告','AN202606181823672615', 6382330, 12103739.59, '2026-06-18'),
                   ('002475','回购','2026-06-30','进展','进展公告','AN202607011826636778', 17665380, 999915944.13, '2026-06-30'),
                   ('600000','回购','2026-09-02','进展','进展公告','AN202609011800000001', 100, 1000, '2026-09-01');
            INSERT INTO OrderWinAnnouncement (code, name, title, publish_date, art_code, content, total_amount_yuan)
            VALUES ('300351','x','中标公告','2026-06-24','AN202606261824772241','正文', 5e7),
                   ('300352','y','中标公告','2026-06-24','AN202606231824772241','正文', 6e7);
            """);

        using (var c = Open()) SqliteDataMigrations.Apply(c);

        using (var c = Open())
        {
            Assert.True(SqliteDataMigrations.IsApplied(c, SqliteDataMigrations.AnnouncementMismatchCleanup));
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT announce_date || '|' || IFNULL(art_code,'') || '|' || IFNULL(cum_shares,'') || '|' || title FROM PlanAnnouncement ORDER BY code, announce_date;";
            var rows = new List<string>();
            using (var r = cmd.ExecuteReader()) while (r.Read()) rows.Add(r.GetString(0));
            Assert.Equal(
            [
                "2026-06-30|||进展公告",                                   // 002475 东财晚一天：清掉
                "2026-09-02|AN202609011800000001|100.0|进展公告",           // 600000 正常：不动
                "2026-06-18|||进展公告",                                   // 600636 06-18 不是真主：清掉
                "2026-06-19|AN202606181823672615|6382330.0|进展公告",       // 600636 06-19 东财早一天：真主
            ], rows);

            cmd.CommandText = "SELECT code || '|' || IFNULL(art_code,'') || '|' || IFNULL(total_amount_yuan,'') FROM OrderWinAnnouncement ORDER BY code;";
            rows.Clear();
            using (var r = cmd.ExecuteReader()) while (r.Read()) rows.Add(r.GetString(0));
            Assert.Equal(["300351||", "300352|AN202606231824772241|60000000.0"], rows);
        }

        // 再启动一次：已记账，不再动（假如之后又写进来一行"看着像配错"的，也不归这条迁移管）
        Exec("UPDATE PlanAnnouncement SET art_code = 'AN202607011826636778', cum_shares = 1 WHERE code = '002475';");
        using (var c = Open()) SqliteDataMigrations.Apply(c);
        using (var c = Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT cum_shares FROM PlanAnnouncement WHERE code = '002475';";
            Assert.Equal(1.0, Convert.ToDouble(cmd.ExecuteScalar()));
        }
    }
}
