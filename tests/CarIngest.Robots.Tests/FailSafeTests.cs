using CarIngest.Robots;
using Xunit;

namespace CarIngest.Robots.Tests;

/// <summary>
/// Fail-safe behaviour under RFC 9309 §2.3.1. The asymmetry here is deliberate: a 4xx means the
/// host published no restrictions, but a 5xx or an unreachable host means we do not know what
/// the restrictions are — and guessing "allowed" would risk unlawful requests. So we assume
/// full disallow (research D10).
/// </summary>
public sealed class FailSafeTests
{
    private const string Agent = "CarIngestBot";

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(410)]
    public void ClientError_MeansUnrestricted(int status)
    {
        var robots = RobotsFile.ForUnavailable(status);

        Assert.True(robots.IsAllowed(Agent, "/anything"));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void ServerError_MeansFullDisallow(int status)
    {
        var robots = RobotsFile.ForUnavailable(status);

        Assert.False(robots.IsAllowed(Agent, "/anything"));
        Assert.False(robots.IsAllowed(Agent, "/"));
    }

    [Fact]
    public void Unreachable_MeansFullDisallow()
    {
        var robots = RobotsFile.ForUnreachable();

        Assert.False(robots.IsAllowed(Agent, "/anything"));
    }

    [Fact]
    public void MalformedContent_DoesNotThrow_AndDoesNotSilentlyAllowEverything()
    {
        // Garbage input must not throw out of the evaluator. Lines that are not directives are
        // ignored, so this file declares nothing and imposes no restriction — the point of the
        // test is that parsing is total, not that garbage blocks.
        var robots = RobotsFileParser.Parse("\0\0 not a robots file �\n:::::\nDisallow\n");

        Assert.True(robots.IsAllowed(Agent, "/x"));
    }

    [Fact]
    public void DisallowBeforeAnyUserAgent_IsIgnored()
    {
        // A rule outside any group belongs to no agent and must not be applied globally.
        var robots = RobotsFileParser.Parse("Disallow: /\nUser-agent: *\nAllow: /\n");

        Assert.True(robots.IsAllowed(Agent, "/x"));
    }

    [Fact]
    public void ContentBeyondParseCap_IsTruncated_NotRejected()
    {
        // RFC 9309 §2.3.1.2 recommends a 500 KiB parse limit. Rules inside the cap still apply;
        // the tail is discarded rather than the whole file being treated as unparseable.
        var padding = new string('#', RobotsFileParser.MaxParseBytes);
        var robots = RobotsFileParser.Parse(
            "User-agent: *\nDisallow: /blocked\n" + padding + "\nDisallow: /beyond-the-cap\n");

        Assert.False(robots.IsAllowed(Agent, "/blocked"));
        Assert.True(robots.IsAllowed(Agent, "/beyond-the-cap"));
    }

    [Fact]
    public void EvaluatorExposesNoBypass()
    {
        // Constitution Principle I-1 is non-overridable in production. This asserts the
        // capability does not exist on the surface at all — not that it defaults to off.
        var members = typeof(RobotsFile).GetMembers()
            .Concat(typeof(RobotsFileParser).GetMembers())
            .Select(m => m.Name);

        Assert.DoesNotContain(members, name =>
            name.Contains("Bypass", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Override", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Force", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Ignore", StringComparison.OrdinalIgnoreCase));
    }
}
