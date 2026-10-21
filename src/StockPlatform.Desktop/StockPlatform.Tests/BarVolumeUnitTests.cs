using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 成交量单位（2026-09-10 统一成手；2026-10-20 改存股，见 doc/unit-normalization-inventory.md）。
///
/// 三个数据源口径不同——腾讯主板/ETF/指数给手、**科创板给股**，新浪**一律给股**，东财都给手——
/// 解析出口统一换成股（<see cref="BarVolumeUnit.ToShares"/>）。存量由【成交量改存股】一次性 ×100
/// （<see cref="SqliteBarVolumeMigration"/>），没做完之前准入闸挡住别的任务（<see cref="BarVolumeMigrationGate"/>）。
/// 这里钉死三件事：**解析出口的换算**、**迁移（分批、能续、不乘两遍、不碰 fetched_at）**、**准入闸**。
/// 存取全部打在临时 SQLite 上（假仓储验不到真 SQL）。
/// </summary>
public class BarVolumeUnitTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"volunit_{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!,
                                             Path.GetFileNameWithoutExtension(_dbPath) + "*"))
            try { File.Delete(f); } catch { /* 临时文件 */ }
    }

    // ─────────────────── 解析出口的换算 ───────────────────

    /// <summary>
    /// 腾讯：科创板给股、原样；其余给手、×100。数字取自 2026-09-09 实抓——
    /// 688001 腾讯给 19,050,931（股），而东财同一天给 190,509（手）。
    /// </summary>
    [Theory]
    [InlineData("688001", 19050931, 19050931)]      // 科创板 → 原样（股）
    [InlineData("689009", 1000000, 1000000)]        // 科创板 CDR → 原样
    [InlineData("600000", 505325, 50532500)]        // 沪主板 → ×100
    [InlineData("000001", 123456, 12345600)]        // 深主板 → ×100
    [InlineData("300750", 390240, 39024000)]        // 创业板 → ×100
    [InlineData("920371", 237344, 23734400)]        // 北交所 → ×100
    [InlineData("sh510300", 12105648, 1210564800)]  // ETF（带前缀）→ ×100（份）
    [InlineData("sh000001", 417791248, 41779124800)] // 指数（带前缀）→ ×100
    public void Tencent_科创板原样_其余乘100(string code, double raw, double expected)
        => Assert.Equal(expected, BarVolumeUnit.ToShares(code, raw, BarVolumeUnit.Source.Tencent));

    /// <summary>
    /// 新浪：**所有板块**都给股，原样。实抓佐证：2026-09-09 新浪给 600000 的是 50,532,458，
    /// 而当天真实成交是 505,325 手——正好 100 倍。
    /// </summary>
    [Theory]
    [InlineData("600000", 50532458)]
    [InlineData("688001", 19050931)]
    [InlineData("sh000001", 41779124800)]
    public void Sina_一律原样(string code, double raw)
        => Assert.Equal(raw, BarVolumeUnit.ToShares(code, raw, BarVolumeUnit.Source.Sina));

    /// <summary>东财三个板块都给手，一律 ×100（原来这条路不走 BarVolumeUnit、原样存手）。</summary>
    [Theory]
    [InlineData("600000")]
    [InlineData("688001")]
    [InlineData("920371")]
    public void EastMoney_一律乘100(string code)
        => Assert.Equal(50532500d, BarVolumeUnit.ToShares(code, 505325, BarVolumeUnit.Source.EastMoney));

    // ─────────────────── 存量迁移 ───────────────────

    private SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={_dbPath}");
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

    private object? One(string sql)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    /// <summary>
    /// 建一个"迁移上线前"的库：建表时 Bar 是空的会直接记完成，所以写完老单位的行再把那条记录删掉。
    /// 个股（含科创板两位小数的手）、ETF、指数、板块，四个口径，外加 NULL / 0。
    /// </summary>
    private void LegacyDb()
    {
        using (var c = Open()) SqliteSchema.EnsureSchema(c);
        Exec("""
            INSERT INTO Bar (code, granularity, period_start, close, volume, amount, fetched_at) VALUES
              ('600519',   'day_raw', '2026-10-19 00:00:00', 1324.97, 39729,     5308935900, '2026-10-19 21:00:00'),
              ('600519',   'day',     '2026-10-19 00:00:00', 1324.97, 39729,     5308935900, '2026-10-19 21:00:00'),
              ('600519',   'day_hfq', '2026-10-19 00:00:00', 9999.99, 39729,     5308935900, '2026-10-19 21:00:00'),
              ('600519',   'day_adj', '2026-10-19 00:00:00', 1300.00, 39729,     5308935900, '2026-10-19 21:30:00'),
              ('688981',   'day_raw', '2026-10-19 00:00:00', 99.7,    317113.34, 3157696800, '2026-10-19 21:00:00'),
              ('sh510300', 'day_raw', '2026-10-19 00:00:00', 4.24,    12105648,  5137102800, '2026-10-19 21:00:00'),
              ('sh000001', 'day',     '2026-10-19 00:00:00', 3730.92, 417791248, 687009728000, '2026-10-19 21:00:00'),
              ('BK0594',   'day',     '2026-10-19 00:00:00', 1000,    123456.78, NULL,       '2026-10-19 22:00:00'),
              ('000001',   'day_raw', '2026-10-18 00:00:00', 10,      NULL,      NULL,       '2026-10-18 21:00:00'),
              ('000001',   'day_raw', '2026-10-19 00:00:00', 10,      0,         0,          '2026-10-19 21:00:00');
            """);
        Exec($"DELETE FROM DataMigration WHERE id = '{SqliteBarVolumeMigration.Id}';");
    }

    [Fact]
    public void 新库建表时Bar是空的_直接记完成()
    {
        using (var c = Open()) SqliteSchema.EnsureSchema(c);
        Assert.True(new SqliteBarVolumeMigration(_dbPath).IsDone());
    }

    [Fact]
    public void 老库启动时不做_要等任务()
    {
        LegacyDb();
        using (var c = Open()) SqliteSchema.EnsureSchema(c);       // 再启动一次也不自己做
        Assert.False(new SqliteBarVolumeMigration(_dbPath).IsDone());
        Assert.Equal(39729d, One("SELECT volume FROM Bar WHERE code='600519' AND granularity='day_raw'"));
    }

    /// <summary>
    /// 全表 ×100：四个口径、ETF、指数、板块一起；科创板两位小数的手 ×100 后是精确整数，
    /// 跟 <see cref="UnitScale"/>（decimal）逐位相同；NULL / 0 不动；fetched_at 一个都不碰
    /// （碰了【重算回测序列】会以为 day_raw 被重抓过）。
    /// </summary>
    [Fact]
    public void 全表乘100_跟UnitScale逐位相同_不碰fetched_at()
    {
        LegacyDb();
        var fetchedBefore = One("SELECT group_concat(fetched_at, '|') FROM (SELECT fetched_at FROM Bar ORDER BY rowid)");

        var m = new SqliteBarVolumeMigration(_dbPath);
        while (!m.RunChunk().Done) { }

        foreach (var g in new[] { "day_raw", "day", "day_hfq", "day_adj" })
            Assert.Equal(3972900d, One($"SELECT volume FROM Bar WHERE code='600519' AND granularity='{g}'"));
        Assert.Equal(UnitScale.Apply(317113.34, 100m), One("SELECT volume FROM Bar WHERE code='688981'"));
        Assert.Equal(31711334d, One("SELECT volume FROM Bar WHERE code='688981'"));
        Assert.Equal(1210564800d, One("SELECT volume FROM Bar WHERE code='sh510300'"));
        Assert.Equal(41779124800d, One("SELECT volume FROM Bar WHERE code='sh000001'"));
        Assert.Equal(UnitScale.Apply(123456.78, 100m), One("SELECT volume FROM Bar WHERE code='BK0594'"));
        Assert.Null(One("SELECT volume FROM Bar WHERE code='000001' AND period_start LIKE '2026-10-18%'"));
        Assert.Equal(0d, One("SELECT volume FROM Bar WHERE code='000001' AND period_start LIKE '2026-10-19%'"));
        Assert.Equal(fetchedBefore, One("SELECT group_concat(fetched_at, '|') FROM (SELECT fetched_at FROM Bar ORDER BY rowid)"));

        // 量额比现在 ≈1（值体检 V6 的新区间）
        Assert.InRange(Convert.ToDouble(One("SELECT amount / (volume * close) FROM Bar WHERE code='600519' AND granularity='day_raw'")), 0.5, 2.5);
        Assert.True(m.IsDone());
    }

    /// <summary>
    /// 分批、中途"被杀"（换一个实例接着做）、做完再调：每一行恰好乘一次。
    /// 每批 3 个 rowid，10 行要做 4 批。
    /// </summary>
    [Fact]
    public void 分批能续_做完再调不再改()
    {
        LegacyDb();
        var first = new SqliteBarVolumeMigration(_dbPath).RunChunk(3);
        Assert.False(first.Done);
        Assert.Equal(3, first.Cursor);
        Assert.Equal(3, first.Rows);
        Assert.Equal((3L, 10L), new SqliteBarVolumeMigration(_dbPath).Progress());

        var again = new SqliteBarVolumeMigration(_dbPath);              // "重启"后接着做
        BarVolumeMigrationChunk c;
        int batches = 1;
        do { c = again.RunChunk(3); batches++; } while (!c.Done);
        Assert.Equal(4, batches);

        var after = new SqliteBarVolumeMigration(_dbPath).RunChunk(3);  // 做完再调
        Assert.True(after.Done);
        Assert.Equal(0, after.Rows);
        Assert.Equal(3972900d, One("SELECT volume FROM Bar WHERE code='600519' AND granularity='day_raw'"));
        Assert.Equal(8L, One($"SELECT rows FROM DataMigration WHERE id = '{SqliteBarVolumeMigration.Id}'"));  // NULL/0 两行不算
    }

    // ─────────────────── 任务 + 准入闸 ───────────────────

    [Fact]
    public async Task 任务跑完全表_日志说清楚()
    {
        LegacyDb();
        var task = new BarVolumeSharesTask(new SqliteBarVolumeMigration(_dbPath));
        var log = new List<string>();
        task.OnProgress += p => { if (!p.Quiet) log.Add(p.ToString()); };

        var r = await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.True(new SqliteBarVolumeMigration(_dbPath).IsDone());
        Assert.Contains(log, l => l.Contains("成交量改存股完成"));

        var second = await new BarVolumeSharesTask(new SqliteBarVolumeMigration(_dbPath)).RunAsync(new TaskRunArgs(), CancellationToken.None);
        Assert.Equal(TaskState.Completed, second.State);
        Assert.Equal(3972900d, One("SELECT volume FROM Bar WHERE code='600519' AND granularity='day_raw'"));
    }

    /// <summary>
    /// 没做完之前：别的任务一律「本轮没开工」（不是失败，计划过一会儿再来），迁移任务自己放行；
    /// 做完之后全部放行。闸挂在注册表上——计划、手动、分派都从那里过。
    /// </summary>
    [Fact]
    public async Task 准入闸_没做完只放行迁移任务()
    {
        LegacyDb();
        var migration = new SqliteBarVolumeMigration(_dbPath);
        var gate = new BarVolumeMigrationGate(migration);
        var ran = 0;
        var registry = new FetchTaskRegistry { RunGate = gate.Check };
        registry.Register(FetchActionId.StepLhb, () => new CountingTask(() => ran++));
        registry.Register(FetchActionId.StepBarVolumeShares, () => new BarVolumeSharesTask(migration));

        Assert.NotNull(gate.Check(FetchActionId.StepLhb));
        Assert.Null(gate.Check(FetchActionId.StepBarVolumeShares));

        var blocked = await registry.RunAsync(FetchActionId.StepLhb, new TaskRunArgs(), null, CancellationToken.None);
        Assert.Contains("成交量改存股", blocked.SkippedReason);
        Assert.Equal(0, ran);

        await registry.RunAsync(FetchActionId.StepBarVolumeShares, new TaskRunArgs(), null, CancellationToken.None);

        Assert.Null(gate.Check(FetchActionId.StepLhb));
        var ok = await registry.RunAsync(FetchActionId.StepLhb, new TaskRunArgs(), null, CancellationToken.None);
        Assert.Null(ok.SkippedReason);
        Assert.Equal(1, ran);
    }

    /// <summary>
    /// 【统一成交量单位】必须退役：它的判据是"比值≈1 就当成按股存、除以 100"，
    /// 改存股以后每一行都≈1，跑一次就把全库除回手。目录里标退役、计划里原地删掉。
    /// </summary>
    [Fact]
    public void 统一成交量单位已退役()
    {
        Assert.True(FetchTaskCatalog.Info(FetchActionId.StepFixVolumeUnit).Retired);
        Assert.Empty(FetchTaskCatalog.RetiredInto[FetchActionId.StepFixVolumeUnit]);
        Assert.False(FetchTaskCatalog.Info(FetchActionId.StepBarVolumeShares).Retired);
    }

    private sealed class CountingTask(Action onRun) : FetchTaskBase<int>
    {
        public override FetchActionId Id => FetchActionId.StepLhb;
        protected override async IAsyncEnumerable<IReadOnlyList<int>> FetchAsync(
            TaskRunArgs args,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            onRun();
            await Task.CompletedTask;
            yield break;
        }
        protected override Task SaveBatchAsync(IReadOnlyList<int> batch, CancellationToken ct)
            => Task.CompletedTask;
    }
}
