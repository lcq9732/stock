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

    public int SatisfiedCount { get; set; }
    public int TotalCount { get; set; }

    /// <summary>Frozen full breakdown of why this stock passed, at pick time — see
    /// CriterionSnapshot's doc comment for why this is copied rather than referencing
    /// StockScreenResult directly.</summary>
    public List<CriterionSnapshot> Criteria { get; set; } = new();
}
