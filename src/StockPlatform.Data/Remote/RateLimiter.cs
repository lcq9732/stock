namespace StockPlatform.Data.Remote;

/// <summary>
/// Conservative, uniform rate limiting for all remote fetches — deliberately not different
/// between first-time full backfill and daily incremental runs (see doc/data-platform-design.md
/// section 6.7): avoiding IP bans matters more than raw speed, and daily runs are small anyway.
///
/// Four layers of protection (see doc/data-platform-design.md section 6.7):
/// 1. Concurrency cap + fixed delay between requests (the original, always-on throttle)
/// 2. Proactive batching — after every <see cref="_batchSize"/> completed requests, rest for
///    <see cref="_restDuration"/> regardless of whether anything has failed yet. This is a
///    deliberate slowdown to avoid *triggering* anti-scraping in the first place, not a reaction
///    to one.
/// 3. Retry on failure — up to 2 retries, waiting 2s then 10s
/// 4. Circuit breaker — once a call's own retries (#3) are exhausted and the final failure was
///    a <see cref="RateLimitedException"/> (403/429/empty response/connection failure), trip a
///    global pause of 5–15 random minutes. Only calls that start (or retry) *after* that point
///    wait out the remaining pause — a call's own retries are never gated by a pause it just
///    tripped itself, so the fast 2s/10s retry cadence stays fast.
/// </summary>
public class RateLimiter
{
    private static readonly TimeSpan[] DefaultRetryDelays = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10) };
    private readonly TimeSpan[] RetryDelays;

    // How often WaitOutPauseAsync reports "still intentionally paused" while sitting out a long
    // pause (circuit-breaker OR proactive batch rest) — without this, a long pause is silent and
    // looks identical to the program having hung.
    private static readonly TimeSpan PauseReportInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _semaphore;
    private readonly TimeSpan _delayBetweenRequests;
    private readonly double _jitter;
    private readonly int _batchSize;
    private readonly TimeSpan _restDuration;
    private readonly object _pauseLock = new();
    private DateTime _pausedUntilUtc = DateTime.MinValue;
    private int _completedCount;

    // ── 指数退避（2026-09-04，按东财 push2 的实测日志加的）──
    // 实测：连发 16~35 个请求就被切断（库里 fetched_at 反推出三串：25只/61秒、16只/31秒、
    // 33只/97秒），而固定 5~15 分钟的暂停明显不够——那天每 24 分钟撞一次，连撞 7 次全空，
    // 第一次被切 26 分钟就恢复，第二次连撞之后花了 3 小时 14 分。所以连续熔断要翻倍等下去。
    private int _consecutiveTrips;
    private int _successSinceTrip;

    // ── 连续失败到多少个才认定"真被封了"（2026-09-04，梯度实测之后加的）──
    // 实测（5 秒间隔连发 60 个 push2 请求）：**失败是常态，58% 的请求会被拒**，
    // 而且成败呈周期交替——失败 3~4 个、成功 5~6 个、再失败 3~4 个，一直循环，
    // 60 个打完始终没进长封禁。这是令牌桶的正常表现：桶空了就拒，等十几秒补上又能过。
    //
    // 原来的逻辑是"一次调用重试完还失败就熔断 15 分钟"，于是每撞一次正常的"桶空了"
    // 就去睡一刻钟——桶明明 20 秒后就补上了。今天日志里那些"连续第 1 次""16 分钟"
    // 全是这么来的，实际吞吐被砍到接近零。
    //
    // 所以熔断的判据改成**连续**失败。阈值 15 是量出来的：两轮 60 个请求的实测里，
    // push2 最长连续失败 7 个、push2his 6 个——阈值 10 只剩 3 个余量，正常波动就会误判成
    // "被封了"然后白睡 15 分钟。15 留了一倍余量，同时在真被长封禁（一直失败）时
    // 75 秒内就能刹住车（5 秒间隔）。
    private int _consecutiveFailures;
    private const int FailuresBeforeBreaker = 15;

    /// <summary>
    /// 连续熔断多少次之后才认为"真的恢复了"、把退避时长归零的成功请求数。
    /// 为什么不是"成功一次就归零"：被切之后能抓 15~20 个再被切是常态，一次成功就归零的话
    /// 退避永远停在起步值，等于没有递增。要能连抓过这个数，才说明配额是真回来了。
    /// </summary>
    private const int SuccessesToResetBackoff = 40;

    /// <summary>退避的起步时长；每连续熔断一次翻倍，到 <see cref="MaxBackoff"/> 封顶。</summary>
    private readonly TimeSpan BaseBackoff;
    private readonly TimeSpan MaxBackoff;

    /// <summary>
    /// 还要暂停多久（本地时刻）；没在暂停时返回 null。
    /// 给界面用：暂停期里再点【执行】只会干等到超时然后报失败（实测干等了 8 分钟），
    /// 不如直接告诉人还要等多久。
    /// </summary>
    public DateTime? PausedUntil
    {
        get
        {
            lock (_pauseLock)
                return _pausedUntilUtc > DateTime.UtcNow ? _pausedUntilUtc.ToLocalTime() : null;
        }
    }

    // Guards WaitOutPauseAsync's "still paused" report against duplicate spam: with thousands of
    // stocks queued behind only a handful of concurrency slots, many different tasks can each
    // grab a freshly-freed slot and independently check the SAME shared pause right as it's about
    // to end, all seeing "~1 second left" within the same instant and each logging it. Only the
    // first one to observe a given remaining-seconds value is allowed to report it.
    private int _lastReportedRemainingSeconds = int.MaxValue;

    /// <summary>See <see cref="Logic.Abstractions.IBarDataFetcher.OnStatus"/> — the fetchers that
    /// own a RateLimiter just forward this event through their own OnStatus.</summary>
    public event Action<string>? OnStatus;

    /// <param name="baseBackoff">
    /// 熔断退避的起步时长，每连续熔断一次翻倍（默认 15 分钟，按 2026-09-04 东财实测定的）。
    /// 做成参数是为了两件事：不同数据源的脾气不一样，以及测试能用毫秒级的值跑完——
    /// 写死的话一个退避测试就得真等 15 分钟。
    /// </param>
    /// <param name="maxBackoff">退避封顶（默认 120 分钟）。</param>
    /// <param name="retryDelays">单次调用自己的快速重试节奏（默认 2 秒、10 秒）。</param>
    /// <param name="jitter">
    /// 请求间隔的随机抖动比例（0~1，默认 0＝不抖）。填 0.3 就是在
    /// <paramref name="delayBetweenRequests"/> 上下浮动 ±30%。
    ///
    /// 为什么要它（2026-09-04）：人不会精确每 5.000 秒点一次，固定间隔是机器行为里最好认的特征。
    /// 既然整条通道都在模仿浏览器，节奏也该像人——代价只是平均耗时不变、单次有快有慢。
    /// </param>
    public RateLimiter(int maxConcurrency = 3, TimeSpan? delayBetweenRequests = null,
                       int batchSize = 50, TimeSpan? restDuration = null,
                       TimeSpan? baseBackoff = null, TimeSpan? maxBackoff = null,
                       TimeSpan[]? retryDelays = null, double jitter = 0)
    {
        _jitter = Math.Clamp(jitter, 0, 1);
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _delayBetweenRequests = delayBetweenRequests ?? TimeSpan.FromSeconds(1);
        _batchSize = batchSize;
        _restDuration = restDuration ?? TimeSpan.FromSeconds(30);
        BaseBackoff = baseBackoff ?? TimeSpan.FromMinutes(15);
        MaxBackoff = maxBackoff ?? TimeSpan.FromMinutes(120);
        RetryDelays = retryDelays ?? DefaultRetryDelays;
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                await WaitOutPauseAsync(ct);
                try
                {
                    var result = await action();
                    Interlocked.Exchange(ref _consecutiveFailures, 0);   // 成功一次就不算"连续失败"了
                    // 连续抓够一批才认为限流真的过去了，把退避时长归零——见 SuccessesToResetBackoff
                    if (Interlocked.Increment(ref _successSinceTrip) >= SuccessesToResetBackoff)
                    {
                        lock (_pauseLock) { _consecutiveTrips = 0; }
                        Interlocked.Exchange(ref _successSinceTrip, 0);
                    }
                    await Task.Delay(NextDelay(), ct);
                    MaybeStartBatchRest();
                    return result;
                }
                catch (RateLimitedException) when (!ct.IsCancellationRequested)
                {
                    // Trip the global pause only once THIS call has exhausted its own fast
                    // retries — tripping on every failed attempt made the 2s/10s retry delays
                    // meaningless, because the very next attempt would immediately sit through
                    // the 5-15 minute pause it had just set for itself (verified with a
                    // simulated test: a request that should retry in ~12s instead took ~20
                    // minutes). The pause is meant to protect *other, later* calls once this
                    // one has given up, not to gate this call's own retries.
                    if (attempt >= RetryDelays.Length)
                    {
                        // ⚠ 单次失败**不**熔断（2026-09-04）：被拒是常态，不是异常。
                        // 只有连成一串才说明真被封了，见 FailuresBeforeBreaker 那段注释。
                        var streak = Interlocked.Increment(ref _consecutiveFailures);
                        if (streak >= FailuresBeforeBreaker) TripBreaker();
                        throw;
                    }
                    await Task.Delay(RetryDelays[attempt], ct);
                }
                // The `!ct.IsCancellationRequested` guards above and below matter: a user-
                // triggered Stop cancels `ct`, which surfaces as an exception here (possibly
                // wrapped as RateLimitedException by the fetcher) — without this guard, Stop
                // would sit through a 2s/10s retry delay before actually stopping.
                catch (Exception) when (attempt < RetryDelays.Length && !ct.IsCancellationRequested)
                {
                    await Task.Delay(RetryDelays[attempt], ct);
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>这一次要等多久：基准间隔 ± 抖动。抖动为 0 时就是原来的固定间隔。</summary>
    private TimeSpan NextDelay()
    {
        if (_jitter <= 0) return _delayBetweenRequests;
        var factor = 1 + (Random.Shared.NextDouble() * 2 - 1) * _jitter;
        return _delayBetweenRequests * factor;
    }

    private void MaybeStartBatchRest()
    {
        var count = Interlocked.Increment(ref _completedCount);
        if (count % _batchSize != 0) return; // only the request that crosses a batch boundary rests

        var candidate = DateTime.UtcNow.Add(_restDuration);
        var announce = false;
        lock (_pauseLock)
        {
            if (candidate > _pausedUntilUtc)
            {
                _pausedUntilUtc = candidate;
                _lastReportedRemainingSeconds = int.MaxValue; // fresh pause — allow it to be reported again
                announce = true;
            }
        }
        if (announce)
        {
            // Explicitly "网络请求" (network requests), not "股票" (stocks) — this count only
            // includes requests that actually went out over the network, so it legitimately runs
            // behind the "正在抓取 (X/总数)" stock-progress counter whenever some stocks are
            // skipped for already being up to date (see FetchOrchestrator.FetchStats). Without
            // this distinction the two numbers look inconsistent side by side in the log.
            OnStatus?.Invoke($"已发出 {count} 次网络请求，主动休息 {_restDuration.TotalSeconds:0} 秒");
        }
    }

    private void TripBreaker()
    {
        // 指数退避（2026-09-04）：第 n 次连续熔断等 15 × 2^(n-1) 分钟，到 120 分钟封顶。
        // 原来固定 5~15 分钟，实测不够——那天按 24 分钟一轮连撞 7 次全空，白撞还可能把封禁拖长。
        // 仍旧带 ±20% 抖动：并发的几个请求同时熔断时，别掐着同一秒一起醒来又一起撞回去。
        int trips;
        lock (_pauseLock) { trips = ++_consecutiveTrips; }
        Interlocked.Exchange(ref _successSinceTrip, 0);
        // 清零连续失败计数：不清的话暂停结束后第一个请求一失败就又立刻熔断，
        // 等于每次只给一次机会，永远回不到正常节奏。
        Interlocked.Exchange(ref _consecutiveFailures, 0);

        var backoff = TimeSpan.FromMinutes(Math.Min(
            BaseBackoff.TotalMinutes * Math.Pow(2, Math.Min(trips - 1, 10)),
            MaxBackoff.TotalMinutes));
        var pauseMinutes = backoff.TotalMinutes * (0.8 + Random.Shared.NextDouble() * 0.4);
        var candidate = DateTime.UtcNow.AddMinutes(pauseMinutes);
        var announce = false;
        lock (_pauseLock)
        {
            if (candidate > _pausedUntilUtc)
            {
                _pausedUntilUtc = candidate;
                _lastReportedRemainingSeconds = int.MaxValue; // fresh pause — allow it to be reported again
                announce = true; // only the call that actually extends the pause announces it,
                                  // so several calls tripping around the same moment don't each
                                  // log a duplicate line
            }
        }
        if (announce)
        {
            OnStatus?.Invoke(
                $"连续 {FailuresBeforeBreaker} 个请求被拒，判定被限流（连续第 {trips} 次熔断），" +
                $"程序主动暂停约 {pauseMinutes:F0} 分钟" +
                $"（预计 {candidate.ToLocalTime():HH:mm:ss} 恢复）——这是主动降速保护，不是卡死。" +
                (trips > 1 ? "连续熔断会逐次翻倍等待，撞得越勤恢复越慢。" : ""));
        }
    }

    private async Task WaitOutPauseAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan remaining;
            lock (_pauseLock) { remaining = _pausedUntilUtc - DateTime.UtcNow; }
            if (remaining <= TimeSpan.Zero) return;

            var wait = remaining < PauseReportInterval ? remaining : PauseReportInterval;
            await Task.Delay(wait, ct);

            var shouldReport = false;
            var remainingSeconds = 0;
            lock (_pauseLock)
            {
                remaining = _pausedUntilUtc - DateTime.UtcNow;
                remainingSeconds = (int)Math.Ceiling(remaining.TotalSeconds);
                if (remaining > TimeSpan.Zero && remainingSeconds < _lastReportedRemainingSeconds)
                {
                    _lastReportedRemainingSeconds = remainingSeconds;
                    shouldReport = true;
                }
            }
            if (shouldReport)
                OnStatus?.Invoke($"仍在主动暂停中，预计还需 {remainingSeconds} 秒恢复（不是卡死）");
        }
    }
}
