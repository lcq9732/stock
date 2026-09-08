using System.Globalization;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 行业景气指标（东财 <c>RPTA_DATA_IF_*</c> 三张报表，走 datacenter）。
///
/// 这是**传统行业分析**那一路的输入，跟风口分析分开：风口看叙事能不能兑现成别人的报表，
/// 周期股看价格和库存本身——猪粮比、螺纹钢库存、焦煤期货价这些是日/周频的，
/// 比季报早一个季度告诉你利润要往哪走。
///
/// ════ 接口是怎么找到的（2026-09-07）════
/// 起因是想找"产业链上/中/下游"标签。东财网页侧没有，终端本地文件里也没有，最后是在终端
/// 缓存的 F10 前端代码里（<c>pc_hsf10/js/chunk-*.js</c>）翻到的这三个报表名。
/// 顺带确认了一件事：前端确实有 <c>INDICATOR_GRANULARITY=003 产业链</c> 这个分支，
/// 但**线上一条数据都没有**——上/中/下游这个维度，东财这条路是走不通的，别再花时间找了。
///
/// ════ 三个报表的分工 ════
///   RPTA_DATA_IF_INDICATOR   目录：股票 × 指标的映射，附带指标的全部属性。不带 filter 就是全量。
///   RPTA_DATA_IF_LINECHART   折线图指标的历史序列（72 个）
///   RPTA_DATA_IF_BARCHART    柱状图指标的历史序列（44 个），多给一列同比
///
/// ⚠ 走错接口不会报错，**返回 0 条**——所以必须按 <see cref="IndustryIndicator.ChartType"/> 分流。
///
/// ⚠ 查序列**必须带 SECUCODE**。不带的话接口返回的是"关联的每只股票 × 每个日期"的笛卡尔积
///   （实测玻璃期货 39 只股票 × 570 天 ≈ 2.2 万行），会直接撞上分页上限被静默截断。
///   带上任意一只即可——同一指标各股票的值完全一样（7 个多股共享指标 569 个日期 0 冲突）。
///
/// <b>不碰 push2</b>：datacenter 稳定可达、无需人工过验证码，适合无人值守。
/// </summary>
public class EastMoneyIndustryIndicatorProvider
{
    private const string CatalogReport = "RPTA_DATA_IF_INDICATOR";
    private const string LineReport = "RPTA_DATA_IF_LINECHART";
    private const string BarReport = "RPTA_DATA_IF_BARCHART";

    /// <summary>柱状图走 BARCHART，其余走 LINECHART。东财只有这两种值。</summary>
    private const string BarChart = "柱状图";

    private readonly EastMoneyDataCenterClient _dc;

    public event Action<string>? OnStatus;

    public EastMoneyIndustryIndicatorProvider(EastMoneyDataCenterClient dc)
    {
        _dc = dc;
        _dc.OnStatus += s => OnStatus?.Invoke(s);
    }

    /// <summary>
    /// 拉目录：全部指标定义 + 股票↔指标映射。实测 116 个指标、1190 条映射，3 页拉完。
    ///
    /// 调用方要么两样都用、要么都别用——这是**快照**，半截名单进库等于凭空少掉一批关联。
    /// </summary>
    public async Task<(List<IndustryIndicator> Indicators, List<StockIndicatorLink> Links)>
        FetchCatalogAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var byId = new Dictionary<string, IndustryIndicator>(StringComparer.OrdinalIgnoreCase);
        var links = new List<StockIndicatorLink>(1500);
        int reported = 0, rows = 0, skippedCode = 0;
        var now = DateTime.Now;

        progress?.Report("行业景气指标：拉取指标目录...");

        // ⚠ 排序键必须能**唯一定序**。一只股票在这张表里有多行（关联多个指标），
        // 光按 SECUCODE 排的话同一只股票内部先后不定，翻页会既重复又丢行——
        // 这个项目在龙虎榜、板块、个股题材上都踩过同一个坑，所以补上 INDICATOR_ID。
        await foreach (var el in _dc.QueryAsync(CatalogReport,
                                                sortColumns: "SECUCODE,INDICATOR_ID",
                                                descending: false,
                                                onTotalCount: n => reported = n, ct: ct))
        {
            var secu = Str(el, "SECUCODE");
            var id = Str(el, "INDICATOR_ID");
            if (id.Length == 0) continue;
            rows++;

            if (!byId.ContainsKey(id))
                byId[id] = ParseIndicator(el, now);

            var code = ToLocalCode(secu);
            if (code == null) { skippedCode++; continue; }
            links.Add(new StockIndicatorLink(code, id, (int?)Num(el, "INDICATOR_ORDER")));
        }

        // 对账：接口自报多少行、实际收到多少行。对不上说明翻页中途悄悄少了——
        // 这道防线在这个项目里救过好几次命（板块成分股靠它抓出过漏股）。
        if (reported > 0 && rows != reported)
            progress?.Report($"⚠ 指标目录行数对不上：接口自报 {reported}，实际收到 {rows}。");

        progress?.Report($"行业景气指标目录：{byId.Count} 个指标、{links.Count} 条股票关联"
                       + (skippedCode > 0 ? $"（跳过 {skippedCode} 条非 A 股代码）" : "") + "。");

        return (byId.Values.ToList(), links);
    }

    /// <summary>
    /// 拉一个指标的历史序列。
    /// </summary>
    /// <param name="secuCode">代表股，形如 002714.SZ。必须给，理由见类注释。</param>
    /// <param name="since">
    /// 增量水位线：只取这个日期**之后**的。null＝全量（首轮）。
    /// 历史只到 2024-04，全量也就几百行。
    /// </param>
    public async Task<List<IndicatorPoint>> FetchSeriesAsync(
        IndustryIndicator ind, string secuCode, DateTime? since, CancellationToken ct = default)
    {
        var points = new List<IndicatorPoint>(600);
        await foreach (var el in _dc.QueryAsync(ReportFor(ind),
                                                filter: BuildSeriesFilter(ind, secuCode, since),
                                                sortColumns: "UPDATE_DATE", descending: false, ct: ct))
        {
            if (ParsePoint(el) is { } p) points.Add(p);
        }
        return points;
    }

    /// <summary>柱状图走 BARCHART，其余走 LINECHART。<b>走错不报错，只返回 0 条。</b></summary>
    public static string ReportFor(IndustryIndicator ind)
        => ind.ChartType == BarChart ? BarReport : LineReport;

    /// <summary>
    /// 拼序列查询的 filter。抽出来是因为它**错了不会报错、只会静默返回 0 条**：
    /// 少一个 IS_POSED、日期格式不对、引号用错，表现都一样——"这个指标没数据"。
    /// </summary>
    /// <param name="since">只取这个日期**之后**的；null＝全量。</param>
    public static string BuildSeriesFilter(IndustryIndicator ind, string secuCode, DateTime? since)
    {
        // IS_POSED="1" 是折线图那条路的必要条件（前端就是这么发的）；柱状图不带。
        var f = $"(SECUCODE=\"{secuCode}\")"
              + (ind.ChartType == BarChart ? "" : "(IS_POSED=\"1\")")
              + $"(INDICATOR_ID=\"{ind.IndicatorId}\")";
        if (since is { } s) f += $"(UPDATE_DATE>'{s:yyyy-MM-dd}')";
        return f;
    }

    /// <summary>解析目录里的一行 → 指标定义。抽成静态是为了能拿真实 JSON 做单测。</summary>
    public static IndustryIndicator ParseIndicator(JsonElement el, DateTime fetchedAt) => new()
    {
        IndicatorId = Str(el, "INDICATOR_ID"),
        Name = Str(el, "INDICATOR_NAME"),
        OrigName = Str(el, "ORIG_NAME"),
        Unit = Str(el, "UNIT"),
        Frequency = Str(el, "UPDATE_FREQUENCY"),
        Granularity = Str(el, "INDICATOR_GRANULARITY"),
        ChartType = Str(el, "CHART_TYPE"),
        Source = Str(el, "SOURCE"),
        FetchedAt = fetchedAt,
    };

    /// <summary>
    /// 解析序列里的一行。日期或值缺了就返回 null（调用方丢弃这行，不是报错）。
    ///
    /// 同比取 <c>YOY_VALUE1</c>（数值）而不是 <c>YOY_VALUE</c>（"-25.59%" 带百分号的字符串），
    /// 省一次解析；折线图两列都没有，留 null。
    /// </summary>
    public static IndicatorPoint? ParsePoint(JsonElement el)
    {
        var id = Str(el, "INDICATOR_ID");
        var d = Date(el, "UPDATE_DATE");
        var v = Num(el, "VALUE");
        if (id.Length == 0 || d == null || v == null) return null;
        return new IndicatorPoint(id, d.Value, v.Value, Num(el, "YOY_VALUE1"));
    }

    /// <summary>
    /// <c>002714.SZ</c> → <c>002714</c>。非 A 股（港股、美股等后缀）返回 null。
    ///
    /// 这里只做**去后缀**，不自己判市场——判市场归 MarketClassifier 管。
    /// （provider 自写市场前缀规则害得 342 只北交所票静默抓不到，那个教训不重演。）
    /// </summary>
    public static string? ToLocalCode(string secuCode)
    {
        if (string.IsNullOrWhiteSpace(secuCode)) return null;
        int dot = secuCode.LastIndexOf('.');
        if (dot <= 0) return null;

        var suffix = secuCode[(dot + 1)..];
        if (!suffix.Equals("SH", StringComparison.OrdinalIgnoreCase)
            && !suffix.Equals("SZ", StringComparison.OrdinalIgnoreCase)
            && !suffix.Equals("BJ", StringComparison.OrdinalIgnoreCase))
            return null;

        var code = secuCode[..dot];
        return code.Length == 6 && code.All(char.IsDigit) ? code : null;
    }

    /// <summary>
    /// <c>002714</c> → <c>002714.SZ</c>。跟 <see cref="ToLocalCode"/> 是一对。
    ///
    /// 市场判定走 <see cref="Logic.Services.MarketClassifier"/>，**不自己写前缀规则**——
    /// provider 自写规则害得 342 只北交所票（920 开头）静默抓不到，那个教训不重演。
    /// </summary>
    public static string? ToSecuCode(string code)
        => Logic.Services.MarketClassifier.Classify(code) switch
        {
            Logic.Services.MarketBoard.ShanghaiMain
                or Logic.Services.MarketBoard.ShanghaiStar
                or Logic.Services.MarketBoard.ShanghaiB => code + ".SH",
            Logic.Services.MarketBoard.ShenzhenMain
                or Logic.Services.MarketBoard.ShenzhenChiNext
                or Logic.Services.MarketBoard.ShenzhenB => code + ".SZ",
            Logic.Services.MarketBoard.Beijing => code + ".BJ",
            _ => null,
        };

    private static string Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static double? Num(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any,
                                                      CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static DateTime? Date(JsonElement el, string name)
    {
        var s = Str(el, name);
        return s.Length >= 10 && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                                                   DateTimeStyles.None, out var d) ? d.Date : null;
    }
}
