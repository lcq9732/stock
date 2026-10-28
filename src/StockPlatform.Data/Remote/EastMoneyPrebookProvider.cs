using System.Globalization;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 定期报告**预约披露日**·东财版（2026-10-27，见 doc/financial-fetch-disclosure-race-design.md）。
/// 数据源：datacenter 的 <c>RPT_PUBLIC_BS_APPOIN</c>（页面入口 data.eastmoney.com/yjyg/yypl.html）。
///
/// ════ 为什么在巨潮之外再加一家 ════
/// 【拉取财务报表】拿"已披露了没有"当闸门，而巨潮作为交易所指定披露网站，这个标记要走正式
/// 入库流程，**实测滞后半天以上**：2026-10-27 00:39 抓时本期标了 1325 只，同一天 14:12 再抓
/// 变成 1690 只——当晚披露的 410 只里有 365 只是白天才补上的。格力(000651) 10-26 晚间就发了
/// 三季报，凌晨那轮仍是"未披露"，于是财务抓取整整晚了一轮。
///
/// 判据改成「**任一家标了已披露就算已披露**」，**下限就是现状**：东财快就赚一天，东财慢也不会
/// 比只用巨潮更差。所以这一家不需要先证明它更快才敢上。
///
/// ════ 当初为什么没选东财（这条理由已经作废）════
/// <see cref="CninfoPrebookProvider"/> 的注释里写着"东财也有，但用户环境里东财基本不通"。
/// 那是 2026-09-01 写的，**09-03 已更正**：不通的只有 push2 这一个域名，datacenter 正常——
/// 龙虎榜、大宗交易、分红、业绩预告全走它。排除东财的唯一理由没了，但判据一直没回头复查。
///
/// ════ 沙箱实测（2026-10-27）════
///   · 全表 256642 条，**带全部历史**；巨潮接口只给最近两期（想做"财报公布前后异动"回测，
///     巨潮那路拿不到历史，这里是白捡的）。
///   · 本期(2026-09-30) 5582 条 vs 巨潮 5585 条，差 3 条（0.05%）。并集方案里无所谓。
///   · 覆盖北交所：920970 大禹生物，<c>TRADE_MARKET=北京证券交易所</c>。
///   · 字段比巨潮全，多出 IS_PUBLISH（直接的已披露标记）、TRADE_MARKET、BOARD_NAME 等。
///
/// ⚠ <c>EITIME</c> 不是回填时间：格力是 2026-09-30 18:53:36、大禹生物 2026-09-30 02:05:40，
///   都是**这张预约表当初建档**的时间，跟实际披露日什么时候被标上无关。别拿它判"谁更快"。
/// </summary>
public sealed class EastMoneyPrebookProvider
{
    private const string ReportName = "RPT_PUBLIC_BS_APPOIN";

    private readonly EastMoneyDataCenterClient _client;

    public string SourceName => "东财 datacenter";

    /// <summary>自己的播报 + 转发客户端的（限流暂停那类）。</summary>
    public event Action<string>? OnStatus;

    public EastMoneyPrebookProvider(EastMoneyDataCenterClient client)
    {
        _client = client;
        _client.OnStatus += s => OnStatus?.Invoke(s);
    }

    /// <summary>
    /// 抓某一期的全市场预约披露情况。
    ///
    /// ⚠ <b>排序必须能定唯一序</b>：本期 5582 行、pageSize 500 ⇒ 12 页，只按 REPORT_DATE 排的话
    /// 同一报告期内几千行之间没有确定次序，翻页会既重复又遗漏（限售解禁按单字段排丢过 0.6%，
    /// 龙虎榜席位那次更惨）。所以次键给 SECURITY_CODE——报告期+代码在这张表里是唯一的。
    /// </summary>
    public async Task<List<EarningsScheduleEmRow>> FetchAsync(
        DateTime period, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var filter = $"(REPORT_DATE='{period:yyyy-MM-dd}')";
        var rows = new List<EarningsScheduleEmRow>();
        int total = -1;

        await foreach (var el in _client.QueryAsync(
                           ReportName, filter, "REPORT_DATE,SECURITY_CODE",
                           onTotalCount: n => total = n, ct: ct))
        {
            var code = Str(el, "SECURITY_CODE");
            if (code.Length == 0) continue;

            rows.Add(new EarningsScheduleEmRow(
                Code: code,
                ReportPeriod: Date(el, "REPORT_DATE") ?? period,
                AppointDate: Date(el, "APPOINT_PUBLISH_DATE"),
                FirstAppointDate: Date(el, "FIRST_APPOINT_DATE"),
                Change1: Date(el, "FIRST_CHANGE_DATE"),
                Change2: Date(el, "SECOND_CHANGE_DATE"),
                Change3: Date(el, "THIRD_CHANGE_DATE"),
                ActualDate: Date(el, "ACTUAL_PUBLISH_DATE"),
                IsPublish: Int(el, "IS_PUBLISH"),
                ReportTypeName: NullIfEmpty(Str(el, "REPORT_TYPE_NAME")),
                Quarter: NullIfEmpty(Str(el, "QUARTER")),
                TradeMarket: NullIfEmpty(Str(el, "TRADE_MARKET")),
                SecurityType: NullIfEmpty(Str(el, "SECURITY_TYPE")),
                BoardName: NullIfEmpty(Str(el, "BOARD_NAME")),
                NewBoardCode: NullIfEmpty(Str(el, "NEW_BOARD_CODE")),
                ResidualDays: Int(el, "RESIDUAL_DAYS"),
                AppointChange: NullIfEmpty(Str(el, "APPOINT_CHANGE")),
                AppointChangeDays: Int(el, "APPOINT_CHANGE_DAYS"),
                InfoCode: NullIfEmpty(Str(el, "INFO_CODE")),
                Eitime: NullIfEmpty(Str(el, "EITIME"))));

            if (rows.Count % 1000 == 0)
                progress?.Report($"  东财预约日 {rows.Count}" + (total > 0 ? $"/{total}" : "") + " 条…");
        }

        // 接口报的总数跟实际拿到的对不上，多半是分页漏了——排序键那条坑的告警面，
        // 宁可吵一句也别让遗漏静默过去（见类注释）。
        if (total > 0 && rows.Count < total)
            OnStatus?.Invoke($"⚠ 东财预约日 {period:yyyy-MM-dd}：接口报 {total} 条、实际只拿到 {rows.Count} 条，"
                           + "可能有分页遗漏。");

        return rows;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

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

    private static int? Int(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : null,
            JsonValueKind.String => int.TryParse(v.GetString(), NumberStyles.Integer,
                                                 CultureInfo.InvariantCulture, out var i2) ? i2 : null,
            _ => null,
        };
    }

    /// <summary>东财返回的是 "2026-10-27 00:00:00"，只取日期部分。</summary>
    private static DateTime? Date(JsonElement el, string prop)
    {
        var s = Str(el, prop);
        if (s.Length < 10) return null;
        return DateTime.TryParseExact(s[..10], "yyyy-MM-dd",
                                      CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }
}
