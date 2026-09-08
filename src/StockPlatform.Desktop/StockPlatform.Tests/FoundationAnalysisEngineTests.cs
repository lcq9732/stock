using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 峰哥法（<see cref="FoundationAnalysisEngine"/>，2026-09-07 换成"一根K线从最低到最高贯穿
/// MA5/MA10/MA20 + 三线粘合 + 处于低位"）的判据回归。
///
/// 这里钉住的是三条规则各自的边界，尤其两条**容易被后来人"顺手放宽"**的：
///   · 贯穿必须是**严格**穿透且用最高/最低价（含影线）——用户的原话是"从最低到最高，从头到尾
///     穿破三根均线"，改成实体口径会把大部分命中筛掉；
///   · **不限阴阳**——阴线也必须能入选（用户明确要求，实测阳线只比阴线略好，不足以做硬条件）。
/// 另外钉住"回看N根"的语义：N=1 只看最新那根（今天收盘后跑、明天开盘前用），前一天命中不算。
/// </summary>
public class FoundationAnalysisEngineTests
{
    private const string Code = "600000";

    /// <summary>只实现 Query/GetAllCodes 的假仓库——引擎只用到 Query。</summary>
    private sealed class FakeBarRepository : IBarRepository
    {
        private readonly List<Bar> _bars;
        public FakeBarRepository(List<Bar> bars) => _bars = bars;
        public List<Bar> Query(string code, string granularity, DateTime? start = null, DateTime? end = null) => _bars;
        public List<string> GetAllCodes() => new() { Code };
        public void EnsureSchema() => throw new NotSupportedException();
        public void InsertOrIgnore(IEnumerable<Bar> bars) => throw new NotSupportedException();
        public DateTime? GetLatestPeriodStart(string code, string granularity) => throw new NotSupportedException();
        public DateTime? GetOverallLatestPeriodStart(string granularity) => throw new NotSupportedException();
        public DateTime? GetOverallEarliestPeriodStart(string granularity) => throw new NotSupportedException();
        public DateTime? GetOverallLatestPeriodStartOnOrBefore(string granularity, DateTime cutoff) => throw new NotSupportedException();
    }

    private static Bar B(int i, double open, double close, double high, double low) => new()
    {
        Code = Code,
        Granularity = Granularity.Day,
        PeriodStart = new DateTime(2026, 1, 1).AddDays(i),
        Open = open,
        Close = close,
        High = high,
        Low = low,
        Volume = 1_000_000,
    };

    /// <summary>造一段"先从13跌到10、再横盘在10"的历史（0~59根）：横盘段让 MA5/10/20 粘合在10附近，
    /// 下跌段把60日区间的上沿推到13，于是收盘10附近就落在区间底部两成里——正好是用户圈出来的
    /// "低位 + 均线纠缠"那种形状。今天那根（第60根）由各用例自己追加。</summary>
    private static List<Bar> LowFlatHistory()
    {
        var bars = new List<Bar>();
        for (int i = 0; i < 40; i++)
        {
            double p = 13.0 - i * (3.0 / 40);   // 13 → 10
            bars.Add(B(i, p, p, p + 0.05, p - 0.05));
        }
        for (int i = 40; i < 60; i++)
            bars.Add(B(i, 10.0, 10.0, 10.05, 9.95));
        return bars;
    }

    [Fact]
    public void PassesWhenSingleBarSpansAllThreeMovingAveragesAtLowPosition()
    {
        var bars = LowFlatHistory();
        bars.Add(B(60, 10.00, 10.10, 10.50, 9.50));   // 影线上下都穿出三线

        var r = new FoundationAnalysisEngine(new FakeBarRepository(bars)).Analyze(Code, "测试", lookbackDays: 1);

        Assert.Null(r.Error);
        Assert.True(r.Passed, string.Join(" | ", r.Criteria.Select(c => $"{c.Name}={c.Satisfied}:{c.Basis}")));
        Assert.All(r.Criteria, c => Assert.True(c.Satisfied));
        Assert.Equal("阳线", r.Category);
        Assert.Contains("站上三线", r.PatternNote);
    }

    [Fact]
    public void PassesForBearishBarToo_DirectionIsNotFiltered()
    {
        // 阴线（收盘<开盘）且收盘没站上三线，照样入选——不限方向是用户明确要求的口径。
        var bars = LowFlatHistory();
        bars.Add(B(60, 10.20, 9.90, 10.50, 9.50));

        var r = new FoundationAnalysisEngine(new FakeBarRepository(bars)).Analyze(Code, "测试", lookbackDays: 1);

        Assert.True(r.Passed);
        Assert.Equal("阴线", r.Category);
        Assert.Contains("未站上", r.PatternNote);
    }

    [Fact]
    public void FailsWhenHighDoesNotClearTheTopMovingAverage()
    {
        // 最低价穿到三线之下，但最高价没过三线上沿 —— 只算"跌破"，不是"从头到尾穿破"。
        // （注意今天的收盘会把三根均线一起拉低，所以 high 要比"横盘价10"低出一截才真的没穿。）
        var bars = LowFlatHistory();
        bars.Add(B(60, 9.88, 9.85, 9.88, 9.50));

        var r = new FoundationAnalysisEngine(new FakeBarRepository(bars)).Analyze(Code, "测试", lookbackDays: 1);

        Assert.False(r.Passed);
        Assert.False(r.Criteria[0].Satisfied);
        Assert.Equal("", r.Category);
    }

    [Fact]
    public void FailsWhenMovingAveragesAreNotClusteredTightly()
    {
        // 一路下跌（没有横盘段）→ MA5 明显低于 MA20，三线发散；这时要"包住三线"得靠巨大振幅，
        // 实测这类命中没有超额，所以间距上限把它挡掉。
        var bars = new List<Bar>();
        for (int i = 0; i < 60; i++)
        {
            double p = 13.0 - i * 0.05;
            bars.Add(B(i, p, p, p + 0.05, p - 0.05));
        }
        bars.Add(B(60, 10.05, 10.10, 11.20, 9.00));

        var r = new FoundationAnalysisEngine(new FakeBarRepository(bars)).Analyze(Code, "测试", lookbackDays: 1);

        Assert.True(r.Criteria[0].Satisfied);      // 贯穿这条是满足的
        Assert.False(r.Criteria[1].Satisfied);     // 但三线不粘合
        Assert.False(r.Passed);
    }

    [Fact]
    public void FailsWhenPriceIsNotInTheLowerPartOfThe60DayRange()
    {
        // 先从8涨到10再横盘：三线同样粘合在10，但收盘处在60日区间的高位 → 低位那条挡掉。
        var bars = new List<Bar>();
        for (int i = 0; i < 40; i++)
        {
            double p = 8.0 + i * (2.0 / 40);
            bars.Add(B(i, p, p, p + 0.05, p - 0.05));
        }
        for (int i = 40; i < 60; i++)
            bars.Add(B(i, 10.0, 10.0, 10.05, 9.95));
        bars.Add(B(60, 10.00, 10.10, 10.50, 9.50));

        var r = new FoundationAnalysisEngine(new FakeBarRepository(bars)).Analyze(Code, "测试", lookbackDays: 1);

        Assert.True(r.Criteria[0].Satisfied);
        Assert.True(r.Criteria[1].Satisfied);
        Assert.False(r.Criteria[2].Satisfied);
        Assert.False(r.Passed);
    }

    [Fact]
    public void LookbackOfOneOnlyLooksAtTheLatestBar()
    {
        // 昨天贯穿、今天是根平淡的小K线：N=1 不该入选（用户是"今天收盘后找今天的票"），N=3 才算。
        var bars = LowFlatHistory();
        bars.Add(B(60, 10.00, 10.10, 10.50, 9.50));   // 命中那根
        bars.Add(B(61, 10.10, 10.12, 10.15, 10.05));  // 今天，没穿

        var engine = new FoundationAnalysisEngine(new FakeBarRepository(bars));

        var one = engine.Analyze(Code, "测试", lookbackDays: 1);
        Assert.False(one.Passed);
        Assert.False(one.Criteria[0].Satisfied);
        Assert.Contains("未评估", one.Criteria[1].Basis);   // 没有命中日就别假装评估过后两条

        var three = engine.Analyze(Code, "测试", lookbackDays: 3);
        Assert.True(three.Passed);
        Assert.Contains("03-02", three.PatternNote);       // 命中的是第60根(2026-01-01+60天)，不是今天
    }

    [Fact]
    public void ReportsErrorWhenHistoryIsShorterThanThePositionWindow()
    {
        var bars = LowFlatHistory().Take(30).ToList();

        var r = new FoundationAnalysisEngine(new FakeBarRepository(bars)).Analyze(Code, "测试", lookbackDays: 1);

        Assert.NotNull(r.Error);
        Assert.False(r.Passed);
    }
}
