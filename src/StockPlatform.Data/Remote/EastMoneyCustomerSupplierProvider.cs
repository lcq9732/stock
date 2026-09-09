using System.Globalization;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 前五大客户/供应商（东财 <c>RPT_F10_BUSINESS_CUSTSUPP</c>，走 datacenter）。
///
/// 这是找"产业链上/中/下游"标签一路找空之后能拿到的**最硬的产业链数据**：不是别人的分类判断，
/// 是年报里的交易金额——供应商就是上游，客户就是下游。
/// （标签那条路已经查死：东财网页、终端本地文件、终端「数据」/「分析」菜单、F10 全部栏目都没有；
///  F10 前端代码里确实有 <c>INDICATOR_GRANULARITY=003 产业链</c> 的分支，但线上一条数据都没有。）
///
/// ════ 数据全貌（2026-09-07 实测）════
///   76.5 万行 / <b>2002 年至今</b> / 2025 年覆盖 5284 只股票（92%）
///   每家每期最多 12 行：客户 5 + 「其余客户」1 + 供应商 5 + 「其余供应商」1
///   对手名 <b>53% 是真名</b>，其余是"第一名""客户1"这类匿名披露；71% 的股票至少有一个真名对手
///
/// ⚠ <b>按年切片抓</b>，不做深分页：全表 1531 页，而单年只有约 128 页。
///   这个项目在深分页上吃过亏（见 EastMoneyQuerySlicer 的注释）。
///
/// ⚠ 排序键必须 <c>SECUCODE,REPORT_DATE,TYPE_CODE,RANK</c> 四列。一家公司一期就有 12 行，
///   键不唯一时同键行在页与页之间先后不保证，翻页会既重复又丢行——龙虎榜、板块、个股题材
///   都踩过这个坑。这四列实测抽 500 条 0 重复。
/// </summary>
public class EastMoneyCustomerSupplierProvider
{
    private const string Report = "RPT_F10_BUSINESS_CUSTSUPP";

    /// <summary>数据最早到 2002 年报（实测）。再往前查是空的，白费请求。</summary>
    public const int FirstYear = 2002;

    private readonly EastMoneyDataCenterClient _dc;

    public event Action<string>? OnStatus;

    public EastMoneyCustomerSupplierProvider(EastMoneyDataCenterClient dc)
    {
        _dc = dc;
        _dc.OnStatus += s => OnStatus?.Invoke(s);
    }

    /// <summary>
    /// 抓一年的全部行（约 6.4 万行、128 页），**流式产出**：攒够一批就 yield 一次。
    ///
    /// 不攒到年底再一次性返回，是为了配合新式任务骨架——它在 Deadline / MaxItems 到点时
    /// 会在批边界收尾，批越细，"停在哪都不丢"的粒度就越细。
    /// </summary>
    /// <param name="onReported">接口自报的总行数（第一页时回调一次），给年度完成度对账用。</param>
    /// <param name="onSkipped">
    /// 这一年**主动丢掉**多少行（收尾时回调一次）。必须记下来：接口自报的 reported 含非 A 股
    /// 主体（形如 A21653，约 16.4%），而落库的只有 A 股，只比 saved 和 reported 会把主动过滤
    /// 误判成"没抓齐"，那几年每轮重抓且永远抓不齐。
    /// </param>
    public async IAsyncEnumerable<IReadOnlyList<CustomerSupplier>> StreamYearAsync(
        int year, Action<int> onReported, int batchSize = 2000,
        Action<int>? onSkipped = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var buf = new List<CustomerSupplier>(batchSize);
        int skipped = 0;
        var now = DateTime.Now;

        await foreach (var el in _dc.QueryAsync(Report,
                                                filter: YearFilter(year),
                                                sortColumns: "SECUCODE,REPORT_DATE,TYPE_CODE,RANK",
                                                descending: false,
                                                onTotalCount: onReported, ct: ct))
        {
            if (Parse(el, now) is { } row)
            {
                buf.Add(row);
                if (buf.Count >= batchSize)
                {
                    yield return buf;
                    // 新建而不是 Clear：yield 出去的那个 list 调用方还拿着，
                    // 复用同一个实例的话下一批会把它的内容改掉。
                    buf = new List<CustomerSupplier>(batchSize);
                }
            }
            else skipped++;
        }
        if (buf.Count > 0) yield return buf;
        onSkipped?.Invoke(skipped);
    }

    /// <summary>某一年的 filter。<c>REPORT_YEAR</c> 是东财自带的年份列，不用拿日期区间去凑。</summary>
    public static string YearFilter(int year) => $"(REPORT_YEAR=\"{year}\")";

    /// <summary>
    /// 这一轮该抓哪些年（新的在前）。
    ///
    /// 抽成纯函数是因为它错了的后果**无声**：数据会永久残缺，而界面上一切正常。
    ///
    /// 规则只有两条：
    ///   · <b>今年和去年每轮都抓</b>——年报是分批披露的，3 月抓到的 2025 年报只有一部分，
    ///     4、5 月还在陆续出；中报一季报同理。
    ///   · 更早的年份**只在没抓齐时抓**——判据是"落库数 &lt; 接口自报数"。
    ///     不能只看"这一年有没有数据"：新式任务骨架会在 Deadline / MaxItems 到点时**从批中间
    ///     截断**，而且那算正常完成（界面上打勾）。抓了 6000/66000 行的年份要是被当成抓过了，
    ///     剩下 6 万行永远不来，**毫无征兆**。今年去年靠"每轮重抓"能自愈，2002-2024 不能。
    /// </summary>
    /// <param name="thisYear">当前年份。</param>
    /// <param name="states">每年的 (接口自报, 实际落库, 主动丢弃)，来自 CustSuppYearState。</param>
    /// <param name="rebuild">首次整段回补：无视完成度，所有年份重来。</param>
    public static List<int> PlanYearsToFetch(
        int thisYear, IReadOnlyDictionary<int, (int Reported, int Saved, int Skipped)> states,
        bool rebuild = false)
    {
        var years = new List<int>();
        for (int y = thisYear; y >= FirstYear; y--)
        {
            if (rebuild || y >= thisYear - 1) { years.Add(y); continue; }
            // 没记录过 → 抓；记录了但没收全 → 重抓。
            //
            // ⚠ 判据是 **saved + skipped < reported**，三个数缺一不可：
            //   · 不能只看"这一年有没有数据"——骨架会在 Deadline / MaxItems 到点时从批中间
            //     截断，抓了 6000/66000 行的年份会被当成抓过了跳过；
            //   · 也不能只比 saved 和 reported——reported 是接口自报的、**含非 A 股主体**
            //     （约 16.4%），而落库的只有 A 股。2026-09-09 就是这么误报的：
            //     2023 年 51,677/62,791、2025 年 52,214/62,774，差额比例正好是非 A 股占比，
            //     那几年每轮重抓、而且**永远抓不齐**。
            if (!states.TryGetValue(y, out var st) || st.Saved + st.Skipped < st.Reported)
                years.Add(y);
        }
        return years;
    }

    /// <summary>
    /// 解析一行。关键字段缺了就返回 null（调用方丢弃并计数，不是报错）。
    /// 抽成静态是为了能拿真实 JSON 做单测。
    /// </summary>
    public static CustomerSupplier? Parse(JsonElement el, DateTime fetchedAt)
    {
        var code = Str(el, "SECURITY_CODE");
        var date = Date(el, "REPORT_DATE");
        var typeCode = Str(el, "TYPE_CODE");
        var rank = Num(el, "RANK");

        // ⚠ 必须**同时**判长度和"全是数字"（2026-09-09 补）。只判长度的话，东财那边形如
        //   A21653 / A04018 的 6 位代码会一路混进来——实测落库 1946 只、118,155 行（16.4%），
        //   而它们**一条都不在 StockMeta 里**：那是非 A 股主体（新三板/改制前之类），
        //   作为"上市公司之间的交易关系"一端根本不在股票池里，连不成任何有用的边。
        //   同一批写的 EastMoneyCompanyProfileProvider.Parse 一直有这个检查，这边漏了，两处不一致。
        if (code.Length != 6 || !code.All(char.IsDigit) || date == null || rank == null) return null;
        // TYPE_CODE：1=客户、2=供应商。别的值不认——宁可少几行，也不能把上游下游搞反。
        if (typeCode != "1" && typeCode != "2") return null;

        return new CustomerSupplier
        {
            Code = code,
            ReportDate = date.Value,
            IsSupplier = typeCode == "2",
            Rank = (int)rank.Value,
            PartnerName = Str(el, "ITEM_NAME"),
            Amount = Num(el, "AMOUNT") ?? 0,
            // 东财那列叫 TOI_RATIO，但它其实是 AMOUNT/SUM_AMOUNT（供应商组分母是采购总额
            // 而非营收）。这里落到 Pct，不沿用那个会骗人的名字，理由见模型注释。
            Pct = Num(el, "TOI_RATIO") ?? 0,
            TotalAmount = Num(el, "SUM_AMOUNT") ?? 0,
            ReportName = Str(el, "REPORT_NAME"),
            FetchedAt = fetchedAt,
        };
    }

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
