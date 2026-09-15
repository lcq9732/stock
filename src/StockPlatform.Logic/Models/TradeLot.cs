using System.Text.Json.Serialization;

namespace StockPlatform.Logic.Models;

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

    /// <summary>
    /// 这一笔为什么成交，取值见 <see cref="TradeReasons"/>（2026-09-14 新增）。
    ///
    /// ⚠ 记它是为了**半年后能做归因**：把收益拆成"纪律带来的"和"判断带来的"。
    ///   等数据攒够了再回头补是补不了的——人想不起来那笔到底是"到 +2% 止盈走的"
    ///   还是"看着不对提前跑的"，而这两者正是要区分的东西。
    ///
    /// 老数据是空串（当时没这个字段），界面显示"未填"。**不强制回填**：
    /// 凭记忆填的归因还不如空着，空着至少知道自己不知道。
    /// </summary>
    public string Reason { get; set; } = "";

    /// <summary>
    /// 自由备注，补充 <see cref="Reason"/> 说不清的细节（"财报前减半仓"这种）。
    /// 码用来分组统计，这里记人话——只有自由文本的话半年后统计不了
    /// （"到点了"/"到2%"/"止盈"是同一件事的三种写法）。
    /// </summary>
    public string Note { get; set; } = "";
}
