using Polly;

namespace CarIngest.Infrastructure.Fetching;

/// <summary>
/// Writes the request's host onto the resilience context so the rate limiter can partition on
/// it. Must be registered <b>before</b> the resilience handler in the pipeline: the limiter
/// reads this property, so if it runs first the partition key is missing and every host shares
/// one bucket (research D11).
/// </summary>
public sealed class HostStampingHandler : DelegatingHandler
{
    public static readonly ResiliencePropertyKey<string> HostKey = new("carringest.host");

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var host = request.RequestUri?.Host ?? "unknown";
        var context = request.GetResilienceContext();
        context?.Properties.Set(HostKey, host);

        return base.SendAsync(request, cancellationToken);
    }
}
