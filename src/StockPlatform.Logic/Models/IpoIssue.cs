using System.Globalization;

namespace StockPlatform.Logic.Models;

/// <summary>新股发行表一列的类型（按 2026-10-16 全量 5,640 行实际出现过的值定的）。</summary>
public enum IpoColumnKind
{
    /// <summary>数值（存 REAL）。</summary>
    Number,

    /// <summary>文本（存 TEXT，原样）。</summary>
    Text,

    /// <summary>
    /// 日期。接口给 "yyyy-MM-dd 00:00:00"，存成 yyyy-MM-dd；**带具体时刻的原样保留**
    /// （OFFLINE_PLACING__DATE 在 2006~2008 年的老股里有 334 条是 "2007-04-05 09:00:00" 这种，截掉就丢信息）。
    /// </summary>
    Date,
}

/// <summary>新股发行表的一列：东财原名 + 类型。库里的列名是原名转小写（SECURITY_CODE 例外，存成主键 code）。</summary>
public sealed record IpoColumn(string Api, IpoColumnKind Kind)
{
    /// <summary>库里的列名。</summary>
    public string Db => Api == IpoIssueColumns.CodeColumn ? "code" : Api.ToLowerInvariant();
}

/// <summary>
/// 东财新股发行表（<c>RPTA_APP_IPOAPPLY</c>）的**全部 151 列**（2026-10-16，见 doc/ipo-issue-design.md）。
///
/// 用户定"取到的全存"：每一列都有对应字段，顺序跟接口一致。列表是拿 2026-10-16 拉的全量响应生成的，
/// 不是手抄的。接口以后多出来的列存不进来——任务会在日志里点名，见 <c>IpoIssueTask</c>。
/// </summary>
public static class IpoIssueColumns
{
    /// <summary>主键那一列（6 位代码）。</summary>
    public const string CodeColumn = "SECURITY_CODE";

    public static readonly IReadOnlyList<IpoColumn> All =
    [
        new("SECUCODE", IpoColumnKind.Text),
        new("SECURITY_CODE", IpoColumnKind.Text),
        new("APPLY_CODE", IpoColumnKind.Text),
        new("APPLY_DATE", IpoColumnKind.Date),
        new("LISTING_DATE", IpoColumnKind.Date),
        new("TRADE_MARKET", IpoColumnKind.Text),
        new("MARKET_TYPE_NEW", IpoColumnKind.Text),
        new("BALLOT_PAY_DATE", IpoColumnKind.Date),
        new("ASSIGN_DATE", IpoColumnKind.Date),
        new("ONLINE_ISSUE_DATE", IpoColumnKind.Date),
        new("OFFLINE_PLACING__DATE", IpoColumnKind.Date),
        new("BALLOT_NUM_DATE", IpoColumnKind.Date),
        new("ISSUE_PRICE", IpoColumnKind.Number),
        new("PREDICT_ISSUE_PRICE", IpoColumnKind.Number),
        new("PREDICT_ISSUE_PRICE1", IpoColumnKind.Number),
        new("PREDICT_ISSUE_PRICE2", IpoColumnKind.Number),
        new("ISSUE_NUM", IpoColumnKind.Number),
        new("ONLINE_ISSUE_NUM", IpoColumnKind.Number),
        new("ONLINE_APPLY_UPPER", IpoColumnKind.Number),
        new("AFTER_ISSUE_PE", IpoColumnKind.Number),
        new("PREDICT_ISSUE_PE", IpoColumnKind.Number),
        new("PREDICT_PE", IpoColumnKind.Number),
        new("INDUSTRY_PE_RATIO", IpoColumnKind.Number),
        new("INDUSTRY_PE", IpoColumnKind.Number),
        new("ONLINE_ISSUE_LWR", IpoColumnKind.Number),
        new("TOTAL_RAISE_FUNDS", IpoColumnKind.Number),
        new("ISSUE_WAY", IpoColumnKind.Text),
        new("INDUSTRY_CODE", IpoColumnKind.Text),
        new("OFFLINE_VAP_RATIO", IpoColumnKind.Number),
        new("OFFLINE_VAS_MULTIPLE", IpoColumnKind.Number),
        new("ONLINE_VA_NUM", IpoColumnKind.Number),
        new("OFFLINE_VAP_OBJECT", IpoColumnKind.Number),
        new("ONLINE_VA_SHARES", IpoColumnKind.Number),
        new("OFFLINE_VATS", IpoColumnKind.Number),
        new("OFFLINE_PLACING_UPPER", IpoColumnKind.Number),
        new("BALLOT_NUM", IpoColumnKind.Text),
        new("TOTAL_APPLY_NUM", IpoColumnKind.Number),
        new("INITIAL_MULTIPLE", IpoColumnKind.Number),
        new("OFFLINE_PLACING_NUM", IpoColumnKind.Number),
        new("TOP_APPLY_MARKETCAP", IpoColumnKind.Number),
        new("MZYQ_PAY_FUND", IpoColumnKind.Number),
        new("INDUSTRY_NAME", IpoColumnKind.Text),
        new("MARKET_CAP_CONFIRMDATE", IpoColumnKind.Text),
        new("OPEN_PRICE", IpoColumnKind.Number),
        new("CLOSE_PRICE", IpoColumnKind.Number),
        new("TURNOVERRATE", IpoColumnKind.Number),
        new("LD_CLOSE_CHANGE", IpoColumnKind.Number),
        new("LD_OPEN_PREMIUM", IpoColumnKind.Number),
        new("LD_HIGH_CHANG", IpoColumnKind.Number),
        new("OPEN_AVERAGE_PRICE", IpoColumnKind.Number),
        new("OPEN_DATE", IpoColumnKind.Date),
        new("BVPS", IpoColumnKind.Number),
        new("SECURITY_NAME", IpoColumnKind.Text),
        new("TOTAL_CHANGE", IpoColumnKind.Number),
        new("PROFIT", IpoColumnKind.Number),
        new("CONTINUOUS_1WORD_NUM", IpoColumnKind.Text),
        new("MARKET_TYPE", IpoColumnKind.Text),
        new("LIMIT_UP_PRICE", IpoColumnKind.Number),
        new("ORG_TYPE", IpoColumnKind.Text),
        new("TRADE_MARKET_CODE", IpoColumnKind.Text),
        new("CHANGE_RATE", IpoColumnKind.Number),
        new("LATELY_PRICE", IpoColumnKind.Number),
        new("CHANGE", IpoColumnKind.Number),
        new("IS_BALLOT_NUM", IpoColumnKind.Number),
        new("PE_TTM", IpoColumnKind.Number),
        new("OPEN_CHANGERATE", IpoColumnKind.Number),
        new("SECURITY_TYPE_CODE", IpoColumnKind.Text),
        new("CONTINUOUS_N1WORD_NUM", IpoColumnKind.Text),
        new("SECURITY_INNER_CODE", IpoColumnKind.Text),
        new("KB_TRADE_DATE", IpoColumnKind.Date),
        new("AVERAGE_PRICE", IpoColumnKind.Number),
        new("PREDICT_PE_THREE", IpoColumnKind.Number),
        new("MARKET", IpoColumnKind.Text),
        new("ONLINE_APPLY_PRICE", IpoColumnKind.Number),
        new("ONLINE_FUND_UPPER", IpoColumnKind.Number),
        new("FREECAP_AVG_LOWER", IpoColumnKind.Number),
        new("OFFLINE_EP_OBJECT", IpoColumnKind.Number),
        new("INFO_CODE", IpoColumnKind.Text),
        new("START_DATE", IpoColumnKind.Date),
        new("MAIN_BUSINESS", IpoColumnKind.Text),
        new("EACHBALLOT_SHARES", IpoColumnKind.Text),
        new("UP_DATE", IpoColumnKind.Date),
        new("PREDICT_ONAPPLY_UPPER", IpoColumnKind.Number),
        new("PREDICT_ONFUND_UPPER", IpoColumnKind.Number),
        new("PAR_VALUE", IpoColumnKind.Number),
        new("ORIG_TRANSFER_NUM", IpoColumnKind.Number),
        new("FREECAP_AVG_DATE", IpoColumnKind.Date),
        new("PAGE_PREDICT_PRICE1", IpoColumnKind.Number),
        new("PAGE_PREDICT_PRICE2", IpoColumnKind.Number),
        new("PAGE_PREDICT_PRICE3", IpoColumnKind.Number),
        new("PAGE_PREDICT_PE1", IpoColumnKind.Number),
        new("PAGE_PREDICT_PE2", IpoColumnKind.Number),
        new("PAGE_PREDICT_PE3", IpoColumnKind.Number),
        new("LD_AVERAGE_PRICE", IpoColumnKind.Number),
        new("IS_VALID", IpoColumnKind.Text),
        new("APPLY_DATE_NUM", IpoColumnKind.Number),
        new("INDUSTRY_PE_NEW", IpoColumnKind.Number),
        new("CONTINUOUS_1WORD_PX", IpoColumnKind.Number),
        new("TOTAL_ISSUE_NUM", IpoColumnKind.Number),
        new("ONLINE_ES_MULTIPLE", IpoColumnKind.Number),
        new("ISSUE_WAY_EXPLAIN", IpoColumnKind.Text),
        new("PRICE_WAY", IpoColumnKind.Text),
        new("RESULT_NOTICE_DATE", IpoColumnKind.Date),
        new("SELECT_LISTING_DATE", IpoColumnKind.Date),
        new("ONLINE_PAY_DATE", IpoColumnKind.Date),
        new("ONLINE_REFUND_DATE", IpoColumnKind.Date),
        new("IS_BEIJING", IpoColumnKind.Number),
        new("PER_SHARES_INCOME", IpoColumnKind.Number),
        new("ONLINE_ISSUE_RATIO", IpoColumnKind.Number),
        new("OFFFLINE_INITIAL_MULTIPLE", IpoColumnKind.Number),
        new("NEWEST_PRICE", IpoColumnKind.Number),
        new("ORG_CODE", IpoColumnKind.Text),
        new("TYPE", IpoColumnKind.Text),
        new("AMPLITUDE", IpoColumnKind.Number),
        new("REVIEW_DATE", IpoColumnKind.Date),
        new("PREDICT_RAISE_FUNDS", IpoColumnKind.Number),
        new("TOTAL_SHARES", IpoColumnKind.Number),
        new("RECOMMEND_ORG", IpoColumnKind.Text),
        new("UNDERWRITER_ORG", IpoColumnKind.Text),
        new("SECURITY_NAME_FULL", IpoColumnKind.Text),
        new("ISSUE_STATE", IpoColumnKind.Text),
        new("NETFLOWIN", IpoColumnKind.Number),
        new("IPO_YIELD", IpoColumnKind.Number),
        new("FREEZE_FUND", IpoColumnKind.Number),
        new("BOARD_CODE", IpoColumnKind.Text),
        new("IS_REGISTRATION", IpoColumnKind.Text),
        new("TRADE_MARKET_CST", IpoColumnKind.Text),
        new("CURRENT_ONEWORD_NUM", IpoColumnKind.Number),
        new("IS_UNOPEN", IpoColumnKind.Number),
        new("CONTINUOUS_ONEWORD_NUM", IpoColumnKind.Number),
        new("OD_HIGH_CHANGE", IpoColumnKind.Number),
        new("SUSPENSION_STATE", IpoColumnKind.Text),
        new("REGISTERED_TYPE", IpoColumnKind.Text),
        new("IS_1YEAR", IpoColumnKind.Number),
        new("IS_CALENDAR_DATA", IpoColumnKind.Text),
        new("SECURITY_NAME_ABBR", IpoColumnKind.Text),
        new("OFFLINE_VATS_NONUNIT", IpoColumnKind.Number),
        new("ONLINE_VASHARES_NONUNIT", IpoColumnKind.Number),
        new("ENQUIRY_SHARE_NUM", IpoColumnKind.Number),
        new("IS_RISKWARNING", IpoColumnKind.Number),
        new("NETSUMFINA", IpoColumnKind.Number),
        new("DEC_SUMFINA", IpoColumnKind.Number),
        new("TNEW_PRICE", IpoColumnKind.Number),
        new("TCHANGE_RATE", IpoColumnKind.Number),
        new("IS_REGISTRATION_NEW", IpoColumnKind.Text),
        new("IS_PROFIT", IpoColumnKind.Text),
        new("STAR_SIGN", IpoColumnKind.Text),
        new("ONLINE_APPLY_LOWER", IpoColumnKind.Number),
        new("APPLY_AMT_UPPER", IpoColumnKind.Number),
        new("OFFFLINE_INITIAL_MULTIPLE_BJ", IpoColumnKind.Number),
        new("IS_1YEAR_APPLY", IpoColumnKind.Number),
    ];

    /// <summary>
    /// **随时间变化**的列：存的是抓取那一刻的值，每次抓取都会被覆盖（2026-10-16 拿本地收盘价对过：
    /// TNEW_PRICE/LATELY_PRICE 是盘中实时价，UP_DATE 是抓取当天，APPLY_DATE_NUM 是申购至今的天数）。
    /// 别拿它们当历史数据用。OPEN_PRICE/CLOSE_PRICE/TURNOVERRATE 这类是**上市首日**的，不会变。
    /// </summary>
    public static readonly IReadOnlySet<string> Volatile = new HashSet<string>(StringComparer.Ordinal)
    {
        "NEWEST_PRICE", "LATELY_PRICE", "TNEW_PRICE", "CHANGE", "CHANGE_RATE", "TCHANGE_RATE",
        "PE_TTM", "UP_DATE", "APPLY_DATE_NUM", "IS_1YEAR", "IS_1YEAR_APPLY",
    };
}

/// <summary>
/// 一只新股的一行：151 列的值按东财原名索引（数值是 double，文本/日期是 string，空就是 null）。
/// 不做成 151 个属性的 record——列目录已经在 <see cref="IpoIssueColumns"/> 里，存取都按目录走，
/// 只把要用的那两列包成属性。
/// </summary>
public sealed class IpoIssueRow
{
    public required string Code { get; init; }

    /// <summary>东财原名 → 值。</summary>
    public required IReadOnlyDictionary<string, object?> Values { get; init; }

    public object? this[string api] => Values.TryGetValue(api, out var v) ? v : null;

    public string? Name => this["SECURITY_NAME"] as string;

    /// <summary>发行价（元）。</summary>
    public double? IssuePrice => this["ISSUE_PRICE"] as double?;

    /// <summary>上市日；还没上市的为 null。</summary>
    public DateOnly? ListingDate =>
        this["LISTING_DATE"] is string s && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;
}
