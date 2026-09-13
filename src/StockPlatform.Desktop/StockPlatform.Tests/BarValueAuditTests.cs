using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// K线**值体检**六条判据（2026-09-09，设计见 doc/bar-value-audit-design.md）。
///
/// 为什么每条都要卡边界：这套判据存在的意义就是抓"行在但值错"，而它自己判错也是静默的——
/// 阈值写松了满屏误报（然后没人再看体检结果），写紧了漏掉真问题（然后回测拿着错数据跑）。
/// 两个真实事故的复现用例都在这里：turnover 整列 NULL（V2）、盘中固化的半天快照（V1）。
/// </summary>
public class BarValueAuditTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteBarRepository _bars;
    private readonly SqliteBarValueAuditor _auditor;

    private static readonly DateTime Day = new(2026, 9, 1);
    private static readonly DateTime Cutoff = new(2026, 9, 7);

    public BarValueAuditTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"valaudit_{Guid.NewGuid():N}.sqlite");
        _bars = new SqliteBarRepository(_dbPath);
        _bars.EnsureSchema();
        _auditor = new SqliteBarValueAuditor(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    /// <summary>一条各方面都正常的行：ratio = 10000/(100×1.0)/… 这里凑成 100（手口径）。</summary>
    private void Ok(string code, string gran = Granularity.Day, DateTime? day = null,
                    double close = 10, double volume = 100, double? amount = null,
                    DateTime? fetchedAt = null)
    {
        var d = day ?? Day;
        _bars.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = code, Granularity = gran, PeriodStart = d,
            Open = close, Close = close, High = close, Low = close,
            Volume = volume, Amount = amount ?? volume * close * 100, Turnover = 1.5,
            FetchedAt = fetchedAt ?? d.AddHours(20),
        }]);
    }

    /// <summary>直接写一行（绕过写入路径，用来造 NULL 这类"批量导入才会有"的脏数据）。</summary>
    private void Raw(string code, string gran, DateTime day, string columns, string values)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT OR REPLACE INTO Bar (code, granularity, period_start, {columns}) " +
                          $"VALUES ($c, $g, $p, {values});";
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$g", gran);
        cmd.Parameters.AddWithValue("$p", day.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>单独改一行的 turnover（<see cref="Ok"/> 固定写 1.5，测容差要能拨这个值）。</summary>
    private void SetTurnover(string code, string gran, double turnover)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Bar SET turnover = $t WHERE code = $c AND granularity = $g;";
        cmd.Parameters.AddWithValue("$t", turnover);
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$g", gran);
        cmd.ExecuteNonQuery();
    }

    private List<SqliteBarValueAuditor.RowIssue> Issues(string kind) =>
        _auditor.RowIssues(Cutoff).Where(i => i.Kind == kind).ToList();

    // ─────────────────── V1 盘中固化 ───────────────────

    [Fact]
    public void V1_盘中抓的行报出来_盘后的不报()
    {
        Ok("600000", fetchedAt: Day.AddHours(9).AddMinutes(33));   // 09:33 抓 → 半天快照
        Ok("600001", fetchedAt: Day.AddHours(20));                 // 当晚 20:00 抓 → 正常
        Ok("600002", fetchedAt: Day.AddDays(3));                   // 隔几天才补 → 正常

        var hits = Issues(AuditFindingKind.Intraday);
        Assert.Single(hits);
        Assert.Equal("600000", hits[0].Code);
    }

    [Theory]
    [InlineData(15, 59, true)]    // 收盘了但没到判定时刻——宁可多等，不可提前认定
    [InlineData(16, 0, false)]
    [InlineData(9, 25, true)]
    public void V1_判据卡在16点(int hour, int minute, bool shouldReport)
    {
        Ok("600000", fetchedAt: Day.AddHours(hour).AddMinutes(minute));
        Assert.Equal(shouldReport, Issues(AuditFindingKind.Intraday).Count == 1);
    }

    // ─────────────────── V2 关键列 NULL ───────────────────

    [Fact]
    public void V2_turnover整列NULL被报出来()
    {
        // 复现 2026-09-06 东财导入那次：价格量额都有，就是没有换手率
        Raw("600000", Granularity.DayRaw, Day,
            "open, close, high, low, volume, amount, turnover, fetched_at",
            "10, 10, 10, 10, 100, 100000, NULL, '2026-09-06 15:23:42'");
        Ok("600001", Granularity.DayRaw);

        var hits = Issues(AuditFindingKind.NullValue);
        Assert.Single(hits);
        Assert.Equal("600000", hits[0].Code);
        Assert.Equal(Granularity.DayRaw, hits[0].Granularity);
    }

    [Fact]
    public void V2_价格为NULL也报()
    {
        Raw("600000", Granularity.Day, Day,
            "open, close, high, low, volume, amount, turnover, fetched_at",
            "NULL, 10, 10, 10, 100, 100000, 1.5, '2026-09-01 20:00:00'");
        Assert.Single(Issues(AuditFindingKind.NullValue));
    }

    // ─────────────────── V4 OHLC 自洽 ───────────────────

    [Theory]
    [InlineData(10, 10, 9.5, 9.0, true)]     // high 装不下 open/close
    [InlineData(10, 10, 10.5, 10.2, true)]   // low 比 open/close 还高
    [InlineData(10, 10, 10.5, 9.5, false)]   // 正常
    // "价格 ≤ 0" 不在这里测：那一条只对不复权判（前复权减法式的负价是已知失真），
    // 见下面「前复权的负价不算OHLC不自洽」和「不复权的非正价格照样报」两个用例
    public void V4_OHLC不自洽才报(double open, double close, double high, double low, bool shouldReport)
    {
        _bars.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = "600000", Granularity = Granularity.Day, PeriodStart = Day,
            Open = open, Close = close, High = high, Low = low,
            Volume = 100, Amount = 100 * close * 100, Turnover = 1.5,
            FetchedAt = Day.AddHours(20),
        }]);
        Assert.Equal(shouldReport, Issues(AuditFindingKind.Ohlc).Count == 1);
    }

    // ─────────────────── V6 量额比率 ───────────────────

    /// <summary>
    /// volume 的单位是**手**，所以 <c>amount / (volume × close)</c> 只有 ≈100 一个正常区间。
    ///
    /// ⚠ 2026-09-10 收紧：**≈1 不再放过**。原来它是放行的，因为当时科创板 688/689 的 volume
    /// 确实按股存（腾讯给股、fetcher 原样入库）。那是 bug 不是第二种合法口径——它让含科创板的
    /// 板块指数成交量常年虚高 100 倍。现在解析层归一化（<c>BarVolumeUnit</c>）、历史由
    /// 【统一成交量单位】修，这里就该报出来，否则以后再掺进股口径照样没人发现。
    /// </summary>
    [Theory]
    [InlineData(100.0, true)]     // 手口径：amount = volume × close × 100
    [InlineData(1.0, false)]      // 按股存的——单位错了，要报
    [InlineData(33.0, false)]     // 603999 那种：amount 少了约 2/3
    [InlineData(1000.0, false)]   // 量额差一个数量级
    [InlineData(5.0, false)]      // 1991-92 老数据那种：成交量少记 5 倍（比值 ≈500 的反面）
    [InlineData(10.0, false)]     // 差 10 倍
    // ── 2026-09-12 放宽到 [50,250] 之后，这一档不再报 ──
    // 成因是**当日均价偏离收盘**，不是数据错。生产实测 592 行命中里 318 行是这种，
    // 集中在北交所 920xxx 和 1990 年代成交稀疏的老股。见 RatioLow/RatioHigh 的注释。
    [InlineData(60.0, true)]      // 均价比收盘低 40%
    [InlineData(200.0, true)]     // 均价比收盘高一倍
    public void V6_只有手口径算正常(double multiplier, bool isNormal)
    {
        double close = 10, volume = 100;
        Ok("600000", Granularity.DayRaw, close: close, volume: volume, amount: volume * close * multiplier);
        Assert.Equal(!isNormal, Issues(AuditFindingKind.Ratio).Count == 1);
    }

    [Fact]
    public void V6_停牌那种零成交行跳过不报()
    {
        Ok("600000", Granularity.DayRaw, volume: 0, amount: 0);
        Assert.Empty(Issues(AuditFindingKind.Ratio));
    }

    // ─────────────────── V3 跨口径一致性 ───────────────────

    [Fact]
    public void V3_三列全等不报()
    {
        foreach (var g in new[] { Granularity.Day, Granularity.DayHfq, Granularity.DayRaw })
            Ok("600000", g);
        Assert.Empty(_auditor.CrossGranularityMismatch(Cutoff));
    }

    [Fact]
    public void V3_不复权的量对不上前复权就报()
    {
        Ok("600000", Granularity.Day, volume: 20953, amount: 20953 * 10 * 100);
        Ok("600000", Granularity.DayHfq, volume: 20953, amount: 20953 * 10 * 100);
        // 复现 2026-09-01 那批盘中行：量只有正常值的一小部分
        Ok("600000", Granularity.DayRaw, volume: 23, amount: 23 * 10 * 100);

        var hits = _auditor.CrossGranularityMismatch(Cutoff);
        Assert.Single(hits);
        Assert.Equal(Granularity.DayRaw, hits[0].Granularity);
        Assert.Equal(AuditFindingKind.Inconsistent, hits[0].Kind);
    }

    [Fact]
    public void V3_末位浮点噪声不算不一致()
    {
        Ok("600000", Granularity.Day, amount: 495638100);
        Ok("600000", Granularity.DayRaw, amount: 495638100.00000006);   // 1e4 换算的末位噪声
        Assert.Empty(_auditor.CrossGranularityMismatch(Cutoff));
    }

    /// <summary>
    /// 腾讯成交额只有 100 元刻度，两个端点各自四舍五入，偶尔落在相邻的两格上——
    /// 2026-09-11 查到 31 只北交所票的 49 行全部**正好差 100 元**（920010 2021-06-02：
    /// 434,000 vs 434,100），东财本地那份元级数据证明余数恒为 49，两个值都只是同一个真值的
    /// 舍入结果。判据要求的精度超过了源的分辨率，那样的段修不掉也报不完。
    /// </summary>
    [Fact]
    public void V3_成交额差一个刻度不算不一致()
    {
        Ok("920010", Granularity.Day, amount: 434_000);
        Ok("920010", Granularity.DayRaw, amount: 434_100);
        Assert.Empty(_auditor.CrossGranularityMismatch(Cutoff));
    }

    [Fact]
    public void V3_一格上再带末位浮点噪声也要放过()
    {
        // 入库的 amount 是「万元 × 1e4」算出来的，自带末位噪声：920819 的 day 存成
        // 1010699.9999999999，跟 day_raw 的 1010800 差 100.00000000011——
        // 判据写 > 100 的话，正好差一格的行会险些全部漏网（2026-09-12 实测 49 行里漏了 3 行）
        Ok("920819", Granularity.Day, amount: 1_010_699.9999999999);
        Ok("920819", Granularity.DayRaw, amount: 1_010_800);
        Assert.Empty(_auditor.CrossGranularityMismatch(Cutoff));
    }

    [Fact]
    public void V3_成交额差两个刻度照样报()
    {
        // 只放过**一格**。差 200 元就不是舍入能解释的了
        Ok("920010", Granularity.Day, amount: 434_000);
        Ok("920010", Granularity.DayRaw, amount: 434_200);
        Assert.Single(_auditor.CrossGranularityMismatch(Cutoff));
    }

    [Fact]
    public void V3_放宽刻度之后量额差100倍照样报()
    {
        // 放过的是绝对 100 元，不是比例——单位错（手/股）那种相对误差 99，一格都藏不住
        Ok("688122", Granularity.Day, volume: 331_101.6, amount: 1_358_249_400);
        Ok("688122", Granularity.DayRaw, volume: 33_110_160, amount: 1_358_249_400);
        Assert.Single(_auditor.CrossGranularityMismatch(Cutoff));
    }

    // ─────────────────── V5 day_adj 与 day_raw ───────────────────

    [Fact]
    public void V5_回测序列落后于不复权_报行集差()
    {
        Ok("600000", Granularity.DayRaw, Day);
        Ok("600000", Granularity.DayRaw, Day.AddDays(1));
        Ok("600000", Granularity.DayAdj, Day);      // 少算了第二天

        var d = _auditor.AdjVsRawDrift(Cutoff);
        Assert.Equal(1, d.RawOnlyRows);
        Assert.Equal(0, d.AdjOnlyRows);
        Assert.Equal(0, d.ValueMismatchRows);
    }

    [Fact]
    public void V5_两边对齐且量额相同时不报()
    {
        Ok("600000", Granularity.DayRaw, Day);
        Ok("600000", Granularity.DayAdj, Day);

        var d = _auditor.AdjVsRawDrift(Cutoff);
        Assert.Equal(0, d.RawOnlyRows);
        Assert.Equal(0, d.AdjOnlyRows);
        Assert.Equal(0, d.ValueMismatchRows);
    }

    [Fact]
    public void V5_量额被改过_报值不一致()
    {
        Ok("600000", Granularity.DayRaw, Day, volume: 100);
        Ok("600000", Granularity.DayAdj, Day, volume: 23);   // 继承自旧的盘中值
        Assert.Equal(1, _auditor.AdjVsRawDrift(Cutoff).ValueMismatchRows);
    }

    // ─────────────────── 范围与上限 ───────────────────

    [Fact]
    public void cutoff之后的日子不查()
    {
        Ok("600000", day: new DateTime(2026, 9, 8), fetchedAt: new DateTime(2026, 9, 8, 9, 30, 0));
        Assert.Empty(Issues(AuditFindingKind.Intraday));   // 09-08 > cutoff 09-07
    }

    [Fact]
    public void since可以限下界()
    {
        Ok("600000", day: new DateTime(2020, 1, 2), fetchedAt: new DateTime(2020, 1, 2, 9, 30, 0));
        Ok("600001", day: Day, fetchedAt: Day.AddHours(9).AddMinutes(30));

        var all = _auditor.RowIssues(Cutoff).Where(i => i.Kind == AuditFindingKind.Intraday).ToList();
        var recent = _auditor.RowIssues(Cutoff, since: new DateTime(2026, 1, 1))
            .Where(i => i.Kind == AuditFindingKind.Intraday).ToList();
        Assert.Equal(2, all.Count);
        Assert.Single(recent);
        Assert.Equal("600001", recent[0].Code);
    }

    // ─────────────────── 段级聚合（体检用的那条路）───────────────────

    /// <summary>
    /// 体检走段级、不走行级——早先是"行级 + 每类 5 万条上限"，那个上限把**截断伪装成精确数字**
    /// （日志显示"50000 行"看不出后面还有多少），2026-09-09 按用户的质疑去掉了：SQL 一次扫描、
    /// C# 流式聚合成段，内存里只留段，行数只累加计数。
    /// </summary>
    [Fact]
    public void 同一只票同一口径的多天_聚合成一段并带上行数()
    {
        foreach (var d in new[] { Day, Day.AddDays(1), Day.AddDays(2) })
            Ok("600000", day: d, fetchedAt: d.AddHours(9).AddMinutes(30));

        var segs = _auditor.RowIssueSegments(Cutoff)
            .Where(s => s.Kind == AuditFindingKind.Intraday).ToList();

        Assert.Single(segs);
        Assert.Equal("600000", segs[0].Code);
        Assert.Equal(Day, segs[0].From);
        Assert.Equal(Day.AddDays(2), segs[0].To);
        Assert.Equal(3, segs[0].Days);          // 段里命中的行数，不是区间长度
    }

    [Fact]
    public void 不同口径分成不同段()
    {
        foreach (var g in new[] { Granularity.Day, Granularity.DayRaw })
            Ok("600000", g, fetchedAt: Day.AddHours(9));

        var segs = _auditor.RowIssueSegments(Cutoff)
            .Where(s => s.Kind == AuditFindingKind.Intraday).ToList();
        Assert.Equal(2, segs.Count);
        Assert.Equal([Granularity.Day, Granularity.DayRaw],
            segs.Select(s => s.Granularity).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void V3也能聚合成段()
    {
        foreach (var d in new[] { Day, Day.AddDays(1) })
        {
            Ok("600000", Granularity.Day, d, volume: 20953, amount: 20953 * 10 * 100);
            Ok("600000", Granularity.DayRaw, d, volume: 23, amount: 23 * 10 * 100);
        }
        var segs = _auditor.CrossGranularitySegments(Cutoff);
        Assert.Single(segs);
        Assert.Equal(2, segs[0].Days);
        Assert.Equal(AuditFindingKind.Inconsistent, segs[0].Kind);
    }

    // ─────────────────── 2026-09-09 生产实测暴露的两个误报 ───────────────────

    /// <summary>
    /// 前复权是**减法式**的（原价 − 累计分红），高分红股票往前推十几年会被减到零以下
    /// （万科 1997 年的前复权价是 −8.17）。那是已知的口径失真、不是脏数据——
    /// 拿 "价格≤0" 去报它，第一轮生产体检就报出 5 万行（244 只票，判据上限被打满），
    /// 真问题全被淹掉。所以这一条只对不复权用。
    /// </summary>
    [Fact]
    public void 前复权的负价不算OHLC不自洽()
    {
        _bars.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = "000002", Granularity = Granularity.Day, PeriodStart = Day,
            Open = -8.17, Close = -8.17, High = -8.11, Low = -8.24,   // 自洽，只是为负
            Volume = 100, Amount = 100 * 8.17 * 100, Turnover = 1,
            FetchedAt = Day.AddHours(20),
        }]);
        Assert.Empty(Issues(AuditFindingKind.Ohlc));
    }

    [Fact]
    public void 不复权的非正价格照样报()
    {
        _bars.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = "000002", Granularity = Granularity.DayRaw, PeriodStart = Day,
            Open = 0, Close = 0, High = 0, Low = 0,
            Volume = 100, Amount = 100, Turnover = 1,
            FetchedAt = Day.AddHours(20),
        }]);
        Assert.Single(Issues(AuditFindingKind.Ohlc));
    }

    /// <summary>
    /// 指数的 <c>close</c> 是**点位**、不是价格，跟成交额压根没有那个倍数关系——
    /// 上证指数首日 1990-12-19 就被报出来了，一轮体检打满 5 万行上限。判据是"代码 6 位纯数字"：
    /// 指数/ETF/板块指数在本库里都带前缀，个股不带。
    /// </summary>
    [Theory]
    [InlineData("sh000001", false)]   // 指数：不判
    [InlineData("sz399001", false)]   // 深证成指
    [InlineData("sh510300", false)]   // ETF（带前缀，量额口径没验过，先一起排除）
    [InlineData("BK0594", false)]     // 板块指数（本地合成）
    [InlineData("600000", true)]      // 个股：判
    public void 量额比率只对个股判(string code, bool shouldReport)
    {
        // 口径必须是不复权——复权口径一律不判，见「V6_只对不复权判」
        Ok(code, Granularity.DayRaw, close: 10, volume: 100, amount: 100 * 10 * 33);
        Assert.Equal(shouldReport, Issues(AuditFindingKind.Ratio).Count == 1);
    }

    // ─────────────── 2026-09-09 生产实测暴露的判据过严 ───────────────

    /// <summary>
    /// 换手率是数据源**算出来的派生值**（成交量 ÷ 流通股本），而各口径是不同时刻抓的：
    /// 期间股本一变（解禁/增发），同一天的换手率就被重算成另一个数。实测 000153 的 08-27
    /// volume/amount 完全一致、turnover 7.39 vs 7.36（相隔 5 天抓的）——拿 1e-6 判会报出
    /// 一堆重抓也修不掉的段（首批 500 段里 69 段是这么"还在"的）。
    /// </summary>
    [Fact]
    public void V3_换手率的小幅差异不算不一致()
    {
        Ok("000153", Granularity.Day, volume: 339197, amount: 219073800);
        Ok("000153", Granularity.DayRaw, volume: 339197, amount: 219073800);
        SetTurnover("000153", Granularity.Day, 7.39);
        SetTurnover("000153", Granularity.DayRaw, 7.36);
        Assert.Empty(_auditor.CrossGranularityMismatch(Cutoff));
    }

    [Fact]
    public void V3_换手率差太多还是要报()
    {
        Ok("000153", Granularity.Day, volume: 339197, amount: 219073800);
        Ok("000153", Granularity.DayRaw, volume: 339197, amount: 219073800);
        SetTurnover("000153", Granularity.Day, 7.39);
        SetTurnover("000153", Granularity.DayRaw, 0.26);   // 盘中固化那种量级的差
        Assert.Single(_auditor.CrossGranularityMismatch(Cutoff));
    }

    [Fact]
    public void V3_量额只要差一点就报_不受换手率容差影响()
    {
        Ok("000153", Granularity.Day, volume: 339197, amount: 219073800);
        Ok("000153", Granularity.DayRaw, volume: 339198, amount: 219073800);   // volume 差 1 手
        Assert.Single(_auditor.CrossGranularityMismatch(Cutoff));
    }

    /// <summary>
    /// V6 的比值里只有 close 随复权变，而 amount 永远是真实成交额——拿前复权的复权价去除
    /// 真实成交额，比值必然对不上。第一次只排除了指数/板块，仍报出 2621 万行
    /// （最多的 600601 一只票 8420 行＝它的全部历史）。所以只对不复权判。
    /// </summary>
    [Theory]
    [InlineData(Granularity.DayRaw, true)]
    [InlineData(Granularity.Day, false)]
    [InlineData(Granularity.DayHfq, false)]
    [InlineData(Granularity.DayAdj, false)]
    public void V6_只对不复权判(string gran, bool shouldReport)
    {
        Ok("600601", gran, close: 10, volume: 100, amount: 100 * 10 * 33);   // ratio≈33
        Assert.Equal(shouldReport, Issues(AuditFindingKind.Ratio).Count == 1);
    }
}
