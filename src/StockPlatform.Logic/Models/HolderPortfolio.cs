namespace StockPlatform.Logic.Models;

/// <summary>
/// 某个股东（按名称精确匹配）在本地库里出现过的全部股票——"其他数据"窗口点股东名称时打开
/// （读取见 SqliteHolderPortfolioReader，界面见 HolderPortfolioWindow）。
///
/// ⚠ 只能按**名称**认人：自然人同名极常见（「刘洋」在 40 只票的十大流通股东里出现），这里分不出
///   是不是同一个人，所以 <see cref="IsNaturalPerson"/> 为真时界面要明确提示。
/// </summary>
public class HolderPortfolio
{
    public string HolderName { get; init; } = "";

    /// <summary>TopShareholder.kind：float=十大流通股东，total=十大股东。</summary>
    public string Kind { get; init; } = TopShareholderRow.KindFloat;

    /// <summary>这个名字的任一行股本性质含"自然人"——同名不等于同一人，要提示。</summary>
    public bool IsNaturalPerson { get; init; }

    /// <summary>每只票一项；顺序：仍在前十的在前、已退出的在后，各自按占比倒序。</summary>
    public List<HolderPosition> Positions { get; init; } = new();
}

/// <summary>股东在一只票上的持仓（取该股东在这只票上**最近一次**出现的那期）。</summary>
public class HolderPosition
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>该股东最近一次出现在这只票前十名单里的报告期。</summary>
    public string Period { get; init; } = "";

    /// <summary>这只票本地最新的报告期。跟 <see cref="Period"/> 不同 = 股东已经掉出前十。</summary>
    public string StockLatestPeriod { get; init; } = "";

    public bool Exited => !string.Equals(Period, StockLatestPeriod, StringComparison.Ordinal);

    public string StatusText => Exited ? $"已退出前十（本票最新一期 {StockLatestPeriod}）" : "仍在前十";

    public int Rank { get; init; }
    public double Shares { get; init; }

    /// <summary>占比(%)：kind=float 是占流通股，kind=total 是占总股本。</summary>
    public double Ratio { get; init; }

    public string ShareType { get; init; } = "";

    /// <summary>这只票上的明细，日期倒序：各期前十名单里的持仓 + 增减持公告。</summary>
    public List<HolderActivity> History { get; init; } = new();
}

/// <summary>一条明细。值已格式化成字符串（两类来源的字段不一样，统一成一张表显示）。</summary>
public class HolderActivity
{
    /// <summary>日期：季报行是报告期，公告行是变动截止日。</summary>
    public string Date { get; init; } = "";

    /// <summary>"前十名单" / "增减持公告"。</summary>
    public string Source { get; init; } = "";

    /// <summary>动作：新进 / 增持 / 减持 / 不变 / 退出前十 / 公告里的增持/减持。</summary>
    public string Action { get; init; } = "";

    public string Change { get; init; } = "";
    public string Shares { get; init; } = "";
    public string Ratio { get; init; } = "";
    public string Rank { get; init; } = "";
    public string Price { get; init; } = "";
    public string Note { get; init; } = "";
}
