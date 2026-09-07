namespace StockPlatform.Logic.Models;

/// <summary>板块类型：概念/题材、行业分类、地域。</summary>
public enum BoardType
{
    Concept,   // 概念/题材（如"存储芯片""液冷服务器"）
    Industry,  // 行业分类
    /// <summary>
    /// 地域板块（如"广东板块""浙江板块"），2026-09-06 加。
    ///
    /// 为什么以前没有：原来的两个数据源都给不了——push2 要按 <c>t:1</c> 单独抓一轮，
    /// 菜单 JSON（sidemenu_new.json）里压根没有这一类。现在成分股和名单都走东财终端
    /// 落在本地的那份文件，地域板块本来就在里面（31 个、5,556 条成分），白捡的。
    ///
    /// ⚠ 加了这个值之后，凡是 <c>type == Concept ? A : B</c> 这种二选一的写法都会把地域
    /// 错判成行业，而且编译器不提醒。中文名一律走 <see cref="BoardTypeNames.Label"/>，
    /// 遍历一律用 <c>Enum.GetValues&lt;BoardType&gt;()</c>，别再写死两个。
    /// </summary>
    Region,
}

/// <summary>
/// 板块类型的中文名。散在各处的 <c>type == Concept ? "概念" : "行业"</c> 统一到这儿
/// （2026-09-06 加地域时）：那种写法在只有两类时没毛病，加第三类之后每一处都会把地域
/// 显示成"行业"——而且编译器一声不吭。
/// </summary>
public static class BoardTypeNames
{
    public static string Label(this BoardType t) => t switch
    {
        BoardType.Concept => "概念/题材",
        BoardType.Industry => "行业",
        BoardType.Region => "地域",
        _ => t.ToString(),
    };
}

/// <summary>
/// 一个板块在某一时刻的行情快照（新浪板块接口）——板块自己的涨跌幅/成交额是数据商算好的板块口径，
/// 我们本地没法准确复算，所以直接存下来。成分股仅存代码（MemberCodes），名称/最新行情由分析程序
/// 用本地的 StockMeta / 日K 现查，避免存一份易过期的成分股行情。
/// </summary>
public class Board
{
    public string BoardCode { get; set; } = "";   // 东财板块代码，如 BK1137
    public BoardType Type { get; set; }
    public string Name { get; set; } = "";
    public int MemberCount { get; set; }
    public double ChangePct { get; set; }          // 板块涨跌幅（%）
    public double Amount { get; set; }             // 板块合计成交额（元）
    public string LeaderCode { get; set; } = "";   // 领涨股代码（已去掉 sh/sz/bj 前缀）
    public string LeaderName { get; set; } = "";
    public DateTime AsOf { get; set; }             // 这份快照的抓取时刻

    /// <summary>成分股代码列表——只在抓取/写入时用来落 BoardMember 表，读出来的 Board 不填。</summary>
    public List<string> MemberCodes { get; set; } = new();
}
