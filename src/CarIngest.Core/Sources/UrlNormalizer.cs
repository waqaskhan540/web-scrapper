using System.Diagnostics.CodeAnalysis;

namespace CarIngest.Core.Sources;

/// <summary>Normalizes addresses so de-duplication is reliable (FR-004).</summary>
public static class UrlNormalizer
{
    /// <summary>
    /// Normalizes for comparison, or returns false for anything that is not an absolute
    /// http/https URL.
    /// </summary>
    /// <remarks>
    /// Three choices here are deliberate and load-bearing:
    /// <list type="bullet">
    /// <item>
    /// <b>Path case is preserved.</b> RFC 9309 path matching is case-sensitive, so lowercasing
    /// the path would change which robots rules apply to the address.
    /// </item>
    /// <item>
    /// <b>The query string is preserved.</b> Listing URLs commonly carry the vehicle id there;
    /// stripping it would collapse every listing on a host into one address.
    /// </item>
    /// <item>
    /// <b>The fragment is dropped.</b> It never reaches the server, so two addresses differing
    /// only by fragment are the same page.
    /// </item>
    /// </list>
    /// </remarks>
    public static bool TryNormalize(string? candidate, [NotNullWhen(true)] out Uri? normalized)
    {
        normalized = null;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }

        var builder = new UriBuilder(parsed)
        {
            // Scheme and host are case-insensitive per RFC 3986; UriBuilder already lowercases
            // them. The fragment is dropped because the server never sees it.
            Fragment = string.Empty,
        };

        // A bare "?" with no parameters is not a distinct address.
        if (builder.Query is "?" or "")
        {
            builder.Query = string.Empty;
        }

        normalized = builder.Uri;
        return true;
    }

    /// <summary>The rate-limiter and host-policy key. Includes the port when non-default.</summary>
    public static string HostKey(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}";
    }
}
