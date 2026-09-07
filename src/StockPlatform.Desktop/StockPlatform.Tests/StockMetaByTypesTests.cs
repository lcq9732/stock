using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 按 type 取标的（<see cref="SqliteStockMetaUpsert.GetByTypes"/>）。
///
/// 存在的理由是 <see cref="SqliteStockMetaUpsert.GetAll"/> 动不得——它被 20 多处共用（9 个选股页
/// 靠它补股票名、Mobile 也在用），一旦让它带上 delisted，退市股就会混进各页的候选池。
/// 所以分红抓取要覆盖退市股，只能另开一个方法。
///
/// 关键判据：**不含 'stock' 时不能把老库的 NULL 行捎带出来**——NULL 在本项目里等同于个股，
/// 只查 delisted 却返回一堆 NULL 行的话，分红抓取的标的集就悄悄变了。
/// </summary>
public class StockMetaByTypesTests : IDisposable
{
    private readonly string _dbPath;

    public StockMetaByTypesTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"meta_types_{Guid.NewGuid():N}.sqlite");
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        SqliteSchema.EnsureSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO StockMeta (code,name,type) VALUES
              ('600000','浦发银行','stock'),
              ('000001','平安银行','stock'),
              ('600705','中航产融','delisted'),
              ('000666','经纬纺机','delisted'),
              ('sh510050','50ETF','etf'),
              ('sh000001','上证指数','index'),
              ('BK0475','银行Ⅱ','board'),
              ('600666','老库遗留',NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() { try { File.Delete(_dbPath); } catch { } }

    [Fact]
    public void StockOnly_IncludesNullTypeLegacyRows()
    {
        var got = SqliteStockMetaUpsert.GetByTypes(_dbPath, "stock").Select(x => x.Code).ToList();
        Assert.Equal(["000001", "600000", "600666"], got);   // NULL 行(600666)算个股
    }

    [Fact]
    public void StockPlusDelisted_IsWhatDividendFetchNeeds()
    {
        var got = SqliteStockMetaUpsert.GetByTypes(_dbPath, "stock", "delisted").Select(x => x.Code).ToList();
        Assert.Equal(["000001", "000666", "600000", "600666", "600705"], got);
        Assert.DoesNotContain("sh510050", got);   // 不能混进 ETF/指数/板块
        Assert.DoesNotContain("sh000001", got);
        Assert.DoesNotContain("BK0475", got);
    }

    [Fact]
    public void DelistedOnly_DoesNotDragInNullRows()
    {
        // 不含 'stock' 时，老库的 NULL 行不能被捎带出来
        var got = SqliteStockMetaUpsert.GetByTypes(_dbPath, "delisted").Select(x => x.Code).ToList();
        Assert.Equal(["000666", "600705"], got);
    }

    [Fact]
    public void EmptyTypes_ReturnsEmpty()
    {
        Assert.Empty(SqliteStockMetaUpsert.GetByTypes(_dbPath));
    }

    [Fact]
    public void GetAll_StaysUnchanged()
    {
        // 回归：GetAll 的口径不能因为新增方法而变（它被 20 多处共用）
        var got = SqliteStockMetaUpsert.GetAll(_dbPath).Select(x => x.Code).ToList();
        Assert.Equal(["000001", "600000", "600666"], got);
        Assert.DoesNotContain("600705", got);
    }
}
