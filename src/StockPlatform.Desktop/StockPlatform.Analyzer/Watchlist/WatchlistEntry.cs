namespace StockPlatform.Analyzer.Watchlist;

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
/// total.sqlite (that file is Fetcher's read-only output, see AnalyzerPaths doc comment) — this
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

    /// <summary>Only meaningful for 峰哥法 (its "前N根K线" parameter) — null for the other three
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

    /// <summary>手动录入的实际买入日期（2026-07-29新增，在自选股Tab里直接编辑）。跟
    /// <see cref="BuyPrice"/>/<see cref="Shares"/>一起构成"持仓"信息：三个都没填=还没买、只是
    /// 观察中；填了=真实持仓，每日晨检的止损/止盈纪律按买入价/买入日（而非自选价）计算。
    /// 老 JSON 里没有这些字段，反序列化自然为 null，向后兼容。</summary>
    public DateTime? BuyDate { get; set; }

    /// <summary>手动录入的实际买入价——见 <see cref="BuyDate"/>。</summary>
    public double? BuyPrice { get; set; }

    /// <summary>手动录入的买入股数——见 <see cref="BuyDate"/>；有它才能算持仓盈亏金额。</summary>
    public int? Shares { get; set; }

    /// <summary>手动录入的卖出日期（2026-07-29新增）——填了买入价又填了卖出价=这笔交易已平仓：
    /// 自选股Tab显示最终已实现盈亏，晨检不再对它执行持仓纪律（状态标"已平仓"、回到观察语义），
    /// 交易记录留痕供事后复盘（前向记录纪律：把每笔信号和结果攒下来）。</summary>
    public DateTime? SellDate { get; set; }

    /// <summary>手动录入的卖出价——见 <see cref="SellDate"/>。</summary>
    public double? SellPrice { get; set; }

    /// <summary>是否放进"我的交易池"（2026-07-31新增）——把两种用途分开：各选股方法丢进自选的票默认
    /// 只是**算法验证样本**（用来统计各方法的准确率，见晨检的方法过滤器），不代表我要买；勾上这个才
    /// 表示"这只我打算买/卖、请每天盯着它"。每日晨检默认只体检交易池里的票。
    ///
    /// "显式加入"标记。</summary>
    public bool InTradePool { get; set; }

    /// <summary>"显式移出"标记（2026-07-31新增，为了让已平仓的能移出交易池）。
    ///
    /// 为什么不把 <see cref="InTradePool"/> 改成 bool? 用 null/false 区分"没设置过"和"移出过"：因为库里
    /// 已经存在的记录早就被写成了显式 <c>false</c>（那是加上这个字段时的默认值，不是用户的决定），
    /// 再用 false 表示"移出过"会把历史上的持仓/已平仓记录误判成"用户主动移出"、让它们凭空从交易池
    /// 消失。单独加一个新字段就没有这种歧义：老数据里没有它 → 反序列化为 false → 一切照旧。</summary>
    public bool RemovedFromPool { get; set; }

    /// <summary>实际是否属于交易池：
    /// ① **未平仓的持仓**（填了买入价、还没填卖出价）恒为真——钱还在里面就必须每天盯，移不出去；
    /// ② 否则：没被显式移出，且（显式加入过 或 有买入记录）。
    /// 已平仓（买入价+卖出价都有）落在②：默认仍留在池里当交易留痕，但**允许显式移出**——那笔交易
    /// 已经结束，没道理继续占着每天要看的清单。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsInTradePool =>
        (BuyPrice is > 0 && SellPrice is not (> 0))
        || (!RemovedFromPool && (InTradePool || BuyPrice is > 0));

    public int SatisfiedCount { get; set; }
    public int TotalCount { get; set; }

    /// <summary>Frozen full breakdown of why this stock passed, at pick time — see
    /// CriterionSnapshot's doc comment for why this is copied rather than referencing
    /// StockScreenResult directly.</summary>
    public List<CriterionSnapshot> Criteria { get; set; } = new();
}
