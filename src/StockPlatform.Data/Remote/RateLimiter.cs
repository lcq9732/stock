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
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10) };

    // How often WaitOutPauseAsync reports "still intentionally paused" while sitting out a long
    // pause (circuit-breaker OR proactive batch rest) — without this, a long pause is silent and
    // looks identical to the program having hung.
    private static readonly TimeSpan PauseReportInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _semaphore;
    private readonly TimeSpan _delayBetweenRequests;
    private readonly int _batchSize;
    private readonly TimeSpan _restDuration;
    private readonly object _pauseLock = new();
    private DateTime _pausedUntilUtc = DateTime.MinValue;
    private int _completedCount;

    // Guards WaitOutPauseAsync's "still paused" report against duplicate spam: with thousands of
    // stocks queued behind only a handful of concurrency slots, many different tasks can each
    // grab a freshly-freed slot and independently check the SAME shared pause right as it's about
    // to end, all seeing "~1 second left" within the same instant and each logging it. Only the
    // first one to observe a given remaining-seconds value is allowed to report it.
    private int _lastReportedRemainingSeconds = int.MaxValue;

    /// <summary>See <see cref="Logic.Abstractions.IBarDataFetcher.OnStatus"/> — the fetchers that
    /// own a RateLimiter just forward this event through their own OnStatus.</summary>
    public event Action<string>? OnStatus;

    public RateLimiter(int maxConcurrency = 3, TimeSpan? delayBetweenRequests = null, int batchSize = 50, TimeSpan? restDuration = null)
    {
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _delayBetweenRequests = delayBetweenRequests ?? TimeSpan.FromSeconds(1);
        _batchSize = batchSize;
        _restDuration = restDuration ?? TimeSpan.FromSeconds(30);
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
                    await Task.Delay(_delayBetweenRequests, ct);
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
                        TripBreaker();
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
            OnStatus?.Invoke(
                $"已发出 {count} 次网络请求，主动休息 {_restDuration.TotalSeconds:0} 秒" +
                "（预防性降速，防止触发反爬限流，不是卡死；这个计数只算真正发出的网络请求，跳过的股票不算在内，所以会比抓取进度数字小）");
        }
    }

    private void TripBreaker()
    {
        // Randomized within 5–15 minutes so many concurrent stock fetches tripping around the
        // same moment don't all resume in the exact same instant and immediately re-trip it.
        var pauseMinutes = 5 + Random.Shared.NextDouble() * 10;
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
                $"疑似触发反爬限流，程序主动暂停约 {pauseMinutes:F0} 分钟（预计 {candidate.ToLocalTime():HH:mm:ss} 恢复）——" +
                "这是主动降速保护，不是卡死");
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
