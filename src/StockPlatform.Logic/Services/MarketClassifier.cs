namespace StockPlatform.Logic.Services;

/// <summary>A-share market board (see doc/data-platform-design.md — used both to build the
/// correct vendor-specific request prefix for bar fetching, and to show "板块" in analysis
/// results). Deliberately code-only — this is a pure function of the 6-digit code, no lookup
/// needed.</summary>
public enum MarketBoard
{
    ShanghaiMain,
    ShanghaiStar,    // 科创板
    ShanghaiB,
    ShenzhenMain,
    ShenzhenChiNext, // 创业板
    ShenzhenB,
    Beijing,         // 北交所
    Unknown,
}

/// <summary>
/// Classifies a 6-digit A-share code into its market board purely from the code prefix.
///
/// 北交所 (Beijing) is the one that's easy to get wrong: since the market's 2024-2025 "920代码"
/// migration (fully rolled out by 2025-10-09 — see the exchange's own announcements), essentially
/// all 北交所 stocks now use codes starting with "92", not the old 43/83/87 prefixes. Before this
/// class existed, <c>EastMoneyBarFetcher.ToSecId</c>/<c>TencentBarFetcher.ToSymbol</c> each had
/// their own copy of a prefix switch that mapped bare "9" to Shanghai — a reasonable
/// approximation back when "9" only meant the (tiny, mostly dead) 900xxx Shanghai B-share
/// segment, but wrong now that "92xxxx" 北交所 codes exist: those requests were going out with a
/// Shanghai secid/symbol and coming back "code doesn't exist". This class is the single place
/// that gets the distinction right, so both fetchers and any future display code — e.g. the
/// Analyzer's "板块" column — agree.
/// </summary>
public static class MarketClassifier
{
    public static MarketBoard Classify(string code)
    {
        code = code.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit)) return MarketBoard.Unknown;

        // Check Beijing first — "92" would otherwise fall through to the bare-'9' Shanghai case.
        if (code.StartsWith("92") || code.StartsWith("43") || code.StartsWith("83") || code.StartsWith("87"))
            return MarketBoard.Beijing;

        if (code.StartsWith("688")) return MarketBoard.ShanghaiStar;
        if (code.StartsWith("900")) return MarketBoard.ShanghaiB;
        if (code[0] is '6' or '9') return MarketBoard.ShanghaiMain;

        if (code.StartsWith("300") || code.StartsWith("301") || code.StartsWith("302")) return MarketBoard.ShenzhenChiNext;
        if (code.StartsWith("200")) return MarketBoard.ShenzhenB;
        if (code[0] is '0' or '3') return MarketBoard.ShenzhenMain;

        return MarketBoard.Unknown;
    }

    public static string DisplayName(MarketBoard board) => board switch
    {
        MarketBoard.ShanghaiMain => "上海主板",
        MarketBoard.ShanghaiStar => "科创板",
        MarketBoard.ShanghaiB => "上海B股",
        MarketBoard.ShenzhenMain => "深圳主板",
        MarketBoard.ShenzhenChiNext => "创业板",
        MarketBoard.ShenzhenB => "深圳B股",
        MarketBoard.Beijing => "北交所",
        _ => "未知",
    };

    public static string DisplayName(string code) => DisplayName(Classify(code));

    /// <summary>EastMoney secid market prefix. Their own stock-list filter (see
    /// EastMoneyStockListProvider's fs parameter) groups 北交所 under market "0" — the same
    /// number as 深市 — via "m:0+t:81+s:2048", not a distinct market id, so 北交所 uses "0." here
    /// too, same as Shenzhen.</summary>
    public static string EastMoneySecIdPrefix(string code) =>
        Classify(code) is MarketBoard.ShanghaiMain or MarketBoard.ShanghaiStar or MarketBoard.ShanghaiB ? "1." : "0.";

    public static string TencentSymbolPrefix(string code) => Classify(code) switch
    {
        MarketBoard.ShanghaiMain or MarketBoard.ShanghaiStar or MarketBoard.ShanghaiB => "sh",
        MarketBoard.Beijing => "bj",
        _ => "sz",
    };
}
