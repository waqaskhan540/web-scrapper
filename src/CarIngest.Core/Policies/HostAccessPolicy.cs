namespace CarIngest.Core.Policies;

/// <summary>How a host may be extracted. Never an automatic fallback (research D9).</summary>
public enum ExtractorKind
{
    /// <summary>schema.org JSON-LD. The default and the drift-resistant path.</summary>
    JsonLd,

    /// <summary>
    /// Per-host CSS selectors. Selected only by explicit configuration — absence of JSON-LD
    /// must fail loudly rather than silently degrade to selector scraping, because a selector
    /// that matches the wrong element produces plausible wrong data (FR-011).
    /// </summary>
    Css,
}

/// <summary>
/// The recorded rules governing retrieval from one host (spec FR-008). Config-sourced, never
/// writable at runtime (constitution gate T-5). A host without an entry is not ingested.
/// </summary>
public sealed class HostAccessPolicy
{
    public string Host { get; init; } = string.Empty;

    /// <summary>Token-bucket capacity. Must be greater than zero.</summary>
    public int RequestsPerPeriod { get; init; } = 1;

    /// <summary>
    /// Bucket replenishment period. The effective delay is
    /// max(this, robots Crawl-delay) — robots can only slow us down, never speed us up.
    /// </summary>
    public TimeSpan ReplenishmentPeriod { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Bounded per host, never only globally (FR-006).</summary>
    public int MaxConcurrency { get; init; } = 1;

    /// <summary>
    /// The allowlist for this host. Must be non-empty, and every entry must name a field in
    /// <see cref="Documents.VehicleFieldNames.All"/>.
    /// </summary>
    public IReadOnlyList<string> PermittedFields { get; init; } = [];

    public ExtractorKind ExtractorKind { get; init; } = ExtractorKind.JsonLd;

    /// <summary>Profile name when <see cref="ExtractorKind"/> is <see cref="ExtractorKind.Css"/>.</summary>
    public string? CssProfile { get; init; }
}
