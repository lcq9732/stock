using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 资金面诊断的算法（2026-09-16，设计见 doc/capital-diagnosis-design.md）。
///
/// **纯计算、零 IO**：输入是已读好的序列（<see cref="CapitalDiagnosisInput"/>），输出是六个维度的
/// 结论与证据表。这样回测、晨检、FactorLab 都能复用同一份逻辑，不只服务于一个窗口。
///
/// 三条自我约束，都是踩过坑定下来的：
/// 1. **结论句规则拼装**——按符号、比值、阈值套模板，同一组数据永远得出同一句话，可回归测试。
/// 2. **阈值克制**——只对"明显偏离"提示，不设多档评分。RisingLows 回测已经吃过阈值过拟合的亏
///    （project_risinglows_backtest_findings），没有前向验证之前不发明新分档。
/// 3. **不合成总分**——六个维度压成一个数会掩盖矛盾信号，而矛盾信号恰恰最该被看见。
/// </summary>
public class CapitalDiagnosisAnalyzer
{
    /// <summary>回看窗口（交易日）——既定的短线口径（project_shortterm_focus）。</summary>
    public const int DefaultLookback = 60;

    /// <summary>放量日阈值：成交额 &gt; 区间日均 × 这个倍数。</summary>
    private const double VolumeSpikeFactor = 1.5;

    /// <summary>量能"明显偏离"的上下界，落在区间内就不提示。</summary>
    private const double VolumeLowRatio = 0.70;
    private const double VolumeHighRatio = 1.50;

    /// <summary>同业样本低于这个数就退回上一级口径，再不足则放弃同业对照。</summary>
    public const int MinPeerSample = 10;

    /// <summary>大宗交易的折溢价分类边界（%）。</summary>
    private const double BlockFlatBand = 0.5;
    private const double BlockDiscount = -2.0;
    private const double BlockPremium = 2.0;

    /// <summary>
    /// 定区间。**必须先于读同业数据调用**——同业要按这里算出的区间去查，而区间只依赖本股K线。
    ///
    /// 主锚点取近 <paramref name="lookback"/> 个交易日内的**最高收盘日**：自动适配"这波走了多久"，
    /// 不用人去翻图找高点，也不必让用户手工选区间（手工选会让不同票的结论不可比）。
    /// 涨势中锚点自然落在最近几天、区间很短，退化成"近期"诊断——这正是"不管涨跌"的实现方式。
    /// </summary>
    public static DiagnosisWindows ResolveWindows(IReadOnlyList<Bar> bars, int lookback = DefaultLookback)
    {
        if (bars.Count == 0) return new DiagnosisWindows { Lookback = lookback };

        int n = bars.Count;
        int from = Math.Max(0, n - lookback);
        int anchor = from;
        for (int i = from; i < n; i++)
            if (bars[i].Close > bars[anchor].Close) anchor = i;

        var w = new DiagnosisWindows
        {
            AnchorDate = bars[anchor].PeriodStart,
            AnchorIndex = anchor,
            Lookback = lookback,
        };
        w.Ranges.Add(MakeRange("本波", anchor, bars));
        // 固定对照。**不是装饰**：宁德实测本波跑输指数 13.5pct、近60日反而跑赢 3.9pct，
        // 换窗口结论符号就翻转，只给主锚点会误导。
        if (n > 20) w.Ranges.Add(MakeRange("近20日", n - 20, bars));
        if (n > lookback) w.Ranges.Add(MakeRange($"近{lookback}日", n - lookback, bars));
        return w;
    }

    private static DiagnosisRange MakeRange(string label, int start, IReadOnlyList<Bar> bars) =>
        new(label, start, bars[start].PeriodStart, bars[^1].PeriodStart, bars.Count - start);

    public CapitalDiagnosis Analyze(CapitalDiagnosisInput input, DiagnosisWindows windows)
    {
        var bars = input.Bars;
        if (bars.Count == 0)
            return new CapitalDiagnosis
            {
                Code = input.Code,
                Name = input.Name,
                Error = "本地没有这只票的日K，无法做资金面诊断——先跑一次【拉取全部/当天】。",
            };

        int a = windows.AnchorIndex;
        var result = new CapitalDiagnosis
        {
            Code = input.Code,
            Name = input.Name,
            AsOf = bars[^1].PeriodStart,
            Windows = windows,
            AnchorClose = bars[a].Close,
            LatestClose = bars[^1].Close,
            AnchorChangePct = Pct(bars[a].Close, bars[^1].Close),
        };

        result.Dimensions.Add(BuildRelativeStrength(input, windows));
        result.Dimensions.Add(BuildVolume(input, windows));
        result.Dimensions.Add(BuildMoneyFlow(input, windows));
        result.Dimensions.Add(BuildLeverage(input, windows));
        result.Dimensions.Add(BuildBlockTrade(input, windows));
        result.Dimensions.Add(BuildLhb(input, windows));

        result.GlobalNote = BuildGlobalNote(result);
        return result;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  维度 1：相对强弱
    // ══════════════════════════════════════════════════════════════════════

    private static DiagnosisDimension BuildRelativeStrength(CapitalDiagnosisInput input, DiagnosisWindows w)
    {
        var scope = input.PeerScopeName.Length > 0 ? input.PeerScopeName : "（无行业归属）";
        var d = new DiagnosisDimension
        {
            Index = 1,
            Title = "相对强弱（vs 指数/同业）",
            Tooltip =
                "同业中位数 = 同行业所有票在同一区间涨跌幅的中位数；样本 < 10 只时退回上一级门类口径，"
                + "再不足则放弃同业对照、只留指数。指数按所属板块自动选（创业板→创业板指，主板→上证指数，"
                + $"科创→科创50，北交→北证50）。本次口径：{scope}"
                + (input.PeerTotal > 0 ? $"，{input.PeerTotal} 只中有效 {input.PeerValid} 只。" : "。"),
        };

        if (input.PeerDowngraded)
            d.Warnings.Add($"同业样本不足 {MinPeerSample} 只，已退回门类口径「{input.PeerScopeName}」——跨行业可比性下降");
        if (input.PeerScopeName.Length == 0)
            d.Warnings.Add("这只票没有行业归属，同业一列为空，只能跟指数比");
        if (input.IndexReturns.Count == 0)
            d.Warnings.Add($"本地还没有{input.IndexName}在这些区间的K线，指数对照为空（指数常比个股晚抓一轮）");

        var bars = input.Bars;
        var cols = new List<DiagnosisColumn>
        {
            new("区间", 62),
            new("本股", 72, true),
            new(input.IndexName.Length > 0 ? input.IndexName : "指数", 78, true),
            new("同业中位", 78, true),
            new("vs指数", 78, true),
            new("vs同业", 78, true),
        };
        var table = new DiagnosisTable { Columns = cols };

        foreach (var r in w.Ranges)
        {
            double me = Pct(bars[r.StartIndex].Close, bars[^1].Close);
            input.IndexReturns.TryGetValue(r.Label, out double ix);
            bool hasIx = input.IndexReturns.ContainsKey(r.Label);
            input.PeerMedianReturns.TryGetValue(r.Label, out double pm);
            bool hasPm = input.PeerMedianReturns.ContainsKey(r.Label);

            table.Rows.Add(new[]
            {
                new DiagnosisCell(r.Label),
                Signed(me, "F2", "%"),
                hasIx ? Signed(ix, "F2", "%") : Dash(),
                hasPm ? Signed(pm, "F2", "%") : Dash(),
                hasIx ? Signed(me - ix, "F1", "pct") : Dash(),
                hasPm ? Signed(me - pm, "F1", "pct") : Dash(),
            });
        }
        d.Tables.Add(table);

        // ── 结论
        var main = w.Ranges[0];
        double mainMe = Pct(bars[main.StartIndex].Close, bars[^1].Close);
        if (input.IndexReturns.TryGetValue(main.Label, out double mainIx))
        {
            double ex = mainMe - mainIx;
            string verb = ex < 0 ? "跑输" : "跑赢";
            var sb = $"本波{verb}{input.IndexName} {Math.Abs(ex):F1}pct";
            if (input.PeerMedianReturns.TryGetValue(main.Label, out double mainPm))
            {
                double exP = mainMe - mainPm;
                sb += $"、{(exP < 0 ? "跑输" : "跑赢")}同业中位 {Math.Abs(exP):F1}pct";
            }
            sb += ex < 0 ? "，弱于所处板块" : "，强于所处板块";
            d.Conclusions.Add(sb);

            // 符号翻转是这个维度最值得说的事——只看一个区间会得出相反结论
            var longest = w.Ranges[^1];
            if (longest.Label != main.Label
                && input.IndexReturns.TryGetValue(longest.Label, out double longIx))
            {
                double longEx = Pct(bars[longest.StartIndex].Close, bars[^1].Close) - longIx;
                if (Math.Sign(longEx) != Math.Sign(ex) && Math.Abs(longEx) >= 1)
                    d.Conclusions.Add(
                        $"但{longest.Label}窗口{(longEx > 0 ? "反而跑赢" : "反而跑输")}指数 {longEx:+0.0;-0.0}pct"
                        + " —— 换窗口结论符号翻转，不可只看一个区间");
            }
        }
        else
        {
            d.Conclusions.Add($"本波{(mainMe < 0 ? "下跌" : "上涨")} {Math.Abs(mainMe):F2}%，暂无指数可比");
        }
        return d;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  维度 2：量能档位
    // ══════════════════════════════════════════════════════════════════════

    private static DiagnosisDimension BuildVolume(CapitalDiagnosisInput input, DiagnosisWindows w)
    {
        var bars = input.Bars;
        var d = new DiagnosisDimension
        {
            Index = 2,
            Title = "量能档位（近期 vs 前期日均额/换手）",
            Tooltip =
                "前期 = 区间之前**等长**的一段，长度随区间自动变。用成交额与换手率而不用成交量——"
                + "volume 字段在科创板是「股」、其余板块是「手」，跨板块不可比（project_bar_volume_unit_bug）。"
                + $"放量日阈值 = 区间日均成交额 × {VolumeSpikeFactor}。",
        };

        var table = new DiagnosisTable
        {
            Columns =
            {
                new("区间", 62),
                new("日均额", 82, true),
                new("日均换手", 78, true),
                new("前期日均额", 92, true),
                new("量能比", 68, true),
            },
        };

        double mainRatio = double.NaN;
        foreach (var r in w.Ranges)
        {
            int len = bars.Count - r.StartIndex;
            double cur = Avg(bars, r.StartIndex, bars.Count, b => b.Amount);
            double turn = Avg(bars, r.StartIndex, bars.Count, b => b.Turnover);
            int p0 = Math.Max(0, r.StartIndex - len);
            double pre = p0 < r.StartIndex ? Avg(bars, p0, r.StartIndex, b => b.Amount) : double.NaN;
            double ratio = pre > 0 ? cur / pre : double.NaN;
            if (r.Label == w.Ranges[0].Label) mainRatio = ratio;

            table.Rows.Add(new[]
            {
                new DiagnosisCell(r.Label),
                new DiagnosisCell(Yi(cur), CellTone.Neutral),
                new DiagnosisCell($"{turn:F2}%"),
                double.IsNaN(pre) ? Dash() : new DiagnosisCell(Yi(pre), CellTone.Muted),
                double.IsNaN(ratio) ? Dash()
                    : new DiagnosisCell($"{ratio:P0}",
                        ratio < VolumeLowRatio || ratio > VolumeHighRatio ? CellTone.Alert : CellTone.Neutral),
            });
        }
        d.Tables.Add(table);

        // ── 放量日
        var main = w.Ranges[0];
        double avg = Avg(bars, main.StartIndex, bars.Count, b => b.Amount);
        var spikes = new DiagnosisTable
        {
            Caption = $"本波放量日（> 区间均值 {Yi(avg)} × {VolumeSpikeFactor}）",
            Columns = { new("日期", 82), new("成交额", 78, true), new("换手", 62, true), new("涨跌", 68, true), new("") },
        };
        int up = 0, total = 0;
        for (int i = Math.Max(main.StartIndex, 1); i < bars.Count; i++)
        {
            if (!(bars[i].Amount > avg * VolumeSpikeFactor)) continue;
            double chg = Pct(bars[i - 1].Close, bars[i].Close);
            total++;
            if (chg >= 0) up++;
            spikes.Rows.Add(new[]
            {
                new DiagnosisCell($"{bars[i].PeriodStart:yyyy-MM-dd}"),
                new DiagnosisCell(Yi(bars[i].Amount)),
                new DiagnosisCell($"{bars[i].Turnover:F2}%"),
                Signed(chg, "F2", "%"),
                new DiagnosisCell(chg >= 0 ? "放量上涨" : "放量下跌",
                    chg >= 0 ? CellTone.Positive : CellTone.Alert),
            });
        }
        if (spikes.Rows.Count > 0) d.Tables.Add(spikes);

        // ── 结论
        if (!double.IsNaN(mainRatio))
        {
            int len = bars.Count - main.StartIndex;
            int p0 = Math.Max(0, main.StartIndex - len);
            double pre = Avg(bars, p0, main.StartIndex, b => b.Amount);
            double cur = Avg(bars, main.StartIndex, bars.Count, b => b.Amount);
            bool falling = bars[^1].Close < bars[main.StartIndex].Close;
            string shape = mainRatio < VolumeLowRatio
                // 缩量的含义取决于价格方向：缩量下跌=没人恐慌砸也没人接，缩量上行=惜售
                ? (falling ? "，属缩量下跌而非放量抛售" : "，属缩量上行")
                : mainRatio > VolumeHighRatio
                    ? (falling ? "，放量下跌" : "，量能明显放大")
                    : "，量能与前期相当";
            d.Conclusions.Add($"本波量能为前期的 {mainRatio:P0}（{Yi(pre)} → {Yi(cur)}）{shape}");
        }
        if (total > 0)
        {
            string detail = up == 0 ? "全部是放量下跌" : up == total ? "全部是放量上涨" : $"其中 {up} 个放量上涨、{total - up} 个放量下跌";
            d.Conclusions.Add($"区间内 {total} 个放量日，{detail}"
                              + (up == 0 ? " —— 放量都出现在下跌方向" : up == total ? " —— 放量都出现在上涨方向" : ""));
        }
        else
        {
            d.Conclusions.Add("区间内没有明显放量日，成交额全程平稳");
        }
        return d;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  维度 3：资金流
    // ══════════════════════════════════════════════════════════════════════

    private static DiagnosisDimension BuildMoneyFlow(CapitalDiagnosisInput input, DiagnosisWindows w)
    {
        var d = new DiagnosisDimension
        {
            Index = 3,
            Title = "主力/超大单资金累计、散户反向",
            Tooltip =
                "五档按单笔成交额分：超大单 > 100 万、大单 20–100 万、中单 4–20 万、小单 < 4 万；主力 = 超大 + 大。"
                + (input.FlowStart is { } fs ? $"本票分档数据自 {fs:yyyy-MM-dd} 起" : "本票暂无分档数据")
                + (input.FlowMarketStart is { } ms ? $"、全市场自 {ms:yyyy-MM-dd} 起" : "")
                + "（东财接口只给 120 天，更早的回补不了，只能靠往后每天累积）。"
                + "起始日按标的各自算，不可用全表 MIN——全表 MIN 那天可能只有 1 只票，会虚报几个月可用历史。",
        };

        if (input.Flows.Count == 0)
        {
            d.Unavailable = input.FlowMarketStart is { } m
                ? $"本地分档资金流自 {m:yyyy-MM-dd} 起才有，这只票一条都没有——它可能在那之后才上市，或从未抓到。"
                : "本地还没有分档资金流数据。";
            return d;
        }

        var byDate = input.Flows.ToDictionary(f => f.TradeDate.Date);
        var bars = input.Bars;

        var table = new DiagnosisTable
        {
            Columns =
            {
                new("区间", 62),
                new("主力", 76, true), new("超大单", 76, true), new("大单", 76, true),
                new("中单", 76, true), new("小单", 76, true),
                new("覆盖", 64, true),
            },
        };
        foreach (var r in w.Ranges)
        {
            var rows = Slice(bars, r.StartIndex, byDate);
            if (rows.Count == 0)
            {
                table.Rows.Add(new[]
                {
                    new DiagnosisCell(r.Label),
                    Dash(), Dash(), Dash(), Dash(), Dash(),
                    new DiagnosisCell($"0/{r.TradingDays}日", CellTone.Alert),
                });
                continue;
            }
            table.Rows.Add(new[]
            {
                new DiagnosisCell(r.Label),
                YiCell(rows.Sum(x => x.MainNet ?? 0)),
                YiCell(rows.Sum(x => x.SuperNet ?? 0)),
                YiCell(rows.Sum(x => x.BigNet ?? 0)),
                YiCell(rows.Sum(x => x.MidNet ?? 0)),
                YiCell(rows.Sum(x => x.SmallNet ?? 0)),
                new DiagnosisCell($"{rows.Count}/{r.TradingDays}日",
                    rows.Count < r.TradingDays ? CellTone.Alert : CellTone.Muted),
            });
        }
        d.Tables.Add(table);

        var main = w.Ranges[0];
        var mainRows = Slice(bars, main.StartIndex, byDate);
        if (mainRows.Count < main.TradingDays)
            d.Warnings.Add($"本波区间 {main.TradingDays} 个交易日里只有 {mainRows.Count} 天有分档数据"
                           + (input.FlowStart is { } s && s > main.Start ? $"（本票分档数据 {s:yyyy-MM-dd} 才开始）" : "")
                           + " —— 累计值不含缺失那几天");

        if (mainRows.Count == 0) { d.Conclusions.Add("本波区间内没有分档资金流数据。"); return d; }

        double mainSum = mainRows.Sum(x => x.MainNet ?? 0);
        double superSum = mainRows.Sum(x => x.SuperNet ?? 0);
        double smallSum = mainRows.Sum(x => x.SmallNet ?? 0);
        string dir = mainSum < 0 ? "净流出" : "净流入";
        var line = $"本波主力{dir} {Yi(Math.Abs(mainSum))}（超大单 {YiSigned(superSum)}）";
        // 主力与小单反向是"机构派发/散户承接"的直接证据，同向则说明分歧不大
        if (Math.Sign(smallSum) != Math.Sign(mainSum) && Math.Abs(smallSum) > 0)
            line += $"，同期小单{(smallSum > 0 ? "净流入" : "净流出")} {YiSigned(smallSum)} —— "
                    + (mainSum < 0 ? "机构减仓、散户承接" : "机构加仓、散户在卖");
        d.Conclusions.Add(line);

        var last5 = mainRows.TakeLast(5).ToList();
        double m5 = last5.Sum(x => x.MainNet ?? 0);
        d.Conclusions.Add($"近 {last5.Count} 日主力 {YiSigned(m5)}，与区间方向"
                          + (Math.Sign(m5) == Math.Sign(mainSum) ? $"一致，仍在{dir}" : "相反，近期已转向"));

        // Top3：单日极值配上当天涨跌，能看出"是消息砸的还是慢慢渗的"
        bool outflow = mainSum < 0;
        var top = (outflow
            ? mainRows.OrderBy(x => x.MainNet ?? 0)
            : mainRows.OrderByDescending(x => x.MainNet ?? 0)).Take(3).ToList();
        var topTable = new DiagnosisTable
        {
            Caption = $"本波主力{dir} Top3",
            Columns = { new("日期", 82), new("主力", 76, true), new("超大单", 76, true), new("当日涨跌", 78, true) },
        };
        foreach (var x in top)
        {
            int i = IndexOfDate(bars, x.TradeDate);
            topTable.Rows.Add(new[]
            {
                new DiagnosisCell($"{x.TradeDate:yyyy-MM-dd}"),
                YiCell(x.MainNet ?? 0),
                YiCell(x.SuperNet ?? 0),
                i > 0 ? Signed(Pct(bars[i - 1].Close, bars[i].Close), "F2", "%") : Dash(),
            });
        }
        d.Tables.Add(topTable);
        return d;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  维度 4：杠杆与背离
    // ══════════════════════════════════════════════════════════════════════

    private static DiagnosisDimension BuildLeverage(CapitalDiagnosisInput input, DiagnosisWindows w)
    {
        var d = new DiagnosisDimension
        {
            Index = 4,
            Title = "融资余额与股价背离、融券变化",
            Tooltip =
                "融资余额 = 借钱买入尚未偿还的金额，升 = 杠杆资金在加仓；融券余额 = 借券卖出尚未偿还，升 = 做空在加。"
                + "两者与股价同向为顺势、反向为背离。**只有两融标的有数据**，非标的显示「非两融标的」而不是 0。"
                + $"交易所按 T+{input.MarginNormalLagDays} 披露，比行情天然晚一日，这是规律不是故障。　"
                + "**净买入 = 买入额 − 偿还额**，两种算法等价：沪市数据源直接给偿还额，深市不给、"
                + "改用融资余额的日变动（交易所自己的定义：本日融资余额 = 前日融资余额 + 本日买入 − 本日偿还，"
                + "所以余额差分恒等于净买入；实测两种算法在已定稿的历史数据上只差 ±1 元的舍入）。"
                + "⚠ 看净买入不要只看买入额：买入额是**总流水**，买了又还、还了又买都计在内。"
                + "宁德 2026-09-08 买入额 15.9 亿排区间第二，净买入却只有 2.53 亿——那天是大进大出，"
                + "不是单边抄底。⚠ 最新一个交易日的净买入可能随数据定稿而变，别当精确值。",
        };

        if (!input.IsMarginTarget)
        {
            d.Unavailable = "非两融标的 —— 这只票不在交易所的融资融券标的名单里，天然没有数据（不是漏抓，也不等于余额为 0）。";
            return d;
        }
        var main = w.Ranges[0];
        // 取**全集**而不是只取区间内：算区间第一天的净买入要用到它前一个交易日的余额。
        var all = input.Margins.OrderBy(m => m.TradeDate).ToList();
        int from = all.FindIndex(m => m.TradeDate.Date >= main.Start.Date);
        var rows = from < 0 ? new List<MarginDetailRow>() : all.Skip(from).ToList();
        if (rows.Count < 2)
        {
            d.Unavailable = "本波区间内两融数据不足两天，算不出变化。";
            return d;
        }

        // 滞后：跟该表**惯常**滞后比，超出才算异常——直接用"日期<K线日期"判会天天误报
        var bars = input.Bars;
        if (input.MarginLatest is { } latest)
        {
            int lag = bars.Count(b => b.PeriodStart.Date > latest.Date);
            if (lag > input.MarginNormalLagDays)
                d.Warnings.Add($"两融数据截止 {latest:yyyy-MM-dd}，比K线晚 {lag} 个交易日"
                               + $"（惯常只晚 {input.MarginNormalLagDays} 日）—— 超出披露规律，可能是漏抓");
            else if (lag > 0)
                d.Warnings.Add($"两融数据截止 {latest:yyyy-MM-dd}，比K线晚 {lag} 日 —— "
                               + "按交易所披露规律属正常滞后，但下方变化率不含最新一天");
        }
        foreach (var miss in input.MarginMissingDays)
            d.Warnings.Add($"{miss:yyyy-MM-dd} 全市场这一半没抓到，本票当天无两融数据 —— 首末值不受影响，但明细会漏这天");

        double b0 = rows[0].MarginBalance, b1 = rows[^1].MarginBalance;
        // 融券余额可空：沪市源头不给（rqylje 恒 null），要靠本地按「余量 × 收盘价」补算。
        // null 不是 0——没补算过就如实说"尚未补算"，不能显示成"融券余额为零"。
        double? s0 = rows[0].ShortBalance, s1 = rows[^1].ShortBalance;
        double px = Pct(bars[main.StartIndex].Close, bars[^1].Close);
        double mc = b0 > 0 ? Pct(b0, b1) : double.NaN;
        double sc = s0 is > 0 && s1.HasValue ? Pct(s0.Value, s1.Value) : double.NaN;
        if (!s0.HasValue || !s1.HasValue)
            d.Warnings.Add("融券余额尚未补算（沪市数据源不提供这一列，需按「融券余量 × 收盘价」本地补）"
                           + " —— 下方融券一行为空，不代表没有融券");
        double peak = rows.Max(r => r.MarginBalance);

        d.Tables.Add(new DiagnosisTable
        {
            Columns = { new("", 76), new("起", 92, true), new("末", 92, true), new("变化", 78, true) },
            Rows =
            {
                new[] { new DiagnosisCell("股价"), new DiagnosisCell($"{bars[main.StartIndex].Close:F2}"),
                        new DiagnosisCell($"{bars[^1].Close:F2}"), Signed(px, "F1", "%") },
                new[] { new DiagnosisCell("融资余额"), new DiagnosisCell(Yi(b0)),
                        new DiagnosisCell(Yi(b1)), double.IsNaN(mc) ? Dash() : Signed(mc, "F1", "%") },
                new[] { new DiagnosisCell("融券余额"),
                        s0.HasValue ? new DiagnosisCell(Yi(s0.Value)) : Dash(),
                        s1.HasValue ? new DiagnosisCell(Yi(s1.Value)) : Dash(),
                        double.IsNaN(sc) ? Dash() : Signed(sc, "F1", "%") },
            },
        });

        // ── 结论：背离是这个维度最有信息量的事
        if (!double.IsNaN(mc) && Math.Sign(px) != Math.Sign(mc) && Math.Abs(px) >= 1 && Math.Abs(mc) >= 1)
        {
            d.Conclusions.Add($"⚠ 背离：股价 {px:+0.0;-0.0}% 而融资余额 {mc:+0.0;-0.0}%"
                              + $"（{Yi(b0)} → {Yi(b1)}，峰值 {Yi(peak)}）");
            d.Conclusions.Add(px < 0
                ? "杠杆资金在逆势加仓，当前位置累积了一批高杠杆浮亏筹码"
                : "股价在涨但融资在撤，杠杆资金没有跟随这轮上行");
        }
        else if (!double.IsNaN(mc))
        {
            // ⚠ 走到这儿有**两种**完全不同的情况，2026-09-16 实机验证时才发现原先混成了一句：
            // 中国平安显示"股价 -5.9%、融资余额 +0.5%，方向一致 —— 杠杆资金顺势减仓"，
            // 符号明明相反却说"方向一致"，而"减仓"还是拿**股价**方向判的（融资余额实际微增）。
            // 单元测试没覆盖是因为造的数据都是"明显背离"或"明显同向"，没有"幅度太小"这一档。
            if (Math.Abs(mc) < 1)
                d.Conclusions.Add($"股价 {px:+0.0;-0.0}%，而融资余额几乎没动（{mc:+0.0;-0.0}%）"
                                  + " —— 杠杆资金既没明显加仓也没撤出");
            else if (Math.Abs(px) < 1)
                d.Conclusions.Add($"股价几乎没动（{px:+0.0;-0.0}%），融资余额 {mc:+0.0;-0.0}%"
                                  + $" —— 杠杆资金在{(mc > 0 ? "加仓" : "减仓")}，但价格没反应");
            else
                // 顺势/逆势说的是**融资余额**在加还是在减，所以方向必须取 mc 而不是 px
                d.Conclusions.Add($"股价 {px:+0.0;-0.0}%、融资余额 {mc:+0.0;-0.0}%，方向一致 —— 杠杆资金顺势"
                                  + (mc < 0 ? "减仓" : "加仓"));
        }
        if (!double.IsNaN(sc) && Math.Abs(sc) >= 10 && s0.HasValue && s1.HasValue)
            d.Conclusions.Add($"融券余额同期 {sc:+0.0;-0.0}%（{Yi(s0.Value)} → {Yi(s1.Value)}）—— "
                              + (sc > 0 ? "做空力量也在增加" : "做空力量在撤出"));

        // ── 净买入。**买入额和净买入必须并列**（2026-09-16 用户定）：只给买入额会把"大进大出"
        // 读成"猛加仓"（宁德 09-08 买入 15.9 亿 / 净买入 2.53 亿），只给净买入又看不出流水规模。
        var net = NetBuySeries(all, from);
        var withNet = rows.Select((r, k) => (Row: r, Net: net[k])).Where(x => x.Net.HasValue).ToList();
        if (withNet.Count > 0)
        {
            // 排序方向跟区间累计方向一致——融资在增就看加得最多的几天，在减就看减得最多的几天
            double sum = withNet.Sum(x => x.Net!.Value);
            var top = (sum >= 0
                ? withNet.OrderByDescending(x => x.Net!.Value)
                : withNet.OrderBy(x => x.Net!.Value)).Take(3).ToList();
            var t = new DiagnosisTable
            {
                Caption = $"本波融资净{(sum >= 0 ? "买入" : "偿还")} Top3（买入额是总流水，净买入才是增仓）",
                Columns = { new("日期", 82), new("融资买入", 82, true), new("净买入", 82, true), new("当日涨跌", 78, true) },
            };
            foreach (var x in top)
            {
                int i = IndexOfDate(bars, x.Row.TradeDate);
                t.Rows.Add(new[]
                {
                    new DiagnosisCell($"{x.Row.TradeDate:yyyy-MM-dd}"),
                    new DiagnosisCell(Yi(x.Row.MarginBuy), CellTone.Muted),
                    YiCell(x.Net!.Value),
                    i > 0 ? Signed(Pct(bars[i - 1].Close, bars[i].Close), "F2", "%") : Dash(),
                });
            }
            d.Tables.Add(t);

            double buySum = withNet.Sum(x => x.Row.MarginBuy);
            if (buySum > 0 && Math.Abs(sum) < buySum * 0.2)
                d.Conclusions.Add($"区间融资买入额合计 {Yi(buySum)}，净{(sum >= 0 ? "买入" : "偿还")}仅 {Yi(Math.Abs(sum))}"
                                  + " —— 融资盘大进大出，真实增仓远小于流水");
        }
        return d;
    }

    /// <summary>
    /// 逐日融资净买入。返回的数组跟 <c>all[from..]</c> 一一对应，算不出来的位置是 null。
    ///
    /// 两种口径，**结果等价**（实测差 ±1 元的舍入）：
    /// · 源头给了偿还额（沪市 rzche）→ 直接 <c>买入 − 偿还</c>，这是交易所口径；
    /// · 没给（深市）→ 用融资余额的日变动。依据是交易所报表自己写的恒等式
    ///   「本日融资余额 = 前日融资余额 + 本日买入 − 本日偿还」，所以差分恒等于净买入。
    ///
    /// ⚠ 差分要求**相邻两行是相邻交易日**。两融有整天缺数据的情况（实测 2026-08-21 /
    /// 2026-09-02 深市整天没抓到），跨过缺口差出来的是多日累计而不是单日净买入——
    /// 那种位置返回 null，宁可没有也不给个错的。缺口本身另有告警（MarginMissingDays）。
    /// </summary>
    private static double?[] NetBuySeries(List<MarginDetailRow> all, int from)
    {
        var result = new double?[Math.Max(0, all.Count - Math.Max(0, from))];
        if (from < 0) return result;
        for (int i = from; i < all.Count; i++)
        {
            var r = all[i];
            int k = i - from;
            // ⚠ 锚点日那一天**不算**（2026-09-16 对账时发现的口径不一致）：区间的"起"是锚点日
            // **收盘后**的余额（表格里那个 220.39亿），所以锚点日当天的净买入发生在"到达高点
            // 的过程中"，不属于"从高点以来"。股价那行就是这个口径（高点收盘 → 现在收盘）。
            // 不排除的话，宁德实测净买入累加 13.63亿 vs 余额增量 15.78亿，差的正好是锚点日
            // 当天的 -2.16亿——用户自己对账会对不上。
            if (k == 0) continue;
            // 官方口径优先
            if (r.MarginRepay is { } repay) { result[k] = r.MarginBuy - repay; continue; }
            if (i == 0) continue;                       // 没有前一天，差不出来
            var prev = all[i - 1];
            // 相邻两行必须是相邻交易日。这里用"中间有几个工作日"近似判断：隔了不止一个
            // 工作日说明中间有交易日缺数据（节假日不算），那就不给值。
            int businessDays = 0;
            for (var day = prev.TradeDate.Date.AddDays(1); day <= r.TradeDate.Date; day = day.AddDays(1))
                if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) businessDays++;
            if (businessDays > 1) continue;
            result[k] = r.MarginBalance - prev.MarginBalance;
        }
        return result;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  维度 5：大宗交易
    // ══════════════════════════════════════════════════════════════════════

    private static DiagnosisDimension BuildBlockTrade(CapitalDiagnosisInput input, DiagnosisWindows w)
    {
        var d = new DiagnosisDimension
        {
            Index = 5,
            Title = "大宗交易性质（折溢价/对倒/甩卖）",
            Tooltip =
                "溢价率 = 成交价相对当日收盘的偏离。大幅折价常是股东减持套现，溢价接盘可能是产业资本，"
                + "平价对倒多为机构间调仓或换券商席位。⚠ 溢价率按**成交金额加权**："
                + "简单平均会被小额单带偏（宁德实测 33 笔简单平均 -1.13%、加权仅 -0.05%，两笔 288 万的"
                + "小单占总额 0.14% 却改变了结论方向）。分类占比同样按金额而非笔数，且**向下取整不进位**"
                + "（否则 99.7% 会显示成 100%，与折价 0.3% 相加超过 100%）。",
        };

        var main = w.Ranges[0];
        var list = input.BlockTrades.Where(b => b.TradeDate.Date >= main.Start.Date
                                                && b.DealAmount is > 0).ToList();
        if (list.Count == 0)
        {
            d.Conclusions.Add("本波区间内没有大宗交易 —— 没有大额筹码通过场外易手。");
            return d;
        }

        double total = list.Sum(b => b.DealAmount ?? 0);
        double weighted = list.Sum(b => (b.DealAmount ?? 0) * (b.PremiumRatio ?? 0) * 100) / total;
        double simple = list.Average(b => (b.PremiumRatio ?? 0) * 100);
        double ShareBy(Func<double, bool> f) =>
            list.Where(b => f((b.PremiumRatio ?? 0) * 100)).Sum(b => b.DealAmount ?? 0) / total;
        double flat = ShareBy(p => Math.Abs(p) < BlockFlatBand);
        double disc = ShareBy(p => p < BlockDiscount);
        double prem = ShareBy(p => p > BlockPremium);
        double inst = list.Where(b => b.BuyerName == "机构专用" && b.SellerName == "机构专用")
                          .Sum(b => b.DealAmount ?? 0) / total;

        d.Tables.Add(new DiagnosisTable
        {
            Columns = { new("", 168), new("金额占比", 82, true) },
            Rows =
            {
                new[] { new DiagnosisCell($"平价对倒（|溢价| < {BlockFlatBand}%）"), new DiagnosisCell(Floor1(flat)) },
                new[] { new DiagnosisCell($"折价（< {BlockDiscount}%）"),
                        new DiagnosisCell(Floor1(disc), disc > 0.05 ? CellTone.Alert : CellTone.Neutral) },
                new[] { new DiagnosisCell($"溢价（> {BlockPremium}%）"), new DiagnosisCell(Floor1(prem)) },
                new[] { new DiagnosisCell("机构专用 ↔ 机构专用"), new DiagnosisCell(Floor1(inst), CellTone.Muted) },
            },
        });

        string nature = disc > 0.20 ? "存在显著折价出货"
            : flat > 0.80 ? "以机构间平价调仓为主，无折价甩卖迹象"
            : prem > 0.20 ? "有溢价接盘，可能是产业资本进场"
            : "折溢价结构分散，没有单一性质占主导";
        d.Conclusions.Add($"{list.Count} 笔 / {Yi(total)}，金额加权溢价率 {weighted:+0.00;-0.00}%，"
                          + $"平价对倒占金额 {Floor1(flat)} —— {nature}");
        if (Math.Abs(weighted - simple) > 0.3)
            d.Conclusions.Add($"（简单平均为 {simple:+0.00;-0.00}%，被小额单带偏，不采用）");
        return d;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  维度 6：龙虎榜
    // ══════════════════════════════════════════════════════════════════════

    private static DiagnosisDimension BuildLhb(CapitalDiagnosisInput input, DiagnosisWindows w)
    {
        var d = new DiagnosisDimension
        {
            Index = 6,
            Title = "龙虎榜",
            Tooltip =
                "交易所对涨跌幅/换手/振幅达标的个股披露买卖前 5 席位。"
                + "**没上榜本身是信息**：说明这段时间的波动没有触发交易所的异动披露阈值，"
                + "也就没有游资席位的痕迹 —— 不是数据缺失。",
        };
        var main = w.Ranges[0];
        int n = input.LhbDates.Count;
        if (n == 0)
        {
            d.Conclusions.Add(input.LhbLastEver is { } last
                ? $"本波 0 次上榜，最近一次远在 {last:yyyy-MM-dd} —— 这波{(input.Bars[^1].Close < input.Bars[main.StartIndex].Close ? "下跌" : "上涨")}没有游资参与的痕迹"
                : "本波 0 次上榜，历史上也从未上过榜 —— 没有游资参与的痕迹");
        }
        else
        {
            d.Conclusions.Add($"本波 {n} 次上榜（{string.Join("、", input.LhbDates.OrderBy(x => x).Select(x => $"{x:M-d}"))}）"
                              + " —— 波动已触发交易所异动披露，有游资席位参与");
        }
        d.Tables.Add(new DiagnosisTable
        {
            Columns = { new("", 110), new("", 0) },
            Rows =
            {
                new[] { new DiagnosisCell("本波上榜次数"), new DiagnosisCell(n.ToString()) },
                new[] { new DiagnosisCell("最近一次上榜"),
                        new DiagnosisCell(input.LhbLastEver is { } l ? $"{l:yyyy-MM-dd}" : "从未", CellTone.Muted) },
            },
        });
        return d;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  全局提示
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 点名**矛盾信号**——这是不合成总分的理由本身。宁德时代 2026-09 实测就是
    /// "主力在跑 + 融资在买"，压成一个分数这个矛盾就没了。
    /// 措辞用维度名不用编号：界面上的标题已经不带"维度 N/6"了（2026-09-16），提编号对不上。
    /// </summary>
    private static string BuildGlobalNote(CapitalDiagnosis r)
    {
        var flow = r.Dimensions.FirstOrDefault(x => x.Index == 3);
        var lev = r.Dimensions.FirstOrDefault(x => x.Index == 4);
        bool flowOut = flow?.Conclusions.FirstOrDefault()?.Contains("净流出") == true;
        bool flowIn = flow?.Conclusions.FirstOrDefault()?.Contains("净流入") == true;
        bool levDiverge = lev?.Conclusions.Any(c => c.StartsWith("⚠ 背离")) == true;

        if ((flowOut || flowIn) && levDiverge)
            return "六个维度不合成总分 —— 主力资金与杠杆资金方向矛盾，"
                   + "压成一个分数会把这个矛盾抹掉，而它恰恰是当前最该被看见的信息。";
        return "六个维度不合成总分：把它们压成一个分数会掩盖维度之间的矛盾信号，"
               + "而矛盾恰恰是最值得注意的地方。";
    }

    // ══════════════════════════════════════════════════════════════════════
    //  小工具
    // ══════════════════════════════════════════════════════════════════════

    private static double Pct(double from, double to) => from > 0 ? (to / from - 1) * 100 : double.NaN;

    private static double Avg(IReadOnlyList<Bar> bars, int from, int to, Func<Bar, double> pick)
    {
        if (to <= from) return double.NaN;
        double s = 0;
        for (int i = from; i < to; i++) s += pick(bars[i]);
        return s / (to - from);
    }

    private static List<NetInflowDetail> Slice(IReadOnlyList<Bar> bars, int start,
        Dictionary<DateTime, NetInflowDetail> byDate)
    {
        var list = new List<NetInflowDetail>();
        for (int i = start; i < bars.Count; i++)
            if (byDate.TryGetValue(bars[i].PeriodStart.Date, out var f)) list.Add(f);
        return list;
    }

    private static int IndexOfDate(IReadOnlyList<Bar> bars, DateTime d)
    {
        for (int i = bars.Count - 1; i >= 0; i--)
            if (bars[i].PeriodStart.Date == d.Date) return i;
        return -1;
    }

    /// <summary>元 → "12.3亿" / "4560万"。诊断里的金额跨度从几百万到几百亿，固定单位会很难读。</summary>
    private static string Yi(double yuan)
    {
        double abs = Math.Abs(yuan);
        if (double.IsNaN(yuan)) return "—";
        if (abs >= 1e8) return $"{yuan / 1e8:F1}亿";
        if (abs >= 1e4) return $"{yuan / 1e4:F0}万";
        return $"{yuan:F0}";
    }

    private static string YiSigned(double yuan) => (yuan >= 0 ? "+" : "-") + Yi(Math.Abs(yuan));

    private static DiagnosisCell YiCell(double yuan) =>
        new(YiSigned(yuan), yuan >= 0 ? CellTone.Positive : CellTone.Negative);

    private static DiagnosisCell Signed(double v, string fmt, string suffix)
    {
        if (double.IsNaN(v)) return Dash();
        string t = (v >= 0 ? "+" : "") + v.ToString(fmt) + suffix;
        return new DiagnosisCell(t, v >= 0 ? CellTone.Positive : CellTone.Negative);
    }

    private static DiagnosisCell Dash() => new("—", CellTone.Muted);

    /// <summary>占比**向下取整**到 0.1%——不进位，否则 99.7% 会变 100%、跟折价那 0.3% 相加超过 100%。</summary>
    private static string Floor1(double ratio) => $"{Math.Floor(ratio * 1000) / 10:F1}%";
}
