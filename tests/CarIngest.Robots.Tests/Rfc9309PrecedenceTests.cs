using CarIngest.Robots;
using Xunit;

namespace CarIngest.Robots.Tests;

/// <summary>
/// RFC 9309 §2.2 conformance. These are the rules that decide whether a request is lawful, so
/// they are asserted against published spec behaviour rather than against our implementation's
/// convenience.
/// </summary>
public sealed class Rfc9309PrecedenceTests
{
    private const string Agent = "CarIngestBot";

    private static RobotsFile Parse(string content) => RobotsFileParser.Parse(content);

    [Fact]
    public void ExactUserAgentGroup_BeatsWildcardGroup()
    {
        var robots = Parse("""
            User-agent: *
            Disallow: /

            User-agent: CarIngestBot
            Allow: /listing/
            """);

        Assert.True(robots.IsAllowed(Agent, "/listing/123"));
        // The wildcard group still governs an agent with no group of its own.
        Assert.False(robots.IsAllowed("SomeOtherBot", "/listing/123"));
    }

    [Fact]
    public void LongestMatchingRule_Wins_RegardlessOfOrder()
    {
        var robots = Parse("""
            User-agent: *
            Disallow: /listing/
            Allow: /listing/public/
            """);

        Assert.False(robots.IsAllowed(Agent, "/listing/private/1"));
        Assert.True(robots.IsAllowed(Agent, "/listing/public/1"));
    }

    [Fact]
    public void EqualLengthMatch_AllowBeatsDisallow()
    {
        // RFC 9309 §2.2.2: on an equal-length match, Allow wins.
        var robots = Parse("""
            User-agent: *
            Disallow: /x
            Allow: /x
            """);

        Assert.True(robots.IsAllowed(Agent, "/x"));
    }

    [Fact]
    public void EmptyDisallow_MeansAllowEverything()
    {
        var robots = Parse("""
            User-agent: *
            Disallow:
            """);

        Assert.True(robots.IsAllowed(Agent, "/anything"));
    }

    [Theory]
    [InlineData("/*.pdf$", "/doc.pdf", false)]
    [InlineData("/*.pdf$", "/doc.pdf?x=1", true)]   // $ anchors end-of-path
    [InlineData("/a/*/c", "/a/b/c", false)]
    [InlineData("/a/*/c", "/a/c", true)]             // '*' matching empty gives "/a//c", not "/a/c"
    [InlineData("/a*c", "/ac", false)]               // here '*' genuinely matches empty
    [InlineData("/private", "/private/deep/path", false)]
    [InlineData("/private", "/publicly", true)]
    public void WildcardAndAnchorMatching_FollowsSpec(string rule, string path, bool expectedAllowed)
    {
        var robots = Parse($"""
            User-agent: *
            Disallow: {rule}
            """);

        Assert.Equal(expectedAllowed, robots.IsAllowed(Agent, path));
    }

    [Fact]
    public void PathMatching_IsCaseSensitive()
    {
        var robots = Parse("""
            User-agent: *
            Disallow: /Private
            """);

        Assert.False(robots.IsAllowed(Agent, "/Private"));
        Assert.True(robots.IsAllowed(Agent, "/private"));
    }

    [Fact]
    public void PercentEncoding_IsNormalizedBeforeComparison()
    {
        var robots = Parse("""
            User-agent: *
            Disallow: /a%2Fb
            """);

        // %2F and %2f are the same octet; normalization must make them compare equal.
        Assert.False(robots.IsAllowed(Agent, "/a%2fb"));
    }

    [Fact]
    public void UserAgentMatching_IsCaseInsensitive()
    {
        var robots = Parse("""
            User-agent: cARiNGESTbOT
            Disallow: /
            """);

        Assert.False(robots.IsAllowed(Agent, "/x"));
    }

    [Fact]
    public void NoMatchingGroupAndNoWildcard_MeansUnrestricted()
    {
        var robots = Parse("""
            User-agent: SomeOtherBot
            Disallow: /
            """);

        Assert.True(robots.IsAllowed(Agent, "/anything"));
    }

    [Fact]
    public void CrawlDelay_IsReadFromTheMatchingGroup()
    {
        var robots = Parse("""
            User-agent: *
            Crawl-delay: 2

            User-agent: CarIngestBot
            Crawl-delay: 11
            """);

        Assert.Equal(TimeSpan.FromSeconds(11), robots.CrawlDelay(Agent));
        Assert.Equal(TimeSpan.FromSeconds(2), robots.CrawlDelay("SomeOtherBot"));
    }

    [Fact]
    public void UnknownDirectivesAreIgnored_ForForwardCompatibility()
    {
        var robots = Parse("""
            User-agent: *
            Some-Future-Directive: whatever
            Disallow: /blocked
            """);

        Assert.False(robots.IsAllowed(Agent, "/blocked"));
        Assert.True(robots.IsAllowed(Agent, "/ok"));
    }

    [Fact]
    public void CommentsAndBom_AreStripped()
    {
        var robots = Parse("﻿# a comment\nUser-agent: *   # trailing\nDisallow: /blocked\n");

        Assert.False(robots.IsAllowed(Agent, "/blocked"));
    }

    [Fact]
    public void SitemapDirective_IsCaptured()
    {
        var robots = Parse("""
            Sitemap: https://example.test/sitemap.xml
            User-agent: *
            Disallow:
            """);

        Assert.Contains(new Uri("https://example.test/sitemap.xml"), robots.Sitemaps);
    }
}
