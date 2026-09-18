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
    /// 批内主键去重自检。返回去重后的列表；**真的丢了东西**才告警。
    /// 之所以是"告警+去重"而不是"抛异常"：抓了几十万行不该因为主键差一列就整轮失败，
    /// 但也绝不能让它悄悄发生——龙虎榜那次就是悄悄丢了 7.5%。
    ///
    /// ════ 主键撞了分两种，只有一种是问题（2026-09-18 拆开）════
    ///   · **内容也一样**——接口自己把同一条记录返回了两遍，覆盖掉一条什么都没丢。
    ///     实测：机构调研里东财把"2 家投资者"展开成两行，而区分列 <c>NUM</c> 两行都给 1，
    ///     个人投资者又没有机构代码和参与人员，于是两行逐字段相同。真实家数在 <c>SUM</c>
    ///     （<c>OrgTotal</c>）里存着，一条都没少。
    ///   · **内容不一样**——那才是"主键少了区分列"，覆盖会静默丢数据，必须报。
    ///
    /// 拆开之前这两种都报同一句"会静默丢数据"，于是【拉取市场事件】每轮都带着 1 条错误、
    /// 连着十几天，而它其实零影响——**天天喊的告警等于没有告警**。
    ///
    /// 内容相同的那批不报错，但**占比过高时提一句**（见 <see cref="EchoNoticeRatio"/>）：
    /// 接口哪天开始大批量重复返回，仍然该有人知道，只是那不叫"丢数据"。
    /// </summary>
    private List<T> DedupeAndWarn<T>(List<T> list, Func<T, string> keyOf, string table)
    {
        var seen = new Dictionary<string, T>(list.Count);
        int lost = 0;      // 主键撞了、而且内容不同 —— 真的丢了东西
        int echo = 0;      // 主键撞了、内容也完全一样 —— 接口自己的重复返回
        var samples = new List<string>();
        foreach (var x in list)
        {
            var k = keyOf(x);
            if (seen.TryGetValue(k, out var prev))
            {
                if (SameContent(prev, x))
                {
                    echo++;
                }
                else
                {
                    lost++;
                    // 带上样例 key：光知道"有重复"没法修，得知道是哪几行撞了才能定位缺哪个区分列。
                    // 机构调研那次就是靠样例才发现取错了字段（ORG_NAME 是上市公司名不是调研机构）。
                    if (samples.Count < 3) samples.Add(k);
                }
            }
            seen[k] = x;      // 后来的覆盖先前的，跟 INSERT OR REPLACE 行为一致
        }

        if (lost > 0)
            OnWarning?.Invoke(
                $"⚠ {table}：本批 {list.Count} 行里有 {lost} 行主键重复、**内容还不一样**"
                + $"（{lost * 100.0 / list.Count:F1}%），已按后到覆盖处理。"
                + "主键少了区分列——这类问题不会报错但会静默丢数据。"
                + $"重复样例：{string.Join(" / ", samples)}");

        if (echo >= EchoNoticeMin && echo >= list.Count * EchoNoticeRatio)
            OnWarning?.Invoke(
                $"（{table}：本批 {list.Count} 行里有 {echo} 行是接口原样重复返回的" +
                $"（{echo * 100.0 / list.Count:F1}%，逐字段相同），已去重。没有丢数据，" +
                $"但占比这么高值得看一眼接口是不是变了。）");

        return seen.Values.ToList();
    }

    /// <summary>接口原样重复返回的占比超过这个数才提一句。实测机构调研常年 0.004%（5 万行里 2 行），
    /// 那个量级不值得每轮都说。</summary>
    private const double EchoNoticeRatio = 0.01;

    /// <summary>
    /// 而且至少要有这么多行才看占比——**小样本里的比例是噪声不是信号**：
    /// 一批只有 2 行、其中 1 行重复就是 50%，按占比判必报，可那说明不了任何事。
    /// 这跟日频表体检里的"市场判据样本量下限"是同一个道理（那边取 30）。
    /// </summary>
    private const int EchoNoticeMin = 20;

    /// <summary>
    /// 两行的**内容**是不是完全一样（<see cref="DedupeAndWarn"/> 用来区分"接口重复返回"和"真丢数据"）。
    ///
    /// 走反射比所有属性——只在主键撞了的时候才调，一批最多几行，代价可以忽略；
    /// 而写死字段清单的话，以后给模型加一列就得记得同步改这里，漏了就会把"真丢数据"误判成重复返回。
    ///
    /// ⚠ 排除 <c>FetchedAt</c>：那是**我们什么时候抓的**，不是数据内容。同一批里它也可能差几毫秒。
    /// </summary>
    private static bool SameContent<T>(T a, T b)
    {
        foreach (var p in typeof(T).GetProperties())
        {
            if (p.Name == nameof(OrgSurvey.FetchedAt)) continue;
            if (!Equals(p.GetValue(a), p.GetValue(b))) return false;
        }
        return true;
    }

    // ── 大宗交易 ───────────────────────────────────────────────────
    /// <summary>
    /// **整日替换**某个交易日的大宗交易（2026-09-17）——先删该日全部，再按本次返回的顺序重写。
    ///
    /// ════ 为什么不是 UPSERT ════
    /// UPSERT 要靠主键认出"同一笔"，而主键第三列 <c>daily_rank</c> 原来存的是东财的
    /// <c>DAILY_RANK</c>，它**跨抓取不稳定**：同一笔交易换个时间抓，值就变了，于是每次重抓
    /// 都 INSERT 一份副本。全表因此多出 3087 行、最近一个月的金额普遍虚高一倍。
    /// 详见 doc/block-trade-task-design.md。
    ///
    /// 整日替换之后，<c>daily_rank</c> 改由这里**按 code 分组、按接口返回顺序自赋 1..N**，
    /// 于是它真的就是"该股当日第几笔"了（比东财那个还准确），而且反复跑多少次结果都一样。
    ///
    /// ⚠ 两条铁律：
    /// ① <b>删和写必须在同一个事务里</b>——中途崩溃要回滚到删除前，不能留下空的一天；
    /// ② <b>调用方必须先确认这一天抓全了</b>（<c>BlockTradeDay.IsComplete</c>）。
    ///   拿残缺的一天覆盖完整的一天，事后完全看不出来。
    ///
    /// <paramref name="rows"/> 为空时**不删**、直接返回 0：接口偶尔会返回空结果，
    /// 真按空的去替换就会把一整天抹掉。"那天真的没有大宗交易"由调用方去判断和记录。
    /// </summary>
    /// <returns>写入的行数。</returns>
    public int ReplaceBlockTradesForDay(DateTime day, IReadOnlyList<BlockTrade> rows)
    {
        if (rows.Count == 0) return 0;

        // 按 code 分组、组内保持接口返回顺序，赋 1..N。GroupBy 在 LINQ to Objects 里是稳定的，
        // 组内元素顺序就是原顺序——正是我们要的"第几笔"。
        var numbered = rows.GroupBy(x => x.Code)
                           .SelectMany(g => g.Select((x, i) => (Row: x, Rank: i + 1)))
                           .ToList();

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM BlockTrade WHERE trade_date = $td;";
            del.Parameters.AddWithValue("$td", D(day.Date));
            del.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO BlockTrade
                (trade_date, code, daily_rank, name, deal_price, deal_volume, deal_amount,
                 premium_ratio, close_price, change_rate, turnover_rate, buyer_name, seller_name,
                 buyer_code, seller_code, discount_ratio, free_shares_ratio, total_shares_ratio,
                 change_rate_1d, change_rate_5d, change_rate_10d, change_rate_20d, fetched_at)
            VALUES ($td,$code,$rank,$name,$price,$vol,$amt,$prem,$close,$chg,$turn,$buyer,$seller,
                    $bcode,$scode,$disc,$fsr,$tsr,$c1,$c5,$c10,$c20,$f);
            """;
        var p = Params(cmd, "$td", "$code", "$rank", "$name", "$price", "$vol", "$amt",
                       "$prem", "$close", "$chg", "$turn", "$buyer", "$seller",
                       "$bcode", "$scode", "$disc", "$fsr", "$tsr", "$c1", "$c5", "$c10", "$c20", "$f");
        foreach (var (x, rank) in numbered)
        {
            p["$td"].Value = D(x.TradeDate); p["$code"].Value = x.Code; p["$rank"].Value = rank;
            p["$name"].Value = x.Name; p["$price"].Value = N(x.DealPrice); p["$vol"].Value = N(x.DealVolume);
            p["$amt"].Value = N(x.DealAmount); p["$prem"].Value = N(x.PremiumRatio);
            p["$close"].Value = N(x.ClosePrice); p["$chg"].Value = N(x.ChangeRate);
            p["$turn"].Value = N(x.TurnoverRate); p["$buyer"].Value = x.BuyerName;
            p["$seller"].Value = x.SellerName;
            p["$bcode"].Value = x.BuyerCode; p["$scode"].Value = x.SellerCode;
            p["$disc"].Value = N(x.DiscountRatio);
            p["$fsr"].Value = N(x.FreeSharesRatio); p["$tsr"].Value = N(x.TotalSharesRatio);
            p["$c1"].Value = N(x.ChangeRate1D); p["$c5"].Value = N(x.ChangeRate5D);
            p["$c10"].Value = N(x.ChangeRate10D); p["$c20"].Value = N(x.ChangeRate20D);
            p["$f"].Value = T(x.FetchedAt);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return numbered.Count;
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
                 free_ratio, total_ratio, holder_count,
                 pre_free_shares, non_free_shares, before20_change, after20_change, fetched_at)
            VALUES ($code,$fd,$type,$name,$sh,$cap,$fr,$tr,$hc,$pfs,$nfs,$b20,$a20,$f);
            """;
        var p = Params(cmd, "$code", "$fd", "$type", "$name", "$sh", "$cap", "$fr", "$tr", "$hc",
                       "$pfs", "$nfs", "$b20", "$a20", "$f");
        foreach (var x in list)
        {
            p["$code"].Value = x.Code; p["$fd"].Value = D(x.FreeDate); p["$type"].Value = x.ShareType;
            p["$name"].Value = x.Name; p["$sh"].Value = N(x.LiftShares); p["$cap"].Value = N(x.LiftMarketCap);
            p["$fr"].Value = N(x.FreeRatio); p["$tr"].Value = N(x.TotalRatio);
            p["$hc"].Value = N(x.HolderCount);
            p["$pfs"].Value = N(x.PreFreeShares); p["$nfs"].Value = N(x.NonFreeShares);
            p["$b20"].Value = N(x.Before20Change); p["$a20"].Value = N(x.After20Change);
            p["$f"].Value = T(x.FetchedAt);
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
                 change_ratio, after_shares, after_ratio, start_date, average_price,
                 change_free_ratio, close_price, real_price, change_rate_quotes, fetched_at)
            VALUES ($code,$nd,$holder,$ed,$name,$dir,$cs,$cr,$as,$ar,$sd,$ap,
                    $cfr,$cp,$rp,$crq,$f);
            """;
        var p = Params(cmd, "$code", "$nd", "$holder", "$ed", "$name", "$dir", "$cs",
                       "$cr", "$as", "$ar", "$sd", "$ap",
                       "$cfr", "$cp", "$rp", "$crq", "$f");
        foreach (var x in list)
        {
            p["$code"].Value = x.Code; p["$nd"].Value = D(x.NoticeDate); p["$holder"].Value = x.HolderName;
            p["$ed"].Value = x.EndDate.HasValue ? D(x.EndDate.Value) : "";
            p["$name"].Value = x.Name; p["$dir"].Value = x.Direction;
            p["$cs"].Value = N(x.ChangeShares); p["$cr"].Value = N(x.ChangeRatio);
            p["$as"].Value = N(x.AfterShares); p["$ar"].Value = N(x.AfterRatio);
            p["$sd"].Value = x.StartDate.HasValue ? D(x.StartDate.Value) : (object)DBNull.Value;
            p["$ap"].Value = N(x.AveragePrice);
            p["$cfr"].Value = N(x.ChangeFreeRatio); p["$cp"].Value = N(x.ClosePrice);
            p["$rp"].Value = N(x.RealPrice); p["$crq"].Value = N(x.ChangeRateQuotes);
            p["$f"].Value = T(x.FetchedAt);
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
