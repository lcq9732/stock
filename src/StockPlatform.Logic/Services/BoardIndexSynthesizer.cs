using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 本地合成板块指数日K（2026-07-15新增）——板块指数的历史K线，官方源只有东方财富/同花顺，
/// 前者在用户环境连不上、后者未验证（新浪/腾讯都只有板块实时快照+成分股，没有历史K，见
/// doc/data-platform-design.md）。所以这里不抓官方数值，而是用本地已有的**成分股(BoardMember)
/// + 个股日K**自己合成一条**等权**板块指数：每个交易日的指数涨幅 = 当天有数据的成分股各自
/// (今收/昨收-1) 的均值，累乘成指数点位（基点1000）。口径跟阶梯低点法回测里用的"全市场等权大盘"
/// 一致，趋势可靠；代价是数值跟数据商官方板块指数不完全一样（成分/加权/基期口径不同）。
///
/// 指数bar在Bar表里用**板块代码**（gn_xxx / new_xxx）当code存——既不是6位纯数字、也不是带
/// sh/sz前缀的8位符号，所以 SqliteBarRepository.GetAllCodes（只认6位纯数字）天然不会把它当个股
/// 捞进选股全集。纯函数，不碰网络/数据库写入（写入由调用方负责），方便直接拿真实库验证。
/// </summary>
public static class BoardIndexSynthesizer
{
    /// <summary>某个交易日至少要有这么多成分股当天有数据，才给这天算指数点（否则跳过，避免
    /// 早期只有一两只成分股上市时的噪声）。</summary>
    public const int MinMembersPerDay = 5;
    private const double BaseLevel = 1000.0;

    /// <summary>用成分股日K合成一个板块的等权指数日K。members为空/数据太少时返回空列表。
    /// <paramref name="asOf"/> 写进每根bar的 FetchedAt（合成时刻），默认调用方传入。</summary>
    public static List<Bar> Synthesize(string boardCode, IReadOnlyList<string> memberCodes,
        IBarRepository barRepository, DateTime asOf)
    {
        // 每只成分股：日期 → 当天相对自身上一交易日的涨幅（收/开/高/低），以及量、额。
        // 用各成分股"自己的"上一根bar算涨幅，天然处理停牌造成的日期缺口。
        var perMemberDailyReturn = new List<Dictionary<DateTime, DayReturn>>();
        foreach (var code in memberCodes)
        {
            var bars = barRepository.Query(code, Granularity.Day);
            if (bars.Count < 2) continue;
            var map = new Dictionary<DateTime, DayReturn>();
            for (int i = 1; i < bars.Count; i++)
            {
                double prevClose = bars[i - 1].Close;
                if (prevClose <= 0) continue;
                var b = bars[i];
                map[b.PeriodStart.Date] = new DayReturn(
                    b.Close / prevClose - 1, b.Open / prevClose - 1,
                    b.High / prevClose - 1, b.Low / prevClose - 1, b.Volume, b.Amount);
            }
            if (map.Count > 0) perMemberDailyReturn.Add(map);
        }
        if (perMemberDailyReturn.Count == 0) return new List<Bar>();

        var allDates = perMemberDailyReturn.SelectMany(m => m.Keys).Distinct().OrderBy(d => d).ToList();
        var result = new List<Bar>();
        double level = BaseLevel;
        foreach (var date in allDates)
        {
            double sumC = 0, sumO = 0, sumH = 0, sumL = 0, vol = 0, amt = 0;
            int n = 0;
            foreach (var m in perMemberDailyReturn)
            {
                if (!m.TryGetValue(date, out var r)) continue;
                sumC += r.Close; sumO += r.Open; sumH += r.High; sumL += r.Low;
                vol += r.Volume; amt += r.Amount; n++;
            }
            if (n < MinMembersPerDay) continue; // 成分股数据太少的日子跳过

            double prevLevel = level;
            double meanC = sumC / n;
            level = prevLevel * (1 + meanC);
            double open = prevLevel * (1 + sumO / n);
            double high = prevLevel * (1 + sumH / n);
            double low = prevLevel * (1 + sumL / n);
            // 合成的均值不保证 high 是当日最高、low 是最低——夹一下，避免出现不合法K线
            high = Math.Max(Math.Max(high, open), level);
            low = Math.Min(Math.Min(low, open), level);

            result.Add(new Bar
            {
                Code = boardCode,
                Granularity = Granularity.Day,
                PeriodStart = date,
                Open = open,
                Close = level,
                High = high,
                Low = low,
                Volume = vol,
                Amount = amt,
                Turnover = 0,
                FetchedAt = asOf,
            });
        }
        return result;
    }

    private readonly record struct DayReturn(double Close, double Open, double High, double Low, double Volume, double Amount);
}
