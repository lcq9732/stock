using System.Globalization;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 分红对账的东财侧（2026-09-18）。datacenter 的 <c>RPT_SHAREBONUS_DET</c>，
/// 按**除权日所在年**切片拉全量，只要"已实施且有除权日"的行。
///
/// ════ 实测（2026-09-18，沙箱出口）════
/// 全表约 56,976 行；按年切片 1990-2026 共约 120 个请求、3 分钟拉完，
/// 其中"已实施+有除权日"去重后 56,191 条。跟库里新浪的 59,081 条比：
/// 重叠 55,100、东财独有 1,050（99% 是北交所）、我们独有 3,982（其中 2,516 条是退市股）。
///
/// ════ 为什么按年切片 ════
/// 深分页在这个项目上栽过（排序键排不到主键末列就跨页重复+遗漏，见龙虎榜席位那次）。
/// 按年切片后单年最多几千行、十几页，翻页永远是浅的。
///
/// ════ 字段口径 ════
/// <c>PRETAX_BONUS_RMB</c> / <c>BONUS_RATIO</c> / <c>IT_RATIO</c> 都是**每 10 股**口径，
/// 跟库里 <see cref="DividendRow"/> 一致（实测样例 000400：<c>PRETAX_BONUS_RMB=0.5</c>，
/// 对应页面上的"10派0.50元"）。
///
/// ⚠ <c>announce_date</c> 取 <c>PLAN_NOTICE_DATE</c>（预案公告日），跟新浪那列的
/// "公告日期"**语义不同**——所以判重绝不能按主键，见
/// <see cref="IDividendRepository.GetImplementedExDates"/>。
/// </summary>
public sealed class EastMoneyDividendTableProvider : IDividendCrossCheckSource
{
    private const string ReportName = "RPT_SHAREBONUS_DET";
    private const string Columns =
        "SECURITY_CODE,EX_DIVIDEND_DATE,EQUITY_RECORD_DATE,PLAN_NOTICE_DATE," +
        "PRETAX_BONUS_RMB,BONUS_RATIO,IT_RATIO,ASSIGN_PROGRESS";

    private readonly EastMoneyDataCenterClient _client;

    public string SourceName => "东财 datacenter";

    public event Action<string>? OnStatus;

    public EastMoneyDividendTableProvider(EastMoneyDataCenterClient client)
    {
        _client = client;
        _client.OnStatus += s => OnStatus?.Invoke(s);
    }

    public async IAsyncEnumerable<IReadOnlyList<DividendRow>> StreamImplementedAsync(
        int fromYear, int toYear,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var now = DateTime.Now;
        for (int y = fromYear; y <= toYear; y++)
        {
            ct.ThrowIfCancellationRequested();
            var filter = $"(EX_DIVIDEND_DATE>='{y}-01-01')(EX_DIVIDEND_DATE<='{y}-12-31')";
            var batch = new List<DividendRow>();
            await foreach (var el in _client.QueryAsync(
                               ReportName, filter, "EX_DIVIDEND_DATE,SECURITY_CODE",
                               descending: false, ct: ct))
            {
                // 只要已实施的：预案没有除权日，影响不了复权因子，对账也就不关心。
                if (!Str(el, "ASSIGN_PROGRESS").Contains("实施")) continue;
                if (Date(el, "EX_DIVIDEND_DATE") is not { } ex) continue;
                var code = Str(el, "SECURITY_CODE");
                if (code.Length == 0) continue;

                batch.Add(new DividendRow
                {
                    Code = code,
                    // 预案公告日缺失时退回除权日——announce_date 是主键的一半，不能为空。
                    AnnounceDate = Date(el, "PLAN_NOTICE_DATE") ?? ex,
                    DividendYuan = Num(el, "PRETAX_BONUS_RMB"),
                    BonusShares = Num(el, "BONUS_RATIO"),
                    TransferShares = Num(el, "IT_RATIO"),
                    Progress = "实施",
                    RecordDate = Date(el, "EQUITY_RECORD_DATE"),
                    ExDate = ex,
                    FetchedAt = now,
                    Source = "eastmoney",
                });
            }
            OnStatus?.Invoke($"　{y} 年：已实施 {batch.Count} 条");
            yield return batch;
        }
    }

    private static string Str(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "";
        return (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()).Trim();
    }

    private static DateTime? Date(JsonElement el, string prop)
        => DateTime.TryParse(Str(el, prop), CultureInfo.InvariantCulture,
                             DateTimeStyles.None, out var d) ? d.Date : null;

    private static double Num(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : 0,
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Any,
                                                    CultureInfo.InvariantCulture, out var d2) ? d2 : 0,
            _ => 0,
        };
    }
}
