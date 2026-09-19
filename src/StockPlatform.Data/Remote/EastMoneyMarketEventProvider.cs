using System.Globalization;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 四类"市场事件"数据（东财 datacenter）：大宗交易、机构调研、限售解禁、股东增减持。
/// 本地此前全都没有，也都没有回退源。
///
/// 放一个 provider 里是因为它们同构——都是 datacenter 报表，差别只在 reportName、
/// 日期字段和字段映射。分成四个类只会把同一套分片/翻页/解析逻辑抄四遍。
///
/// 切片粒度按数据量给：大宗交易 68 万行按月切，其余几万到几十万行按年切即可
/// （切太细会产生大量小请求，反而更容易撞限流）。
/// </summary>
public class EastMoneyMarketEventProvider : IBlockTradeDayFetcher
{
    private readonly EastMoneyDataCenterClient _dc;

    public event Action<string>? OnStatus;

    public EastMoneyMarketEventProvider(EastMoneyDataCenterClient dc)
    {
        _dc = dc;
        _dc.OnStatus += s => OnStatus?.Invoke(s);
    }

    /// <summary>
    /// 大宗交易：抓**某一个交易日**的全市场（2026-09-17 从按月切片改成按日）。
    ///
    /// ════ 为什么改按日 ════
    /// 原来按月切片、靠 <c>UPSERT</c> 去重，前提是主键能认出"同一笔"。可主键第三列
    /// <c>DAILY_RANK</c> 是**不稳定**的——同一笔交易在不同时刻抓，东财给的值不一样
    /// （实测 300750 的 2026-09-15 两笔：09-15 抓到 rank 22/27、09-16 抓到 rank 1/2），
    /// 于是每次重抓都 INSERT 一份副本而不是覆盖。叠加"增量往前回看 30 天补滞后字段"，
    /// 最近一个月的行数普遍虚高一倍（2026-09-08：20.3 亿 → 真值 9.8 亿）。
    /// 详见 doc/block-trade-task-design.md。
    ///
    /// 按日抓 + 整日替换才是对的形状：一天 100~600 笔、<c>pageSize=500</c> 只需 1~2 页，
    /// **永远浅分页**，而且每天都能拿接口自报的 <c>count</c> 校验收全没有。
    ///
    /// ⚠ <see cref="BlockTradeDay.ReportedCount"/> 跟实收行数不一致时**不要落库**——
    /// 那说明某页被限流截断了，拿残缺的一天去覆盖完整的一天，事后完全看不出来。
    /// </summary>
    public async Task<BlockTradeDay> FetchBlockTradesOfDayAsync(
        DateTime day, CancellationToken ct = default)
    {
        var filter = EastMoneyQuerySlicer.DateFilter("TRADE_DATE", day.Date, day.Date);
        int reported = -1;
        var rows = new List<BlockTrade>();
        // 排序键沿用 TRADE_DATE,SECURITY_CODE,DAILY_RANK：单日只有 1~2 页，而且 count 校验
        // 兜在后面。DAILY_RANK 跨抓取不稳定，但同一次查询内部是稳的，用来定序没问题——
        // 2021-12-15（558 行 / 2 页）实测与接口逐行一致。
        await foreach (var el in _dc.QueryAsync(
            "RPT_DATA_BLOCKTRADE", filter, "TRADE_DATE,SECURITY_CODE,DAILY_RANK",
            onTotalCount: n => reported = n, ct: ct))
        {
            var row = ParseBlockTrade(el);
            if (row != null) rows.Add(row);
        }
        // 该日一笔都没有时接口连 result 都不给（走 yield break），拿不到 count——记 0，
        // 跟"抓到 0 行"一致，调用方才能把这天认成"数据源就是没有"而不是"抓漏了"。
        return new BlockTradeDay(day.Date, rows, reported < 0 ? 0 : reported);
    }

    /// <summary>机构调研。约 28 万行，按年切片。</summary>
    public async Task<int> FetchOrgSurveysAsync(
        DateTime start, DateTime end, Func<List<OrgSurvey>, int> onBatch,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => await RunSlicedAsync(
            "RPT_ORG_SURVEYNEW", "NOTICE_DATE", EastMoneyQuerySlicer.ByYear(start, end),
            "机构调研", el =>
            {
                var code = Str(el, "SECURITY_CODE");
                var d = Date(el, "NOTICE_DATE");
                if (code.Length == 0 || d == null) return null;
                return new OrgSurvey
                {
                    Code = code,
                    Name = Str(el, "SECURITY_NAME_ABBR"),
                    NoticeDate = d.Value,
                    ReceiveStartDate = Date(el, "RECEIVE_START_DATE"),
                    ReceiveEndDate = Date(el, "RECEIVE_END_DATE"),
                    // ⚠ 是 RECEIVE_OBJECT 不是 ORG_NAME——后者是**被调研的上市公司全名**，
                    // 同一次调研的所有行都一样，拿它做主键会 95% 撞车（实测 10280 行只剩 506 行）。
                    // RECEIVE_OBJECT 才是参与调研的机构（"同犇投资""华能贵诚"…）。
                    OrgName = Str(el, "RECEIVE_OBJECT"),
                    ObjectCode = Str(el, "OBJECT_CODE"),
                    OrgType = Str(el, "ORG_TYPE"),
                    ReceiveWay = Str(el, "RECEIVE_WAY_EXPLAIN"),
                    ReceivePlace = Str(el, "RECEIVE_PLACE"),
                    Investigators = Str(el, "INVESTIGATORS"),
                    Receptionist = Str(el, "RECEPTIONIST"),
                    SurveyNo = (int)(Num(el, "NUM") ?? 0),
                    OrgTotal = (int?)Num(el, "SUM"),
                    FetchedAt = DateTime.Now,
                };
            }, onBatch, progress, ct,
            // 主键是 (code, notice_date, org_name, receive_start_date, survey_no)：同一天同一股
            // 常有十几家机构、同一家又可能有多次调研，后三列都得进排序键，否则分页边界上顺序不稳
            tieBreaker: "RECEIVE_OBJECT,RECEIVE_START_DATE,NUM");

    /// <summary>
    /// 限售解禁。⚠ <b>含未来的解禁计划</b>（样例里有 2035 年的），所以不做日期切片、
    /// 每次全量重取——按"抓到今天为止"做增量会永远漏掉未来那部分，而未来正是它的价值所在。
    /// 好在只有 3 万行、63 页。
    /// </summary>
    /// <param name="fromYear">起始年份（含）。默认 2010。</param>
    /// <param name="toYear">
    /// 截止年份（含）。默认"今年+15"——**必须给未来留足余量**，这张表的价值恰恰在未来那部分
    /// （实测样例里有 2035 年的解禁计划）。测试可以传小范围避免跑满 30 多片。
    /// </param>
    public async Task<int> FetchShareLiftsAsync(
        Func<List<ShareLift>, int> onBatch,
        IProgress<string>? progress = null, CancellationToken ct = default,
        int? fromYear = null, int? toYear = null)
    {
        // 按年切片，范围覆盖"历史 + 未来的解禁计划"。
        //
        // 为什么切片：这张表原本是唯一不切片的（全量 3 万行、63 页一路翻到底），结果撞上了
        // 东财的深分页问题——实测有 0.1~0.2% 的跨页重复（同一条记录在两页里各出现一次）。
        // 前两页查不出来，重复都发生在几十页之后。按年切片后每片只有两三页，永远是浅分页。
        //
        // 上界取"今年+15年"而不是今天：这张表的价值恰恰在未来那部分（实测样例里有 2035 年的
        // 解禁计划），按"抓到今天为止"会把它全漏掉。
        var start = new DateTime(fromYear ?? 2010, 1, 1);
        var end = new DateTime(toYear ?? (DateTime.Today.Year + 15), 12, 31);
        var slices = EastMoneyQuerySlicer.ByYear(start, end);

        progress?.Report($"限售解禁：全量重取 {start:yyyy}~{end:yyyy}（含未来解禁计划，" +
                         $"不能按日期做增量），{slices.Count} 片...");

        int total = 0;
        for (int i = 0; i < slices.Count; i++)
        {
            var s = slices[i];
            ct.ThrowIfCancellationRequested();
            var filter = EastMoneyQuerySlicer.DateFilter("FREE_DATE", s.Start, s.End);
            var rows = new List<ShareLift>();
            // 排序键要排到主键末列：主键第三列是 share_type（同一天同一股可以有多类限售股份同时
            // 解禁），不带它的话这些行在分页边界上顺序不稳——跨页重复 + 遗漏，遗漏那半没有告警。
            await foreach (var el in _dc.QueryAsync(
                "RPT_LIFT_STAGE", filter, "FREE_DATE,SECURITY_CODE,FREE_SHARES_TYPE", ct: ct))
            {
                var code = Str(el, "SECURITY_CODE");
                var d = Date(el, "FREE_DATE");
                if (code.Length == 0 || d == null) continue;
                rows.Add(new ShareLift
                {
                    Code = code,
                    Name = Str(el, "SECURITY_NAME_ABBR"),
                    FreeDate = d.Value,
                    ShareType = Str(el, "FREE_SHARES_TYPE"),
                    LiftShares = Num(el, "CURRENT_FREE_SHARES"),
                    LiftMarketCap = Num(el, "LIFT_MARKET_CAP"),
                    FreeRatio = Num(el, "FREE_RATIO"),
                    TotalRatio = Num(el, "TOTALSHARES_RATIO") ?? Num(el, "TOTAL_RATIO"),
                    HolderCount = (int?)Num(el, "BATCH_HOLDER_NUM"),
                    // FREE_SHARES 是解禁**前**的已流通股数（FreeRatio 的分母），别跟
                    // CURRENT_FREE_SHARES（本次解禁股数，上面的 LiftShares）弄混。
                    PreFreeShares = Num(el, "FREE_SHARES"),
                    NonFreeShares = Num(el, "NON_FREE_SHARES"),
                    Before20Change = Num(el, "B20_ADJCHRATE"),
                    After20Change = Num(el, "A20_ADJCHRATE"),
                    FetchedAt = DateTime.Now,
                });
            }
            int written = rows.Count > 0 ? onBatch(rows) : 0;
            total += written;
            if (written > 0 || i % 10 == 0)
                progress?.Report($"限售解禁 {s.Name}（{i + 1}/{slices.Count}）：{written} 行，累计 {total}");
        }
        progress?.Report($"限售解禁完成：{total} 行");
        return total;
    }

    /// <summary>股东增减持。约 15 万行，按年切片。</summary>
    public async Task<int> FetchHolderChangesAsync(
        DateTime start, DateTime end, Func<List<HolderChange>, int> onBatch,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => await RunSlicedAsync(
            "RPT_SHARE_HOLDER_INCREASE", "NOTICE_DATE", EastMoneyQuerySlicer.ByYear(start, end),
            "股东增减持", ParseHolderChange, onBatch, progress, ct,
            // 主键是 (code, notice_date, holder_name, end_date)：同一天同一股会有多个股东，
            // 同一股东也可能有多个变动区间，两列都要进排序键
            tieBreaker: "HOLDER_NAME,END_DATE");

    /// <summary>
    /// 大宗交易一行的解析。提成命名方法（而不是留在 lambda 里）是为了能被测试直接调用——
    /// 这张表的字段有几个单位/基准不一致的坑（见 <see cref="BlockTrade.DiscountRatio"/>），
    /// 靠实抓样本的单元测试钉住比靠注释可靠。
    /// </summary>
    private static BlockTrade? ParseBlockTrade(JsonElement el)
    {
        var code = Str(el, "SECURITY_CODE");
        var d = Date(el, "TRADE_DATE");
        if (code.Length == 0 || d == null) return null;
        return new BlockTrade
        {
            Code = code,
            Name = Str(el, "SECURITY_NAME_ABBR"),
            TradeDate = d.Value,
            // ⚠ 不读东财的 DAILY_RANK——它跨抓取不稳定（见 FetchBlockTradesOfDayAsync 的注释），
            // 拿它当主键第三列正是重复行的根因。序号由落库时按返回顺序自赋，见
            // SqliteMarketEventRepository.ReplaceBlockTradesForDay。
            DealPrice = Num(el, "DEAL_PRICE"),
            DealVolume = Num(el, "DEAL_VOLUME"),
            DealAmount = Num(el, "DEAL_AMT"),
            PremiumRatio = Num(el, "PREMIUM_RATIO"),
            ClosePrice = Num(el, "CLOSE_PRICE"),
            ChangeRate = Num(el, "CHANGE_RATE"),
            TurnoverRate = Num(el, "TURNOVER_RATE"),
            BuyerName = Str(el, "BUYER_NAME"),
            SellerName = Str(el, "SELLER_NAME"),
            BuyerCode = Str(el, "BUYER_CODE"),
            SellerCode = Str(el, "SELLER_CODE"),
            DiscountRatio = Num(el, "DISCOUNT_RATIO"),
            FreeSharesRatio = Num(el, "FREE_SHARES_RATIO"),
            TotalSharesRatio = Num(el, "TOTAL_SHARES_RATIO"),
            // 滞后字段：抓当天必为 null，靠增量往前推 30 天重抓才填得上，见模型注释。
            ChangeRate1D = Num(el, "CHANGE_RATE_1DAYS"),
            ChangeRate5D = Num(el, "CHANGE_RATE_5DAYS"),
            ChangeRate10D = Num(el, "CHANGE_RATE_10DAYS"),
            ChangeRate20D = Num(el, "CHANGE_RATE_20DAYS"),
            FetchedAt = DateTime.Now,
        };
    }

    /// <summary>股东增减持一行的解析。提成命名方法的理由同 <see cref="ParseBlockTrade"/>。</summary>
    private static HolderChange? ParseHolderChange(JsonElement el)
    {
        var code = Str(el, "SECURITY_CODE");
        var d = Date(el, "NOTICE_DATE");
        if (code.Length == 0 || d == null) return null;
        return new HolderChange
        {
            Code = code,
            Name = Str(el, "SECURITY_NAME_ABBR"),
            NoticeDate = d.Value,
            HolderName = Str(el, "HOLDER_NAME"),
            Direction = Str(el, "DIRECTION"),
            ChangeShares = Num(el, "CHANGE_NUM_SYMBOL") ?? Num(el, "CHANGE_NUM"),
            // ⚠ 不是 CHANGE_RATE——那是**公告日的股价涨跌幅**，跟增减持方向无关
            // （实测 2026-08-25 起 200 条：34 只"增持"里 15 只为负、166 只"减持"里 97 只为正，
            // 还有增持而值为 0 的）。AFTER_CHANGE_RATE 才是变动占总股本的比例：
            // 002203 增持 1519.76 万股 ÷ 0.663142% = 22.9 亿总股本，与实际吻合。
            ChangeRatio = Num(el, "AFTER_CHANGE_RATE"),
            AfterShares = Num(el, "AFTER_HOLDER_NUM"),
            AfterRatio = Num(el, "HOLD_RATIO"),
            StartDate = Date(el, "START_DATE"),
            EndDate = Date(el, "END_DATE"),
            AveragePrice = Num(el, "TRADE_AVERAGE_PRICE"),
            ChangeFreeRatio = Num(el, "CHANGE_FREE_RATIO"),
            ClosePrice = Num(el, "CLOSE_PRICE"),
            RealPrice = Num(el, "REAL_PRICE"),
            ChangeRateQuotes = Num(el, "CHANGE_RATE_QUOTES"),
            FetchedAt = DateTime.Now,
        };
    }

    /// <summary>分片抓取的公共骨架：切片 → 翻页 → 解析 → 整片回调落库。</summary>
    /// <param name="tieBreaker">
    /// 追加到排序键末尾的**定序列**，逗号分隔。
    ///
    /// ⚠ 必须给到能唯一定序，否则深分页会跨页重复/遗漏——**不报错但静默丢数据**。
    /// 日期 + SECURITY_CODE 往往还不够：一只股票同一天可以有几十笔大宗交易，它们之间
    /// 没有确定顺序，翻页时服务端返回的次序就会变。
    ///
    /// 2026-09-07 实测 2016-03 的大宗交易（2435 行 / 5 页）：
    ///   排序 TRADE_DATE,SECURITY_CODE            → 5 行重复，入库只剩 2430
    ///   排序 TRADE_DATE,SECURITY_CODE,DAILY_RANK → 0 行重复，2435 行一行不少
    /// 全期回填累计因此丢了 773 行（0.164%）。这是这个项目在龙虎榜、板块、限售解禁上
    /// 反复踩到的同一个坑。
    /// </param>
    private async Task<int> RunSlicedAsync<T>(
        string report, string dateField, List<EastMoneyQuerySlicer.Slice> slices, string label,
        Func<JsonElement, T?> parse, Func<List<T>, int> onBatch,
        IProgress<string>? progress, CancellationToken ct, string? tieBreaker = null) where T : class
    {
        int total = 0;
        for (int i = 0; i < slices.Count; i++)
        {
            var s = slices[i];
            ct.ThrowIfCancellationRequested();
            var filter = EastMoneyQuerySlicer.DateFilter(dateField, s.Start, s.End);
            var sortColumns = dateField + ",SECURITY_CODE"
                            + (string.IsNullOrEmpty(tieBreaker) ? "" : "," + tieBreaker);
            var rows = new List<T>();
            await foreach (var el in _dc.QueryAsync(report, filter, sortColumns, ct: ct))
            {
                var row = parse(el);
                if (row != null) rows.Add(row);
            }
            int written = rows.Count > 0 ? onBatch(rows) : 0;
            total += written;
            if (written > 0 || i % 12 == 0)
                progress?.Report($"{label} {s.Name}（{i + 1}/{slices.Count}）：本片 {written} 行，累计 {total}");
        }
        return total;
    }

    // ── JSON 取值 ──────────────────────────────────────────────────
    private static string Str(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            _ => "",
        };
    }

    private static double? Num(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float,
                                                    CultureInfo.InvariantCulture, out var d2) ? d2 : null,
            _ => null,
        };
    }

    private static DateTime? Date(JsonElement el, string prop)
    {
        var s = Str(el, prop);
        if (s.Length < 10) return null;
        return DateTime.TryParseExact(s.Substring(0, 10), "yyyy-MM-dd",
                                      CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }
}
