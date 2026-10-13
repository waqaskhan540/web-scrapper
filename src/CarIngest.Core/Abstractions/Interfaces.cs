using CarIngest.Core.Documents;
using CarIngest.Core.Policies;

namespace CarIngest.Core.Abstractions;

/// <summary>
/// Evaluates a host's robots.txt. Deliberately exposes no bypass, override, or "force"
/// parameter: constitution Principle I-1 is non-overridable in production, so the capability
/// must not exist on the type at all.
/// </summary>
public interface IRobotsEvaluator
{
    Task<RobotsDecision> EvaluateAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>The robots verdict for one URL, plus the host's crawl delay if it declared one.</summary>
public sealed record RobotsDecision
{
    public required bool Allowed { get; init; }

    public TimeSpan? CrawlDelay { get; init; }

    /// <summary>Set when <see cref="Allowed"/> is false: robots-disallowed or robots-unavailable.</summary>
    public string? ReasonCode { get; init; }
}

public interface IPageFetcher
{
    Task<FetchResult> FetchAsync(FetchRequest request, CancellationToken cancellationToken);
}

public sealed record FetchRequest
{
    public required Uri Url { get; init; }

    public required HostAccessPolicy Policy { get; init; }

    /// <summary>Replayed as If-None-Match when present — an optimization, not a correctness mechanism.</summary>
    public string? KnownETag { get; init; }

    public DateTimeOffset? KnownLastModified { get; init; }
}

public interface ICarExtractor
{
    ExtractorKind Kind { get; }

    /// <summary>
    /// Extracts every car described by one retrieved page.
    /// </summary>
    /// <remarks>
    /// Returns a list rather than a single result because a page is not always one car. A
    /// listing page can describe many, and on hosts whose per-car detail pages are
    /// robots-disallowed the listing page is the only lawful source — so all of them must come
    /// from one fetch, which is also strictly politer than one request per car. A single-car
    /// page simply returns a one-element list.
    /// </remarks>
    IReadOnlyList<ExtractedCar> Extract(string content, Uri sourceUrl, HostAccessPolicy policy);
}

/// <summary>One card from a page: its stable within-page identity and the extraction outcome.</summary>
/// <param name="CardKey">
/// Stable identity for this car within the page, so re-ingesting matches cards to existing
/// documents instead of duplicating them. Derived from the detail-page slug when present.
/// </param>
/// <param name="DetailUrl">
/// The car's own page, <b>recorded but never fetched</b> — on webscraper.io these sit under a
/// robots-disallowed prefix. Kept because it is useful provenance and because recording it lets
/// a reviewer confirm we did not request it.
/// </param>
public sealed record ExtractedCar(string CardKey, string? DetailUrl, ExtractionResult Extraction);

/// <summary>
/// Builds the text that gets embedded. Takes the already-screened projection, so personal data
/// cannot reach the vector — where it would be unredactable (research D6, D8).
/// </summary>
public interface IEmbeddingTextBuilder
{
    string Build(VehicleFields fields);
}
