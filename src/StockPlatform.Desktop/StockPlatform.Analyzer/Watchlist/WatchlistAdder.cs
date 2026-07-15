using StockPlatform.Analyzer.ViewModels;

namespace StockPlatform.Analyzer.Watchlist;

/// <summary>Shared "加入自选" logic for all four method tabs — each tab's Results grid produces
/// ResultRowViewModel rows the same way, only the method name/granularity/lookback differ.</summary>
public static class WatchlistAdder
{
    /// <returns>How many were actually added (after dedup on Code+Method+DataDate — see
    /// JsonWatchlistStore.Add), so the caller can tell the user "已加入N只" vs "都已经在自选里了".</returns>
    public static int AddSelected(
        JsonWatchlistStore store, IEnumerable<ResultRowViewModel> results, string method, string granularity,
        int? lookback = null, double? difThreshold = null)
    {
        var selected = results.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) return 0;

        var entries = selected.Select(r => new WatchlistEntry
        {
            Code = r.Code,
            Name = r.Name,
            Method = method,
            Granularity = granularity,
            Lookback = lookback,
            DifThreshold = difThreshold,
            DataDate = r.Result.DataDate ?? DateTime.Today,
            PriceAtPick = r.Result.LastClose ?? 0,
            AddedAt = DateTime.Now,
            SatisfiedCount = r.SatisfiedCount,
            TotalCount = r.TotalCount,
            Criteria = r.Result.Criteria.Select(c => new CriterionSnapshot
            {
                Name = c.Name,
                Satisfied = c.Satisfied,
                Basis = c.Basis,
                DataMissing = c.DataMissing,
            }).ToList(),
        });

        var added = store.Add(entries);
        foreach (var r in selected) r.IsSelected = false; // uncheck once handled, checked or not
        return added;
    }
}
