using System.Globalization;
using System.Text;

namespace CarIngest.Robots;

/// <summary>
/// Line-based RFC 9309 parser. Total by construction: malformed input yields a file with no
/// rules rather than an exception, because a parse failure must not become an unhandled fault
/// inside the fetch path.
/// </summary>
public static class RobotsFileParser
{
    /// <summary>RFC 9309 §2.3.1.2 recommends parsing at least 500 KiB. Content beyond is discarded.</summary>
    public const int MaxParseBytes = 500 * 1024;

    public static RobotsFile Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length > MaxParseBytes)
        {
            content = content[..MaxParseBytes];
        }

        var groups = new List<RobotsGroup>();
        var sitemaps = new List<Uri>();

        var currentAgents = new List<string>();
        var currentRules = new List<RobotsRule>();
        TimeSpan? currentDelay = null;
        var collectingAgents = false;

        void FlushGroup()
        {
            if (currentAgents.Count > 0)
            {
                groups.Add(new RobotsGroup([.. currentAgents], [.. currentRules], currentDelay));
            }

            currentAgents = [];
            currentRules = [];
            currentDelay = null;
        }

        foreach (var rawLine in content.Split('\n'))
        {
            var line = StripComment(rawLine).Trim().TrimStart('﻿');
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                // Not a directive. Ignored for forward compatibility.
                continue;
            }

            var field = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (field.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
            {
                // Consecutive User-agent lines accumulate into one group; a User-agent after
                // rules starts a new group.
                if (!collectingAgents)
                {
                    FlushGroup();
                    collectingAgents = true;
                }

                if (value.Length > 0)
                {
                    currentAgents.Add(value);
                }

                continue;
            }

            collectingAgents = false;

            if (field.Equals("sitemap", StringComparison.OrdinalIgnoreCase))
            {
                // Sitemap is file-scoped, not group-scoped.
                if (Uri.TryCreate(value, UriKind.Absolute, out var sitemap))
                {
                    sitemaps.Add(sitemap);
                }

                continue;
            }

            if (currentAgents.Count == 0)
            {
                // A rule outside any group belongs to no agent, so it is dropped rather than
                // applied globally.
                continue;
            }

            if (field.Equals("disallow", StringComparison.OrdinalIgnoreCase))
            {
                currentRules.Add(new RobotsRule(Allow: false, value));
            }
            else if (field.Equals("allow", StringComparison.OrdinalIgnoreCase))
            {
                currentRules.Add(new RobotsRule(Allow: true, value));
            }
            else if (field.Equals("crawl-delay", StringComparison.OrdinalIgnoreCase) &&
                     double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                     seconds is >= 0 and < 86400)
            {
                currentDelay = TimeSpan.FromSeconds(seconds);
            }

            // Any other field is an unknown directive: ignored, per forward compatibility.
        }

        FlushGroup();
        return new RobotsFile(groups, sitemaps, denyAll: false);
    }

    public static RobotsFile Parse(ReadOnlySpan<byte> utf8Content)
    {
        var length = Math.Min(utf8Content.Length, MaxParseBytes);
        return Parse(Encoding.UTF8.GetString(utf8Content[..length]));
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#', StringComparison.Ordinal);
        return hash >= 0 ? line[..hash] : line;
    }
}
