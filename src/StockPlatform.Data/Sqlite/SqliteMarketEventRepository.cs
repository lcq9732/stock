using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 大宗交易 / 机构调研 / 限售解禁 / 股东增减持的本地存取（2026-09-03）。
///
/// 四张表放一个仓储里是因为它们同构：都是增量累积的事件表、都靠 INSERT OR REPLACE 幂等、
/// 都按日期做水位线（限售解禁除外，它含未来计划所以全量重取）。
///
/// <b>每个写入方法都带批内主键去重自检</b>——这是从龙虎榜席位那次事故来的：主键少一列，
/// 重复行会静默互相覆盖，一天 415 行丢 31 行（7.5%）都不会报错，等到分析结果不对了才发现
/// 就晚了。现在一批数据里出现主键重复会通过 <see cref="OnWarning"/> 立刻告警。
/// </summary>
public class SqliteMarketEventRepository : IMarketEventRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    /// <summary>批内主键重复时的告警回调（编排层把它转成 progress 提示 + errors）。</summary>
    public event Action<string>? OnWarning;

    public SqliteMarketEventRepository(string dbFilePath)
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

    /// <summary>
    /// 批内主键去重自检。返回去重后的列表；发现重复就告警。
    /// 之所以是"告警+去重"而不是"抛异常"：抓了几十万行不该因为主键差一列就整轮失败，
    /// 但也绝不能让它悄悄发生——龙虎榜那次就是悄悄丢了 7.5%。
    /// </summary>
    private List<T> DedupeAndWarn<T>(List<T> list, Func<T, string> keyOf, string table)
    {
        var seen = new Dictionary<string, T>(list.Count);
        int dup = 0;
        var samples = new List<string>();
        foreach (var x in list)
        {
            var k = keyOf(x);
            if (seen.ContainsKey(k))
            {
                dup++;
                // 带上样例 key：光知道"有重复"没法修，得知道是哪几行撞了才能定位缺哪个区分列。
                // 机构调研那次就是靠样例才发现取错了字段（ORG_NAME 是上市公司名不是调研机构）。
                if (samples.Count < 3) samples.Add(k);
            }
            seen[k] = x;      // 后来的覆盖先前的，跟 INSERT OR REPLACE 行为一致
        }
        if (dup > 0)
            OnWarning?.Invoke(
                $"⚠ {table}：本批 {list.Count} 行里有 {dup} 行主键重复（{dup * 100.0 / list.Count:F1}%），" +
                $"已按后到覆盖处理。主键可能少了区分列——这类问题不会报错但会静默丢数据。" +
                $"重复样例：{string.Join(" / ", samples)}");
        return seen.Values.ToList();
    }

    // ── 大宗交易 ───────────────────────────────────────────────────
    public int UpsertBlockTrades(IEnumerable<BlockTrade> items)
    {
        var list = DedupeAndWarn(items.ToList(),
            x => $"{x.TradeDate:yyyyMMdd}|{x.Code}|{x.DailyRank}", "BlockTrade");
        if (list.Count == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO BlockTrade
                (trade_date, code, daily_rank, name, deal_price, deal_volume, deal_amount,
                 premium_ratio, close_price, change_rate, turnover_rate, buyer_name, seller_name, fetched_at)
            VALUES ($td,$code,$rank,$name,$price,$vol,$amt,$prem,$close,$chg,$turn,$buyer,$seller,$f);
            """;
        var p = Params(cmd, "$td", "$code", "$rank", "$name", "$price", "$vol", "$amt",
                       "$prem", "$close", "$chg", "$turn", "$buyer", "$seller", "$f");
        foreach (var x in list)
        {
            p["$td"].Value = D(x.TradeDate); p["$code"].Value = x.Code; p["$rank"].Value = x.DailyRank;
            p["$name"].Value = x.Name; p["$price"].Value = N(x.DealPrice); p["$vol"].Value = N(x.DealVolume);
            p["$amt"].Value = N(x.DealAmount); p["$prem"].Value = N(x.PremiumRatio);
            p["$close"].Value = N(x.ClosePrice); p["$chg"].Value = N(x.ChangeRate);
            p["$turn"].Value = N(x.TurnoverRate); p["$buyer"].Value = x.BuyerName;
            p["$seller"].Value = x.SellerName; p["$f"].Value = T(x.FetchedAt);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    // ── 机构调研 ───────────────────────────────────────────────────
    public int UpsertOrgSurveys(IEnumerable<OrgSurvey> items)
    {
        var list = DedupeAndWarn(items.ToList(),
            x => $"{x.Code}|{x.NoticeDate:yyyyMMdd}|{x.OrgName}|{x.ReceiveStartDate:yyyyMMdd}|{x.SurveyNo}", "OrgSurvey");
        if (list.Count == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO OrgSurvey
                (code, notice_date, org_name, receive_start_date, survey_no, name, receive_end_date,
                 object_code, org_type, receive_way, receive_place, investigators, receptionist, org_total, fetched_at)
            VALUES ($code,$nd,$org,$rs,$no,$name,$re,$ocode,$otype,$way,$place,$inv,$rec,$cnt,$f);
            """;
        var p = Params(cmd, "$code", "$nd", "$org", "$rs", "$no", "$name", "$re", "$ocode", "$otype",
                       "$way", "$place", "$inv", "$rec", "$cnt", "$f");
        foreach (var x in list)
        {
            p["$code"].Value = x.Code; p["$nd"].Value = D(x.NoticeDate); p["$org"].Value = x.OrgName;
            p["$rs"].Value = x.ReceiveStartDate.HasValue ? D(x.ReceiveStartDate.Value) : "";
            p["$no"].Value = x.SurveyNo; p["$name"].Value = x.Name;
            p["$re"].Value = x.ReceiveEndDate.HasValue ? D(x.ReceiveEndDate.Value) : (object)DBNull.Value;
            p["$ocode"].Value = x.ObjectCode; p["$otype"].Value = x.OrgType; p["$way"].Value = x.ReceiveWay; p["$place"].Value = x.ReceivePlace;
            p["$inv"].Value = x.Investigators; p["$rec"].Value = x.Receptionist;
            p["$cnt"].Value = N(x.OrgTotal); p["$f"].Value = T(x.FetchedAt);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    // ── 限售解禁 ───────────────────────────────────────────────────
    public int UpsertShareLifts(IEnumerable<ShareLift> items)
    {
        var list = DedupeAndWarn(items.ToList(),
            x => $"{x.Code}|{x.FreeDate:yyyyMMdd}|{x.ShareType}", "ShareLift");
        if (list.Count == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO ShareLift
                (code, free_date, share_type, name, lift_shares, lift_market_cap,
                 free_ratio, total_ratio, holder_count, fetched_at)
            VALUES ($code,$fd,$type,$name,$sh,$cap,$fr,$tr,$hc,$f);
            """;
        var p = Params(cmd, "$code", "$fd", "$type", "$name", "$sh", "$cap", "$fr", "$tr", "$hc", "$f");
        foreach (var x in list)
        {
            p["$code"].Value = x.Code; p["$fd"].Value = D(x.FreeDate); p["$type"].Value = x.ShareType;
            p["$name"].Value = x.Name; p["$sh"].Value = N(x.LiftShares); p["$cap"].Value = N(x.LiftMarketCap);
            p["$fr"].Value = N(x.FreeRatio); p["$tr"].Value = N(x.TotalRatio);
            p["$hc"].Value = N(x.HolderCount); p["$f"].Value = T(x.FetchedAt);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    // ── 股东增减持 ─────────────────────────────────────────────────
    public int UpsertHolderChanges(IEnumerable<HolderChange> items)
    {
        var list = DedupeAndWarn(items.ToList(),
            x => $"{x.Code}|{x.NoticeDate:yyyyMMdd}|{x.HolderName}|{x.EndDate:yyyyMMdd}", "HolderChange");
        if (list.Count == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO HolderChange
                (code, notice_date, holder_name, end_date, name, direction, change_shares,
                 change_ratio, after_shares, after_ratio, start_date, average_price, fetched_at)
            VALUES ($code,$nd,$holder,$ed,$name,$dir,$cs,$cr,$as,$ar,$sd,$ap,$f);
            """;
        var p = Params(cmd, "$code", "$nd", "$holder", "$ed", "$name", "$dir", "$cs",
                       "$cr", "$as", "$ar", "$sd", "$ap", "$f");
        foreach (var x in list)
        {
            p["$code"].Value = x.Code; p["$nd"].Value = D(x.NoticeDate); p["$holder"].Value = x.HolderName;
            p["$ed"].Value = x.EndDate.HasValue ? D(x.EndDate.Value) : "";
            p["$name"].Value = x.Name; p["$dir"].Value = x.Direction;
            p["$cs"].Value = N(x.ChangeShares); p["$cr"].Value = N(x.ChangeRatio);
            p["$as"].Value = N(x.AfterShares); p["$ar"].Value = N(x.AfterRatio);
            p["$sd"].Value = x.StartDate.HasValue ? D(x.StartDate.Value) : (object)DBNull.Value;
            p["$ap"].Value = N(x.AveragePrice); p["$f"].Value = T(x.FetchedAt);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    // ── 水位线与统计 ───────────────────────────────────────────────
    public DateTime? GetLatestDate(string table, string column) => ScalarDate($"SELECT MAX({column}) FROM {table}");

    public int Count(string table)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private DateTime? ScalarDate(string sql)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        if (v == null || v is DBNull) return null;
        var s = v.ToString();
        return string.IsNullOrEmpty(s) || s.Length < 10 ? null
            : DateTime.ParseExact(s.Substring(0, 10), DateFormat, CultureInfo.InvariantCulture);
    }

    // ── 小工具 ─────────────────────────────────────────────────────
    private static Dictionary<string, SqliteParameter> Params(SqliteCommand cmd, params string[] names)
    {
        var map = new Dictionary<string, SqliteParameter>();
        foreach (var n in names)
        {
            var p = cmd.CreateParameter(); p.ParameterName = n; cmd.Parameters.Add(p); map[n] = p;
        }
        return map;
    }

    private static string D(DateTime d) => d.ToString(DateFormat, CultureInfo.InvariantCulture);
    private static string T(DateTime d) => d.ToString(TimeFormat, CultureInfo.InvariantCulture);
    private static object N(double? v) => (object?)v ?? DBNull.Value;
    private static object N(int? v) => (object?)v ?? DBNull.Value;
}
