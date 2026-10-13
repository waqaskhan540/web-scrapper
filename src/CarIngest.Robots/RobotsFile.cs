using System.Collections.ObjectModel;

namespace CarIngest.Robots;

/// <summary>One User-agent group: the agents it names and the rules it carries.</summary>
public sealed class RobotsGroup
{
    internal RobotsGroup(IReadOnlyList<string> agents, IReadOnlyList<RobotsRule> rules, TimeSpan? crawlDelay)
    {
        this.Agents = agents;
        this.Rules = rules;
        this.CrawlDelay = crawlDelay;
    }

    public IReadOnlyList<string> Agents { get; }

    public IReadOnlyList<RobotsRule> Rules { get; }

    public TimeSpan? CrawlDelay { get; }

    public bool IsWildcard => this.Agents.Contains("*", StringComparer.Ordinal);

    internal bool Matches(string userAgentToken) =>
        this.Agents.Any(a => string.Equals(a, userAgentToken, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A single Allow or Disallow rule.</summary>
public sealed record RobotsRule(bool Allow, string Pattern)
{
    /// <summary>
    /// Match length for precedence purposes. RFC 9309 §2.2.2 resolves competing rules by the
    /// length of the matched path, so this is the comparison key, not rule order.
    /// </summary>
    public int Specificity => this.Pattern.Length;
}

/// <summary>
/// A parsed robots.txt, or a stand-in for one that could not be fetched.
/// </summary>
/// <remarks>
/// Exposes no bypass, override, or force operation. Constitution Principle I-1 is
/// non-overridable in production, so the capability is absent from the type rather than
/// present-and-defaulted-off.
/// </remarks>
public sealed class RobotsFile
{
    private readonly IReadOnlyList<RobotsGroup> groups;
    private readonly bool denyAll;

    internal RobotsFile(IReadOnlyList<RobotsGroup> groups, IReadOnlyList<Uri> sitemaps, bool denyAll)
    {
        this.groups = groups;
        this.Sitemaps = sitemaps;
        this.denyAll = denyAll;
    }

    public IReadOnlyList<Uri> Sitemaps { get; }

    /// <summary>
    /// True when this stands in for a robots.txt we could not read, rather than one we parsed.
    /// Lets the caller distinguish robots-unavailable from robots-disallowed, which matters
    /// because the operator's remedy differs: one is a transient host problem, the other is the
    /// host telling us not to.
    /// </summary>
    public bool Unavailable { get; private init; }

    /// <summary>
    /// Stand-in for a robots.txt that returned an error status. A 4xx means the host published
    /// no restrictions; a 5xx means we cannot know them, so we assume full disallow.
    /// </summary>
    public static RobotsFile ForUnavailable(int statusCode)
    {
        var serverError = statusCode >= 500;
        return new RobotsFile([], [], denyAll: serverError) { Unavailable = serverError };
    }

    /// <summary>Stand-in for an unreachable host. Fail-safe: assume full disallow.</summary>
    public static RobotsFile ForUnreachable() => new([], [], denyAll: true) { Unavailable = true };

    /// <summary>Evaluates a path against the group matching <paramref name="userAgentToken"/>.</summary>
    public bool IsAllowed(string userAgentToken, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(userAgentToken);
        ArgumentNullException.ThrowIfNull(path);

        if (this.denyAll)
        {
            return false;
        }

        var group = this.SelectGroup(userAgentToken);
        if (group is null)
        {
            // No group names this agent and there is no wildcard group: unrestricted.
            return true;
        }

        var normalizedPath = PathMatcher.NormalizePath(path);

        RobotsRule? best = null;
        foreach (var rule in group.Rules)
        {
            if (rule.Pattern.Length == 0)
            {
                // "Disallow:" with an empty value imposes nothing.
                continue;
            }

            if (!PathMatcher.Matches(rule.Pattern, normalizedPath))
            {
                continue;
            }

            if (best is null ||
                rule.Specificity > best.Specificity ||
                (rule.Specificity == best.Specificity && rule.Allow))
            {
                // Equal-length ties go to Allow (RFC 9309 §2.2.2).
                best = rule;
            }
        }

        return best is null || best.Allow;
    }

    public TimeSpan? CrawlDelay(string userAgentToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(userAgentToken);
        return this.SelectGroup(userAgentToken)?.CrawlDelay;
    }

    /// <summary>Most specific group wins: an exact agent match beats the wildcard group.</summary>
    private RobotsGroup? SelectGroup(string userAgentToken) =>
        this.groups.FirstOrDefault(g => g.Matches(userAgentToken))
        ?? this.groups.FirstOrDefault(g => g.IsWildcard);
}
