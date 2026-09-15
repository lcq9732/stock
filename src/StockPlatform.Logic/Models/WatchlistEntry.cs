namespace StockPlatform.Logic.Models;

/// <summary>Frozen snapshot of one CriterionResult at pick time — kept alongside the entry so the
/// original "为什么选中"依据 doesn't change if the analysis logic changes later (StockScreenResult
/// itself isn't persisted, only this flattened copy).</summary>
public class CriterionSnapshot
{
    public string Name { get; set; } = "";
    public bool Satisfied { get; set; }
    public string Basis { get; set; } = "";

    /// <summary>Mirrors CriterionResult.DataMissing — kept so reopening a saved pick's "条件详情"
    /// still shows a skipped-for-missing-data condition as ⚠ rather than looking like it was
    /// actually checked and failed (see CriterionDisplay.From).</summary>
    public bool DataMissing { get; set; }
}

/// <summary>
/// One user-picked stock, saved so it can be tracked/reviewed later — see
/// doc/analysis-app-design.md section 3.5 "自选股跟踪". Deliberately NOT stored in the shared
/// current.sqlite (that file is Fetcher's output, read-only here, see AnalyzerPaths doc comment) — this
/// is the Analyzer's own local state, in its own JSON file (JsonWatchlistStore).
/// </summary>
public class WatchlistEntry
{
    /// <summary>Stable identity for removal — a fresh JSON deserialize gives back new object
    /// instances each time, so reference-equality can't be used to find "this entry" again once
    /// reloaded.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Code { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Which of the four methods picked this stock — "峰哥法"/"金叉法"/"耀哥法"/"彬哥法".
    /// Plain display name, not a code-level type reference, since this is what the user
    /// actually needs to see when reviewing picks later.</summary>
    public string Method { get; set; } = "";

    /// <summary>Only meaningful for 峰哥法 (user-adjustable); the other three methods are always
    /// "day" — kept anyway so re-opening the detail chart later queries the right granularity.</summary>
    public string Granularity { get; set; } = "";

    /// <summary>Only meaningful for 峰哥法 (its "回看N根K线" parameter, default 1) — null for the other three
    /// methods, which have no user-adjustable lookback. Used to rebuild the detail chart with the
    /// same BOLL/pattern-search window the pick was actually made under.</summary>
    public int? Lookback { get; set; }

    /// <summary>Only meaningful for 耀哥法 (its "DIF阈值" parameter) — null otherwise. Used to
    /// rebuild its detail chart's DIF-threshold reference line the same way it looked at pick
    /// time.</summary>
    public double? DifThreshold { get; set; }

    /// <summary>The latest bar's date the analysis was based on when this pick was made — i.e.
    /// "今天" as of pick time, NOT when the user clicked "加入自选" (see AddedAt for that). This is
    /// the actual "什么数据日期" the pick's numbers came from.</summary>
    public DateTime DataDate { get; set; }

    /// <summary>Closing price on DataDate — the price the pick was actually made at, for later
    /// comparing against where the stock is now.</summary>
    public double PriceAtPick { get; set; }

    public DateTime AddedAt { get; set; }

    /// <summary>这笔票的全部实际成交（2026-08-11新增）——买入可以有多笔（金字塔式建仓）、卖出也
    /// 可以有多笔（分批止盈），在"主动仓"Tab点【交易记录】录入。一条都没有=还没买、只是观察中；
    /// 有买入笔=真实持仓，每日晨检的止损/止盈纪律按**加权平均买入价**和**首次买入日**计算。
    ///
    /// 这里是唯一的事实来源，下面那五个单笔字段（BuyDate/BuyPrice/Shares/SellDate/SellPrice）
    /// 从 2026-08-11 起降级为**由本列表汇总出来的冗余快照**，只为兼容老数据/老版本读取，
    /// 见 <see cref="SyncLegacyFromLots"/>。</summary>
    public List<TradeLot> Lots { get; set; } = new();

    /// <summary>【兼容字段，勿直接写】首次买入日期——新代码用 <see cref="FirstBuyDate"/>。
    /// 2026-07-29 加入时是手动录入的单笔买入日期，2026-08-11 改成多笔后由 <see cref="Lots"/> 汇总，
    /// 仍然序列化到 JSON 里，这样老版本 exe 读同一份 watchlist.json 也还能显示出持仓。</summary>
    public DateTime? BuyDate { get; set; }

    /// <summary>【兼容字段，勿直接写】加权平均买入价——新代码用 <see cref="AvgBuyPrice"/>。见 <see cref="BuyDate"/>。</summary>
    public double? BuyPrice { get; set; }

    /// <summary>【兼容字段，勿直接写】股数——未平仓时是**剩余持仓股数**、已平仓时是买入总股数
    /// （这样老版本那套"(现价-买入价)×股数"的算法两种情况都还对得上）。新代码用
    /// <see cref="RemainingShares"/>/<see cref="TotalBuyShares"/>。</summary>
    public int? Shares { get; set; }

    /// <summary>【兼容字段，勿直接写】最后一笔卖出日期，**只在全部卖完（已平仓）时才有值**——
    /// 部分卖出留 null，否则老版本会把"还拿着一半"的仓位误判成已平仓。新代码用 <see cref="LastSellDate"/>。</summary>
    public DateTime? SellDate { get; set; }

    /// <summary>【兼容字段，勿直接写】加权平均卖出价，同样只在已平仓时才有值——见 <see cref="SellDate"/>。
    /// 新代码用 <see cref="AvgSellPrice"/>。</summary>
    public double? SellPrice { get; set; }

    /// <summary>下一次财报的披露日期（2026-08-17新增，"主动仓"页手填）——交易所/公司预约的披露日，
    /// 本地数据库里没有这个信息（<c>FinancialReport</c> 只有已经披露的报告期），所以只能手工录。
    ///
    /// 用途：**跨财报持仓是短线法/回调法回测里没有的风险**——那些参数是按普通交易日回测出来的，没有
    /// 区分财报窗口；预期打得越满，兑现日越容易利好出尽。填了以后每日晨检会在临近时提醒（见
    /// MorningStockRowViewModel 的财报提醒），披露完还会提醒去跑一次季度抓取，把新报告期入库。</summary>
    public DateTime? EarningsDate { get; set; }

    /// <summary>是否放进"主动仓"（2026-07-31新增）
    ///
    /// ⚠ 命名对不上是历史遗留：这一族标识符里的 <c>TradePool</c>（本属性、<c>IsInTradePool</c>、
    /// <c>AddToTradePoolCommand</c>、<c>TradePoolText</c>…）是它早先叫「交易池」时留下的名字。
    /// **界面上一律叫「主动仓」**，2026-09-01 已把所有用户可见的文字统一过来；标识符没改，
    /// 因为要连着 XAML 绑定名和存量 json 字段一起动，风险大而收益小。看到 TradePool 就当主动仓。——把两种用途分开：各选股方法丢进自选的票默认
    /// 只是**算法验证样本**（用来统计各方法的准确率，见晨检的方法过滤器），不代表我要买；勾上这个才
    /// 表示"这只我打算买/卖、请每天盯着它"。每日晨检默认只体检主动仓里的票。
    ///
    /// "显式加入"标记。</summary>
    public bool InTradePool { get; set; }

    /// <summary>"显式移出"标记（2026-07-31新增，为了让已平仓的能移出主动仓）。
    ///
    /// 为什么不把 <see cref="InTradePool"/> 改成 bool? 用 null/false 区分"没设置过"和"移出过"：因为库里
    /// 已经存在的记录早就被写成了显式 <c>false</c>（那是加上这个字段时的默认值，不是用户的决定），
    /// 再用 false 表示"移出过"会把历史上的持仓/已平仓记录误判成"用户主动移出"、让它们凭空从主动仓
    /// 消失。单独加一个新字段就没有这种歧义：老数据里没有它 → 反序列化为 false → 一切照旧。</summary>
    public bool RemovedFromPool { get; set; }

    /// <summary>实际是否属于主动仓：
    /// ① **未平仓的持仓**（买过、还没卖完）恒为真——钱还在里面就必须每天盯，移不出去；
    /// ② 否则：没被显式移出，且（显式加入过 或 有买入记录）。
    /// 已平仓（买过且已全部卖出）落在②：默认仍留在池里当交易留痕，但**允许显式移出**——那笔交易
    /// 已经结束，没道理继续占着每天要看的清单。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsInTradePool =>
        IsHoldingPosition || (!RemovedFromPool && (InTradePool || HasBought));

    // ── 多笔成交的汇总（2026-08-11新增，全部现算不缓存：一条自选也就几笔，加载/刷新时算一遍够快） ──

    /// <summary>按股数加权的汇总。<paramref name="lots"/> 里价格 &lt;= 0 的笔直接忽略（没法参与均价）。
    /// 全部笔的股数都是 0（老数据只填了价格没填股数）时退化成简单平均，并返回 shares=0 表示"股数未知"，
    /// 调用方据此不显示金额。</summary>
    private static (int Shares, double? Avg, DateTime? First, DateTime? Last) Aggregate(IEnumerable<TradeLot> lots)
    {
        var list = lots.Where(l => l.Price > 0).ToList();
        if (list.Count == 0) return (0, null, null, null);
        int shares = list.Sum(l => Math.Max(0, l.Shares));
        double avg = shares > 0
            ? list.Sum(l => l.Price * Math.Max(0, l.Shares)) / shares
            : list.Average(l => l.Price);
        return (shares, avg, list.Min(l => l.Date), list.Max(l => l.Date));
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<TradeLot> BuyLots => Lots.Where(l => l.Side == TradeSide.Buy && l.Price > 0).ToList();

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<TradeLot> SellLots => Lots.Where(l => l.Side == TradeSide.Sell && l.Price > 0).ToList();

    /// <summary>累计买入股数（0 = 没买过，或老数据没填股数）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int TotalBuyShares => Aggregate(BuyLots).Shares;

    /// <summary>加权平均买入成本——**持仓成本价**，晨检的止损/止盈线都按它算。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double? AvgBuyPrice => Aggregate(BuyLots).Avg;

    [System.Text.Json.Serialization.JsonIgnore]
    public int TotalSellShares => Aggregate(SellLots).Shares;

    [System.Text.Json.Serialization.JsonIgnore]
    public double? AvgSellPrice => Aggregate(SellLots).Avg;

    /// <summary>首次买入日期——晨检算"买入后最高收盘"的起点用它（最早的峰值最高，止损最保守）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? FirstBuyDate => Aggregate(BuyLots).First;

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? LastSellDate => Aggregate(SellLots).Last;

    /// <summary>剩余持仓股数 = 累计买入 − 累计卖出（部分卖出后就是还拿着的那部分）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int RemainingShares => Math.Max(0, TotalBuyShares - TotalSellShares);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasBought => AvgBuyPrice is > 0;

    /// <summary>是否已平仓：买过、卖过，且已经没有剩余股数。股数未知的老数据（买卖股数都是0）
    /// 也算平仓——那正是它当年"填了卖出价=平仓"的语义。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsClosedTrade => HasBought && AvgSellPrice is > 0 && TotalBuyShares - TotalSellShares <= 0;

    /// <summary>是否还持有仓位（买过且没平仓）——**部分卖出仍然算持仓**，钱还在里面。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsHoldingPosition => HasBought && !IsClosedTrade;

    /// <summary>已卖出部分的已实现收益率（按加权均价算，A股常用的移动加权成本口径）；没卖过为 null。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double? RealizedPct => HasBought && AvgSellPrice is > 0
        ? (AvgSellPrice.Value - AvgBuyPrice!.Value) / AvgBuyPrice.Value * 100
        : null;

    /// <summary>已卖出部分的已实现盈亏金额；没卖过、或股数未知时为 null。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double? RealizedPnl => HasBought && AvgSellPrice is > 0 && TotalSellShares > 0
        ? (AvgSellPrice.Value - AvgBuyPrice!.Value) * TotalSellShares
        : null;

    /// <summary>把老数据（单笔 BuyDate/BuyPrice/Shares/SellDate/SellPrice）补成 <see cref="Lots"/> 里的
    /// 一买一卖。幂等：已经有 Lots 就什么都不做，所以每次加载都调一遍没关系。加载时调用
    /// （<see cref="JsonWatchlistStore"/>），下次保存时这份迁移结果就一起落盘了。
    ///
    /// 卖出笔的股数照抄买入股数——老语义就是"填了卖出价 = 整仓卖掉"，这样迁移后 <see cref="IsClosedTrade"/>
    /// 仍然为真，不会把历史上的已平仓记录变成还持仓。</summary>
    public void MigrateLegacyLots()
    {
        if (Lots.Count > 0 || BuyPrice is not (> 0)) return;

        int shares = Shares is > 0 ? Shares.Value : 0;
        Lots.Add(new TradeLot
        {
            Side = TradeSide.Buy,
            Date = (BuyDate ?? DataDate).Date,
            Price = BuyPrice.Value,
            Shares = shares,
        });
        if (SellPrice is > 0)
        {
            Lots.Add(new TradeLot
            {
                Side = TradeSide.Sell,
                Date = (SellDate ?? BuyDate ?? DataDate).Date,
                Price = SellPrice.Value,
                Shares = shares,
            });
        }
    }

    /// <summary>把 <see cref="Lots"/> 的汇总结果写回那五个兼容字段——每次改动成交记录后调用
    /// （<see cref="JsonWatchlistStore.UpdateLots"/>）。用意见 <see cref="BuyDate"/> 的注释：
    /// 让老版本 exe 读同一份 JSON 时看到的持仓/平仓状态跟新版一致。</summary>
    public void SyncLegacyFromLots()
    {
        var buy = Aggregate(BuyLots);
        var sell = Aggregate(SellLots);

        BuyDate = buy.First;
        BuyPrice = buy.Avg;
        // 未平仓给剩余股数、已平仓给买入总股数——老版本那套 (价差×股数) 两种情况才都算得对。
        int legacyShares = IsClosedTrade ? buy.Shares : RemainingShares;
        Shares = legacyShares > 0 ? legacyShares : null;
        // 只有全部卖完才写卖出字段，否则老版本会把"卖了一半"当成已平仓、不再执行持仓纪律。
        SellDate = IsClosedTrade ? sell.Last : null;
        SellPrice = IsClosedTrade ? sell.Avg : null;
    }

    public int SatisfiedCount { get; set; }
    public int TotalCount { get; set; }

    /// <summary>Frozen full breakdown of why this stock passed, at pick time — see
    /// CriterionSnapshot's doc comment for why this is copied rather than referencing
    /// StockScreenResult directly.</summary>
    public List<CriterionSnapshot> Criteria { get; set; } = new();
}
