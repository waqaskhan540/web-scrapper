using System.Net;
using CarIngest.Core.Abstractions;
using CarIngest.Core.Configuration;
using CarIngest.Core.Policies;
using CarIngest.Infrastructure.Fetching;
using CarIngest.Infrastructure.Tests.Harness;
using CarIngest.Robots;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CarIngest.Infrastructure.Tests.Fetching;

/// <summary>
/// Conditional requests are an optimization, never the correctness mechanism.
/// </summary>
/// <remarks>
/// Research R2 could not verify what real listing sites return for caching headers — the only
/// headers observed came from a bot-challenge page, not an origin. So these tests check that
/// validators are sent and honoured when present, and that nothing breaks when they are absent.
/// Correctness rests on the canonical content hash instead, which is why unsupported
/// conditional requests make the system slower rather than wrong.
/// </remarks>
public sealed class ConditionalRequestTests
{
    private const string UserAgent = "CarIngestBot/1.0 (+https://example.invalid; ops@example.invalid)";
    private const string Url = "https://dealer.example/listing/1";

    private static readonly HostAccessPolicy Policy = new()
    {
        Host = "dealer.example",
        ReplenishmentPeriod = TimeSpan.FromMilliseconds(1),
        PermittedFields = ["Make", "Model", "Year"],
    };

    private static (PageFetcher Fetcher, StubHttpHandler Handler) Build(Action<StubHttpHandler> configure)
    {
        var handler = new StubHttpHandler();
        handler.Map("https://dealer.example/robots.txt", "User-agent: *\nDisallow:\n");
        configure(handler);

        var monitor = new StaticOptionsMonitor<CarIngestOptions>(new CarIngestOptions
        {
            UserAgent = UserAgent,
            Sources = [new SourceOptions { Location = "/tmp" }],
            HostPolicies = [Policy],
        });

        var robots = new RobotsCache(
            handler.CreateClient(UserAgent), NullLogger<RobotsCache>.Instance,
            "CarIngestBot", TimeSpan.FromHours(24));

        return (new PageFetcher(
            handler.CreateClient(UserAgent), robots, new PerHostRateLimiter(monitor),
            monitor, NullLogger<PageFetcher>.Instance), handler);
    }

    [Fact]
    public async Task ETagIsReturnedOnFirstFetch_SoItCanBeReplayed()
    {
        var (fetcher, _) = Build(h => h.Map(Url, "<html>ok</html>", etag: "\"v1\""));

        var result = await fetcher.FetchAsync(
            new FetchRequest { Url = new Uri(Url), Policy = Policy }, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("\"v1\"", result.ETag);
    }

    [Fact]
    public async Task NotModified_ShortCircuitsParsing()
    {
        var (fetcher, _) = Build(h => h.MapStatus(Url, HttpStatusCode.NotModified));

        var result = await fetcher.FetchAsync(
            new FetchRequest { Url = new Uri(Url), Policy = Policy, KnownETag = "\"v1\"" },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.NotModified);

        // Nothing to extract and nothing to hash: the caller records 'unchanged'.
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task AbsentCachingHeaders_DoNotBreakTheFetch()
    {
        // The case research R2 says to expect on commercial listing sites.
        var (fetcher, _) = Build(h => h.Map(Url, "<html>ok</html>"));

        var result = await fetcher.FetchAsync(
            new FetchRequest { Url = new Uri(Url), Policy = Policy }, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Null(result.ETag);
        Assert.Null(result.LastModified);

        // Content still arrives, so the hash can still decide whether anything changed.
        Assert.Contains("ok", result.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerIgnoringIfNoneMatch_StillYieldsUsableContent()
    {
        // Many servers ignore the validator and return 200 with a full body. That must be
        // handled as an ordinary success, not as a protocol violation.
        var (fetcher, _) = Build(h => h.Map(Url, "<html>fresh</html>"));

        var result = await fetcher.FetchAsync(
            new FetchRequest { Url = new Uri(Url), Policy = Policy, KnownETag = "\"stale\"" },
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.False(result.NotModified);
        Assert.Contains("fresh", result.Content!, StringComparison.Ordinal);
    }
}
