using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 预约披露日**双源合并**（2026-10-27，见 doc/financial-fetch-disclosure-race-design.md）。
///
/// 起因：巨潮是交易所指定披露网站，「已披露」标记要走正式入库流程，实测滞后半天以上——
/// 2026-10-27 当天 00:39 抓时本期标了 1325 只，14:12 再抓变 1690 只。而【拉取财务报表】
/// 拿这个标记当闸门，于是前一晚披露的票整批晚一轮才抓到财报（格力 10-26 晚发三季报，
/// 凌晨那轮仍算「未披露」）。
///
/// 合并**只发生在抓取时**（<see cref="EarningsScheduleMerge"/>），消费端只读合并表。
/// 所以测试也分两层：
///   · 判据 → 测纯函数，不碰库（下面第一组）
///   · 落库和读回 → 打真 SQLite（第二组）
/// </summary>
public class EarningsScheduleDualSourceTests
{
    private static DateTime D(string s) => DateTime.Parse(s);
    private static DateTime? Dn(string? s) => s is null ? null : D(s);

    private static EarningsScheduleRow Cninfo(string code, string period, string? actual,
                                              string? appoint = null)
        => new(code, D(period), Dn(appoint), null, null, null, Dn(actual));

    private static EarningsScheduleEmRow Em(string code, string period, string? actual,
                                            string? appoint = null)
        => new(Code: code, ReportPeriod: D(period), AppointDate: Dn(appoint),
               FirstAppointDate: Dn(appoint), Change1: null, Change2: null, Change3: null,
               ActualDate: Dn(actual), IsPublish: actual is null ? 0 : 1,
               ReportTypeName: "2026年 三季报", Quarter: "2026q3", TradeMarket: "深交所主板",
               SecurityType: "A股", BoardName: null, NewBoardCode: null, ResidualDays: null,
               AppointChange: null, AppointChangeDays: null, InfoCode: null, Eitime: null);

    private static EarningsScheduleRow One(
        IEnumerable<EarningsScheduleRow> c, IEnumerable<EarningsScheduleEmRow> e)
        => Assert.Single(EarningsScheduleMerge.Merge(c, e));

    // ══════════ 一、判据（纯函数，不碰库）══════════

    /// <summary>
    /// 格力那个场景：公司 10-26 晚发了三季报，东财标了、巨潮还没标。
    /// 并集生效后这一期当天就算"已披露"，闸门早开一轮。
    /// </summary>
    [Fact]
    public void 只有东财标了已披露_合并后就算已披露()
    {
        var m = One([Cninfo("000651", "2026-09-30", actual: null)],
                    [Em("000651", "2026-09-30", actual: "2026-10-27")]);
        Assert.Equal(D("2026-10-27"), m.ActualDate);
        Assert.False(m.Pending);
    }

    /// <summary>反过来也要成立：东财漏了这只票（实测两家差 3 条），巨潮标了就得算已披露。</summary>
    [Fact]
    public void 只有巨潮标了已披露_合并后照样算()
    {
        var m = One([Cninfo("000001", "2026-09-30", actual: "2026-10-24")], []);
        Assert.Equal(D("2026-10-24"), m.ActualDate);
    }

    /// <summary>
    /// 两家都标了但日期不同 ⇒ 取**较晚**的。
    ///
    /// 这个值是「披露已超过 3 天、数据源仍没有这一期」那条告警的起算点
    /// （FinancialFetchPlanner.SourceLagAlarm），取早的会让告警提前触发、把真故障淹在误报里。
    /// 注意这跟"算不算已披露"的方向**相反**：那个要早（并集），这个要晚。
    /// </summary>
    [Fact]
    public void 两家日期不同_取较晚的那个()
    {
        Assert.Equal(D("2026-10-27"),
            One([Cninfo("000651", "2026-09-30", "2026-10-27")],
                [Em("000651", "2026-09-30", "2026-10-26")]).ActualDate);

        // 反过来摆也一样，别写成"总是取巨潮的"
        Assert.Equal(D("2026-10-27"),
            One([Cninfo("000651", "2026-09-30", "2026-10-26")],
                [Em("000651", "2026-09-30", "2026-10-27")]).ActualDate);
    }

    /// <summary>两家都没标 ⇒ 仍是没披露，不能凭空造出一个日期。</summary>
    [Fact]
    public void 两家都没标_仍是没披露()
        => Assert.True(One([Cninfo("000333", "2026-09-30", null)],
                           [Em("000333", "2026-09-30", null)]).Pending);

    /// <summary>预约日以巨潮为准——这一列是给人看的（底仓/主动仓的财报日），官方口径更稳。</summary>
    [Fact]
    public void 预约日两家不同_以巨潮为准()
        => Assert.Equal(D("2027-04-28"),
            One([Cninfo("000651", "2026-12-31", null, appoint: "2027-04-28")],
                [Em("000651", "2026-12-31", null, appoint: "2027-04-25")]).EffectiveDate);

    /// <summary>巨潮那条缺预约日时，用东财的补上——逐字段兜底，不是整条替换。</summary>
    [Fact]
    public void 巨潮缺预约日_用东财补()
        => Assert.Equal(D("2027-04-25"),
            One([Cninfo("000651", "2026-12-31", null, appoint: null)],
                [Em("000651", "2026-12-31", null, appoint: "2027-04-25")]).EffectiveDate);

    /// <summary>巨潮整条都没有（两家差的那几条）⇒ 东财原样进合并结果，否则双源反比单源少。</summary>
    [Fact]
    public void 巨潮没有这条_东财原样补上()
    {
        var m = One([], [Em("920970", "2026-09-30", "2026-10-26", appoint: "2026-10-26")]);
        Assert.Equal("920970", m.Code);
        Assert.Equal(D("2026-10-26"), m.ActualDate);
        Assert.Equal(D("2026-10-26"), m.EffectiveDate);
    }

    /// <summary>
    /// **整个方案"下限就是现状"的落点**：东财缺席时，合并结果跟巨潮逐字段相同。
    /// 这条要是挂了，说明双源在没有东财数据时反而改变了行为，方案的前提就不成立了。
    /// </summary>
    [Fact]
    public void 东财缺席时_与巨潮逐字段相同()
    {
        EarningsScheduleRow[] src =
        [
            new("000001", D("2026-09-30"), D("2026-10-24"), null, null, null, D("2026-10-24")),
            new("000002", D("2026-09-30"), D("2026-10-20"), D("2026-10-25"), D("2026-10-28"), null, null),
            new("000651", D("2026-12-31"), D("2027-04-28"), null, null, null, null),
        ];

        var merged = EarningsScheduleMerge.Merge(src, []).OrderBy(r => r.Code).ToList();

        Assert.Equal(src.Length, merged.Count);
        foreach (var (a, b) in src.OrderBy(r => r.Code).Zip(merged))
            Assert.Equal(a, b);   // record struct 的相等＝逐字段比
    }

    // ══════════ 二、落库和读回（真 SQLite）══════════

    private static (SqliteEarningsScheduleRepository Repo, string Dir) NewRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "earnsched-dual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var repo = new SqliteEarningsScheduleRepository(Path.Combine(dir, "test.sqlite"));
        repo.EnsureSchema();
        return (repo, dir);
    }

    /// <summary>
    /// 走一遍产品里的完整流程：两家原始各自落库 → 合并 → 落合并表 → 消费端读合并表。
    /// 消费端读到的必须是合并后的结果（只有东财标了也算已披露）。
    /// </summary>
    [Fact]
    public void 全流程_消费端读到的是合并结果()
    {
        var (repo, dir) = NewRepo();
        try
        {
            var c = new[] { Cninfo("000651", "2026-09-30", actual: null, appoint: "2026-10-27") };
            var e = new[] { Em("000651", "2026-09-30", actual: "2026-10-27", appoint: "2026-10-27") };

            repo.Upsert(c);
            repo.UpsertEm(e);
            repo.UpsertMerged(EarningsScheduleMerge.Merge(c, e));

            // 闸门：这一期算已披露了
            Assert.Equal(D("2026-09-30"),
                         repo.GetLatestDisclosedPeriodByCode(D("2026-10-27"))["000651"]);
            Assert.Equal(D("2026-10-27"),
                         repo.GetLatestDisclosureByCode(D("2026-10-27"))["000651"].ActualDate);
            // 界面：已经披露了就不该再算"下次财报"
            Assert.False(repo.GetUpcomingByCode().ContainsKey("000651"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// 东财独有的那些列要真落库、真读得回（"取到的全存"），而且同票同期是覆盖不是插新行。
    /// 假仓储测不出写错列名，所以打真库。
    /// </summary>
    [Fact]
    public void 东财原始表_存得进且覆盖写()
    {
        var (repo, dir) = NewRepo();
        try
        {
            repo.UpsertEm([Em("920970", "2026-09-30", actual: "2026-10-26")]);
            Assert.Equal(1, repo.CompareSources(D("2026-09-30")).EmTotal);

            repo.UpsertEm([Em("920970", "2026-09-30", actual: "2026-10-27")]);
            var c = repo.CompareSources(D("2026-09-30"));
            Assert.Equal(1, c.EmTotal);        // 还是一行，没插出第二行
            Assert.Equal(1, c.EmDisclosed);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// 对账计数要准——任务每轮收工报的就是它，而「只有东财标了几只」是判断这条腿
    /// 值不值得留着的唯一依据（长期为 0 就该撤）。它读的是**两家原始表**，不是合并表。
    /// </summary>
    [Fact]
    public void 对账计数_只有一家标了的数得准()
    {
        var (repo, dir) = NewRepo();
        try
        {
            repo.Upsert(
            [
                Cninfo("000001", "2026-09-30", "2026-10-24"),   // 两家都标
                Cninfo("000002", "2026-09-30", "2026-10-25"),   // 只有巨潮标
                Cninfo("000651", "2026-09-30", null),           // 只有东财标
                Cninfo("000333", "2026-09-30", null),           // 两家都没标
            ]);
            repo.UpsertEm(
            [
                Em("000001", "2026-09-30", "2026-10-24"),
                Em("000002", "2026-09-30", null),
                Em("000651", "2026-09-30", "2026-10-27"),
                Em("000333", "2026-09-30", null),
            ]);

            var c = repo.CompareSources(D("2026-09-30"));
            Assert.Equal(4, c.CninfoTotal);
            Assert.Equal(4, c.EmTotal);
            Assert.Equal(2, c.CninfoDisclosed);
            Assert.Equal(2, c.EmDisclosed);
            Assert.Equal(1, c.OnlyCninfo);   // 000002
            Assert.Equal(1, c.OnlyEm);       // 000651 ← 靠东财早一轮抓到的就是它
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// **上线当天不能塌**：老库里已经有巨潮的行、合并表还是空的，启动时的迁移要把它们
    /// 原样灌进合并表。不做这一步的话，在下一次【拉取财报预约日】跑完之前，
    /// 财务/股东/金融监管三处闸门会一起退回兜底判据、界面的财报日列全空。
    /// </summary>
    [Fact]
    public void 迁移基线_老库的巨潮行会灌进合并表()
    {
        var dir = Path.Combine(Path.GetTempPath(), "earnsched-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "test.sqlite");
        try
        {
            // 第一次：建库、只写巨潮那张表（模拟改动之前的老库）
            var repo = new SqliteEarningsScheduleRepository(db);
            repo.EnsureSchema();
            repo.Upsert([Cninfo("000001", "2026-09-30", "2026-10-24", appoint: "2026-10-24")]);

            // 把合并表清空，造出"老库刚升级上来"的状态
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM EarningsScheduleMerged;"
                                + "DELETE FROM DataMigration WHERE id='earnings_schedule_merged_baseline';";
                cmd.ExecuteNonQuery();
            }

            // 再 EnsureSchema 一次 ＝ 程序下次启动：迁移应该把巨潮的行灌进合并表
            new SqliteEarningsScheduleRepository(db).EnsureSchema();

            Assert.Equal(D("2026-09-30"),
                new SqliteEarningsScheduleRepository(db)
                    .GetLatestDisclosedPeriodByCode(D("2026-10-27"))["000001"]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
