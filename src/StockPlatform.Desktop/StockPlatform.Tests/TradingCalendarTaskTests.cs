using StockPlatform.Logic.Abstractions;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【交易日历】任务（2026-09-08）——第一个按新形状写的任务，所以这里既测它自己的业务，
/// 也顺带把 <see cref="FetchTaskBase{TItem}"/> 的骨架跑通（事件、流式落库、结局翻译）。
///
/// 重点盯三件最容易出错、而且**错了不报错**的事：
///   ① 日常增量绝对不能去扫全库K线（那是几十秒的全表索引扫描，每天跑一次就是白烧）；
///   ② 起点要回退到"日历最大日所在月的 1 号"——停在月中的话那个月剩下的日子永远补不上；
///   ③ 空月（交易所还没发布下一年）不能被当成失败，也不能被当成"这个月没有交易日"写进库。
/// </summary>
public class TradingCalendarTaskTests
{
    // ─────────────────── 假实现 ───────────────────

    private sealed class FakeRepo : ITradingDayRepository
    {
        public readonly Dictionary<DateOnly, string> Days = new();
        public int UpsertCalls;

        public void EnsureSchema() { }

        public void Upsert(IEnumerable<(DateOnly Day, string Source)> days)
        {
            UpsertCalls++;
            foreach (var (d, s) in days) Days[d] = s;
        }

        public List<DateTime> GetAll() => Days.Keys.OrderBy(d => d).Select(d => d.ToDateTime(TimeOnly.MinValue)).ToList();

        public (DateTime? Min, DateTime? Max) GetRange() => Days.Count == 0
            ? (null, null)
            : (Days.Keys.Min().ToDateTime(TimeOnly.MinValue), Days.Keys.Max().ToDateTime(TimeOnly.MinValue));

        public int Count(string? source = null) => source == null
            ? Days.Count : Days.Values.Count(v => v == source);

        public HashSet<DateOnly> GetBetween(DateOnly from, DateOnly to)
            => Days.Keys.Where(d => d >= from && d <= to).ToHashSet();
    }

    private sealed class FakeProvider : ITradingCalendarProvider
    {
        public event Action<string>? OnStatus { add { } remove { } }
        public DateOnly EarliestMonth => new(2005, 1, 1);

        /// <summary>问过哪些月（顺序保留），用来断言起点/终点算对了没有。</summary>
        public readonly List<(int Y, int M)> Asked = new();

        /// <summary>每个月给几天；没配的月份返回空（＝交易所还没发布）。</summary>
        public Dictionary<(int, int), List<DateOnly>> Data = new();

        /// <summary>配了就在这个月抛异常，模拟网络挂掉。</summary>
        public (int Y, int M)? ThrowOn;

        public Task<List<DateOnly>> GetMonthAsync(int year, int month, CancellationToken ct = default)
        {
            Asked.Add((year, month));
            if (ThrowOn is { } t && t.Y == year && t.M == month)
                throw new InvalidOperationException("模拟：深交所接口连不上");
            return Task.FromResult(Data.TryGetValue((year, month), out var days) ? days : []);
        }
    }

    private sealed class FakeLocal(params DateTime[] days) : ILocalTradingDaySource
    {
        public int Calls;
        public List<DateTime> GetDistinctDays() { Calls++; return days.ToList(); }
    }

    private static List<DateOnly> MonthDays(int y, int m, params int[] days)
        => days.Select(d => new DateOnly(y, m, d)).ToList();

    // ─────────────────── 用例 ───────────────────

    /// <summary>
    /// 首次（表空）：2004 及以前从本地K线归纳、2005-01 起走官方，两个来源各自打标。
    /// 打标要分得开，否则日后想知道"这一天是官方说的还是我们猜的"就没依据了。
    /// </summary>
    [Fact]
    public async Task 首次运行_归纳早年并拉官方_两个来源分别打标()
    {
        var repo = new FakeRepo();
        var provider = new FakeProvider
        {
            Data = { [(2005, 1)] = MonthDays(2005, 1, 4, 5) },
        };
        var local = new FakeLocal(
            new DateTime(1990, 12, 19), new DateTime(2004, 12, 31),
            new DateTime(2005, 1, 4));   // 2005 那天归本地也不该被当成 local 来源写进去

        var task = new TradingCalendarTask(repo, provider, local);
        var result = await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Equal(1, local.Calls);
        Assert.Equal(ITradingDayRepository.LocalSource, repo.Days[new DateOnly(1990, 12, 19)]);
        Assert.Equal(ITradingDayRepository.LocalSource, repo.Days[new DateOnly(2004, 12, 31)]);
        // 2005-01-04 官方也给了，官方值要覆盖归纳值
        Assert.Equal(ITradingDayRepository.SzseSource, repo.Days[new DateOnly(2005, 1, 4)]);
        Assert.Equal((2005, 1), provider.Asked[0]);
    }

    /// <summary>
    /// 日常增量：**一次都不能去扫全库K线**。那是 23GB 库上几十秒的索引全扫，
    /// 每天跑一次纯属白烧，而且不报错、没人会发现。
    /// </summary>
    [Fact]
    public async Task 增量运行_不扫全库K线()
    {
        var repo = new FakeRepo();
        repo.Days[new DateOnly(1990, 12, 19)] = ITradingDayRepository.LocalSource;   // 早年段已建
        repo.Days[new DateOnly(2026, 9, 5)] = ITradingDayRepository.SzseSource;
        var local = new FakeLocal(new DateTime(1990, 12, 19));

        var task = new TradingCalendarTask(repo, new FakeProvider(), local);
        var result = await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Equal(0, local.Calls);
    }

    /// <summary>
    /// **本地压根没有 2005 年以前的K线**时也不能每轮重扫。第一版判据是"日历最早那天还在 2005 之后
    /// 就去归纳"，可那种环境归纳出来永远是空的，于是每轮都白扫一遍 23GB 的库（2026-09-08 实测到）。
    /// 现在只在表空或「首次整段回补」时才扫。
    /// </summary>
    [Fact]
    public async Task 增量运行_本地没有早年K线时也不重扫()
    {
        var repo = new FakeRepo();
        repo.Days[new DateOnly(2010, 1, 4)] = ITradingDayRepository.SzseSource;   // 日历只有 2005 之后
        var local = new FakeLocal(new DateTime(2010, 1, 4));

        await new TradingCalendarTask(repo, new FakeProvider(), local)
            .RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(0, local.Calls);
    }

    /// <summary>「首次整段回补」＝重建：不管表里现在是什么，都重新归纳一遍早年那段。</summary>
    [Fact]
    public async Task 整段回补模式_重新归纳早年()
    {
        var repo = new FakeRepo();
        repo.Days[new DateOnly(1990, 12, 19)] = ITradingDayRepository.LocalSource;
        repo.Days[new DateOnly(2026, 9, 5)] = ITradingDayRepository.SzseSource;
        var local = new FakeLocal(new DateTime(1990, 12, 19));

        await new TradingCalendarTask(repo, new FakeProvider(), local)
            .RunAsync(new TaskRunArgs(Mode: FetchMode.FirstBackfill), CancellationToken.None);

        Assert.Equal(1, local.Calls);
    }

    /// <summary>
    /// 增量的起点要回退到**日历最大日所在月的 1 号**，整月重抓。
    /// 不回退的话：当月是边长边拉的，某次停在月中，那个月剩下的日子就再也补不上了
    /// （龙虎榜席位按月切片踩过同样的坑，见 RunFetchLhbSeatAsync 的注释）。
    /// </summary>
    [Fact]
    public async Task 增量运行_起点回退到当月一号()
    {
        var repo = new FakeRepo();
        repo.Days[new DateOnly(1990, 12, 19)] = ITradingDayRepository.LocalSource;
        repo.Days[new DateOnly(2026, 9, 5)] = ITradingDayRepository.SzseSource;
        var provider = new FakeProvider();

        await new TradingCalendarTask(repo, provider, new FakeLocal())
            .RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal((2026, 9), provider.Asked[0]);
    }

    /// <summary>
    /// 日历里已经有**未来**月份时（11 月起会把下一年一次性拉进来），起点仍要从**本月**算——
    /// 只按"日历最大日所在月"算的话，当月就再也不会重拉了，而交易所偶尔会调整当年的休市安排。
    /// 2026-09-08 首次实跑时就是这个表现：日历已到 2026-10-30，于是只拉了 10 月、跳过了本月。
    /// </summary>
    [Fact]
    public async Task 日历已含未来月份时_起点仍从本月算()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var repo = new FakeRepo();
        repo.Days[new DateOnly(1990, 12, 19)] = ITradingDayRepository.LocalSource;
        repo.Days[today.AddMonths(2)] = ITradingDayRepository.SzseSource;      // 日历已经拉到两个月后
        var provider = new FakeProvider();

        await new TradingCalendarTask(repo, provider, new FakeLocal())
            .RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal((today.Year, today.Month), provider.Asked[0]);
    }

    /// <summary>
    /// 空月＝交易所还没发布下一年的日历（实测 2026-09-08 时 2027 全空），
    /// 既不算失败，也不能因此往库里写任何东西。
    /// </summary>
    [Fact]
    public async Task 空月不算失败也不写库()
    {
        var repo = new FakeRepo();
        repo.Days[new DateOnly(1990, 12, 19)] = ITradingDayRepository.LocalSource;
        repo.Days[new DateOnly(2026, 9, 5)] = ITradingDayRepository.SzseSource;

        var result = await new TradingCalendarTask(repo, new FakeProvider(), new FakeLocal())
            .RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, result.State);
        Assert.True(result.NothingToDo);
        Assert.Equal(0, repo.UpsertCalls);
    }

    /// <summary>
    /// 请求失败要变成 Failed，**不能**被当成"这个月没有交易日"。
    /// 混淆这两者的后果是把一个真实的月份从日历里抹掉，而后面所有按交易日取数的任务
    /// 都会安静地跳过那些天。
    /// </summary>
    [Fact]
    public async Task 请求失败算失败_不当成没有交易日()
    {
        var repo = new FakeRepo();
        repo.Days[new DateOnly(1990, 12, 19)] = ITradingDayRepository.LocalSource;
        repo.Days[new DateOnly(2026, 9, 5)] = ITradingDayRepository.SzseSource;
        var provider = new FakeProvider { ThrowOn = (2026, 9) };

        var result = await new TradingCalendarTask(repo, provider, new FakeLocal())
            .RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(TaskState.Failed, result.State);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(0, repo.UpsertCalls);
    }

    /// <summary>
    /// 对账：官方说是交易日、本地全市场却一根K线都没有 → 那天**整天漏抓**，必须报出来。
    /// 这是换源验证顺带白捡的体检信号，不报的话没人会发现。
    /// </summary>
    [Fact]
    public async Task 对账_报出本地整天漏抓的交易日()
    {
        var repo = new FakeRepo();
        var provider = new FakeProvider
        {
            Data = { [(2005, 1)] = MonthDays(2005, 1, 4, 5, 6) },
        };
        // 本地只有 4 号和 6 号：5 号是官方交易日但本地一根K线都没有
        var local = new FakeLocal(new DateTime(2005, 1, 4), new DateTime(2005, 1, 6));

        var task = new TradingCalendarTask(repo, provider, local);
        var log = new List<string>();
        task.OnProgress += p => log.Add(p.Text);
        await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Contains(log, m => m.Contains("整天漏抓") && m.Contains("2005-01-05"));
    }

    /// <summary>
    /// 订阅者自己抛异常，不能把任务带崩——多播链断掉的话后面的订阅者（比如静默看门狗）
    /// 就收不到心跳，会被误判成卡死。
    /// </summary>
    [Fact]
    public async Task 订阅者抛异常不影响任务()
    {
        var repo = new FakeRepo();
        repo.Days[new DateOnly(1990, 12, 19)] = ITradingDayRepository.LocalSource;
        repo.Days[new DateOnly(2026, 9, 5)] = ITradingDayRepository.SzseSource;

        var task = new TradingCalendarTask(repo, new FakeProvider(), new FakeLocal());
        int seen = 0;
        task.OnProgress += _ => throw new InvalidOperationException("坏订阅者");
        task.OnProgress += _ => seen++;

        var result = await task.RunAsync(new TaskRunArgs(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, result.State);
        Assert.True(seen > 0, "坏订阅者不该挡住后面的订阅者");
    }
}
