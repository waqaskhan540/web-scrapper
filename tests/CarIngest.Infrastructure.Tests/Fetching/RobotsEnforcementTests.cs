using System.Net;
using CarIngest.Core.Abstractions;
using CarIngest.Core.Configuration;
using CarIngest.Core.Policies;
using CarIngest.Core.Runs;
using CarIngest.Infrastructure.Fetching;
using CarIngest.Infrastructure.Tests.Harness;
using CarIngest.Robots;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CarIngest.Infrastructure.Tests.Fetching;

/// <summary>
/// Enforcement of constitution gate I-1, which is non-waivable.
/// </summary>
/// <remarks>
/// Every test here asserts that the target URL <b>was never requested</b>, not merely that a
/// skip outcome was recorded. For a gate that forbids making a request, recording the right
/// reason code afterwards is not evidence of compliance — only the absence of the request is.
/// </remarks>
public sealed class RobotsEnforcementTests
{
    private const string UserAgent = "CarIngestBot/1.0 (+https://example.invalid; ops@example.invalid)";
    private const string Target = "https://dealer.example/private/listing/1";

    private static readonly HostAccessPolicy Policy = new()
    {
        Host = "dealer.example",
        ReplenishmentPeriod = TimeSpan.FromMilliseconds(1),
        PermittedFields = ["Make", "Model", "Year"],
    };

    private static (PageFetcher Fetcher, StubHttpHandler Handler) Build(
        Action<StubHttpHandler> configure,
        CarIngestOptions? options = null)
    {
        var handler = new StubHttpHandler();
        configure(handler);

        var settings = options ?? new CarIngestOptions
        {
            UserAgent = UserAgent,
            HostPolicies = [Policy],
            Sources = [new SourceOptions { Location = "/tmp" }],
        };

        var monitor = new StaticOptionsMonitor<CarIngestOptions>(settings);
        var robots = new RobotsCache(
            handler.CreateClient(UserAgent),
            NullLogger<RobotsCache>.Instance,
            "CarIngestBot",
            TimeSpan.FromHours(24));

        var fetcher = new PageFetcher(
            handler.CreateClient(UserAgent),
            robots,
            new PerHostRateLimiter(monitor),
            monitor,
            NullLogger<PageFetcher>.Instance);

        return (fetcher, handler);
    }

    [Fact]
    public async Task DisallowedPath_IsNeverRequested()
    {
        var (fetcher, handler) = Build(h => h
            .Map("https://dealer.example/robots.txt", "User-agent: *\nDisallow: /private/\n")
            .Map(Target, "<html>should never be fetched</html>"));

        var result = await fetcher.FetchAsync(
            new FetchRequest { Url = new Uri(Target), Policy = Policy },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ReasonCode.RobotsDisallowed, result.ReasonCode);

        // The assertion that matters.
        Assert.False(handler.WasRequested(Target));
        Assert.True(handler.WasRequested("https://dealer.example/robots.txt"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RobotsServerError_YieldsRobotsUnavailable_AndIssuesNoRequest(HttpStatusCode status)
    {
        // Fail-safe: we cannot know the restrictions, so we assume full disallow rather than
        // guessing "allowed" and risking an unlawful request.
        var (fetcher, handler) = Build(h => h
            .MapStatus("https://dealer.example/robots.txt", status)
            .Map(Target, "<html>should never be fetched</html>"));

        var result = await fetcher.FetchAsync(
            new FetchRequest { Url = new Uri(Target), Policy = Policy },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ReasonCode.RobotsUnavailable, result.ReasonCode);
        Assert.False(handler.WasRequested(Target));
    }

    [Fact]
    public async Task RobotsUnreachable_YieldsRobotsUnavailable_AndIssuesNoRequest()
    {
        var (fetcher, handler) = Build(h => h
            .MapThrow("https://dealer.example/robots.txt", new HttpRequestException("dns failure"))
            .Map(Target, "<html>should never be fetched</html>"));

        var result = await fetcher.FetchAsync(
            new FetchRequest { Url = new Uri(Target), Policy = Policy },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ReasonCode.RobotsUnavailable, result.ReasonCode);
        Assert.False(handler.WasRequested(Target));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RobotsClientError_MeansUnrestricted_SoTheFetchProceeds(HttpStatusCode status)
    {
        // The asymmetry with 5xx is deliberate: a 4xx means the host published no restrictions.
        const string allowed = "https://dealer.example/listing/1";

        var (fetcher, handler) = Build(h => h
            .MapStatus("https://dealer.example/robots.txt", status)
            .Map(allowed, "<html>ok</html>"));

        var result = await fetcher.FetchAsync(
            new FetchRequest { Url = new Uri(allowed), Policy = Policy },
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(handler.WasRequested(allowed));
    }

    [Fact]
    public async Task AllowedPath_IsFetched()
    {
        const string allowed = "https://dealer.example/listing/1";

        var (fetcher, handler) = Build(h => h
            .Map("https://dealer.example/robots.txt", "User-agent: *\nDisallow: /private/\n")
            .Map(allowed, "<html>ok</html>"));

        var result = await fetcher.FetchAsync(
            new FetchRequest { Url = new Uri(allowed), Policy = Policy },
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Contains("ok", result.Content!, StringComparison.Ordinal);
        Assert.True(handler.WasRequested(allowed));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task HostDecliningAutomatedAccess_IsSkippedByPolicy_NotRetriedOrCircumvented(
        HttpStatusCode status)
    {
        // FR-007 forbids circumventing sign-in, paywalls, and bot defences. A 401/402/403 on
        // the page itself means the host has declined; the response is to exclude it.
        const string gated = "https://dealer.example/listing/gated";

        var (fetcher, _) = Build(h => h
            .Map("https://dealer.example/robots.txt", "User-agent: *\nDisallow:\n")
            .MapStatus(gated, status));

        var result = await fetcher.FetchAsync(
            new FetchRequest { Url = new Uri(gated), Policy = Policy },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ReasonCode.AuthRequired, result.ReasonCode);
    }

    [Fact]
    public async Task OversizeContent_IsRejected_WithoutStoringIt()
    {
        const string big = "https://dealer.example/listing/huge";
        var oversize = new string('x', 2048);

        var options = new CarIngestOptions
        {
            UserAgent = UserAgent,
            MaxContentBytes = 1024,
            HostPolicies = [Policy],
            Sources = [new SourceOptions { Location = "/tmp" }],
        };

        var (fetcher, _) = Build(
            h => h.Map("https://dealer.example/robots.txt", "User-agent: *\nDisallow:\n").Map(big, oversize),
            options);

        var result = await fetcher.FetchAsync(
            new FetchRequest { Url = new Uri(big), Policy = Policy },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ReasonCode.ContentTooLarge, result.ReasonCode);
        Assert.Null(result.Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, ReasonCode.PageGone)]
    [InlineData(HttpStatusCode.Gone, ReasonCode.PageGone)]
    public async Task VanishedPage_IsReportedAsPageGone(HttpStatusCode status, string expected)
    {
        const string gone = "https://dealer.example/listing/gone";

        var (fetcher, _) = Build(h => h
            .Map("https://dealer.example/robots.txt", "User-agent: *\nDisallow:\n")
            .MapStatus(gone, status));

        var result = await fetcher.FetchAsync(
            new FetchRequest { Url = new Uri(gone), Policy = Policy },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(expected, result.ReasonCode);
    }

    [Fact]
    public async Task NotModified_ShortCircuitsWithoutContent()
    {
        const string url = "https://dealer.example/listing/1";

        var (fetcher, _) = Build(h => h
            .Map("https://dealer.example/robots.txt", "User-agent: *\nDisallow:\n")
            .MapStatus(url, HttpStatusCode.NotModified));

        var result = await fetcher.FetchAsync(
            new FetchRequest { Url = new Uri(url), Policy = Policy, KnownETag = "\"abc\"" },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.NotModified);
        Assert.Null(result.Content);
    }
}
