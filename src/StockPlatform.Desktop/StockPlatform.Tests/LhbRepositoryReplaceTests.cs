using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 龙虎榜换源要用到的两个新写入语义（2026-09-09）——整天替换和库外备份。
///
/// 为什么单独测这两个：换源那一步会**删掉 26 万行再写回去**，它俩要是有问题，
/// 代价是一段历史悄悄少了或者变成两套并存的乱账，而且不会报错。
/// </summary>
public class LhbRepositoryReplaceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteLhbRepository _repo;

    public LhbRepositoryReplaceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lhb_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteLhbRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!,
                                             Path.GetFileNameWithoutExtension(_dbPath) + "*"))
            try { File.Delete(f); } catch { /* 临时文件 */ }
    }

    private static LhbRow Row(string date, string code, string reason, string source,
                              double amount = 1, double? deviation = null) =>
        new()
        {
            TradeDate = DateTime.Parse(date), StockCode = code, StockName = "测试",
            Reason = reason, Source = source, Amount = amount, ClosePrice = 10,
            Deviation = deviation, FetchedAt = DateTime.Now,
        };

    private int Count() => _repo.CountRows(new DateOnly(2000, 1, 1), new DateOnly(2100, 1, 1));

    /// <summary>
    /// 换源那天真正会发生的事：同一天，库里躺着新浪的粗类原因，东财送来的是交易所原文。
    ///
    /// 这正是**不能用 upsert** 的原因——reason 是主键的一部分，两套文本不冲突，
    /// upsert 只会让这一天并排存着 2 行。26 万行的历史会翻倍，而且没有任何字段能一眼分清。
    /// </summary>
    [Fact]
    public void ReplaceDaysWipesTheOldSourceInsteadOfStackingIt()
    {
        _repo.InsertOrIgnore([Row("2026-09-08", "000560", "换手率达20%的证券", LhbSources.Sina)]);
        Assert.Equal(1, Count());

        _repo.ReplaceDays([Row("2026-09-08", "000560", "日换手率达到20%的前5只证券", LhbSources.EastMoney)]);

        Assert.Equal(1, Count());
        Assert.Equal(1, _repo.CountRows(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), LhbSources.EastMoney));
        Assert.Equal(0, _repo.CountRows(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), LhbSources.Sina));
    }

    /// <summary>只动这一批带到的那些天，别的日子一行都不许碰——回补一个月片时，
    /// 相邻月份的数据必须原样留着。</summary>
    [Fact]
    public void ReplaceDaysOnlyTouchesTheDaysItWasGiven()
    {
        _repo.InsertOrIgnore([
            Row("2026-09-07", "000001", "旧原因", LhbSources.Sina),
            Row("2026-09-08", "000560", "旧原因", LhbSources.Sina),
        ]);

        var (deleted, inserted) = _repo.ReplaceDays([Row("2026-09-08", "000560", "新原因", LhbSources.EastMoney)]);

        Assert.Equal(1, deleted);
        Assert.Equal(1, inserted);
        Assert.Equal(2, Count());
        Assert.Equal(1, _repo.CountRows(new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 7), LhbSources.Sina));
    }

    /// <summary>同一天多行（一只票多个上榜原因、多只票）整批换掉。</summary>
    [Fact]
    public void ReplaceDaysHandlesManyRowsPerDay()
    {
        _repo.InsertOrIgnore([Row("2026-09-08", "920371", "旧原因", LhbSources.Sina)]);

        _repo.ReplaceDays([
            Row("2026-09-08", "920371", "当日换手率达到20%的前5只股票", LhbSources.EastMoney),
            Row("2026-09-08", "920371", "当日收盘价涨幅达到20%的前5只股票", LhbSources.EastMoney),
            Row("2026-09-08", "920371", "当日价格振幅达到30%的前5只股票", LhbSources.EastMoney),
        ]);

        Assert.Equal(3, Count());
    }

    /// <summary>滞后字段是靠"重抓同一天、覆盖旧行"填上的，所以同主键必须能被覆盖——
    /// 老的 InsertOrIgnore 在这里会静默什么都不做，那几列就永远空着。</summary>
    [Fact]
    public void UpsertOverwritesSamePrimaryKeyButInsertOrIgnoreDoesNot()
    {
        var first = Row("2026-09-08", "000560", "日换手率达到20%的前5只证券", LhbSources.EastMoney, amount: 1);
        _repo.InsertOrIgnore([first]);

        var withLagging = Row("2026-09-08", "000560", "日换手率达到20%的前5只证券", LhbSources.EastMoney, amount: 2);
        withLagging.D30Chg = -12.67;

        _repo.InsertOrIgnore([withLagging]);
        Assert.Equal(1.0, ReadAmount());          // 忽略了，旧值还在
        Assert.Null(ReadD30());

        _repo.Upsert([withLagging]);
        Assert.Equal(2.0, ReadAmount());
        Assert.Equal(-12.67, ReadD30()!.Value, 4);
    }

    /// <summary>备份是**库外的独立文件**：主库出事时它还在。跟主库同生共死的副本不叫备份，
    /// 只是把 23GB 的库撑得更大。</summary>
    [Fact]
    public void ExportWritesAStandaloneFileWithAllRows()
    {
        _repo.InsertOrIgnore([
            Row("2026-09-07", "000001", "原因A", LhbSources.Sina),
            Row("2026-09-08", "000560", "原因B", LhbSources.Sina),
        ]);

        var backup = Path.Combine(Path.GetDirectoryName(_dbPath)!,
                                  Path.GetFileNameWithoutExtension(_dbPath) + "_bak.sqlite");
        int exported = _repo.ExportTo(backup);

        Assert.Equal(2, exported);
        Assert.True(File.Exists(backup));

        // 备份之后清空主表，备份里的数据必须还在——这就是它存在的全部意义
        _repo.ReplaceDays([Row("2026-09-07", "000001", "新原因", LhbSources.EastMoney)]);

        using var conn = new SqliteConnection($"Data Source={backup}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Lhb WHERE reason = '原因A';";
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    private double ReadAmount() => ReadScalar<double>("SELECT amount FROM Lhb;");
    private double? ReadD30()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT d30_chg FROM Lhb;";
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToDouble(v);
    }

    private T ReadScalar<T>(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }
}
