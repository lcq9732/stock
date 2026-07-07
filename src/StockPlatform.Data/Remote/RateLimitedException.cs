namespace StockPlatform.Data.Remote;

/// <summary>
/// Signals that a request looks like it hit anti-scraping/rate-limiting (HTTP 403/429, an empty
/// response body, or a connection-level failure) as opposed to an ordinary error (bad stock
/// code, unexpected data shape). <see cref="RateLimiter"/> treats this specially: it retries
/// and trips a global pause, since these symptoms mean "this IP is currently blocked", not
/// "this one request is broken".
/// </summary>
public class RateLimitedException : Exception
{
    public RateLimitedException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
