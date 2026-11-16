using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 反查"某个股东还持有哪些股票"（见 <see cref="HolderPortfolio"/>）——"其他数据"窗口点股东名称时用。
/// 跟 <see cref="SqliteStockDossierReader"/> 同一个理由按表直接读、只读连接。
///
/// 三步：
/// 1. TopShareholder 按 holder_name + kind 取出这个名字的全部行（走 ix_topshareholder_holder；
///    没有索引的老库会全表扫 545 万行，约 1 秒，也能用）。
/// 2. 涉及到的那些票，各自有哪些报告期（走主键 code 前缀）——用来判断"掉出前十"和"新进"：
///    只看股东自己的行看不出来，得知道这只票在那一期**有名单但名单里没有他**。
/// 3. HolderChange 里同名的增减持公告，挂到对应的票下面（那才是真实交易，季报只是季末快照）。
///
/// 名称一律精确匹配，不做模糊——宁可漏（半角/全角括号写法不同的会被当成两个股东），也不把两个
/// 不同的法人并到一起。
/// </summary>
public class SqliteHolderPortfolioReader
{
    private readonly string _connectionString;

    public SqliteHolderPortfolioReader(string dbFilePath)
    {
        _connectionString = $"Data Source={dbFilePath};Mode=ReadOnly";
    }

    public HolderPortfolio Read(string holderName, string kind)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // ① 这个名字的全部行：code → (period → 行)
        var rowsByCode = new Dictionary<string, SortedDictionary<string, HolderRow>>(StringComparer.Ordinal);
        bool natural = false;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT code, report_date, rank, shares, ratio, share_type
                FROM TopShareholder WHERE holder_name = $n AND kind = $k;
                """;
            cmd.Parameters.AddWithValue("$n", holderName);
            cmd.Parameters.AddWithValue("$k", kind);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var code = r.GetString(0);
                var period = r.GetString(1);
                var shareType = r.IsDBNull(5) ? "" : r.GetString(5);
                if (shareType.Contains("自然人", StringComparison.Ordinal)) natural = true;

                if (!rowsByCode.TryGetValue(code, out var byPeriod))
                    rowsByCode[code] = byPeriod = new SortedDictionary<string, HolderRow>(StringComparer.Ordinal);
                // 同一期同一名字出现两行（极少，比如分两种股本性质列示）就合并：股数、占比相加，取靠前的名次。
                var row = new HolderRow(r.GetInt32(2), Dbl(r, 3), Dbl(r, 4), shareType);
                byPeriod[period] = byPeriod.TryGetValue(period, out var prev)
                    ? new HolderRow(Math.Min(prev.Rank, row.Rank), prev.Shares + row.Shares, prev.Ratio + row.Ratio, prev.ShareType)
                    : row;
            }
        }

        var portfolio = new HolderPortfolio { HolderName = holderName, Kind = kind, IsNaturalPerson = natural };
        if (rowsByCode.Count == 0) return portfolio;

        var codesJson = JsonSerializer.Serialize(rowsByCode.Keys);

        // ② 这些票各自的报告期
        var periodsByCode = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT code, report_date FROM TopShareholder
                WHERE kind = $k AND code IN (SELECT value FROM json_each($codes))
                ORDER BY code, report_date;
                """;
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$codes", codesJson);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var code = r.GetString(0);
                if (!periodsByCode.TryGetValue(code, out var list)) periodsByCode[code] = list = new List<string>();
                list.Add(r.GetString(1));
            }
        }

        // 名称：在市取 StockMeta，退市的取 DelistedStock（任一表不存在不影响主体）
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        TryReadNames(conn, "SELECT code, name FROM DelistedStock WHERE code IN (SELECT value FROM json_each($codes));", codesJson, names);
        TryReadNames(conn, "SELECT code, name FROM StockMeta WHERE code IN (SELECT value FROM json_each($codes));", codesJson, names);

        // ③ 增减持公告
        var changesByCode = new Dictionary<string, List<HolderActivity>>(StringComparer.Ordinal);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT code, notice_date, end_date, start_date, direction, change_shares, after_shares, after_ratio, average_price, name
                FROM HolderChange WHERE holder_name = $n;
                """;
            cmd.Parameters.AddWithValue("$n", holderName);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var code = r.GetString(0);
                if (!rowsByCode.ContainsKey(code)) continue;   // 只挂到前十名单里出现过的票上
                if (!names.ContainsKey(code) && !r.IsDBNull(9)) names[code] = r.GetString(9);

                var start = Str(r, 3);
                var end = Str(r, 2);
                if (!changesByCode.TryGetValue(code, out var list)) changesByCode[code] = list = new List<HolderActivity>();
                list.Add(new HolderActivity
                {
                    Date = end,
                    Source = "增减持公告",
                    Action = Str(r, 4),
                    Change = r.IsDBNull(5) ? "" : SignedNum(r.GetDouble(5)),
                    Shares = r.IsDBNull(6) ? "" : Num(r.GetDouble(6)),
                    Ratio = r.IsDBNull(7) ? "" : Pct(r.GetDouble(7)),
                    Price = r.IsDBNull(8) ? "" : r.GetDouble(8).ToString("F2", CultureInfo.InvariantCulture),
                    Note = $"公告 {Str(r, 1)}" + (start.Length > 0 && start != end ? $"，区间 {start} ~ {end}" : "") + "；占比为变动后占总股本",
                });
            }
        }
        catch (SqliteException)
        {
            // 老库没有 HolderChange——只少了公告那部分，前十名单照常显示
        }

        foreach (var (code, byPeriod) in rowsByCode)
        {
            var stockPeriods = periodsByCode.GetValueOrDefault(code) ?? byPeriod.Keys.ToList();
            var latest = byPeriod.Keys.Last();
            var cur = byPeriod[latest];

            var history = BuildSnapshotHistory(byPeriod, stockPeriods);
            if (changesByCode.TryGetValue(code, out var changes)) history.AddRange(changes);
            // 日期倒序；同一天名单行排在公告行前面
            history = history
                .OrderByDescending(h => h.Date, StringComparer.Ordinal)
                .ThenBy(h => h.Source == "前十名单" ? 0 : 1)
                .ToList();

            portfolio.Positions.Add(new HolderPosition
            {
                Code = code,
                Name = names.GetValueOrDefault(code) ?? "",
                Period = latest,
                StockLatestPeriod = stockPeriods.Count > 0 ? stockPeriods[^1] : latest,
                Rank = cur.Rank,
                Shares = cur.Shares,
                Ratio = cur.Ratio,
                ShareType = cur.ShareType,
                History = history,
            });
        }

        portfolio.Positions.Sort((a, b) =>
            a.Exited != b.Exited ? a.Exited.CompareTo(b.Exited) : b.Ratio.CompareTo(a.Ratio));
        return portfolio;
    }

    /// <summary>
    /// 按这只票的**全部**报告期逐期走一遍：在名单里就跟上一期比（新进/增持/减持/不变），上一期在、
    /// 这一期不在就记一条"退出前十"。只拿股东自己的行算不出退出——得知道那一期有名单但没他。
    /// </summary>
    internal static List<HolderActivity> BuildSnapshotHistory(
        SortedDictionary<string, HolderRow> byPeriod, List<string> stockPeriods)
    {
        var result = new List<HolderActivity>();
        HolderRow? prev = null;
        bool first = true;
        foreach (var period in stockPeriods)
        {
            if (byPeriod.TryGetValue(period, out var row))
            {
                string action, change;
                if (prev == null)
                {
                    // 这只票本地第一期就在：不知道之前有没有，不能说"新进"
                    action = first ? "" : "新进";
                    change = "";
                }
                else
                {
                    var diff = row.Shares - prev.Value.Shares;
                    action = diff > 0 ? "增持" : diff < 0 ? "减持" : "不变";
                    change = diff == 0 ? "" : SignedNum(diff);
                }
                result.Add(new HolderActivity
                {
                    Date = period,
                    Source = "前十名单",
                    Action = action,
                    Change = change,
                    Shares = Num(row.Shares),
                    Ratio = Pct(row.Ratio),
                    Rank = row.Rank.ToString(CultureInfo.InvariantCulture),
                });
                prev = row;
            }
            else if (prev != null)
            {
                result.Add(new HolderActivity
                {
                    Date = period,
                    Source = "前十名单",
                    Action = "退出前十",
                    Note = $"上期持 {Num(prev.Value.Shares)} 股；掉出前十不等于清仓",
                });
                prev = null;
            }
            first = false;
        }
        return result;
    }

    private static void TryReadNames(SqliteConnection conn, string sql, string codesJson, Dictionary<string, string> names)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$codes", codesJson);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(1)) names[r.GetString(0)] = r.GetString(1);
        }
        catch (SqliteException)
        {
            // 表不存在就没有名称，代码照样能看
        }
    }

    internal readonly record struct HolderRow(int Rank, double Shares, double Ratio, string ShareType);

    private static double Dbl(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetDouble(i);
    private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static string Num(double v) => v.ToString("N0", CultureInfo.InvariantCulture);
    private static string SignedNum(double v) => (v > 0 ? "+" : "") + v.ToString("N0", CultureInfo.InvariantCulture);
    private static string Pct(double v) => $"{v.ToString("F2", CultureInfo.InvariantCulture)}%";
}
