using System.Text.Json;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 全市场证监会行业分类，**东财两级 + 两所门类字母**（2026-09-10 新增，默认源）。
///
/// 数据来自 F10 公司概况报表 <c>RPT_F10_ORG_BASICINFO</c> 的 <c>CSRC_INDUSTRY_NAME</c>，
/// 它一个字段就给两级、形如 <c>"制造业-酒、饮料和精制茶制造业"</c>，按第一个 <c>-</c> 拆开即可。
/// 走 datacenter（三个域名里最宽松的那个），<c>pageSize=500</c> 约 50 页、2.5MB、一分钟出头。
///
/// ════ 为什么换掉新浪那条路（2026-09-10 全量实测对比）════
/// |                | 两所门类+新浪大类 | 东财 |
/// |----------------|------------------|------|
/// | 有大类的票     | 3886 只          | **6006 只** |
/// | 门类 distinct  | **32 个**（沪深各说各话："住宿餐饮" vs "住宿和餐饮业"） | **19 个**标准全称 |
/// | 大类 distinct  | 84               | 84 |
/// | 库里有、这边无 | —                | **0 只**（是超集，换过去不掉数据） |
///
/// 所以连**门类名**一起换过来（不只是大类）——顺手把沪深两所叫法不统一那个老问题修掉。
/// 门类**代码**（单字母 A~S）东财不给，仍由基类的两所接口提供，见 <see cref="IndustryProviderBase"/>。
///
/// ⚠ 共有的 3886 只里有 402 只（10.3%）大类与新浪不同，分两类：一类是证监会分类的**命名版本**
/// 差异（"开采辅助活动"→"开采专业及辅助性活动"），一类是**真的分到了不同行业**
/// （000900 批发业 vs 道路运输业）。前者正是这张表必须**整表一次换完**的原因——新旧名并存会
/// 把同一个行业拆成两个分组，见 <see cref="IndustrySources"/>。
/// </summary>
public class ExchangeEastMoneyIndustryProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    : IndustryProviderBase(rateLimiter, httpClient)
{
    private const string BaseUrl =
        "https://datacenter.eastmoney.com/securities/api/data/v1/get?reportName=RPT_F10_ORG_BASICINFO" +
        "&columns=SECUCODE,SECURITY_CODE,CSRC_INDUSTRY_NAME&pageSize=500&sortColumns=SECUCODE&sortTypes=1";

    /// <summary>翻页上限的保险丝：实测 50 页，留一倍余量；到顶还没翻完说明接口形状变了。</summary>
    private const int MaxPages = 120;

    /// <summary>两所名单外的票最多占多少（北交所实际约 5%）。超了就是非 A 股混进来了。</summary>
    private const int MaxOutsiderPercent = 15;

    public override string SourceName => IndustrySources.EastMoney;

    protected override async Task ApplyDetailAsync(Dictionary<string, StockIndustry> map, CancellationToken ct)
    {
        var (rows, rawCount, reported) = await FetchAllPagesAsync(ct);

        // 接口自报的 count 是**全部行**（含港股/历史代码，实测 24810），不是 A 股数——
        // 所以对账要拿"解析到的原始行数"跟它比，不能拿过滤后的只数比。对不上说明翻页漏了页。
        if (reported > 0 && rawCount != reported)
            throw new RateLimitedException(
                $"东财行业分页对账不符：接口自报 {reported} 行、实收 {rawCount} 行——本轮放弃，不写库");

        int hit = 0, added = 0;
        foreach (var (code, className, majorName) in rows)
        {
            if (!map.TryGetValue(code, out var row))
            {
                // 两所那两个接口不含北交所，这些票只能由东财这一份建条目（没有门类字母）。
                row = new StockIndustry { Code = code };
                map[code] = row;
                added++;
            }
            // 门类名也用东财的（统一 19 个标准名）；门类**代码**保持两所给的，东财不提供。
            if (!string.IsNullOrEmpty(className)) row.ClassName = className;
            if (!string.IsNullOrEmpty(majorName)) row.MajorName = majorName;
            hit++;
        }

        // 两所名单外的应该只有北交所那两三百只（约 5%）。远超这个数说明过滤又漏了非 A 股，
        // 2026-09-10 实机验证就是这么抓回 21018 只的——当时只在日志里露了个数字，没有东西拦它。
        int outsiderPct = map.Count == 0 ? 0 : added * 100 / map.Count;
        if (outsiderPct > MaxOutsiderPercent)
            throw new RateLimitedException(
                $"东财行业分类里有 {added} 只不在两所名单内（占 {outsiderPct}%，上限 {MaxOutsiderPercent}%）"
                + "——多半是非 A 股混进来了，本轮放弃，不写库");

        int withMajor = map.Values.Count(r => !string.IsNullOrEmpty(r.MajorName));
        Status($"东财证监会分类：{hit} 只（其中 {added} 只是两所名单外的，多为北交所）；" +
               $"合计 {map.Count} 只、有大类 {withMajor} 只");
    }

    /// <summary>翻完所有页。返回 (解析出的A股行, 原始行数, 接口自报总行数)。</summary>
    private async Task<(List<(string Code, string ClassName, string MajorName)> Rows, int RawCount, int Reported)>
        FetchAllPagesAsync(CancellationToken ct)
    {
        var rows = new List<(string, string, string)>();
        int rawCount = 0, reported = 0;

        for (int page = 1; page <= MaxPages; page++)
        {
            var txt = await Limiter.RunAsync(
                () => GetStringAsync($"{BaseUrl}&pageNumber={page}", "https://data.eastmoney.com/", gbk: false, ct), ct);

            using var doc = JsonDocument.Parse(txt);
            if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                break;   // 翻到尾部时 result 是 null，正常收工
            if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;

            if (page == 1 && result.TryGetProperty("count", out var c) && TryReadInt(c, out var n))
                reported = n;

            int inPage = 0;
            foreach (var item in data.EnumerateArray())
            {
                inPage++;
                rawCount++;
                if (!IsAShare(Str(item, "SECUCODE"), Str(item, "SECURITY_CODE"), out var code)) continue;

                var (className, majorName) = SplitCsrcName(Str(item, "CSRC_INDUSTRY_NAME"));
                if (className.Length == 0 && majorName.Length == 0) continue;
                rows.Add((code, className, majorName));
            }

            if (page % 10 == 0) Status($"东财行业分类：已翻 {page} 页、{rows.Count} 只");
            if (inPage < 500) break;
        }

        return (rows, rawCount, reported);
    }

    /// <summary>
    /// 这一行是不是 A 股。**两个条件都要判**，2026-09-10 实机验证踩过：
    /// 只判代码形状（6 位数字 + <c>MarketClassifier</c>）会把港股之类一并收进来——
    /// 那次抓回 21018 只（真实 A 股约 6000 只），而且**一个错都不报**，
    /// 只在日志里表现为"15674 只是两所名单外的"。
    /// 这张报表的 <c>count=24810</c> 本来就含大量非 A 股行，所以后缀才是那个决定性的判据。
    /// </summary>
    public static bool IsAShare(string secucode, string securityCode, out string code)
    {
        code = securityCode.Trim();
        var suffix = secucode.Trim().ToUpperInvariant();
        if (!(suffix.EndsWith(".SH") || suffix.EndsWith(".SZ") || suffix.EndsWith(".BJ"))) return false;
        if (code.Length != 6 || !code.All(char.IsDigit)) return false;
        // 市场判定仍统一走 MarketClassifier（920 是北交所这类规则只该有一份，
        // provider 自写前缀规则害得 342 只票静默抓不到，见 feedback）。
        return MarketClassifier.Classify(code) != MarketBoard.Unknown;
    }

    /// <summary>
    /// 拆 <c>"制造业-酒、饮料和精制茶制造业"</c> → (门类, 大类)。
    /// 按**第一个** <c>-</c> 拆：大类名里有顿号但没有连字符，而拆多了会把名字截断。
    /// 只有一段的当作只有门类。
    /// </summary>
    public static (string ClassName, string MajorName) SplitCsrcName(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return ("", "");
        var parts = raw.Split('-', 2);
        return parts.Length == 2
            ? (parts[0].Trim(), parts[1].Trim())
            : (raw, "");
    }
}
