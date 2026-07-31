namespace StockPlatform.Logic.Models;

/// <summary>一只股票的一个分红方案（新浪 vISSUE_ShareBonus 分红派息页的一行）。金额/股数均为
/// **每10股**口径（数据源如此）：每股股息 = <see cref="DividendYuan"/> / 10。除权除息日/股权登记日
/// 在方案还没实施（进度=预案/董事会通过）时数据源给 "--"，这里存 null。</summary>
public class DividendRow
{
    /// <summary>6位股票代码。</summary>
    public string Code { get; set; } = "";

    /// <summary>公告日期——同一只股票同一公告日唯一，用作方案标识（主键的一部分）。</summary>
    public DateTime AnnounceDate { get; set; }

    /// <summary>送股：每10股送X股。</summary>
    public double BonusShares { get; set; }

    /// <summary>转增：每10股转增X股。</summary>
    public double TransferShares { get; set; }

    /// <summary>派息（税前）：每10股派X元。每股股息 = 这个值 / 10。</summary>
    public double DividendYuan { get; set; }

    /// <summary>进度：实施 / 预案 / 董事会通过 / 不分配 等。做股息率时通常只取"实施"的。</summary>
    public string? Progress { get; set; }

    /// <summary>股权登记日；方案未实施时为 null。</summary>
    public DateTime? RecordDate { get; set; }

    /// <summary>除权除息日；方案未实施时为 null。</summary>
    public DateTime? ExDate { get; set; }

    public DateTime FetchedAt { get; set; }
}
