using StockPlatform.FactorLab.Core;

namespace StockPlatform.FactorLab.Factors;

// ============ 2026-08-03 新增：三个此前用不上的数据源，现在都齐了 ============
// ① 主力净流入：原来库里只有3个月，是当初没做资金流因子的唯一原因，现已补齐十年；
// ② 分红明细：新加的 Dividend 表，让"高股息"这个 A股 2021~2024 最强风格之一第一次可测；
// ③ 十大流通股东：库里躺了 545 万行从没被任何因子用过。
// 三者都与现有的"冷落蓄势"族和基本面族信息来源不同，是补风格多样性的直接手段。

/// <summary>股息率(TTM)：过去12个月每股现金分红 / 当前股价。</summary>
public sealed class DividendYield : IFactor
{
    public string Name => "股息率TTM";
    public string Category => "分红";
    public string Formula => "近12个月已实施现金分红合计(每股) / 当日价格";
    public string Description =>
        "高股息策略的核心指标。买入逻辑有三层：①真金白银的现金回报，是少数不依赖'找人接盘'的收益来源；" +
        "②能持续分红本身就是盈利真实、现金流健康的硬证明（造假公司发不出真钱）；③无风险利率下行期，" +
        "高股息资产的类债券属性使其获得估值溢价——这正是 2021~2024 年'中特估/红利'行情的底层逻辑。" +
        "只统计**已实施且已过除权日**的分红（预案不算），因此天然没有前视。" +
        "注意：一次性大额特别分红会造成虚高的股息率，属于已知噪声。";
    public string Direction => "值越大=股息率越高";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md)
    {
        var res = Rolling.AllocNaN(md.NStocks, md.NDays);
        Parallel.For(0, md.NStocks, s =>
        {
            var divs = md.Dividends[s];
            if (divs.Length == 0) return;
            // 每个交易日回看约250个交易日内的除权分红合计
            const int Window = 250;
            int lo = 0, hi = 0;
            double sum = 0;
            for (int t = 0; t < md.NDays; t++)
            {
                while (hi < divs.Length && divs[hi].ExIdx <= t) { sum += divs[hi].PerShare; hi++; }
                while (lo < hi && divs[lo].ExIdx < t - Window) { sum -= divs[lo].PerShare; lo++; }
                if (sum <= 0) continue;
                // 用真实价格（前复权末日=真实价）算股息率；后复权价是放大过的，直接除会系统性偏低
                double px = md.DisplayClose[s][t];
                if (double.IsNaN(px) || px <= 0) continue;
                res[s][t] = sum / px;
            }
        });
        return res;
    }
}

/// <summary>连续分红年数：截至当日，往前连续有现金分红的自然年数（上限10）。</summary>
public sealed class DividendConsistency : IFactor
{
    public string Name => "连续分红年数";
    public string Category => "分红";
    public string Formula => "截至当日往前连续每个自然年都有已实施现金分红的年数（上限10）";
    public string Description =>
        "分红的**持续性**比某一年的高股息更能说明问题：连续十年分红意味着穿越了完整周期仍有真实盈利，" +
        "是财务质量的强证据，也是管理层股东回报意愿的体现。与股息率互补——" +
        "股息率高但只分过一次的（清仓式分红、大股东套现）和年年稳定分红的，风险完全不同。";
    public string Direction => "值越大=连续分红年数越多";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md)
    {
        var res = Rolling.AllocNaN(md.NStocks, md.NDays);
        Parallel.For(0, md.NStocks, s =>
        {
            var divs = md.Dividends[s];
            if (divs.Length == 0) return;
            // 每个除权日所在自然年记为"该年有分红"
            var years = new HashSet<int>();
            foreach (var (idx, _) in divs) years.Add(md.Dates[idx].Year);
            for (int t = 0; t < md.NDays; t++)
            {
                int y = md.Dates[t].Year;
                int streak = 0;
                // 从上一自然年往前数（当年可能还没到分红季，不计入以免年初集体断档）
                for (int k = y - 1; k >= y - 10 && years.Contains(k); k--) streak++;
                res[s][t] = streak;
            }
        });
        return res;
    }
}

/// <summary>主力净流入强度：近20日净流入合计 / 近20日成交额合计。</summary>
public sealed class MoneyFlow20 : IFactor
{
    public string Name => "主力净流入20日";
    public string Category => "资金";
    public string Formula => "sum(主力净流入, 20日) / sum(成交额, 20日)";
    public string Description =>
        "大单资金的净买入强度。除以成交额做标准化，否则大盘股的绝对金额会碾压一切、变成市值因子。" +
        "假设：主力（大单）资金信息优势更强，其持续净买入领先于股价。" +
        "但要警惕两点——①'主力'是按单笔金额划分的统计口径，不等于机构；" +
        "②A股散户是主力的对手盘，净流入常常只是价格上涨的同步结果而非领先信号，实测很可能是同步指标。";
    public string Direction => "值越大=大单净买入越强";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, 21, (s, t) =>
        {
            double flow = 0, amt = 0; int n = 0;
            for (int i = t - 19; i <= t; i++)
            {
                double f = md.NetInflow[s][i], a = md.Amount[s][i];
                if (double.IsNaN(f) || double.IsNaN(a) || a <= 0) continue;
                flow += f; amt += a; n++;
            }
            return n >= 15 && amt > 0 ? flow / amt : double.NaN;
        });
}

/// <summary>资金流背离：价格在跌、主力却在买（近20日）。</summary>
public sealed class FlowPriceDiverge20 : IFactor
{
    public string Name => "资金流背离20日";
    public string Category => "资金";
    public string Formula => "近20日主力净流入强度 − 近20日涨跌幅（两者各自截面标准化前的原始差）";
    public string Description =>
        "捕捉'股价在跌、大单却在持续买入'的状态——这种背离常被解读为主力在低位吸筹，" +
        "比单看净流入更有信息量（净流入本身与当期涨幅高度同步，减掉涨幅才剩下'超出价格解释的那部分买入'）。" +
        "与'量价背离'是同一思想在资金维度的版本。";
    public string Direction => "值越大=跌得越多但买得越多（背离越明显）";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, 21, (s, t) =>
        {
            double flow = 0, amt = 0; int n = 0;
            for (int i = t - 19; i <= t; i++)
            {
                double f = md.NetInflow[s][i], a = md.Amount[s][i];
                if (double.IsNaN(f) || double.IsNaN(a) || a <= 0) continue;
                flow += f; amt += a; n++;
            }
            if (n < 15 || amt <= 0) return double.NaN;
            double now = md.Close[s][t], past = md.Close[s][t - 20];
            if (double.IsNaN(now) || double.IsNaN(past) || past <= 0) return double.NaN;
            return flow / amt - (now / past - 1);
        });
}

/// <summary>十大流通股东持股集中度。</summary>
public sealed class HolderConcentration : IFactor
{
    public string Name => "十大股东集中度";
    public string Category => "筹码";
    public string Formula => "十大流通股东持股占比合计(%)，报告期后按法定披露截止日生效";
    public string Description =>
        "筹码集中在少数大股东手里=流通盘实际很小、抛压轻，且大股东与公司利益绑定更紧。" +
        "与'股东户数'（已验证无效）不同的是，这里看的是**谁持有**而不是**有多少人持有**，" +
        "信息含量更高。注意国资控股公司天然集中度极高（茅台前十大占54%），" +
        "所以这个因子含很强的股权性质属性，中性化后的IC才更能反映真实信息。";
    public string Direction => "值越大=筹码越集中";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) => HolderSeries.Expand(md, (prev, cur) => cur);
}

/// <summary>十大流通股东持股占比的环比变化（增持=正）。</summary>
public sealed class HolderConcentrationChange : IFactor
{
    public string Name => "十大股东增持";
    public string Category => "筹码";
    public string Formula => "本期十大流通股东占比 − 上期占比（百分点）";
    public string Description =>
        "比集中度水平更干净的信号：剔除了'国资天然集中'这类固定属性，只看**边际变化**。" +
        "大股东/机构在报告期内增持，是内部人看好的直接证据（他们比市场更了解基本面）；" +
        "减持则相反。这是筹码类因子里最接近'内部人信号'的一个。";
    public string Direction => "值越大=十大股东合计增持越多";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        HolderSeries.Expand(md, (prev, cur) => double.IsNaN(prev) ? double.NaN : cur - prev);
}

/// <summary>北向（陆股通）持股占流通股比例。</summary>
public sealed class NorthboundHolding : IFactor
{
    public string Name => "北向持股占比";
    public string Category => "北向";
    public string Formula => "十大流通股东里\"香港中央结算有限公司\"的持股占比(%)，报告期后按法定披露截止日生效";
    public string Description =>
        "外资（陆股通）的持仓比例。库里没有独立的北向数据表，但陆股通持股的名义持有人就是" +
        "\"香港中央结算有限公司\"，它出现在十大流通股东里，等价可用（已排除\"香港中央结算(代理人)有限公司\"" +
        "——那是H股的持有人）。逻辑：外资偏好盈利稳定、治理规范、流动性好的公司，其持仓比例被视为" +
        "一种独立的质量背书，且与国内量价因子来源完全不同。" +
        "⚠️ 这是**截断观测**：只有北向持股大到能进前十大流通股东时才看得见（2016年仅304只可见、" +
        "2025年2903只），看不见不等于没有，只是不足前十大——所以低分档混杂了'外资不买'和'买得少但没进前十'两种情况。";
    public string Direction => "值越大=北向持股比例越高";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        HolderSeries.Expand(md, md.NorthboundRatio, (prev, cur) => cur);
}

/// <summary>北向持股占比的环比变化（外资增持=正）。</summary>
public sealed class NorthboundChange : IFactor
{
    public string Name => "北向增持";
    public string Category => "北向";
    public string Formula => "本期北向持股占比 − 上期（百分点）";
    public string Description =>
        "比持股水平更干净的信号：剔除了'大盘蓝筹天然被外资重仓'这类固定属性，只看**边际变化**。" +
        "外资增持常被视为对基本面改善的确认。注意两点：①同样受截断观测影响（首次进入前十大会显示为" +
        "一个虚高的增幅，实际是从'不可见'跳到'可见'）；②2024年起北向数据披露规则收紧，" +
        "实时资金流已停止公布，只剩季度持股，所以这个因子的时效性天然是季度级的。";
    public string Direction => "值越大=外资增持越多";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        HolderSeries.Expand(md, md.NorthboundRatio, (prev, cur) => double.IsNaN(prev) ? double.NaN : cur - prev);
}

/// <summary>把"按报告期生效"的十大股东序列展开成逐日阶梯（与基本面因子同一套过期规则）。</summary>
internal static class HolderSeries
{
    private const int StaleAfterDays = 300;

    public static double[][] Expand(MarketData md, Func<double, double, double> valueOf) =>
        Expand(md, md.TopHolderRatio, valueOf);

    public static double[][] Expand(MarketData md, (int AvailIdx, double Ratio)[][] source, Func<double, double, double> valueOf)
    {
        var res = Rolling.AllocNaN(md.NStocks, md.NDays);
        Parallel.For(0, md.NStocks, s =>
        {
            var seq = source[s];
            if (seq.Length == 0) return;
            var row = res[s];
            for (int i = 0; i < seq.Length; i++)
            {
                double v = valueOf(i > 0 ? seq[i - 1].Ratio : double.NaN, seq[i].Ratio);
                if (double.IsNaN(v)) continue;
                int from = seq[i].AvailIdx;
                int to = i + 1 < seq.Length
                    ? Math.Min(seq[i + 1].AvailIdx, from + StaleAfterDays)
                    : Math.Min(md.NDays, from + StaleAfterDays);
                for (int t = from; t < to && t < md.NDays; t++) row[t] = v;
            }
        });
        return res;
    }
}
