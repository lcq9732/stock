using Microsoft.Data.Sqlite;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 在市股被误标退市（2026-09-28）。<c>600801 华新建材</c>只有 B股 900933 终止，上交所"终止上市"
/// 名单按公司列、照样填着 A股代码，于是被标成 delisted，日更K线从 09-16 起停抓，没有任何地方报。
///
/// 三道：源头剔除（<see cref="ExchangeDelistedListProvider.IsBShareOnlyTermination"/>）、
/// 入库前跟本地行情对账（<see cref="DelistedTailPlanner.LooksStillTrading"/>）、存量回修（两个仓储方法）。
/// </summary>
public class DelistedMislabelTests : IDisposable
{
    private readonly string _dbPath;
    private static readonly DateTime Today = new(2026, 9, 28);

    public DelistedMislabelTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"delisted_mislabel_{Guid.NewGuid():N}.sqlite");
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        SqliteSchema.EnsureSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO StockMeta (code,name,type) VALUES
              ('600801','华新建材','delisted'),
              ('600087','退市长油','delisted'),
              ('sh510050','50ETF','etf');
            INSERT INTO DelistedStock (code,name,exchange,delist_date) VALUES
              ('600801','华新建材','sse',NULL),
              ('600190','退市锦港','sse','2025-07-28');
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() { try { File.Delete(_dbPath); } catch { } }

    // ── 源头：上交所名单里"只有 B股终止"的行 ──

    [Theory]
    [InlineData("900933", "-", true)]           // 华新：B股终止、A股在市
    [InlineData("900952", "2025-07-28", false)] // 退市锦港：A/B 一起退，有终止日
    [InlineData("-", "-", false)]               // 纯 A股、没终止日（转板/合并那类）——不归这条管
    [InlineData("", "2020-01-01", false)]
    public void 只有B股终止的公司行_不算A股退市(string codeB, string changeDate, bool expected)
        => Assert.Equal(expected, ExchangeDelistedListProvider.IsBShareOnlyTermination(codeB, changeDate));

    // ── 兜底：名单说退市、本地K线还在更新 ──

    private static DelistedStockRow Row(DateTime? delist) => new() { Code = "600801", Name = "X", DelistDate = delist };

    [Fact]
    public void 没终止日且K线30天内还在更新_判为还在交易()
        => Assert.True(DelistedTailPlanner.LooksStillTrading(Row(null), new DateTime(2026, 9, 16), Today));

    [Fact]
    public void 没终止日但K线早已停_照常标退市()
        => Assert.False(DelistedTailPlanner.LooksStillTrading(Row(null), new DateTime(2026, 7, 29), Today));

    /// <summary>刚退市的票最后一根本来就在 30 天内——有官方终止日就信，不拦。</summary>
    [Fact]
    public void 有终止日_即使K线很新也不拦()
        => Assert.False(DelistedTailPlanner.LooksStillTrading(Row(new DateTime(2026, 9, 20)), new DateTime(2026, 9, 12), Today));

    [Fact]
    public void 本地没有K线_不拦()
        => Assert.False(DelistedTailPlanner.LooksStillTrading(Row(null), null, Today));

    // ── 存量回修（真 SQLite）──

    [Fact]
    public void 回修_只删没有终止日的行()
    {
        var repo = new SqliteDelistedRepository(_dbPath);
        var n = repo.DeleteWithoutDelistDate(["600801", "600190"]);

        Assert.Equal(1, n);
        Assert.Equal(["600190"], repo.GetAll().Select(r => r.Code));
    }

    [Fact]
    public void 回修_delisted改回stock_其它类型不碰()
    {
        var n = SqliteStockMetaUpsert.ReinstateDelisted(_dbPath, ["600801", "sh510050"]);

        Assert.Equal(1, n);
        var stocks = SqliteStockMetaUpsert.GetAll(_dbPath).Select(x => x.Code).ToList();
        Assert.Equal(["600801"], stocks);
        Assert.Equal(["600087"], SqliteStockMetaUpsert.GetByTypes(_dbPath, "delisted").Select(x => x.Code));
        Assert.Contains(SqliteStockMetaUpsert.GetAllInstruments(_dbPath), x => x.Code == "sh510050" && x.Type == "etf");
    }
}
