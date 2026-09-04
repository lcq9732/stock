using System.Globalization;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 业绩预告 + 业绩快报（东财 datacenter）。这两份数据本地此前完全没有，没有回退源——
/// 新浪/腾讯/交易所/巨潮都不提供结构化的预告数据（巨潮有公告原文，但要自己从 PDF 里抠）。
/// 所以东财不可用时这一项只能整体跳过，不像板块那样有备胎。
///
/// 按<b>年</b>切片抓：预告的量不大（全历史约 20 万行），月切片会产生上百个小请求反而更慢；
/// 但完全不切又会撞上深分页（东财翻到上千页会拒绝）。年切片下每片 40 页左右，正好。
/// </summary>
public class EastMoneyEarningsForecastProvider
{
    private const string ForecastReport = "RPT_PUBLIC_OP_NEWPREDICT";
    private const string ExpressReport = "RPT_FCI_PERFORMANCEE";

    private readonly EastMoneyDataCenterClient _dc;

    public event Action<string>? OnStatus;

    public EastMoneyEarningsForecastProvider(EastMoneyDataCenterClient dc)
    {
        _dc = dc;
        _dc.OnStatus += s => OnStatus?.Invoke(s);
    }

    /// <summary>
    /// 抓业绩预告。按公告日区间取，调用方给的 start 一般是"本地已有的最新公告日"。
    ///
    /// <b>每抓完一年就回调一次落库</b>，不是攒完再返回：首轮是 20 万行、十几年，
    /// 攒在内存里的话中途一停就全白抓。落库了的部分下一轮靠水位线自然跳过。
    /// </summary>
    /// <param name="onBatch">一片（一年）数据就绪时的回调，返回实际写入行数。</param>
    public async Task<int> FetchForecastsAsync(
        DateTime start, DateTime end, Func<List<EarningsForecast>, int> onBatch,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        int total = 0;
        var slices = EastMoneyQuerySlicer.ByYear(start, end);
        var now = DateTime.Now;

        for (int i = 0; i < slices.Count; i++)
        {
            var s = slices[i];
            ct.ThrowIfCancellationRequested();
            progress?.Report($"业绩预告 {s.Name}（{i + 1}/{slices.Count}）...");

            var filter = EastMoneyQuerySlicer.DateFilter("NOTICE_DATE", s.Start, s.End);
            var result = new List<EarningsForecast>();
            await foreach (var el in _dc.QueryAsync(ForecastReport, filter, "NOTICE_DATE,SECURITY_CODE", ct: ct))
            {
                var code = Str(el, "SECURITY_CODE");
                var report = Date(el, "REPORT_DATE");
                var notice = Date(el, "NOTICE_DATE");
                if (code.Length == 0 || report == null || notice == null) continue;

                result.Add(new EarningsForecast
                {
                    Code = code,
                    Name = Str(el, "SECURITY_NAME_ABBR"),
                    ReportDate = report.Value,
                    NoticeDate = notice.Value,
                    PredictFinanceCode = Str(el, "PREDICT_FINANCE_CODE"),
                    PredictFinance = Str(el, "PREDICT_FINANCE"),
                    AmountLower = Num(el, "PREDICT_AMT_LOWER"),
                    AmountUpper = Num(el, "PREDICT_AMT_UPPER"),
                    AmplitudeLower = Num(el, "ADD_AMP_LOWER"),
                    AmplitudeUpper = Num(el, "ADD_AMP_UPPER"),
                    PredictType = Str(el, "PREDICT_TYPE"),
                    Content = Str(el, "PREDICT_CONTENT"),
                    ChangeReason = Str(el, "CHANGE_REASON_EXPLAIN"),
                    PreYearSamePeriod = Num(el, "PREYEAR_SAME_PERIOD"),
                    IsLatest = EastMoneyJson.Bool(el, "IS_LATEST"),
                    FetchedAt = now,
                });
            }
            total += result.Count > 0 ? onBatch(result) : 0;
            progress?.Report($"业绩预告 {s.Name} 累计 {total} 条");
        }
        return total;
    }

    /// <summary>抓业绩快报。跟预告一样按年分片、每片落库，中断不白抓。</summary>
    public async Task<int> FetchExpressAsync(
        DateTime start, DateTime end, Func<List<EarningsExpress>, int> onBatch,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        int total = 0;
        var slices = EastMoneyQuerySlicer.ByYear(start, end);
        var now = DateTime.Now;

        for (int i = 0; i < slices.Count; i++)
        {
            var s = slices[i];
            ct.ThrowIfCancellationRequested();
            progress?.Report($"业绩快报 {s.Name}（{i + 1}/{slices.Count}）...");

            // 快报按 UPDATE_DATE 切：它会被修正，且 NOTICE_DATE 有时为空
            var filter = EastMoneyQuerySlicer.DateFilter("UPDATE_DATE", s.Start, s.End);
            var result = new List<EarningsExpress>();
            await foreach (var el in _dc.QueryAsync(ExpressReport, filter, "UPDATE_DATE,SECURITY_CODE", ct: ct))
            {
                var code = Str(el, "SECURITY_CODE");
                var report = Date(el, "REPORT_DATE");
                if (code.Length == 0 || report == null) continue;

                result.Add(new EarningsExpress
                {
                    Code = code,
                    Name = Str(el, "SECURITY_NAME_ABBR"),
                    ReportDate = report.Value,
                    NoticeDate = Date(el, "NOTICE_DATE"),
                    UpdateDate = Date(el, "UPDATE_DATE"),
                    Eps = Num(el, "BASIC_EPS"),
                    Revenue = Num(el, "TOTAL_OPERATE_INCOME"),
                    RevenueYoy = Num(el, "YSTZ"),
                    NetProfitParent = Num(el, "PARENT_NETPROFIT"),
                    NetProfitYoy = Num(el, "JLRTBZCL"),
                    Bvps = Num(el, "PARENT_BVPS"),
                    Roe = Num(el, "WEIGHTAVG_ROE"),
                    RevenueQoq = Num(el, "DJDYSHZ"),
                    NetProfitQoq = Num(el, "DJDJLHZ"),
                    FetchedAt = now,
                });
            }
            total += result.Count > 0 ? onBatch(result) : 0;
            progress?.Report($"业绩快报 {s.Name} 累计 {total} 条");
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

    /// <summary>东财返回的是 "2026-09-02 00:00:00"，只取日期部分。</summary>
    private static DateTime? Date(JsonElement el, string prop)
    {
        var s = Str(el, prop);
        if (s.Length < 10) return null;
        return DateTime.TryParseExact(s.Substring(0, 10), "yyyy-MM-dd",
                                      CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }
}
