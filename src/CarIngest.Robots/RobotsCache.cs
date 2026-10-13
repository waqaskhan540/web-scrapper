using System.Collections.Concurrent;
using CarIngest.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace CarIngest.Robots;

/// <summary>
/// Fetches and caches robots.txt per host, then evaluates URLs against it.
/// </summary>
/// <remarks>
/// Constitution gate I-1 requires directives to be re-checked on a cadence rather than cached
/// for process lifetime, so entries expire (RFC 9309 permits up to 24h). When a refresh fetch
/// fails we fall back to the last known good file rather than to "allowed" — a transient blip
/// must not widen what we are permitted to request.
/// <para>
/// This type exposes no bypass or override. Principle I-1 is non-overridable in production, so
/// the capability is absent rather than present-and-off.
/// </para>
/// </remarks>
public sealed partial class RobotsCache : IRobotsEvaluator
{
    private readonly HttpClient httpClient;
    private readonly ILogger<RobotsCache> logger;
    private readonly TimeSpan lifetime;
    private readonly string userAgentToken;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<string, CacheEntry> entries = new(StringComparer.OrdinalIgnoreCase);

    public RobotsCache(
        HttpClient httpClient,
        ILogger<RobotsCache> logger,
        string userAgentToken,
        TimeSpan lifetime,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgentToken);

        this.httpClient = httpClient;
        this.logger = logger;
        this.userAgentToken = userAgentToken;
        this.lifetime = lifetime > TimeSpan.Zero ? lifetime : TimeSpan.FromHours(24);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RobotsDecision> EvaluateAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        var file = await this.GetRobotsAsync(url, cancellationToken).ConfigureAwait(false);

        // Match on path + query: a '$' anchored rule is specified against both.
        var target = url.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
        if (!target.StartsWith('/'))
        {
            target = "/" + target;
        }

        var allowed = file.IsAllowed(this.userAgentToken, target);
        return new RobotsDecision
        {
            Allowed = allowed,
            CrawlDelay = file.CrawlDelay(this.userAgentToken),
            ReasonCode = allowed ? null : file.Unavailable
                ? Core.Runs.ReasonCode.RobotsUnavailable
                : Core.Runs.ReasonCode.RobotsDisallowed,
        };
    }

    private async Task<RobotsFile> GetRobotsAsync(Uri url, CancellationToken cancellationToken)
    {
        var key = url.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        var now = this.timeProvider.GetUtcNow();

        if (this.entries.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            return cached.File;
        }

        var fetched = await this.FetchRobotsAsync(new Uri(new Uri(key), "/robots.txt"), cancellationToken)
            .ConfigureAwait(false);

        if (fetched.Unavailable && cached is not null)
        {
            // Last-known-good beats a fresh failure: a 5xx should not relax what we may request,
            // and it should not tighten it either when we already hold a valid file.
            LogRefreshFailedRetainingCache(this.logger, key, now + this.lifetime);
            this.entries[key] = new CacheEntry(cached.File, now + this.lifetime);
            return cached.File;
        }

        this.entries[key] = new CacheEntry(fetched, now + this.lifetime);
        return fetched;
    }

    private async Task<RobotsFile> FetchRobotsAsync(Uri robotsUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await this.httpClient
                .GetAsync(robotsUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                LogRobotsStatusFallback(this.logger, robotsUrl.Host, (int)response.StatusCode);
                return RobotsFile.ForUnavailable((int)response.StatusCode);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return RobotsFileParser.Parse(bytes);
        }
        catch (HttpRequestException ex)
        {
            LogRobotsUnreachable(this.logger, robotsUrl.Host, ex);
            return RobotsFile.ForUnreachable();
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogRobotsTimedOut(this.logger, robotsUrl.Host, ex);
            return RobotsFile.ForUnreachable();
        }
    }

    // Source-generated, allocation-free, and structured by construction: the host and status
    // are named fields rather than interpolated text, which is what makes gate II-3's
    // "structured logs" queryable. None of these messages can carry page content.
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "robots.txt refresh failed for {Host}; retaining last known good until {Expiry}.")]
    private static partial void LogRefreshFailedRetainingCache(ILogger logger, string host, DateTimeOffset expiry);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "robots.txt for {Host} returned {Status}; applying RFC 9309 fallback.")]
    private static partial void LogRobotsStatusFallback(ILogger logger, string host, int status);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "robots.txt for {Host} unreachable; assuming full disallow.")]
    private static partial void LogRobotsUnreachable(ILogger logger, string host, Exception exception);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Warning,
        Message = "robots.txt for {Host} timed out; assuming full disallow.")]
    private static partial void LogRobotsTimedOut(ILogger logger, string host, Exception exception);

    private sealed record CacheEntry(RobotsFile File, DateTimeOffset ExpiresAt);
}
