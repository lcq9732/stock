using System.Globalization;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 公司档案（东财 <c>RPT_HSF9_BASIC_ORGINFO</c>，走 datacenter）。5634 家 A 股、14 页。
///
/// 直接用途是给客户/供应商做**实体消歧**：年报里写的是"福建时代星云科技有限公司"这种全称，
/// 本地只有简称"宁德时代"，对不上；有了全称才能把对手方还原成股票代码。
/// 但档案本身也有价值——省份、员工数、实控人、主营业务、中介机构，
/// 这些是**同一个请求一起带回来的**，存下来不额外花抓取成本。
///
/// ⚠ 全表 7000 行里有港股等非 A 股，按 SECUCODE 后缀过滤，只留 SH/SZ/BJ（实测剩 5634）。
/// </summary>
public class EastMoneyCompanyProfileProvider
{
    private const string Report = "RPT_HSF9_BASIC_ORGINFO";

    private readonly EastMoneyDataCenterClient _dc;

    public event Action<string>? OnStatus;

    public EastMoneyCompanyProfileProvider(EastMoneyDataCenterClient dc)
    {
        _dc = dc;
        _dc.OnStatus += s => OnStatus?.Invoke(s);
    }

    /// <summary>
    /// 拉全部档案，**流式产出**。5634 家、14 页，一页一批正合适。
    /// </summary>
    /// <param name="onReported">接口自报的总行数（含港股等非 A 股）。</param>
    public async IAsyncEnumerable<IReadOnlyList<(CompanyProfile Profile, CompanyNarrative Narrative)>>
        StreamAsync(Action<int> onReported, int batchSize = 500,
                    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var buf = new List<(CompanyProfile, CompanyNarrative)>(batchSize);

        // 排序键就是主键本身，天然唯一——这张表一股一行，不存在"同主体多行"的翻页坑
        await foreach (var el in _dc.QueryAsync(Report, sortColumns: "SECUCODE", descending: false,
                                                onTotalCount: onReported, ct: ct))
        {
            if (Parse(el, DateTime.Now) is not { } row) continue;
            buf.Add(row);
            if (buf.Count >= batchSize)
            {
                yield return buf;
                buf = new List<(CompanyProfile, CompanyNarrative)>(batchSize);
            }
        }
        if (buf.Count > 0) yield return buf;
    }

    /// <summary>
    /// 解析一行。非 A 股（港股等后缀）或没有代码的返回 null。
    /// 抽成静态是为了能拿真实 JSON 做单测。
    /// </summary>
    public static (CompanyProfile Profile, CompanyNarrative Narrative)? Parse(
        JsonElement el, DateTime fetchedAt)
    {
        var code = Str(el, "SECURITY_CODE");
        if (code.Length != 6 || !code.All(char.IsDigit)) return null;

        // 全表含港股等，按后缀过滤
        var secu = Str(el, "SECUCODE");
        if (!secu.EndsWith(".SH", StringComparison.OrdinalIgnoreCase)
            && !secu.EndsWith(".SZ", StringComparison.OrdinalIgnoreCase)
            && !secu.EndsWith(".BJ", StringComparison.OrdinalIgnoreCase)) return null;

        var p = new CompanyProfile
        {
            Code = code,
            FullName = Str(el, "ORG_NAME"),
            Abbr = Str(el, "SECURITY_NAME_ABBR"),
            NameEn = Str(el, "ORG_NAME_EN"),
            OrgForm = Str(el, "ORG_FORM"),
            FoundDate = Str(el, "FOUND_DATE"),
            ListingDate = Str(el, "LISTING_DATE"),
            ListingState = Str(el, "LISTING_STATE"),
            // ⚠ 这两个差 1 万倍：REG_CAPITAL 是万元、REG_CAPITALY 是元。
            //   宁德时代 462677.041 万 = 4626770410 元。列名分别叫 _wan 和不带后缀，别搞反。
            RegCapitalWan = Num(el, "REG_CAPITAL"),
            RegCapital = Num(el, "REG_CAPITALY"),
            Currency = Str(el, "CURRENCY"),
            Province = Str(el, "PROVINCE"),
            City = Str(el, "CITY"),
            District = Str(el, "DISTRICT"),
            RegAddress = Str(el, "REG_ADDRESS"),
            Address = Str(el, "ADDRESS"),
            Postcode = Str(el, "ADDRESS_POSTCODE"),
            IndustryCsrc = Str(el, "INDUSTRYCSRC1"),
            EmpNum = (int?)Num(el, "EMP_NUM"),
            LegalPerson = Str(el, "LEGAL_PERSON"),
            ActualHolder = Str(el, "ACTUAL_HOLDER"),
            FinalHolder = Str(el, "FINAL_HOLDER"),
            HolderName = Str(el, "HOLDER_NAME"),
            HolderRatio = Num(el, "HOLDER_NUM_RATIO"),
            Chairman = Str(el, "CHAIRMAN"),
            President = Str(el, "PRESIDENT"),
            Secretary = Str(el, "SECRETARY"),
            PublishPerson = Str(el, "PUBLISH_PERSON"),
            SecretaryTel = Str(el, "SECRETARY_TEL"),
            OrgTel = Str(el, "ORG_TEL"),
            OrgFax = Str(el, "ORG_FAX"),
            OrgEmail = Str(el, "ORG_EMAIL"),
            OrgWeb = Str(el, "ORG_WEB"),
            RegNum = Str(el, "REG_NUM"),
            LawFirm = Str(el, "LAW_FIRM"),
            AccountFirm = Str(el, "ACCOUNTFIRM_NAME"),
            Cpa = Str(el, "CPA"),
            AhChange = Str(el, "AH_CHANGE"),
            MainBusiness = Str(el, "MAIN_BUSINESS"),
            OrgCodeEm = Str(el, "ORG_CODE"),
            ReportDate = Str(el, "REPORT_DATE"),
            FetchedAt = fetchedAt,
        };

        var n = new CompanyNarrative
        {
            Code = code,
            OrgProfile = Str(el, "ORG_PROFILE"),
            OrgEvolution = Str(el, "ORG_EVOLUTION"),
            BusinessScope = Str(el, "BUSINESS_SCOPE"),
            BusinessReview = Str(el, "BUSINESS_REVIEW"),
            FetchedAt = fetchedAt,
        };

        return (p, n);
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
}
