using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 本地合成板块指数日K（2026-07-15新增）——板块指数的历史K线，官方源只有东方财富/同花顺，
/// 前者在用户环境连不上、后者未验证（新浪/腾讯都只有板块实时快照+成分股，没有历史K，见
/// doc/data-platform-design.md）。所以这里不抓官方数值，而是用本地已有的成分股（<c>BoardMember</c>）
/// 加个股的**回测口径**日K（<c>day_adj</c>，为什么不能用前复权见 <see cref="Synthesize"/> 里的长注释）
/// 自己合成一条**等权**板块指数：每个交易日的指数涨幅 = 当天有数据的成分股各自
/// (今收/昨收-1) 的均值，累乘成指数点位（基点1000）。口径跟阶梯低点法回测里用的"全市场等权大盘"
/// 一致，趋势可靠；代价是数值跟数据商官方板块指数不完全一样（成分/加权/基期口径不同）。
///
/// 指数bar在Bar表里用**板块代码**当code存（现在是东财的 <c>BKxxxx</c>；2026-09 之前是新浪/腾讯的
/// <c>gn_xxx</c>/<c>new_xxx</c>，库里还留着 223 只那个年代的废弃指数，Board 表里已经没有它们了）
/// ——既不是6位纯数字、也不是带 sh/sz前缀的8位符号，所以 SqliteBarRepository.GetAllCodes
/// （只认6位纯数字）天然不会把它当个股捞进选股全集。纯函数，不碰网络/数据库写入（写入由调用方负责），方便直接拿真实库验证。
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
            // ⚠ 必须用 day_adj，**不能用 day**（2026-09-11 改，拿东财官方板块指数当标尺实测）。
            //
            // 这里算的是收益率，而源给的前复权是**减法式**（原价减去此后的累计分红）：非除权日
            // 两天减的是同一个常数、但分母变小了，所以
            //     前复权收益率 = (C_t − D)/(C_{t−1} − D) − 1  ≠  C_t/C_{t−1} − 1
            // 连非除权日的收益率都是错的，而且 D 越接近老股价放大越狠（万科 1997 年前复权价是
            // −8.17，下面那个 prevClose <= 0 的守卫就是为它加的；prevClose 接近 0 但为正的那些天，
            // 收益率会爆掉，守卫拦不住）。
            //
            // 实测（东财本地 DayData_BK 那 1032 只官方板块指数当标尺，比日收益率序列）：
            //   银行 BK0475 全历史   前复权 相关 0.454 / 平均差 5.88%   day_adj 相关 0.945 / 0.30%
            //            2000-2010  前复权 相关 0.510 / 平均差 14.95%（81% 的天差>2%）
            //   煤炭 BK0437 2016 后  前复权 相关 0.593（26% 的天差>2%）  day_adj 0.994（0 天差>2%）
            //   白酒 BK0896 2016 后  两者持平 0.965 / 0.961  ← 股息率低、股价高，减法式扣掉的占比小
            // 分红越重失真越狠，白酒那一行正好反证了机制。
            //
            // day_adj 是本地算的纯乘法式序列，非除权日收益率**精确等于**真实收益率
            // （见 AdjustFactorCalculator）。覆盖也够：板块成分股 5651 只里 day 和 day_adj
            // 都是 5559 只有 >=2 根（缺的 92 只是 B 股 200xxx，两边都没有），换口径后
            // **没有任何板块的可用成分股掉到 MinMembersPerDay 以下**。
            var bars = barRepository.Query(code, Granularity.DayAdj);
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
