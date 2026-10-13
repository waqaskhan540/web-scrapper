using System.Net;

namespace CarIngest.Infrastructure.Tests.Harness;

/// <summary>
/// Returns canned responses from a URL map. Zero-dependency and the first choice for extraction
/// unit tests — no live network reaches the wire (constitution gate W-2).
/// </summary>
/// <remarks>
/// Also records every requested URL, which lets a test assert that a request was <b>never
/// issued</b> — the difference between "we recorded a robots skip" and "we actually refrained
/// from fetching". For a non-overridable gate, only the latter is proof.
/// </remarks>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Uri> requested = [];
    private readonly Lock gate = new();

    public IReadOnlyList<Uri> RequestedUrls
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.requested];
            }
        }
    }

    public StubHttpHandler Map(string url, string body, HttpStatusCode status = HttpStatusCode.OK, string? etag = null)
    {
        this.routes[url] = _ =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html"),
            };

            if (etag is not null)
            {
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            }

            return response;
        };

        return this;
    }

    public StubHttpHandler MapStatus(string url, HttpStatusCode status)
    {
        this.routes[url] = _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(string.Empty),
        };

        return this;
    }

    public StubHttpHandler MapThrow(string url, Exception exception)
    {
        this.routes[url] = _ => throw exception;
        return this;
    }

    public bool WasRequested(string url) =>
        this.RequestedUrls.Any(u => string.Equals(u.ToString(), url, StringComparison.OrdinalIgnoreCase));

    public HttpClient CreateClient(string? userAgent = null)
    {
        var client = new HttpClient(this, disposeHandler: false);
        if (userAgent is not null)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }

        return client;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (this.gate)
        {
            this.requested.Add(request.RequestUri!);
        }

        var key = request.RequestUri!.ToString();
        if (this.routes.TryGetValue(key, out var factory))
        {
            return Task.FromResult(factory(request));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(string.Empty),
        });
    }
}
