using System.Text.Json.Serialization;

namespace StockPlatform.Analyzer.Watchlist;

/// <summary>一笔成交的方向。用字符串序列化（"Buy"/"Sell"）——watchlist.json 是人可以直接打开看的
/// 本地文件，存数字看不出是买还是卖。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TradeSide
{
    Buy,
    Sell,
}

/// <summary>
/// 一笔实际成交（2026-08-11新增）——一条自选记录下面可以挂任意多笔买入和卖出，用来支持
/// **金字塔式建仓**（分几次买）和**分批止盈**（分几次卖）。界面上"买入/卖出"列显示的总股数和
/// 均价，都是把这些笔按股数加权汇总出来的（见 <see cref="WatchlistEntry"/> 的汇总属性）。
///
/// 老 JSON 里没有 Lots 这个字段：加载时按旧的单笔字段（BuyDate/BuyPrice/Shares/SellDate/
/// SellPrice）自动补成"一笔买入（+一笔卖出）"，见 <see cref="WatchlistEntry.MigrateLegacyLots"/>。
/// </summary>
public class TradeLot
{
    /// <summary>稳定标识——跟 <see cref="WatchlistEntry.Id"/> 同理：反序列化后对象是新实例，
    /// 不能靠引用相等找回"这一笔"。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public TradeSide Side { get; set; }

    public DateTime Date { get; set; }

    public double Price { get; set; }

    /// <summary>股数。从老数据迁移过来的可能是 0（当年只填了价格、没填股数）——汇总时按"股数未知"
    /// 处理：均价退化成各笔价格的简单平均，金额类结果不显示。新录入的强制要求 &gt; 0。</summary>
    public int Shares { get; set; }
}
