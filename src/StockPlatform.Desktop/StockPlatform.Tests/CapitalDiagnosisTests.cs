using System.Text;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 资金面诊断（doc/capital-diagnosis-design.md）。
///
/// 分两类：
/// · <b>纯算法测试</b>（造数据，随时可跑）——锚点、降级、阈值、格式化这些规则的回归护栏。
///   结论句是**规则拼装**的，所以"同一组数据永远得出同一句话"本身就是可测的。
/// · <b>对数测试</b>（读真库，默认 Skip）——跟写产品代码前那份 Python 原型的输出比对。
///   写产品代码之前那份原型已经用真实数据验过口径，这里确认 C# 实现跟它一致。
///   真库路径在别人机器上不存在，所以默认跳过，要跑就去掉 Skip。
/// </summary>
public class CapitalDiagnosisTests
{
    private readonly ITestOutputHelper _out;
    public CapitalDiagnosisTests(ITestOutputHelper output) => _out = output;

    private const string RealDb = @"C:\Chingli\Git\stock\publish\data\local\current.sqlite";

    // ═══════════════════ 纯算法 ═══════════════════

    /// <summary>锚点必须落在近 lookback 日的最高收盘那天，不是全历史最高。</summary>
    [Fact]
    public void 锚点取近60日最高收盘_不是全历史最高()
    {
        // 第 0 天是全历史最高（1000），但它在 60 日窗口之外，不该被选中
        var bars = new List<Bar> { Bar(0, 1000) };
        for (int i = 1; i <= 70; i++) bars.Add(Bar(i, 100 + (i == 30 ? 50 : 0)));

        var w = CapitalDiagnosisAnalyzer.ResolveWindows(bars);

        Assert.Equal(bars[30].PeriodStart, w.AnchorDate);
        Assert.Equal(30, w.AnchorIndex);
    }

    /// <summary>涨势里锚点自然落在最近——"不管涨跌都给同样诊断"就是靠这个实现的。</summary>
    [Fact]
    public void 单调上涨时锚点落在最新一根()
    {
        var bars = Enumerable.Range(0, 80).Select(i => Bar(i, 100 + i)).ToList();

        var w = CapitalDiagnosisAnalyzer.ResolveWindows(bars);

        Assert.Equal(bars.Count - 1, w.AnchorIndex);
        Assert.Equal("本波", w.Ranges[0].Label);
    }

    /// <summary>三个区间齐备：本波 + 两个固定对照。固定对照不是装饰，见设计文档 §3。</summary>
    [Fact]
    public void 数据够长时给三个区间()
    {
        var bars = Enumerable.Range(0, 100).Select(i => Bar(i, 100 - i)).ToList();

        var w = CapitalDiagnosisAnalyzer.ResolveWindows(bars);

        Assert.Equal(new[] { "本波", "近20日", "近60日" }, w.Ranges.Select(r => r.Label).ToArray());
    }

    /// <summary>历史太短时不该硬凑出根本没有的区间。</summary>
    [Fact]
    public void 历史不足时只给本波()
    {
        var bars = Enumerable.Range(0, 10).Select(i => Bar(i, 100 - i)).ToList();

        var w = CapitalDiagnosisAnalyzer.ResolveWindows(bars);

        Assert.Single(w.Ranges);
    }

    /// <summary>没有K线不能抛异常，要返回带 Error 的结果——界面靠它显示提示。</summary>
    [Fact]
    public void 没有K线时返回Error而不是抛异常()
    {
        var r = new CapitalDiagnosisAnalyzer().Analyze(
            new CapitalDiagnosisInput { Code = "000001" },
            CapitalDiagnosisAnalyzer.ResolveWindows(Array.Empty<Bar>()));

        Assert.NotNull(r.Error);
        Assert.Empty(r.Dimensions);
    }

    /// <summary>六个维度一个都不能少——界面按"维度 N/6"显示。</summary>
    [Fact]
    public void 总是产出六个维度()
    {
        var r = Analyze(Falling());

        Assert.Equal(6, r.Dimensions.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, r.Dimensions.Select(d => d.Index).ToArray());
    }

    /// <summary>
    /// **每个维度都必须有结论句**。这是本功能成立与否的关键：早期原型只给表格，
    /// 六个维度铺开后跟档案窗口的原始表没有区别（2026-09-16 用户反馈）。
    /// 维度整个不可用时（非两融标的）用 Unavailable 顶替，但两者不能同时为空。
    /// </summary>
    [Fact]
    public void 每个维度都有结论句或不可用说明()
    {
        var r = Analyze(Falling());

        foreach (var d in r.Dimensions)
            Assert.True(d.Conclusions.Count > 0 || d.Unavailable != null,
                $"维度{d.Index}「{d.Title}」既没有结论句也没有不可用说明");
    }

    /// <summary>非两融标的必须显式说明，不能显示成"余额 0"——两者含义完全不同。</summary>
    [Fact]
    public void 非两融标的显示不可用而不是零()
    {
        var r = Analyze(Falling());   // 默认 IsMarginTarget = false

        var lev = r.Dimensions.Single(d => d.Index == 4);
        Assert.NotNull(lev.Unavailable);
        Assert.Contains("非两融标的", lev.Unavailable);
    }

    /// <summary>股价跌、融资余额升 → 必须报背离。这是这个维度信息量最高的一条。</summary>
    [Fact]
    public void 股价跌而融资升时报背离()
    {
        var bars = Falling();
        var input = new CapitalDiagnosisInput
        {
            Code = "300750",
            Bars = bars,
            IsMarginTarget = true,
            Margins = bars.Select((b, i) => new MarginDetailRow
            {
                TradeDate = b.PeriodStart,
                MarginBalance = 100e8 + i * 1e8,     // 一路加杠杆
                ShortBalance = 1e8,
                MarginBuy = 5e8,
            }).ToList(),
        };

        var r = new CapitalDiagnosisAnalyzer().Analyze(
            input, CapitalDiagnosisAnalyzer.ResolveWindows(bars));

        var lev = r.Dimensions.Single(d => d.Index == 4);
        Assert.Contains(lev.Conclusions, c => c.StartsWith("⚠ 背离"));
        Assert.Contains(lev.Conclusions, c => c.Contains("逆势加仓"));
    }

    /// <summary>方向一致时**不能**报背离——误报会让这个告警失去意义。</summary>
    [Fact]
    public void 股价跌融资也跌时不报背离()
    {
        var bars = Falling();
        var input = new CapitalDiagnosisInput
        {
            Code = "300750",
            Bars = bars,
            IsMarginTarget = true,
            Margins = bars.Select((b, i) => new MarginDetailRow
            {
                TradeDate = b.PeriodStart,
                MarginBalance = 200e8 - i * 1e8,     // 跟着减
                ShortBalance = 1e8,
            }).ToList(),
        };

        var r = new CapitalDiagnosisAnalyzer().Analyze(
            input, CapitalDiagnosisAnalyzer.ResolveWindows(bars));

        var lev = r.Dimensions.Single(d => d.Index == 4);
        Assert.DoesNotContain(lev.Conclusions, c => c.StartsWith("⚠ 背离"));
        Assert.Contains(lev.Conclusions, c => c.Contains("方向一致"));
    }

    /// <summary>
    /// 符号相反但**幅度太小**（融资余额变化 &lt;1%）时，不许说"方向一致"。
    ///
    /// 这一档是 2026-09-16 实机验证抓出来的：中国平安显示"股价 -5.9%、融资余额 +0.5%，
    /// 方向一致 —— 杠杆资金顺势减仓"——符号明明相反却说一致，"减仓"还是拿股价方向判的
    /// （融资余额实际是微增）。之前的测试只造了"明显背离"和"明显同向"两档，正好漏过中间这档。
    /// </summary>
    [Fact]
    public void 融资余额变化太小时不说方向一致也不说背离()
    {
        var bars = Falling();                     // 股价从 400 跌到 321
        var input = new CapitalDiagnosisInput
        {
            Code = "601318",
            Bars = bars,
            IsMarginTarget = true,
            // 融资余额全程只涨 0.5%——符号跟股价相反，但幅度够不上"背离"
            Margins = bars.Select((b, i) => new MarginDetailRow
            {
                TradeDate = b.PeriodStart,
                MarginBalance = 100e8 * (1 + 0.005 * i / (bars.Count - 1)),
                ShortBalance = 1e8,
            }).ToList(),
        };

        var r = new CapitalDiagnosisAnalyzer().Analyze(
            input, CapitalDiagnosisAnalyzer.ResolveWindows(bars));

        var lev = r.Dimensions.Single(d => d.Index == 4);
        Assert.DoesNotContain(lev.Conclusions, c => c.Contains("方向一致"));
        Assert.DoesNotContain(lev.Conclusions, c => c.StartsWith("⚠ 背离"));
        Assert.Contains(lev.Conclusions, c => c.Contains("几乎没动"));
    }

    /// <summary>
    /// **锚点日当天的净买入不计入区间**——区间的"起"是锚点日**收盘后**的余额。
    ///
    /// 2026-09-16 对账时发现口径不一致：宁德界面显示净买入累加 13.63亿，而表格里
    /// 融资余额是 220.39亿 → 236.17亿（增量 15.78亿），差的 2.15亿正好是锚点日当天的
    /// 净买入 -2.16亿。那天的净买入属于"到达高点的过程"，不该计入"从高点以来"
    /// ——股价那行就是这个口径（高点收盘 → 现在收盘）。
    ///
    /// 造数据要点：锚点必须落在**中间**（前面得有数据才差分得出锚点日自己的净买入），
    /// 所以让第 5 根价格最高；锚点日的余额设一个突变（100亿 → 50亿，净偿还 50亿），
    /// 它若被算进去就会出现在 Top3 里。
    /// </summary>
    [Fact]
    public void 锚点日当天的净买入不计入区间()
    {
        // 第 5 根最高 → 锚点落在 index 5，它前面还有 5 根可供差分
        var bars = Enumerable.Range(0, 50)
            .Select(i => Bar(i, i == 5 ? 500 : 400 - i)).ToList();
        var margins = bars.Select((b, i) => new MarginDetailRow
        {
            TradeDate = b.PeriodStart,
            // index 0-4 = 100亿；index 5（锚点日）突降到 50亿；之后每天 +1亿
            MarginBalance = i < 5 ? 100e8 : (i == 5 ? 50e8 : 50e8 + (i - 5) * 1e8),
            ShortBalance = 1e8,
            MarginBuy = 2e8,
        }).ToList();
        var input = new CapitalDiagnosisInput
        {
            Code = "300750", Bars = bars, IsMarginTarget = true, Margins = margins,
        };
        var w = CapitalDiagnosisAnalyzer.ResolveWindows(bars);
        Assert.Equal(5, w.AnchorIndex);                    // 前提：锚点在第 5 根

        var r = new CapitalDiagnosisAnalyzer().Analyze(input, w);
        var lev = r.Dimensions.Single(d => d.Index == 4);
        var netTable = lev.Tables.FirstOrDefault(t => t.Caption.Contains("净买入"));
        Assert.NotNull(netTable);

        // 锚点日那天净偿还 50亿，**不该出现**在 Top3 里
        Assert.DoesNotContain(netTable!.Rows, row => row.Any(c => c.Text.Contains("50.0亿")));
        // 锚点日之后每天净买入 +1亿，Top3 应该全是这个
        Assert.All(netTable.Rows.Skip(1),                  // Skip(1) 跳过表头行
            row => Assert.Contains(row, c => c.Text.Contains("+1.0亿")));
    }

    /// <summary>"顺势加仓/减仓"说的是**融资余额**的方向，不是股价的方向。</summary>
    [Fact]
    public void 顺势加减仓取融资余额的方向而不是股价()
    {
        var bars = Falling();                     // 股价跌
        var input = new CapitalDiagnosisInput
        {
            Code = "601318",
            Bars = bars,
            IsMarginTarget = true,
            // 融资余额同向大幅下降 → 应当说"顺势减仓"
            Margins = bars.Select((b, i) => new MarginDetailRow
            {
                TradeDate = b.PeriodStart,
                MarginBalance = 200e8 - i * 1e8,
                ShortBalance = 1e8,
            }).ToList(),
        };

        var r = new CapitalDiagnosisAnalyzer().Analyze(
            input, CapitalDiagnosisAnalyzer.ResolveWindows(bars));

        var lev = r.Dimensions.Single(d => d.Index == 4);
        Assert.Contains(lev.Conclusions, c => c.Contains("方向一致") && c.Contains("减仓"));
    }

    /// <summary>
    /// 大宗溢价率必须**金额加权**。宁德实测：两笔 288 万的小单（占总额 0.14%）
    /// 把简单平均从 -0.05% 拉到 -1.13%，结论方向都变了。
    /// </summary>
    [Fact]
    public void 大宗溢价率按金额加权_小额单不能带偏结论()
    {
        var bars = Falling();
        var day = bars[^1].PeriodStart;
        var input = new CapitalDiagnosisInput
        {
            Code = "300750",
            Bars = bars,
            BlockTrades = new List<BlockTrade>
            {
                // 一大笔平价 + 两小笔深折价，加权后应当仍是平价
                new() { TradeDate = day, DealAmount = 10e8, PremiumRatio = 0, BuyerName = "机构专用", SellerName = "机构专用" },
                new() { TradeDate = day, DealAmount = 288e4, PremiumRatio = -0.187 },
                new() { TradeDate = day, DealAmount = 288e4, PremiumRatio = -0.187 },
            },
        };

        var r = new CapitalDiagnosisAnalyzer().Analyze(
            input, CapitalDiagnosisAnalyzer.ResolveWindows(bars));

        var blk = r.Dimensions.Single(d => d.Index == 5);
        Assert.Contains(blk.Conclusions, c => c.Contains("无折价甩卖迹象"));
        // 简单平均会是 -12.5%，加权只有 -0.1% 上下；结论里报的必须是加权值
        Assert.Contains(blk.Conclusions, c => c.Contains("-0.1") || c.Contains("-0.0"));
    }

    /// <summary>占比向下取整、不进位——否则 99.7% 显示成 100%，跟折价那 0.3% 相加超过 100%。</summary>
    [Fact]
    public void 占比不进位()
    {
        var bars = Falling();
        var day = bars[^1].PeriodStart;
        var input = new CapitalDiagnosisInput
        {
            Code = "300750",
            Bars = bars,
            BlockTrades = new List<BlockTrade>
            {
                new() { TradeDate = day, DealAmount = 997e6, PremiumRatio = 0 },
                new() { TradeDate = day, DealAmount = 3e6, PremiumRatio = -0.187 },
            },
        };

        var r = new CapitalDiagnosisAnalyzer().Analyze(
            input, CapitalDiagnosisAnalyzer.ResolveWindows(bars));

        var table = r.Dimensions.Single(d => d.Index == 5).Tables[0];
        var flat = table.Rows[0][1].Text;
        Assert.Equal("99.7%", flat);   // 不是 100.0%
    }

    /// <summary>主力流出 + 融资加仓 = 矛盾信号，全局提示必须点名它（这是不合成总分的理由）。</summary>
    [Fact]
    public void 主力流出与融资加仓并存时全局提示点名矛盾()
    {
        var bars = Falling();
        var input = new CapitalDiagnosisInput
        {
            Code = "300750",
            Bars = bars,
            IsMarginTarget = true,
            Margins = bars.Select((b, i) => new MarginDetailRow
            {
                TradeDate = b.PeriodStart, MarginBalance = 100e8 + i * 1e8, ShortBalance = 1e8,
            }).ToList(),
            Flows = bars.Select(b => new NetInflowDetail
            {
                TradeDate = b.PeriodStart,
                MainNet = -1e8, SuperNet = -6e7, BigNet = -4e7, MidNet = 0, SmallNet = 1e8,
            }).ToList(),
        };

        var r = new CapitalDiagnosisAnalyzer().Analyze(
            input, CapitalDiagnosisAnalyzer.ResolveWindows(bars));

        Assert.Contains("矛盾", r.GlobalNote);
        Assert.Contains("不合成总分", r.GlobalNote);
    }

    /// <summary>分档资金流覆盖不满区间时必须告警——累计值不含缺失那几天，不说会被当成完整。</summary>
    [Fact]
    public void 分档数据覆盖不满时告警()
    {
        var bars = Falling();
        var input = new CapitalDiagnosisInput
        {
            Code = "300750",
            Bars = bars,
            // 只给最后 3 天
            Flows = bars.TakeLast(3).Select(b => new NetInflowDetail
            {
                TradeDate = b.PeriodStart, MainNet = -1e8, SuperNet = -1e8, SmallNet = 1e8,
            }).ToList(),
            FlowStart = bars[^3].PeriodStart,
        };

        var r = new CapitalDiagnosisAnalyzer().Analyze(
            input, CapitalDiagnosisAnalyzer.ResolveWindows(bars));

        var flow = r.Dimensions.Single(d => d.Index == 3);
        Assert.NotEmpty(flow.Warnings);
        Assert.Contains(flow.Warnings, x => x.Contains("只有 3 天"));
    }

    /// <summary>
    /// 两融滞后要区分"正常"和"真落后"。两所 T+1 披露，晚一天是规律不是故障——
    /// 直接用"日期 &lt; K线日期"判会天天误报。
    /// </summary>
    [Fact]
    public void 两融晚一天算正常滞后_晚太多才算漏抓()
    {
        var bars = Falling();

        var normal = LeverageWarnings(bars, lagDays: 1);
        Assert.Contains(normal, w => w.Contains("正常滞后"));
        Assert.DoesNotContain(normal, w => w.Contains("可能是漏抓"));

        var late = LeverageWarnings(bars, lagDays: 5);
        Assert.Contains(late, w => w.Contains("可能是漏抓"));
    }

    private static List<string> LeverageWarnings(List<Bar> bars, int lagDays)
    {
        var rows = bars.SkipLast(lagDays).Select((b, i) => new MarginDetailRow
        {
            TradeDate = b.PeriodStart, MarginBalance = 100e8 + i * 1e7, ShortBalance = 1e8,
        }).ToList();
        var input = new CapitalDiagnosisInput
        {
            Code = "300750", Bars = bars, IsMarginTarget = true,
            Margins = rows, MarginLatest = rows[^1].TradeDate, MarginNormalLagDays = 1,
        };
        var r = new CapitalDiagnosisAnalyzer().Analyze(
            input, CapitalDiagnosisAnalyzer.ResolveWindows(bars));
        return r.Dimensions.Single(d => d.Index == 4).Warnings;
    }

    // ═══════════════════ 对数（读真库，默认跳过）═══════════════════

    /// <summary>
    /// 跟写产品代码前那份 Python 原型的输出比对。原型是一次性脚本、已按"诊断脚本只写
    /// scratchpad"的规矩清掉，**基准值抄在下面**，所以脚本本身丢了也不影响这个测试。
    /// 原型跑出来的基准值（宁德时代，
    /// 锚点 2026-08-05 @ 403.80，数据截止 2026-09-15）：
    ///   本波 -21.65% / 创业板指 -8.12% / 同业中位 -5.02%
    ///   量能 94.9亿，前期 138.6亿，比值 69%；3 个放量日全部放量下跌
    ///   主力 -85.3亿、超大单 -48.3亿、小单 +83.4亿
    ///   融资 220.4→236.2亿（+7.2%），股价 -21.7% → 背离
    ///   大宗 33 笔/19.9亿，金额加权 -0.05%，平价对倒 99.7%
    ///   龙虎榜 0 次，最近一次 2020-07-07
    /// 去掉 Skip 手动跑，输出打在测试日志里。
    /// </summary>
    [Fact(Skip = "读本机真库，别人机器上没有；要对数时去掉这行。2026-09-16 实跑：与原型逐条一致")]
    public void 对数_宁德时代()
    {
        var reader = new StockPlatform.Data.Sqlite.SqliteCapitalDiagnosisReader(RealDb);
        var (input, windows) = reader.Read("300750", "宁德时代");
        var r = new CapitalDiagnosisAnalyzer().Analyze(input, windows);

        _out.WriteLine(Dump(r));

        Assert.Null(r.Error);
        Assert.Equal(6, r.Dimensions.Count);
        Assert.Equal(new DateTime(2026, 8, 5), windows.AnchorDate);
        Assert.Equal(403.80, r.AnchorClose, 2);
        // 原型算出 -21.65%；用 InRange 而不是 Equal(…, precision)——后者在 1 位小数下
        // 会把 -21.654 和 -21.65 各自舍入到不同的值，纯粹是断言写法的坑，不是算错了
        Assert.InRange(r.AnchorChangePct, -21.7, -21.6);
    }

    private static string Dump(CapitalDiagnosis r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{r.Code} {r.Name}  截止 {r.AsOf:yyyy-MM-dd}  最新 {r.LatestClose:F2}");
        sb.AppendLine($"锚点 {r.Windows.AnchorDate:yyyy-MM-dd} @ {r.AnchorClose:F2}  累计 {r.AnchorChangePct:+0.00;-0.00}%");
        foreach (var d in r.Dimensions)
        {
            sb.AppendLine();
            sb.AppendLine($"── 维度 {d.Index}/6 · {d.Title}");
            foreach (var w in d.Warnings) sb.AppendLine($"   ⚠ {w}");
            if (d.Unavailable != null) sb.AppendLine($"   （不可用）{d.Unavailable}");
            foreach (var c in d.Conclusions) sb.AppendLine($"   结论：{c}");
            foreach (var t in d.Tables)
            {
                if (t.Caption.Length > 0) sb.AppendLine($"   [{t.Caption}]");
                sb.AppendLine("   " + string.Join(" | ", t.Columns.Select(c => c.Header)));
                foreach (var row in t.Rows)
                    sb.AppendLine("   " + string.Join(" | ", row.Select(c => c.Text)));
            }
        }
        sb.AppendLine();
        sb.AppendLine(r.GlobalNote);
        return sb.ToString();
    }

    // ═══════════════════ 造数据 ═══════════════════

    private static Bar Bar(int i, double close) => new()
    {
        Code = "300750",
        Granularity = Granularity.Day,
        // 一根一天，**不跳周末**：算法只按"相邻两根K线"work，不看星期几。
        // 早先想模拟真实交易日用了 i*7/5+i%5，结果 i=3 和 i=5 撞到同一天，
        // 资金流按日期建字典时直接抛重复键——真库有主键约束不会这样，是造数据的锅。
        PeriodStart = new DateTime(2026, 1, 5).AddDays(i),
        Close = close,
        Amount = 1e9,
        Turnover = 0.5,
    };

    /// <summary>80 根、从 400 跌到 320 的下跌序列——最接近宁德这轮的形状。</summary>
    private static List<Bar> Falling() =>
        Enumerable.Range(0, 80).Select(i => Bar(i, 400 - i)).ToList();

    private static CapitalDiagnosis Analyze(List<Bar> bars) =>
        new CapitalDiagnosisAnalyzer().Analyze(
            new CapitalDiagnosisInput { Code = "300750", Bars = bars },
            CapitalDiagnosisAnalyzer.ResolveWindows(bars));
}
