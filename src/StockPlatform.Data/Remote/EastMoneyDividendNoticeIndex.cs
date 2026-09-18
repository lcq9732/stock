using System.Text.Json;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 分红公告索引·东财实现（2026-09-18）。datacenter 的 <c>RPT_SHAREBONUS_DET</c>，
/// 按公告日 <c>NOTICE_DATE</c> 筛最近一段，只取 <c>SECURITY_CODE</c> 这一列。
///
/// ════ 沙箱实测（2026-09-18）════
///   · 全表约 56976 行；`NOTICE_DATE>='2026-09-01'` ⇒ 337 行/337 代码（18 天）；
///     `>='2026-06-20'` ⇒ 1897 行/1692 代码（90 天）。
///   · <c>ASSIGN_PROGRESS</c> 四种值都在：实施分配 1444 / 董事会预案通过 275 /
///     股东大会通过 177 / 预披露 1 ——**预案也收**，而且方案推进时 <c>NOTICE_DATE</c> 会更新
///     （样例 000400：预案日 08-20、公告日 09-18、进度"实施分配"），所以进度变化也捕捉得到。
///   · **漏检约 1%**：库里近 90 天有公告的 504 只中 5 只不在索引里
///     （000538/000838/000958/300044…），90 天窗口里也没有——是东财这张表自己缺记录，
///     扩大窗口救不了。所以调用方必须留周期性全量兜底。
///
/// ════ 为什么按周切片而不是翻页 ════
/// 一片 7 天约 150 行，稳稳单页；回看 45 天 ⇒ 7 个请求。
/// 不靠 <c>pageSize=500</c> 翻页是因为 <c>NOTICE_DATE+SECURITY_CODE</c> 未必唯一，
/// 而这个项目在"排序键排不到主键末列 ⇒ 跨页既重复又丢行"上栽过
/// （龙虎榜席位那次，见 doc/lhb-seat-task-design.md）。索引只要代码集合，
/// 重复无所谓、**遗漏才要命**，所以选永远单页的切法。
/// </summary>
public sealed class EastMoneyDividendNoticeIndex : IDividendNoticeIndex
{
    private const string ReportName = "RPT_SHAREBONUS_DET";

    /// <summary>一片几天。7 天约 150 行，离 pageSize 上限 500 还远。</summary>
    private const int SliceDays = 7;

    private readonly EastMoneyDataCenterClient _client;

    public string SourceName => "东财 datacenter";

    /// <summary>自己的播报 + 转发客户端的（限流暂停那类）。转发方式跟
    /// <see cref="EastMoneyDataCenterClient"/> 转发 RateLimiter 的一样。</summary>
    public event Action<string>? OnStatus;

    public EastMoneyDividendNoticeIndex(EastMoneyDataCenterClient client)
    {
        _client = client;
        _client.OnStatus += s => OnStatus?.Invoke(s);
    }

    public async Task<IReadOnlyDictionary<string, DateTime>> GetRecentAsync(
        int lookbackDays, CancellationToken ct = default)
    {
        // code → 这段窗口里它最新那条公告的日期。同一只票窗口内可能有好几条
        // （预案、股东大会通过、实施各一条），取最新的那条——判据只关心"最近一次动静"。
        var codes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var end = DateTime.Today;
        var start = end.AddDays(-Math.Max(1, lookbackDays));

        for (var from = start; from <= end; from = from.AddDays(SliceDays))
        {
            ct.ThrowIfCancellationRequested();
            var to = from.AddDays(SliceDays - 1);
            if (to > end) to = end;

            var filter = $"(NOTICE_DATE>='{from:yyyy-MM-dd}')(NOTICE_DATE<='{to:yyyy-MM-dd}')";
            int rows = 0;
            await foreach (var el in _client.QueryAsync(
                               ReportName, filter, "NOTICE_DATE,SECURITY_CODE", ct: ct))
            {
                rows++;
                var code = Str(el, "SECURITY_CODE");
                if (code.Length == 0) continue;
                // 日期解析不出来就退回这一片的右端——宁可判"更晚"（多抓一次），
                // 也不能判成很早而把这只票跳过去。
                var notice = Date(el, "NOTICE_DATE") ?? to;
                if (!codes.TryGetValue(code, out var had) || notice > had) codes[code] = notice;
            }

            // 一片撑到 pageSize 上限就说明"单页"这个前提破了，得知道——不报的话
            // 就会悄悄退化成深分页，而深分页正是会丢行的那条路。
            if (rows >= EastMoneyDataCenterClient.PageSize)
                OnStatus?.Invoke($"⚠ 分红公告索引 {from:MM-dd}~{to:MM-dd} 取回 {rows} 行，"
                               + $"已到单页上限（{EastMoneyDataCenterClient.PageSize}）——这一片可能没取全，"
                               + "该把切片调细了。");
        }

        return codes;
    }

    private static DateTime? Date(JsonElement el, string prop)
    {
        var s = Str(el, prop);
        return DateTime.TryParse(s, out var d) ? d.Date : null;
    }

    private static string Str(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "";
        return (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()).Trim();
    }
}
