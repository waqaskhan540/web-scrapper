using System.Net;
using CarIngest.Core.Abstractions;
using CarIngest.Core.Configuration;
using CarIngest.Core.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarIngest.Infrastructure.Fetching;

/// <summary>
/// The only way a page is retrieved.
/// </summary>
/// <remarks>
/// <para>
/// The robots check happens here, before the request is constructed — not at call sites. That
/// placement is what makes constitution gate I-1 structural: a caller cannot fetch without
/// passing it, because there is no other fetch path in the service.
/// </para>
/// <para>
/// Content is returned to the caller for extraction and never persisted. Error paths log status
/// codes and sizes only, never response bodies: logging a body on failure is the commonest way
/// to recreate a PII leak in the log sink (research D8).
/// </para>
/// </remarks>
public sealed partial class PageFetcher : IPageFetcher
{
    public const string HttpClientName = "car-listing-fetcher";

    private readonly HttpClient httpClient;
    private readonly IRobotsEvaluator robots;
    private readonly PerHostRateLimiter rateLimiter;
    private readonly IOptionsMonitor<CarIngestOptions> options;
    private readonly ILogger<PageFetcher> logger;

    public PageFetcher(
        HttpClient httpClient,
        IRobotsEvaluator robots,
        PerHostRateLimiter rateLimiter,
        IOptionsMonitor<CarIngestOptions> options,
        ILogger<PageFetcher> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(robots);
        ArgumentNullException.ThrowIfNull(rateLimiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.httpClient = httpClient;
        this.robots = robots;
        this.rateLimiter = rateLimiter;
        this.options = options;
        this.logger = logger;
    }

    public async Task<FetchResult> FetchAsync(FetchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = this.options.CurrentValue;

        // 1. Robots first. Nothing below this line runs for a disallowed path.
        var decision = await this.robots.EvaluateAsync(request.Url, cancellationToken).ConfigureAwait(false);
        this.rateLimiter.ObserveCrawlDelay(request.Url.Host, decision.CrawlDelay);

        if (!decision.Allowed)
        {
            LogSkippedByRobots(this.logger, request.Url.Host, decision.ReasonCode ?? ReasonCode.RobotsDisallowed);
            return FetchResult.Fail(
                decision.ReasonCode ?? ReasonCode.RobotsDisallowed,
                "Host access policy forbids this path.");
        }

        // 2. Per-host politeness. Queued, not rejected — a polite crawler waits its turn.
        using var lease = await this.rateLimiter.AcquireAsync(request.Url.Host, cancellationToken)
            .ConfigureAwait(false);

        if (!lease.IsAcquired)
        {
            return FetchResult.Fail(ReasonCode.RateLimitedPersistently, "Local rate limiter queue was full.");
        }

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);

            // Conditional request: an optimization only. Correctness rests on the content hash,
            // because real cache-header support on listing sites is unverified (research R2).
            if (!string.IsNullOrEmpty(request.KnownETag))
            {
                message.Headers.TryAddWithoutValidation("If-None-Match", request.KnownETag);
            }

            if (request.KnownLastModified is { } since)
            {
                message.Headers.IfModifiedSince = since;
            }

            using var response = await this.httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return FetchResult.Unmodified();
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return FetchResult.Fail(ReasonCode.PageGone, $"Host returned {(int)response.StatusCode}.");
            }

            // A sign-in, paywall, or bot challenge means the host has declined automated
            // access. FR-007 forbids circumventing it, so this is a policy skip, not a retry.
            if (response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.PaymentRequired
                or (HttpStatusCode)451)
            {
                LogAccessDeclined(this.logger, request.Url.Host, (int)response.StatusCode);
                return FetchResult.Fail(
                    ReasonCode.AuthRequired,
                    $"Host declined automated access with {(int)response.StatusCode}.");
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                return FetchResult.Fail(
                    ReasonCode.RateLimitedPersistently,
                    $"Host returned {(int)response.StatusCode} after retries were exhausted.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return FetchResult.Fail(ReasonCode.FetchHttpError, $"Host returned {(int)response.StatusCode}.");
            }

            // Reject oversize content before buffering it (FR-012). Declared length first, then
            // enforced while reading, because Content-Length may be absent or lie.
            if (response.Content.Headers.ContentLength is { } declared && declared > settings.MaxContentBytes)
            {
                return FetchResult.Fail(
                    ReasonCode.ContentTooLarge,
                    $"Declared {declared} bytes exceeds the {settings.MaxContentBytes}-byte cap.");
            }

            var content = await ReadCappedAsync(response, settings.MaxContentBytes, cancellationToken)
                .ConfigureAwait(false);

            if (content is null)
            {
                return FetchResult.Fail(
                    ReasonCode.ContentTooLarge,
                    $"Body exceeded the {settings.MaxContentBytes}-byte cap while reading.");
            }

            return FetchResult.Ok(
                content,
                response.Headers.ETag?.Tag,
                response.Content.Headers.LastModified);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FetchResult.Fail(ReasonCode.FetchTimeout, "Request exceeded the configured timeout.");
        }
        catch (HttpRequestException ex)
        {
            // Message, not body: the exception text carries transport detail, never content.
            return FetchResult.Fail(ReasonCode.FetchHttpError, ex.Message);
        }
    }

    /// <summary>Reads at most <paramref name="cap"/> bytes; returns null if the body exceeds it.</summary>
    private static async Task<string?> ReadCappedAsync(
        HttpResponseMessage response,
        long cap,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > cap)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Skipped {Host} by access policy: {ReasonCode}.")]
    private static partial void LogSkippedByRobots(ILogger logger, string host, string reasonCode);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "Host {Host} declined automated access with {Status}; excluding rather than circumventing.")]
    private static partial void LogAccessDeclined(ILogger logger, string host, int status);
}
