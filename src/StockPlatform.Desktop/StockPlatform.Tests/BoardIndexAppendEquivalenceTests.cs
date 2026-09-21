using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// **追加算出来的必须跟全量算出来的逐根一致**（2026-09-21）。
///
/// 这是整个增量改造的命根子：板块指数是路径依赖的（等权平均涨幅累乘），
/// 追加那条路只读"上一根基准 + 新的那几天"，算错了不会有任何地方报——
/// 板块指数只有本地这一份，没有官方值可对。
/// </summary>
public class BoardIndexAppendEquivalenceTests
{
    private const string Board = "BK0001";
    private static readonly DateTime AsOf = new(2026, 9, 21, 12, 0, 0);

    /// <summary>只装 day_adj 的假仓储；<see cref="QueryForAppend"/> 按接口约定实现（带一根基准）。</summary>
    private sealed class FakeBars(Dictionary<string, List<Bar>> byCode) : IBarRepository
    {
        public List<Bar> Query(string code, string granularity, DateTime? start = null, DateTime? end = null)
            => byCode.TryGetValue(code, out var b)
                ? b.Where(x => (start is null || x.PeriodStart >= start) && (end is null || x.PeriodStart <= end))
                   .ToList()
                : [];

        public List<Bar> QueryForAppend(string code, string granularity, DateTime from)
        {
            var all = Query(code, granularity);
            int i = all.FindIndex(b => b.PeriodStart.Date >= from.Date);
            if (i < 0) return all.Count > 0 ? [all[^1]] : [];
            int start = Math.Max(0, i - 1);
            return all.GetRange(start, all.Count - start);
        }

        public void EnsureSchema() { }
        public void InsertOrRefreshUnconfirmed(IEnumerable<Bar> bars) { }
        public DateTime? GetLatestPeriodStart(string code, string granularity) => null;
        public DateTime? GetOverallLatestPeriodStart(string granularity) => null;
        public DateTime? GetOverallEarliestPeriodStart(string granularity) => null;
        public DateTime? GetOverallLatestPeriodStartOnOrBefore(string granularity, DateTime cutoff) => null;
        public Bar? GetLatestBar(string code, string granularity) => null;
        public List<string> GetAllCodes() => [];
    }

    /// <summary>造一只票的 day_adj：从 <paramref name="from"/> 起连续 <paramref name="days"/> 个工作日。</summary>
    private static List<Bar> Series(string code, DateTime from, int days, double start, double step,
                                    Func<int, bool>? present = null)
    {
        var list = new List<Bar>();
        var d = from;
        for (int i = 0; i < days; i++)
        {
            while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
            if (present is null || present(i))
            {
                double c = start + step * i;
                list.Add(new Bar
                {
                    Code = code, Granularity = Granularity.DayAdj, PeriodStart = d,
                    Open = c - step * 0.3, Close = c, High = c + step * 0.5, Low = c - step * 0.5,
                    Volume = 1000 + i, Amount = (1000 + i) * c, FetchedAt = AsOf,
                });
            }
            d = d.AddDays(1);
        }
        return list;
    }

    private static (FakeBars Repo, List<string> Members) Market(Func<int, bool>? gapFor = null)
    {
        var from = new DateTime(2026, 6, 1);
        var byCode = new Dictionary<string, List<Bar>>();
        var members = new List<string>();
        for (int m = 0; m < 6; m++)
        {
            var code = $"60000{m}";
            members.Add(code);
            // 第 3 只故意停牌一段（测"基准是它自己的上一根，可能远在前面"）
            byCode[code] = Series(code, from, 60, 10 + m, 0.11 + m * 0.01,
                                  m == 3 ? gapFor : null);
        }
        return (new FakeBars(byCode), members);
    }

    /// <summary>全量 vs（全量到中点 + 从中点追加）——两条路必须逐根一致。</summary>
    [Fact]
    public void 追加与全量逐根一致()
    {
        var (repo, members) = Market();
        var full = BoardIndexSynthesizer.Synthesize(Board, members, repo, AsOf);
        Assert.True(full.Count > 20, "样本要够长才验得出路径依赖");

        int cut = full.Count / 2;
        var head = full.Take(cut).ToList();
        var tail = BoardIndexSynthesizer.Append(
            Board, members, repo, AsOf,
            from: head[^1].PeriodStart.AddDays(1), baseLevel: head[^1].Close);

        var joined = head.Concat(tail).ToList();
        Assert.Equal(full.Count, joined.Count);
        for (int i = 0; i < full.Count; i++)
        {
            Assert.Equal(full[i].PeriodStart, joined[i].PeriodStart);
            Assert.Equal(full[i].Close, joined[i].Close, 8);
            Assert.Equal(full[i].Open, joined[i].Open, 8);
            Assert.Equal(full[i].High, joined[i].High, 8);
            Assert.Equal(full[i].Low, joined[i].Low, 8);
            Assert.Equal(full[i].Volume, joined[i].Volume, 8);
            Assert.Equal(full[i].Amount, joined[i].Amount, 8);
        }
    }

    /// <summary>
    /// ⭐ 有成分股**长期停牌**时也必须一致——基准是它自己的上一根，可能远在几十天前。
    /// 这正是"不能按固定天数往前切一段"的那条约束（见 <c>IBarRepository.QueryForAppend</c>）。
    /// </summary>
    [Fact]
    public void 有长期停牌的成分股也一致()
    {
        // 第 3 只只在前 10 天和最后 5 天有数据，中间整段停牌
        var (repo, members) = Market(gapFor: i => i < 10 || i >= 55);
        var full = BoardIndexSynthesizer.Synthesize(Board, members, repo, AsOf);

        int cut = full.Count / 2;
        var head = full.Take(cut).ToList();
        var tail = BoardIndexSynthesizer.Append(
            Board, members, repo, AsOf, head[^1].PeriodStart.AddDays(1), head[^1].Close);

        var joined = head.Concat(tail).ToList();
        Assert.Equal(full.Count, joined.Count);
        for (int i = 0; i < full.Count; i++)
        {
            Assert.Equal(full[i].PeriodStart, joined[i].PeriodStart);
            Assert.Equal(full[i].Close, joined[i].Close, 8);
        }
    }

    /// <summary>追加窗口里一天新数据都没有时返回空，不该凭空造根。</summary>
    [Fact]
    public void 没有新数据时追加为空()
    {
        var (repo, members) = Market();
        var full = BoardIndexSynthesizer.Synthesize(Board, members, repo, AsOf);

        var tail = BoardIndexSynthesizer.Append(
            Board, members, repo, AsOf, full[^1].PeriodStart.AddDays(1), full[^1].Close);

        Assert.Empty(tail);
    }
}
