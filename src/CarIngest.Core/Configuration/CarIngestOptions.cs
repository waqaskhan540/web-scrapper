using CarIngest.Core.Policies;

namespace CarIngest.Core.Configuration;

public sealed class EmbeddingOptions
{
    /// <summary>"ollama" (default, self-hosted) or "openai" — a registration swap (research D5).</summary>
    public string Provider { get; init; } = "ollama";

    public string ModelId { get; init; } = "nomic-embed-text";

    public Uri Endpoint { get; init; } = new("http://localhost:11434");

    /// <summary>
    /// Must match the vector column width. 768 for nomic-embed-text. Changing this requires a
    /// new table via migration, not a column change: pgvector columns are fixed-dimension and
    /// zero-padding would silently corrupt distances (research D7).
    /// </summary>
    public int Dimensions { get; init; } = 768;
}

public sealed class SourceOptions
{
    public string Location { get; init; } = string.Empty;
}

/// <summary>
/// All per-host policy, rate, and schedule values live here rather than in code
/// (constitution gate T-5).
/// </summary>
public sealed class CarIngestOptions
{
    public const string SectionName = "CarIngest";

    public IReadOnlyList<SourceOptions> Sources { get; init; } = [];

    /// <summary>
    /// Descriptive agent string including a contact address (FR-007). Identity is never rotated
    /// or disguised, so there is exactly one of these for the whole service.
    /// </summary>
    public string UserAgent { get; init; } = string.Empty;

    /// <summary>
    /// Per-host policies as a <b>list</b>, each carrying its own <see cref="HostAccessPolicy.Host"/>.
    /// </summary>
    /// <remarks>
    /// A list rather than a dictionary keyed by host, because ':' is .NET's configuration
    /// hierarchy separator: a key of "localhost:8099" binds as nested keys "localhost" then
    /// "8099" rather than as one host. Since <see cref="Sources.UrlNormalizer.HostKey"/> includes
    /// the port for non-default ports, a host-keyed dictionary simply cannot express those hosts.
    /// Look policies up via <see cref="PolicyByHost"/>.
    /// </remarks>
    public IReadOnlyList<HostAccessPolicy> HostPolicies { get; init; } = [];

    private IReadOnlyDictionary<string, HostAccessPolicy>? policyByHost;

    /// <summary>Host-keyed view of <see cref="HostPolicies"/>, built once per options instance.</summary>
    public IReadOnlyDictionary<string, HostAccessPolicy> PolicyByHost =>
        this.policyByHost ??= this.HostPolicies
            .Where(p => !string.IsNullOrWhiteSpace(p.Host))
            .GroupBy(p => p.Host, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

    public EmbeddingOptions Embedding { get; init; } = new();

    /// <summary>Bounded retrieval size (FR-012). Oversize content is rejected with a stated reason.</summary>
    public long MaxContentBytes { get; init; } = 5 * 1024 * 1024;

    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>robots.txt cache lifetime. RFC 9309 permits up to 24 hours.</summary>
    public TimeSpan RobotsCacheLifetime { get; init; } = TimeSpan.FromHours(24);
}
