using System.Diagnostics;
using CarIngest.Core.Configuration;
using CarIngest.Core.Policies;
using CarIngest.Infrastructure.Fetching;
using CarIngest.Infrastructure.Tests.Harness;
using Xunit;

namespace CarIngest.Infrastructure.Tests.Fetching;

/// <summary>Per-host politeness (FR-006, constitution gate I-2).</summary>
public sealed class PolitenessTests
{
    private static PerHostRateLimiter Build(params (string Host, int Tokens, int Milliseconds)[] hosts)
    {
        var policies = hosts
            .Select(h => new HostAccessPolicy
            {
                Host = h.Host,
                RequestsPerPeriod = h.Tokens,
                ReplenishmentPeriod = TimeSpan.FromMilliseconds(h.Milliseconds),
                MaxConcurrency = 1,
                PermittedFields = ["Make", "Model", "Year"],
            })
            .ToList();

        return new PerHostRateLimiter(new StaticOptionsMonitor<CarIngestOptions>(new CarIngestOptions
        {
            UserAgent = "CarIngestBot/1.0 (+https://example.invalid)",
            HostPolicies = policies,
            Sources = [new SourceOptions { Location = "/tmp" }],
        }));
    }

    [Fact]
    public async Task ConsecutiveRequestsToOneHost_AreSeparatedByTheReplenishmentPeriod()
    {
        using var limiter = Build(("slow.example", 1, 200));
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < 3; i++)
        {
            using var lease = await limiter.AcquireAsync("slow.example", CancellationToken.None);
            Assert.True(lease.IsAcquired);
        }

        // Three permits from a 1-token bucket refilling every 200ms costs at least two waits.
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 350,
            $"Expected at least ~400ms of enforced delay, observed {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task DifferentHosts_DoNotQueueBehindEachOther()
    {
        // Concurrency is bounded per host, never only globally (FR-006): a slow host must not
        // stall a fast one.
        using var limiter = Build(("a.example", 1, 5), ("b.example", 1, 5));
        var stopwatch = Stopwatch.StartNew();

        using (await limiter.AcquireAsync("a.example", CancellationToken.None))
        using (await limiter.AcquireAsync("b.example", CancellationToken.None))
        {
            // First permit of each independent bucket is immediate.
        }

        Assert.True(
            stopwatch.ElapsedMilliseconds < 200,
            $"Independent hosts should not serialize; observed {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public void RobotsCrawlDelay_WinsWhenLongerThanConfiguration()
    {
        using var limiter = Build(("dealer.example", 1, 1000));

        Assert.Equal(TimeSpan.FromSeconds(1), limiter.EffectiveDelayFor("dealer.example"));

        limiter.ObserveCrawlDelay("dealer.example", TimeSpan.FromSeconds(10));

        // robots.txt can only slow us down.
        Assert.Equal(TimeSpan.FromSeconds(10), limiter.EffectiveDelayFor("dealer.example"));
    }

    [Fact]
    public void RobotsCrawlDelay_IsIgnoredWhenShorterThanConfiguration()
    {
        using var limiter = Build(("dealer.example", 1, 5000));

        limiter.ObserveCrawlDelay("dealer.example", TimeSpan.FromSeconds(1));

        // A host asking us to go faster than our own policy does not get to.
        Assert.Equal(TimeSpan.FromSeconds(5), limiter.EffectiveDelayFor("dealer.example"));
    }

    [Fact]
    public void UnknownHost_FallsBackToAConservativeDefault()
    {
        using var limiter = Build(("known.example", 10, 10));

        // Not zero: an unconfigured host must not be hammered. FR-008 means it should not be
        // fetched at all, but the limiter's own fallback stays conservative regardless.
        Assert.Equal(TimeSpan.FromSeconds(5), limiter.EffectiveDelayFor("unknown.example"));
    }

    [Fact]
    public async Task AcquireHonoursCancellation()
    {
        using var limiter = Build(("slow.example", 1, 60_000));
        using var lease = await limiter.AcquireAsync("slow.example", CancellationToken.None);
        Assert.True(lease.IsAcquired);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var queued = await limiter.AcquireAsync("slow.example", cts.Token);
        });
    }
}
