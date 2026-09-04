using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 东财接口的联网冒烟测试（2026-09-03）。
///
/// <b>默认跳过</b>——它要真的打东财的服务器，跑全量测试时不该每次都发请求（东财对频率敏感，
/// 实测密集请求会被连续拒绝一小时以上）。改数据源相关代码后手工跑一次：
/// <code>
///   dotnet test --filter "FullyQualifiedName~EastMoneySmokeTests" -e EM_SMOKE=1
/// </code>
/// 需要设 EM_SMOKE=1 环境变量才会真跑，否则直接判过。
/// </summary>
public class EastMoneySmokeTests
{
    private readonly ITestOutputHelper _out;
    public EastMoneySmokeTests(ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("EM_SMOKE") == "1";

    /// <summary>间隔给到 1.5 秒——冒烟只发几个请求，宁可慢点也别把额度打掉影响后面真正取数。</summary>
    private static EastMoneyDataCenterClient NewClient() =>
        new(new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromMilliseconds(1500)));

    [Fact]
    public async Task 业绩预告_能抓到并正确解析()
    {
        if (!Enabled) { _out.WriteLine("跳过（设 EM_SMOKE=1 才联网跑）"); return; }

        var provider = new EastMoneyEarningsForecastProvider(NewClient());
        provider.OnStatus += s => _out.WriteLine("  " + s);

        // 只取最近一个月，避免把全量 400 页都拉下来
        var end = DateTime.Today;
        var start = end.AddMonths(-1);
        var list = new List<StockPlatform.Logic.Models.EarningsForecast>();
        await provider.FetchForecastsAsync(start, end, b => { list.AddRange(b); return b.Count; });

        _out.WriteLine($"业绩预告 {start:yyyy-MM-dd}~{end:yyyy-MM-dd}：{list.Count} 条");
        Assert.NotEmpty(list);

        var sample = list.First(x => !string.IsNullOrEmpty(x.ChangeReason));
        _out.WriteLine($"样例：{sample.Code} {sample.Name} 报告期={sample.ReportDate:yyyy-MM-dd} " +
                       $"公告日={sample.NoticeDate:yyyy-MM-dd} 类型={sample.PredictType}");
        _out.WriteLine($"  区间：{sample.AmountLower}~{sample.AmountUpper} 增幅：{sample.AmplitudeLower}~{sample.AmplitudeUpper}");
        _out.WriteLine($"  原因：{sample.ChangeReason[..Math.Min(120, sample.ChangeReason.Length)]}");

        // 解析正确性：这几个字段是后面做景气度判断的基础，空了整件事就没法做
        Assert.All(list, x =>
        {
            Assert.Equal(6, x.Code.Length);
            Assert.True(x.ReportDate > new DateTime(2000, 1, 1));
            Assert.True(x.NoticeDate >= start.AddDays(-1));
        });
        Assert.Contains(list, x => !string.IsNullOrEmpty(x.PredictType));
        Assert.Contains(list, x => !string.IsNullOrEmpty(x.ChangeReason));
    }

    [Fact]
    public async Task 板块成分股_取到的是完整官方名单()
    {
        if (!Enabled) { _out.WriteLine("跳过（设 EM_SMOKE=1 才联网跑）"); return; }

        // push2 限流敏感，冒烟只打 2~3 个请求
        var fetcher = new EastMoneyBoardFetcher(
            new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(2)));
        fetcher.OnStatus += s => _out.WriteLine("  " + s);

        // BK1138 液冷服务器：官方 170 只。这正是 F10 报表漏掉 4 只的那个板块
        // （美的集团、江苏神通、锦富技术、拓普集团），拿它当回归用例。
        var members = await fetcher.FetchMembersAsync("BK1138");
        _out.WriteLine($"液冷服务器 BK1138 取到 {members.Count} 只");

        Assert.Equal(170, members.Count);                       // 跟接口 total 对上（不对会在 fetcher 里抛）
        Assert.Equal(members.Count, members.Distinct().Count()); // 分页不能有跨页重复

        // F10 报表漏掉的那 4 只，必须都在
        foreach (var (code, name) in new[]
                 {
                     ("000333", "美的集团"), ("002438", "江苏神通"),
                     ("300128", "锦富技术"), ("601689", "拓普集团"),
                 })
            Assert.True(members.Contains(code), $"缺少 {code} {name}——F10 报表就是漏了这几只");

        _out.WriteLine("  ✅ F10 漏掉的 4 只全部到位");
    }

    [Fact]
    public async Task 分档资金流_五档都有值且能对上主力净额()
    {
        if (!Enabled) { _out.WriteLine("跳过（设 EM_SMOKE=1 才联网跑）"); return; }

        var provider = new EastMoneyMoneyFlowProvider(
            new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(2)));

        var list = await provider.FetchAsync("600875");   // 东方电气
        _out.WriteLine($"600875 取到 {list.Count} 天");
        Assert.NotEmpty(list);

        // 接口只给约 120 个交易日——这是已知限制，顺带盯着别哪天悄悄变了
        _out.WriteLine($"日期范围 {list[0].TradeDate:yyyy-MM-dd} ~ {list[^1].TradeDate:yyyy-MM-dd}");
        Assert.InRange(list.Count, 60, 200);

        var d = list[^1];
        _out.WriteLine($"最新一天 {d.TradeDate:yyyy-MM-dd}：主力={d.MainNet:N0} 超大单={d.SuperNet:N0} " +
                       $"大单={d.BigNet:N0} 中单={d.MidNet:N0} 小单={d.SmallNet:N0}");
        _out.WriteLine($"  占比：主力={d.MainRatio}% 超大={d.SuperRatio}% 大={d.BigRatio}% " +
                       $"中={d.MidRatio}% 小={d.SmallRatio}%");

        // 五档拆分是这份数据的全部意义，缺哪档都白搭
        Assert.NotNull(d.MainNet); Assert.NotNull(d.SuperNet); Assert.NotNull(d.BigNet);
        Assert.NotNull(d.MidNet); Assert.NotNull(d.SmallNet);

        // 字段顺序对不对，靠这个恒等式验：主力 = 超大单 + 大单。
        // 顺序错了（比如把中单当大单）数值照样非空，只有这个能查出来。
        Assert.Equal(d.SuperNet!.Value + d.BigNet!.Value, d.MainNet!.Value, 0);

        // 五档净额合计应该约等于 0（有买必有卖），偏差不超过成交额的一点点
        var sum = d.SuperNet!.Value + d.BigNet!.Value + d.MidNet!.Value + d.SmallNet!.Value;
        _out.WriteLine($"  五档合计={sum:N0}（应约等于0）");
        Assert.True(Math.Abs(sum) < Math.Abs(d.MainNet!.Value) + 1e7,
                    $"五档净额合计 {sum:N0} 明显不为零，字段映射可能错位");
    }

    [Fact]
    public async Task 市场事件四项_主键不撞车且关键字段齐全()
    {
        if (!Enabled) { _out.WriteLine("跳过（设 EM_SMOKE=1 才联网跑）"); return; }

        var db = Path.Combine(Path.GetTempPath(), $"emevt_{Guid.NewGuid():N}.sqlite");
        try
        {
            var repo = new SqliteMarketEventRepository(db);
            repo.EnsureSchema();
            var warnings = new List<string>();
            repo.OnWarning += w => { warnings.Add(w); _out.WriteLine("  " + w); };

            var provider = new EastMoneyMarketEventProvider(NewClient());
            var end = DateTime.Today;
            var start = end.AddDays(-5);      // 只取最近几天，够验证结构

            int bt = await provider.FetchBlockTradesAsync(start, end, b => repo.UpsertBlockTrades(b));
            int os = await provider.FetchOrgSurveysAsync(start, end, b => repo.UpsertOrgSurveys(b));
            int hc = await provider.FetchHolderChangesAsync(start, end, b => repo.UpsertHolderChanges(b));
            _out.WriteLine($"大宗交易 {bt} 行 / 机构调研 {os} 行 / 股东增减持 {hc} 行");

            Assert.True(bt > 0, "最近 5 天应该有大宗交易");

            // 主键撞车会静默丢数据（龙虎榜席位曾丢 7.5%），有告警就说明主键少了区分列
            Assert.True(warnings.Count == 0,
                $"出现主键重复告警，主键设计需要修正：\n{string.Join("\n", warnings)}");

            // 大宗交易的意义全在买卖双方营业部上，缺了这份数据就白抓
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    SELECT trade_date, code, name, deal_amount, premium_ratio, buyer_name, seller_name
                    FROM BlockTrade WHERE buyer_name <> '' ORDER BY deal_amount DESC LIMIT 1;
                    """;
                using var r = cmd.ExecuteReader();
                Assert.True(r.Read(), "大宗交易应该有带营业部的记录");
                _out.WriteLine($"最大一笔：{r.GetString(0)} {r.GetString(1)} {r.GetString(2)} " +
                               $"成交额={r.GetDouble(3):N0} 折溢价={r.GetDouble(4):F2}%");
                _out.WriteLine($"   买方：{r.GetString(5)}");
                _out.WriteLine($"   卖方：{r.GetString(6)}");
            }

            // 限售解禁：只验证最近两年 + 未来两年的切片（全量 30 多片跑一次要十几分钟，
            // 冒烟没必要；切片逻辑本身跟其余三项共用同一套骨架，验证几片就够）
            int thisYear = DateTime.Today.Year;
            int sl = await provider.FetchShareLiftsAsync(
                b => repo.UpsertShareLifts(b), null, default, thisYear - 1, thisYear + 2);
            _out.WriteLine($"限售解禁 {sl} 行");
            Assert.True(sl > 500, $"最近几年的限售解禁应有上千行，实际 {sl}");
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT MAX(free_date), COUNT(*) FROM ShareLift WHERE free_date > date('now');";
                using var r = cmd.ExecuteReader();
                r.Read();
                _out.WriteLine($"   未来解禁 {r.GetInt32(1)} 条，最远到 {r.GetString(0)}");
                Assert.True(r.GetInt32(1) > 0, "限售解禁必须含未来计划，否则它就没用了");
            }
            Assert.True(warnings.Count == 0, $"限售解禁出现主键重复：\n{string.Join("\n", warnings)}");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(db)) File.Delete(db); } catch { }
        }
    }

    [Fact]
    public async Task 板块归属映射_首页字段齐全()
    {
        if (!Enabled) { _out.WriteLine("跳过（设 EM_SMOKE=1 才联网跑）"); return; }

        // 全量 188 页要 6 分钟，冒烟只看第一页字段对不对
        var dc = NewClient();
        var (pages, rows) = await dc.QueryFirstPageAsync(
            "RPT_F10_CORETHEME_BOARDTYPE", sortColumns: "SECURITY_CODE", descending: false);

        _out.WriteLine($"板块归属：{pages} 页 × 500 ≈ {pages * 500} 行，首页 {rows.Count} 行");
        Assert.True(pages > 100, $"预期约 188 页，实际 {pages}——接口可能改了");
        Assert.NotEmpty(rows);

        var first = rows[0];
        foreach (var f in new[] { "SECURITY_CODE", "NEW_BOARD_CODE", "BOARD_NAME" })
            Assert.True(first.TryGetProperty(f, out _), $"缺字段 {f}");
        _out.WriteLine("首行：" + first.ToString()[..Math.Min(200, first.ToString().Length)]);
    }

    [Fact]
    public async Task 龙虎榜席位_流式回调写库_营业部字段齐全()
    {
        if (!Enabled) { _out.WriteLine("跳过（设 EM_SMOKE=1 才联网跑）"); return; }

        var db = Path.Combine(Path.GetTempPath(), $"emlhb_{Guid.NewGuid():N}.sqlite");
        try
        {
            var repo = new SqliteLhbSeatRepository(db);
            repo.EnsureSchema();

            var provider = new EastMoneyLhbSeatProvider(NewClient());
            provider.OnStatus += s => _out.WriteLine("  " + s);

            // 只取最近 10 天，验证流式回调这条路走得通即可（全量 264 万行要几小时）
            var end = DateTime.Today;
            int batches = 0;
            int total = await provider.FetchAsync(end.AddDays(-10), end,
                batch => { batches++; return repo.Upsert(batch); },
                new Progress<string>(s => _out.WriteLine("  " + s)));

            _out.WriteLine($"抓到 {total} 行，分 {batches} 批落库，库内 {repo.Count()} 行");
            Assert.True(total > 0, "最近 10 天应该有龙虎榜数据");

            // 营业部信息是这份数据的全部意义所在，缺了就白抓
            var sample = repo.QueryBySeat(
                repo.QueryByStock(
                    // 随便取库里一条记录的股票
                    GetAnyCode(db), GetAnyDate(db)).First().SeatCode, 5);
            Assert.NotEmpty(sample);
            var s0 = sample[0];
            _out.WriteLine($"样例席位：{s0.SeatName}（{s0.SeatCode}）{s0.TradeDate:yyyy-MM-dd} " +
                           $"{s0.Code} {s0.Name} {(s0.IsBuy ? "买入" : "卖出")} 净额={s0.Net} " +
                           $"3日胜率={s0.RiseProbability3Day}");
            Assert.False(string.IsNullOrEmpty(s0.SeatName), "营业部名称不能为空");
            Assert.False(string.IsNullOrEmpty(s0.SeatCode), "营业部代码不能为空——没它就没法追踪席位");

            // 买卖两边都要抓到
            var water = repo.GetLatestTradeDate();
            _out.WriteLine($"水位线：{water:yyyy-MM-dd}");
            Assert.NotNull(water);

            // 幂等：同一段重抓一次，行数不该变（主键去重）
            int before = repo.Count();
            await provider.FetchAsync(end.AddDays(-3), end, batch => repo.Upsert(batch));
            Assert.Equal(before, repo.Count());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(db)) File.Delete(db); } catch { }
        }
    }

    private static string GetAnyCode(string db)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT code FROM LhbSeat LIMIT 1";
        return (string)cmd.ExecuteScalar()!;
    }

    private static DateTime GetAnyDate(string db)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT trade_date FROM LhbSeat LIMIT 1";
        return DateTime.Parse((string)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task 抓一段预告写库再读出来_端到端()
    {
        if (!Enabled) { _out.WriteLine("跳过（设 EM_SMOKE=1 才联网跑）"); return; }

        var db = Path.Combine(Path.GetTempPath(), $"emsmoke_{Guid.NewGuid():N}.sqlite");
        try
        {
            var repo = new SqliteEarningsForecastRepository(db);
            repo.EnsureSchema();
            Assert.Null(repo.GetLatestForecastNoticeDate());   // 空库水位线为 null

            var provider = new EastMoneyEarningsForecastProvider(NewClient());
            var end = DateTime.Today;
            var list = new List<StockPlatform.Logic.Models.EarningsForecast>();
            int n = await provider.FetchForecastsAsync(end.AddDays(-20), end,
                b => { list.AddRange(b); return repo.UpsertForecasts(b); });
            _out.WriteLine($"写入 {n} 条，库内 {repo.CountForecasts()} 条");

            // 幂等：同一批再写一次，总数不变
            repo.UpsertForecasts(list);   // 同一批再写一次
            Assert.Equal(n, repo.CountForecasts());

            var water = repo.GetLatestForecastNoticeDate();
            _out.WriteLine($"水位线：{water:yyyy-MM-dd}");
            Assert.NotNull(water);

            // 按报告期查回来
            var rd = list.GroupBy(x => x.ReportDate).OrderByDescending(g => g.Count()).First().Key;
            var back = repo.QueryByReportDate(rd);
            _out.WriteLine($"报告期 {rd:yyyy-MM-dd} 查回 {back.Count} 条");
            Assert.NotEmpty(back);
            Assert.All(back, x => Assert.Equal(rd, x.ReportDate));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(db)) File.Delete(db); } catch { }
        }
    }

    [Fact]
    public async Task 个股行业题材_排序键唯一且字段解析正确()
    {
        if (!Enabled) { _out.WriteLine("跳过（设 EM_SMOKE=1 才联网跑）"); return; }

        // 这张表一只股票有十几行，排序键不唯一就会跨页重复+丢行（实测单列排前 3 页重了 15 行）。
        // 冒烟只抓前几页就够验证：重复一旦出现，第一页和第二页之间就会露出来。
        var dc = NewClient();
        var provider = new EastMoneyStockBoardMapProvider(dc);
        provider.OnStatus += s => _out.WriteLine("  " + s);

        var keys = new HashSet<(string, string)>();
        int rows = 0, ind = 0, theme = 0, withReason = 0, withLevel = 0;

        var seen = new List<(string Code, string Board)>();
        await foreach (var el in dc.QueryAsync("RPT_F10_CORETHEME_BOARDTYPE",
                           sortColumns: "SECURITY_CODE,BOARD_CODE", descending: false))
        {
            var code = el.GetProperty("SECURITY_CODE").GetString() ?? "";
            var board = el.TryGetProperty("NEW_BOARD_CODE", out var b) ? b.GetString() ?? "" : "";
            seen.Add((code, board));
            rows++;
            bool isInd = el.TryGetProperty("BOARD_TYPE", out var t) && t.GetString() == "行业";
            if (isInd) { ind++; if (el.TryGetProperty("BOARD_LEVEL", out var l) && l.ValueKind != System.Text.Json.JsonValueKind.Null) withLevel++; }
            else { theme++; if (el.TryGetProperty("SELECTED_BOARD_REASON", out var r) && !string.IsNullOrEmpty(r.GetString())) withReason++; }
            if (rows >= 1500) break;   // 3 页，够看出跨页重复了
        }

        foreach (var k in seen) keys.Add(k);
        _out.WriteLine($"抓 {rows} 行：行业 {ind}（带层级 {withLevel}）、题材 {theme}（带理由 {withReason}），去重后 {keys.Count}");

        Assert.True(rows > 0, "一行都没抓到——接口可能改版或被限流");
        // 核心断言：主键不能重复。重了就等于同时丢了同样多的行。
        Assert.Equal(rows, keys.Count);
        Assert.True(ind > 0, "一条行业都没有——BOARD_TYPE 的判断可能失效了");
        Assert.True(withLevel == ind, "有行业行没带 BOARD_LEVEL——三级分类取不到层级就没法做中性化");
        Assert.True(withReason > 0, "题材全都没有入选理由——SELECTED_BOARD_REASON 字段名可能变了");
    }
}
