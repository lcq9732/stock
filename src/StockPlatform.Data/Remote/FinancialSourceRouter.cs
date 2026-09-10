using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 财务报表的**按机构类型分流**（2026-09-10）：保险公司走新浪，其余走东财。
///
/// ════ 为什么保险要单开一条 ════
/// 东财 F10 **整组不填**保险公司的支出科目——2026-09-10 查实：平安/国寿/太保三家 ×
/// <c>GINCOME.NET_COMPENSATE_EXPENSE</c> / <c>IINCOME.COMPENSATE_EXPENSE</c> /
/// <c>RPT_DMSK_FN_INCOME.COMPENSATE_EXPENSE</c> 三处**全是 null**；退保金、保单红利、
/// 分保费用同样全空。东财把它们并进了营业支出（平安 OPERATE_EXPENSE 444,310,000,000
/// 含着赔付支出 221,773,000,000），不拆开。
///
/// 而 <c>claim_expense</c> 有真实消费方：<c>BrokerInsurerHealthCheckBuilder</c> 第 03 条的
/// **赔付率**（赔付支出 ÷ 已赚保费）+ 同比。同一份体检里从年报 PDF 解析的综合成本率虽然口径更权威，
/// 但只有 3 家 × 10 期（2024 年起），而新浪这条是 5 家 × 392 期（1997 年起）——
/// 那条指标自己写着"只适合自比趋势"，而自比趋势恰恰要长历史。**替代不了，所以不能丢。**
///
/// ════ 分流是"整只票"，不是"按字段拼" ════
/// 一只票的所有科目仍来自同一个源，<c>FinancialReport</c> 里不会出现"利润表东财、赔付新浪"
/// 这种混源行——那种拼法会让同一期的科目之间口径不一致，而且事后分不出来。
///
/// ════ 判据：ORG_TYPE 为主，内置名单只是省一个请求 ════
/// 名单命中就直接走新浪（省掉那次注定要丢弃的东财请求）；名单外的票照常问东财，
/// 东财一旦报 <c>ORG_TYPE=保险</c> 就改道并**告警**（说明 A 股新上了保险公司、名单该更新）。
/// 这样名单过时不会造成数据错误，只会多花一个请求。
/// </summary>
public class FinancialSourceRouter : IFinancialProvider
{
    private readonly EastMoneyFinancialProvider _eastMoney;
    private readonly SinaFinancialProvider _sina;

    /// <summary>两个 provider 的状态都转发到这里，路由器自己的提示也从这里出去——
    /// 订阅者只认一个事件源，不用知道背后有两条路。</summary>
    public event Action<string>? OnStatus;

    public FinancialSourceRouter(EastMoneyFinancialProvider eastMoney, SinaFinancialProvider sina)
    {
        _eastMoney = eastMoney;
        _sina = sina;
        eastMoney.OnStatus += m => OnStatus?.Invoke(m);
        sina.OnStatus += m => OnStatus?.Invoke(m);
    }

    /// <summary>
    /// A 股保险公司（2026-09-10）。**只是加速用**，判准是东财的 <c>ORG_TYPE</c>——
    /// 名单漏了谁，那只票会先问一次东财再改道，不会拿到错数据。
    /// </summary>
    private static readonly HashSet<string> KnownInsurers = new(StringComparer.Ordinal)
    {
        "601318", // 中国平安
        "601601", // 中国太保
        "601628", // 中国人寿
        "601336", // 新华保险
        "601319", // 中国人保
    };

    public async Task<List<FinancialValue>> GetAllAsync(string code, CancellationToken ct = default)
    {
        if (KnownInsurers.Contains(code)) return await FetchInsurerAsync(code, "内置名单", ct);

        var (orgType, rows) = await _eastMoney.FetchWithOrgTypeAsync(code, ct);
        if (orgType == EastMoneyFinancialProvider.OrgTypeInsurer)
            return await FetchInsurerAsync(code, "东财 ORG_TYPE=保险，内置名单里没有它", ct);

        return rows;
    }

    /// <summary>
    /// 保险走新浪，并**校验特征科目非空**——这 5 家仍吃着中文行名匹配的风险（版式一改就静默丢科目），
    /// 而它们正是唯一还依赖那条路的票，不盯着点就没人发现。
    /// </summary>
    private async Task<List<FinancialValue>> FetchInsurerAsync(string code, string why, CancellationToken ct)
    {
        var rows = await _sina.GetAllAsync(code, ct);

        bool hasPremium = rows.Any(r => r.Key == FinancialKeys.PremiumEarned);
        bool hasClaim = rows.Any(r => r.Key == FinancialKeys.ClaimExpense);
        if (!hasPremium || !hasClaim)
        {
            var missing = (!hasPremium ? "已赚保费 " : "") + (!hasClaim ? "赔付支出" : "");
            OnStatus?.Invoke($"⚠ 保险股 {code} 走新浪抓回 {rows.Count} 条，但缺 {missing.Trim()}——" +
                          "多半是新浪页面版式变了（中文行名匹配失效），赔付率指标会失真，请检查 SinaFinancialProvider 的行名表");
        }
        else
        {
            OnStatus?.Invoke($"保险股 {code} 走新浪（{why}）：{rows.Count} 条，含赔付支出与已赚保费");
        }
        return rows;
    }

}
