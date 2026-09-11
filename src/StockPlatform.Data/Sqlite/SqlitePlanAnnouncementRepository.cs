using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// <c>PlanAnnouncement</c> 的 SQLite 实现。语义见 <see cref="IPlanAnnouncementRepository"/>：
/// **只增不删**、累积而非快照。
/// </summary>
public class SqlitePlanAnnouncementRepository : IPlanAnnouncementRepository
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private const string DateFormat = "yyyy-MM-dd";
    private readonly string _connectionString;

    public SqlitePlanAnnouncementRepository(string dbFilePath)
        => _connectionString = $"Data Source={dbFilePath}";

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void EnsureSchema()
    {
        using var conn = Open();
        SqliteSchema.EnsureSchema(conn);
    }

    public int Upsert(IEnumerable<PlanAnnouncement> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return 0;      // 空集合是空操作

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO PlanAnnouncement
                (code, kind, announce_date, stage, name, as_of_date,
                 cum_shares, cum_amount, price_low, price_high, pct_of_capital,
                 plan_cap_price, plan_amount_low, plan_amount_high,
                 title, art_code, source_url, fetched_at)
            VALUES ($c, $k, $d, $s, $n, $ao,
                    $cs, $ca, $pl, $ph, $pct,
                    $cap, $al, $ah,
                    $t, $art, $url, $at)
            ON CONFLICT(code, kind, announce_date, stage) DO UPDATE SET
                name = excluded.name, as_of_date = excluded.as_of_date,
                cum_shares = excluded.cum_shares, cum_amount = excluded.cum_amount,
                price_low = excluded.price_low, price_high = excluded.price_high,
                pct_of_capital = excluded.pct_of_capital,
                plan_cap_price = excluded.plan_cap_price,
                plan_amount_low = excluded.plan_amount_low,
                plan_amount_high = excluded.plan_amount_high,
                title = excluded.title, art_code = excluded.art_code,
                source_url = excluded.source_url, fetched_at = excluded.fetched_at;
            """;

        var names = new[] { "$c","$k","$d","$s","$n","$ao","$cs","$ca","$pl","$ph","$pct","$cap","$al","$ah","$t","$art","$url","$at" };
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var n in names)
        { var q = cmd.CreateParameter(); q.ParameterName = n; cmd.Parameters.Add(q); p[n] = q; }

        static object V(double? d) => d.HasValue ? d.Value : DBNull.Value;

        foreach (var x in list)
        {
            p["$c"].Value = x.Code;
            p["$k"].Value = x.Kind;
            p["$d"].Value = x.AnnounceDate.ToString(DateFormat, CultureInfo.InvariantCulture);
            p["$s"].Value = x.Stage;
            p["$n"].Value = x.Name ?? "";
            p["$ao"].Value = x.AsOfDate.HasValue
                ? x.AsOfDate.Value.ToString(DateFormat, CultureInfo.InvariantCulture) : DBNull.Value;
            p["$cs"].Value = V(x.CumShares);
            p["$ca"].Value = V(x.CumAmount);
            p["$pl"].Value = V(x.PriceLow);
            p["$ph"].Value = V(x.PriceHigh);
            p["$pct"].Value = V(x.PctOfCapital);
            p["$cap"].Value = V(x.PlanCapPrice);
            p["$al"].Value = V(x.PlanAmountLow);
            p["$ah"].Value = V(x.PlanAmountHigh);
            p["$t"].Value = x.Title ?? "";
            p["$art"].Value = x.ArtCode ?? "";
            p["$url"].Value = x.SourceUrl ?? "";
            p["$at"].Value = x.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    private const string SelectCols = """
        SELECT code, kind, announce_date, stage, name, as_of_date,
               cum_shares, cum_amount, price_low, price_high, pct_of_capital,
               plan_cap_price, plan_amount_low, plan_amount_high,
               title, art_code, source_url, fetched_at
        FROM PlanAnnouncement
        """;

    private static PlanAnnouncement Read(SqliteDataReader r)
    {
        static double? D(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
        static DateTime? Dt(SqliteDataReader r, int i)
            => r.IsDBNull(i) ? null
               : DateTime.TryParse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

        return new PlanAnnouncement
        {
            Code = r.GetString(0),
            Kind = r.GetString(1),
            AnnounceDate = Dt(r, 2) ?? default,
            Stage = r.GetString(3),
            Name = r.IsDBNull(4) ? "" : r.GetString(4),
            AsOfDate = Dt(r, 5),
            CumShares = D(r, 6),
            CumAmount = D(r, 7),
            PriceLow = D(r, 8),
            PriceHigh = D(r, 9),
            PctOfCapital = D(r, 10),
            PlanCapPrice = D(r, 11),
            PlanAmountLow = D(r, 12),
            PlanAmountHigh = D(r, 13),
            Title = r.IsDBNull(14) ? "" : r.GetString(14),
            ArtCode = r.IsDBNull(15) ? "" : r.GetString(15),
            SourceUrl = r.IsDBNull(16) ? "" : r.GetString(16),
            FetchedAt = Dt(r, 17) ?? default,
        };
    }

    public List<PlanAnnouncement> GetByCode(string code, string kind)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectCols + " WHERE code = $c AND kind = $k ORDER BY announce_date DESC;";
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$k", kind);

        var list = new List<PlanAnnouncement>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public Dictionary<string, PlanAnnouncement> GetOpenPlans(string kind)
    {
        // "未结束"＝**最近有过任何回购动静**，且之后没有「完毕」/「终止」。
        //
        // ⚠ 判据**不能是"有『方案』stage 的记录"**（2026-09-11 改）：方案公告可能是一年前发的，
        // 而正文抓取只够到最近三个月（东财按"该股最近 100 条公告"匹配，见 PlanWatchTask
        // 的 FirstRunLookbackDays 注释）——那样刚上线时几乎所有在进行的回购都会被判成"没有方案"，
        // 观察项一条都挂不上，而且**看起来就像全市场没人在回购**。
        //
        // 改成看"最近的动静"：回购期间每月都有进展公告，有进展就说明方案还活着。
        // 取每只票最近一条记录，它不是「完毕/终止」就算进行中。
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectCols + """
             p WHERE kind = $k
               AND announce_date = (SELECT MAX(x.announce_date) FROM PlanAnnouncement x
                                    WHERE x.code = p.code AND x.kind = p.kind)
               AND stage NOT IN ($done, $term)
             -- 同一天可能有好几条（不同 stage）：让「方案」排前面，它带着价格上限和资金区间，
             -- 挂观察项时理由里能写出"价格上限 573 元"这种有信息量的话。
             ORDER BY announce_date DESC, (stage = $proposal) DESC;
            """;
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$proposal", PlanStage.Proposal);
        cmd.Parameters.AddWithValue("$done", PlanStage.Done);
        cmd.Parameters.AddWithValue("$term", PlanStage.Terminated);

        var map = new Dictionary<string, PlanAnnouncement>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var rec = Read(r);
                map.TryAdd(rec.Code, rec);   // 倒序，第一条＝最新状态
            }

        // 最新那条通常是「进展」，而**价格上限和资金区间只写在「方案」里**。
        // 不补的话观察项的理由就只剩一句"回购方案进行中"，丢掉了最有信息量的数——
        // 现价 337 对上限 573，那才是人一眼要看的东西。
        var needCap = map.Values.Where(v => v.PlanCapPrice is null).Select(v => v.Code).ToList();
        foreach (var code in needCap)
        {
            using var q = conn.CreateCommand();
            q.CommandText = """
                SELECT plan_cap_price, plan_amount_low, plan_amount_high
                FROM PlanAnnouncement
                WHERE code = $c AND kind = $k AND stage = $proposal AND plan_cap_price IS NOT NULL
                ORDER BY announce_date DESC LIMIT 1;
                """;
            q.Parameters.AddWithValue("$c", code);
            q.Parameters.AddWithValue("$k", kind);
            q.Parameters.AddWithValue("$proposal", PlanStage.Proposal);
            using var rr = q.ExecuteReader();
            if (!rr.Read()) continue;
            var v = map[code];
            v.PlanCapPrice = rr.IsDBNull(0) ? null : rr.GetDouble(0);
            v.PlanAmountLow = rr.IsDBNull(1) ? null : rr.GetDouble(1);
            v.PlanAmountHigh = rr.IsDBNull(2) ? null : rr.GetDouble(2);
        }
        return map;
    }

    public DateTime? GetLatestAnnounceDate(string kind)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(announce_date) FROM PlanAnnouncement WHERE kind = $k;";
        cmd.Parameters.AddWithValue("$k", kind);
        var v = cmd.ExecuteScalar();
        return v is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t : null;
    }

    public (int Rows, int Stocks, int OpenPlans) GetCounts(string kind)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM PlanAnnouncement WHERE kind = $k),
                   (SELECT COUNT(DISTINCT code) FROM PlanAnnouncement WHERE kind = $k);
            """;
        cmd.Parameters.AddWithValue("$k", kind);
        int rows = 0, stocks = 0;
        using (var r = cmd.ExecuteReader())
            if (r.Read()) { rows = r.GetInt32(0); stocks = r.GetInt32(1); }
        return (rows, stocks, GetOpenPlans(kind).Count);
    }
}
