using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 判据改版之后，**已经匹配过的错值到底能不能被修掉**（2026-09-15）。
///
/// ════ 为什么单独一组 ════
/// 判据本身对不对，<see cref="ParentGroupMatchTests"/> 已经钉了。但判据对了不等于修复能落地——
/// 2026-09-15 查出来两处会让修复**完全失效**的地方，两处都在这一组里钉着：
///
/// 1. <b>GetPartnerNamesToMatch 默认只捞 partner_code IS NULL 的</b>。而错配行的 partner_code
///    不是 NULL、是错值，永远轮不到重新评估——改了正则也白改。所以要有版本号驱动全量。
///
/// 2. <b>ApplyMatches 原来只遍历命中的名字</b>。新规则下「中国铝业集团有限公司」改判了，
///    但如果它压根不在 hits 里（比如改判成不匹配），旧的错值就原封不动留着。
///    所以全量重匹必须把"评估过的全部名字"一起传下去。
/// </summary>
public class MatcherRematchTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dbPath;
    private readonly SqliteCustomerSupplierRepository _repo;

    public MatcherRematchTests(ITestOutputHelper output)
    {
        _out = output;
        _dbPath = Path.Combine(Path.GetTempPath(), $"custsupp_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteCustomerSupplierRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    /// <summary>
    /// ⚠ 主键是 <c>(code, report_date, is_supplier, rank)</c>，所以每行的 (code, rank) 必须唯一，
    ///   否则后写的把先写的顶掉——我第一版让 4500 行共用一个 code、rank 只在 1~5 循环，
    ///   结果库里只剩 5 行，测试红得莫名其妙。这里按下标同时推进 code 和 rank。
    /// </summary>
    private void Seed(params (string Code, string Partner)[] rows)
    {
        _repo.Upsert(rows.Select((r, i) => new CustomerSupplier
        {
            Code = i < 5 ? r.Code : $"{int.Parse(r.Code) + i / 5:D6}",
            ReportDate = new DateTime(2025, 12, 31),
            IsSupplier = false,
            Rank = (i % 5) + 1,
            PartnerName = r.Partner,
            Amount = 1e8,
        }).ToList());
    }

    private string? CodeOf(string partner)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT partner_code FROM StockCustomerSupplier WHERE partner_name = $n LIMIT 1;";
        cmd.Parameters.AddWithValue("$n", partner);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : (string)v;
    }

    /// <summary>
    /// ⚠ **这一条是整个修复的命门**：改判要能覆盖旧值，不再命中要能清成 NULL。
    /// 没有 evaluated 这个参数的话，两件事都做不到。
    /// </summary>
    [Fact]
    public void 全量重匹能改判也能清值()
    {
        Seed(("000001", "中国铝业集团有限公司"), ("000002", "比亚迪股份有限公司"));

        // 第一轮：老规则，母集团被当成了上市公司本人
        _repo.ApplyMatches(new Dictionary<string, (string, string)>
        {
            ["中国铝业集团有限公司"] = ("601600", "normalized"),
            ["比亚迪股份有限公司"] = ("002594", "exact"),
        });
        Assert.Equal("601600", CodeOf("中国铝业集团有限公司"));

        // 第二轮：新规则。母集团改判成 parent_group，另一个不变
        var evaluated = new[] { "中国铝业集团有限公司", "比亚迪股份有限公司" };
        _repo.ApplyMatches(new Dictionary<string, (string, string)>
        {
            ["中国铝业集团有限公司"] = ("601600", "parent_group"),
            ["比亚迪股份有限公司"] = ("002594", "exact"),
        }, evaluated);

        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT match_type FROM StockCustomerSupplier WHERE partner_name = '中国铝业集团有限公司';";
            Assert.Equal("parent_group", cmd.ExecuteScalar());
        }

        // 第三轮：假设规则又收紧，这个名字干脆不再命中——旧值必须被清掉
        _repo.ApplyMatches(new Dictionary<string, (string, string)>
        {
            ["比亚迪股份有限公司"] = ("002594", "exact"),
        }, evaluated);

        Assert.Null(CodeOf("中国铝业集团有限公司"));
        Assert.Equal("002594", CodeOf("比亚迪股份有限公司"));   // 没被误伤
        _out.WriteLine("改判 → 清值 两步都生效，且没误伤其它名字");
    }

    /// <summary>
    /// 增量模式（evaluated 传 null）**绝不能清值**——那一轮只评估了本来就是 NULL 的名字，
    /// 拿它去清会把上一轮的正确结果全抹掉。
    /// </summary>
    [Fact]
    public void 增量模式不清值()
    {
        Seed(("000001", "比亚迪股份有限公司"), ("000002", "某个没认出来的小公司"));
        _repo.ApplyMatches(new Dictionary<string, (string, string)>
        {
            ["比亚迪股份有限公司"] = ("002594", "exact"),
        });

        // 增量只评估了那个没认出来的，比亚迪压根没参与
        _repo.ApplyMatches(new Dictionary<string, (string, string)>(), evaluated: null);

        Assert.Equal("002594", CodeOf("比亚迪股份有限公司"));
    }

    /// <summary>
    /// <b>空字典 + 没给 evaluated = 空操作</b>。这是老铁律：公司档案没拉到时
    /// 一个名字都匹配不上，那一轮必须保留库里上次的结果，不能当成"全都不认识了"。
    /// </summary>
    [Fact]
    public void 档案拉空时不许抹掉已有结果()
    {
        Seed(("000001", "比亚迪股份有限公司"));
        _repo.ApplyMatches(new Dictionary<string, (string, string)>
        {
            ["比亚迪股份有限公司"] = ("002594", "exact"),
        });

        Assert.Equal(0, _repo.ApplyMatches(new Dictionary<string, (string, string)>()));
        Assert.Equal("002594", CodeOf("比亚迪股份有限公司"));
    }

    /// <summary>版本号：没记过是 0，记了能读回来。任务靠它决定要不要全量重匹。</summary>
    [Fact]
    public void 版本号存得下也读得回()
    {
        Assert.Equal(0, _repo.GetMatcherVersion());     // 老库/从没跑过
        _repo.SetMatcherVersion(2);
        Assert.Equal(2, _repo.GetMatcherVersion());
        _repo.SetMatcherVersion(3);                     // 单行表，覆盖不是追加
        Assert.Equal(3, _repo.GetMatcherVersion());
    }

    /// <summary>
    /// 分批提交不能把结果写漏。批大小是 2000，这里塞 4500 个名字跨三个批次。
    /// （这个库有过单事务写 468 万行把 WAL 撑到 162GB 的前科，所以才要分批。）
    /// </summary>
    [Fact]
    public void 跨批次提交结果完整()
    {
        var rows = Enumerable.Range(0, 4500).Select(i => ("000001", $"公司{i}号")).ToArray();
        Seed(rows);

        var hits = Enumerable.Range(0, 4500)
            .ToDictionary(i => $"公司{i}号", i => ($"{600000 + i}", "exact"));
        _repo.ApplyMatches(hits, hits.Keys.ToList());

        Assert.Equal("600000", CodeOf("公司0号"));
        Assert.Equal("602499", CodeOf("公司2499号"));    // 第二批
        Assert.Equal("604499", CodeOf("公司4499号"));    // 第三批
    }
}
