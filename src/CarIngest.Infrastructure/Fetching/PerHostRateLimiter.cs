using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using CarIngest.Core.Configuration;
using CarIngest.Core.Policies;
using Microsoft.Extensions.Options;

namespace CarIngest.Infrastructure.Fetching;

/// <summary>
/// Per-host politeness limiter.
/// </summary>
/// <remarks>
/// <para>
/// Token bucket, deliberately — <b>not</b> a fixed window. A fixed window permits a full burst
/// at each window boundary, which is precisely the behaviour per-host rate limiting exists to
/// prevent. A bucket of capacity N refilling every period expresses "respect this host's
/// crawl-delay" directly (research D11).
/// </para>
/// <para>
/// The effective delay is max(configured, robots Crawl-delay): robots.txt can only slow us
/// down, never speed us up. A host asking for 10s gets 10s even if configuration says 1s.
/// </para>
/// </remarks>
public sealed class PerHostRateLimiter : IDisposable
{
    private readonly ConcurrentDictionary<string, RateLimiter> limiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TimeSpan> observedCrawlDelays = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptionsMonitor<CarIngestOptions> options;
    private bool disposed;

    public PerHostRateLimiter(IOptionsMonitor<CarIngestOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <summary>
    /// Records a host's declared Crawl-delay. If it is longer than what we already use, the
    /// host's bucket is rebuilt to the slower rate.
    /// </summary>
    public void ObserveCrawlDelay(string host, TimeSpan? crawlDelay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (crawlDelay is not { } delay || delay <= TimeSpan.Zero)
        {
            return;
        }

        var previous = this.observedCrawlDelays.GetOrAdd(host, delay);
        if (delay > previous)
        {
            this.observedCrawlDelays[host] = delay;
        }

        if (delay > previous && this.limiters.TryRemove(host, out var stale))
        {
            stale.Dispose();
        }
    }

    public async ValueTask<RateLimitLease> AcquireAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var limiter = this.limiters.GetOrAdd(host, this.CreateLimiter);
        return await limiter.AcquireAsync(permitCount: 1, cancellationToken).ConfigureAwait(false);
    }

    public TimeSpan EffectiveDelayFor(string host)
    {
        var configured = this.PolicyFor(host)?.ReplenishmentPeriod ?? TimeSpan.FromSeconds(5);
        return this.observedCrawlDelays.TryGetValue(host, out var observed) && observed > configured
            ? observed
            : configured;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        foreach (var limiter in this.limiters.Values)
        {
            limiter.Dispose();
        }

        this.limiters.Clear();
    }

    private HostAccessPolicy? PolicyFor(string host) =>
        this.options.CurrentValue.PolicyByHost.TryGetValue(host, out var policy) ? policy : null;

    private RateLimiter CreateLimiter(string host)
    {
        var policy = this.PolicyFor(host);
        var tokens = policy?.RequestsPerPeriod ?? 1;

        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = tokens,
            TokensPerPeriod = tokens,
            ReplenishmentPeriod = this.EffectiveDelayFor(host),
            AutoReplenishment = true,

            // Queue rather than reject: a polite crawler waits its turn. Bounded so a runaway
            // run cannot accumulate unbounded pending requests.
            QueueLimit = 1024,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }
}
