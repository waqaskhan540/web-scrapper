using CarIngest.Core.Runs;
using Pgvector;

namespace CarIngest.Infrastructure.Storage.Entities;

/// <summary>The configured folder holding address-list files (FR-001).</summary>
public sealed class AddressSource
{
    public Guid Id { get; set; }

    public string Location { get; set; } = string.Empty;

    public DateTimeOffset? LastReadAt { get; set; }

    /// <summary>Feeds the health report. A stalled source is detectable from this alone (gate II-5).</summary>
    public DateTimeOffset? LastSuccessfulRunAt { get; set; }

    public ICollection<IngestionTarget> Targets { get; } = [];
}

/// <summary>How an address came to be known. Open set, deliberately (FR-015a).</summary>
public static class DiscoveryMethod
{
    /// <summary>Supplied by an operator in an address-list file. The only value in v1.</summary>
    public const string OperatorFile = "operator-file";

    /// <summary>
    /// Reserved. A future crawling feature writes this without altering existing rows, which is
    /// why the column is text rather than an int-backed enum.
    /// </summary>
    public const string LinkDiscovered = "link-discovered";
}

/// <summary>One distinct car-page address (FR-004: de-duplicated within and across files).</summary>
public sealed class IngestionTarget
{
    public Guid Id { get; set; }

    public Guid AddressSourceId { get; set; }

    public AddressSource? AddressSource { get; set; }

    public string Url { get; set; } = string.Empty;

    /// <summary>The rate-limiter and host-policy key.</summary>
    public string Host { get; set; } = string.Empty;

    public string DiscoveryMethod { get; set; } = Entities.DiscoveryMethod.OperatorFile;

    public string? DiscoverySourceFile { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset? LastAttemptedAt { get; set; }

    /// <summary>Replayed as If-None-Match. An optimization only; the content hash is authoritative.</summary>
    public string? KnownETag { get; set; }

    public DateTimeOffset? KnownLastModified { get; set; }

    /// <summary>Many: one listing page can describe several cars.</summary>
    public ICollection<CarDocument> Documents { get; } = [];
}

public static class Availability
{
    public const string Available = "available";

    /// <summary>Page returned 404/410. The row is retained, never deleted (spec edge case).</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>
/// One accepted car. The stored allowlisted projection — no free-text description, no raw page
/// bytes, no catch-all column (constitution I-4, spec FR-010).
/// </summary>
public sealed class CarDocument
{
    public Guid Id { get; set; }

    public Guid IngestionTargetId { get; set; }

    public IngestionTarget? IngestionTarget { get; set; }

    /// <summary>
    /// Stable identity for this car within its page. A listing page describes several cars, so
    /// the address alone does not identify a document — without this, re-ingesting would either
    /// duplicate every card or collapse them all onto one row.
    /// </summary>
    public string CardKey { get; set; } = "page";

    /// <summary>
    /// The car's own page when the listing links to one. <b>Recorded, never fetched</b> — on
    /// webscraper.io these sit under a robots-disallowed prefix, and storing the URL lets a
    /// reviewer confirm we did not request it.
    /// </summary>
    public string? DetailUrl { get; set; }

    // Allowlisted vehicle fields.
    public string Make { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int Year { get; set; }

    public string? Trim { get; set; }

    public int? MileageValue { get; set; }

    public string? MileageUnit { get; set; }

    public decimal? PriceAmount { get; set; }

    public string? PriceCurrency { get; set; }

    public string? FuelType { get; set; }

    public string? Transmission { get; set; }

    public string? BodyStyle { get; set; }

    public string? Colour { get; set; }

    public string? Vin { get; set; }

    // State.
    public string Availability { get; set; } = Entities.Availability.Available;

    public string? DetectedLanguage { get; set; }

    /// <summary>SHA-256 of the canonical projection. Drives FR-018 change detection.</summary>
    public byte[] ContentHash { get; set; } = [];

    // Provenance — every one of these is non-nullable in the schema (FR-015, gate III-4).
    public string SourceUrl { get; set; } = string.Empty;

    public DateTimeOffset RetrievedAt { get; set; }

    public string ExtractorVersion { get; set; } = string.Empty;

    public string DiscoveryMethod { get; set; } = Entities.DiscoveryMethod.OperatorFile;

    public DateTimeOffset LastVerifiedAt { get; set; }

    public ICollection<CarDocumentEmbedding> Embeddings { get; } = [];
}

/// <summary>
/// One semantic representation, keyed by the model identity that produced it.
/// </summary>
/// <remarks>
/// Separate table rather than a column on <see cref="CarDocument"/> because pgvector columns are
/// fixed-dimension and its indexes are per-column: a 384-dim and a 768-dim vector cannot share
/// one column without zero-padding, which would silently corrupt distances (research D7).
/// </remarks>
public sealed class CarDocumentEmbedding
{
    public Guid Id { get; set; }

    public Guid CarDocumentId { get; set; }

    public CarDocument? CarDocument { get; set; }

    public Vector Embedding { get; set; } = new(new float[1]);

    public string ModelId { get; set; } = string.Empty;

    /// <summary>The resolved digest, not a mutable tag (FR-014).</summary>
    public string ModelVersion { get; set; } = string.Empty;

    public DateTimeOffset EmbeddedAt { get; set; }

    /// <summary>SHA-256 of the exact text embedded. Makes re-embedding idempotent.</summary>
    public byte[] SourceTextHash { get; set; } = [];
}

public static class RunStatus
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    /// <summary>Set by startup reconciliation after a mid-run restart.</summary>
    public const string Interrupted = "interrupted";
}

public static class TriggeredBy
{
    public const string Operator = "operator";
    public const string Retry = "retry";
}

/// <summary>One execution over a source (FR-019).</summary>
public sealed class IngestionRun
{
    public Guid Id { get; set; }

    public Guid AddressSourceId { get; set; }

    public AddressSource? AddressSource { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string Status { get; set; } = RunStatus.Running;

    /// <summary>Set when this run retries another's failures (FR-021).</summary>
    public Guid? RetryOfRunId { get; set; }

    public List<string> FilesRead { get; set; } = [];

    public int AddressesRead { get; set; }

    public int StoredCount { get; set; }

    public int UnchangedCount { get; set; }

    public int SkippedByPolicyCount { get; set; }

    public int FailedCount { get; set; }

    public int RequestCount { get; set; }

    public string TriggeredBy { get; set; } = Entities.TriggeredBy.Operator;

    public ICollection<AddressOutcome> Outcomes { get; } = [];
}

/// <summary>The result for one target within one run (FR-020).</summary>
public sealed class AddressOutcome
{
    public Guid Id { get; set; }

    public Guid IngestionRunId { get; set; }

    public IngestionRun? IngestionRun { get; set; }

    public Guid IngestionTargetId { get; set; }

    public IngestionTarget? IngestionTarget { get; set; }

    /// <summary>stored | unchanged | skipped-by-policy | failed.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>A closed-set <see cref="ReasonCode"/> value. Null only for stored/unchanged.</summary>
    public string? ReasonCode { get; set; }

    /// <summary>Field names and status only — never page content (Principle I-4).</summary>
    public string? Message { get; set; }

    public int AttemptCount { get; set; } = 1;

    public DateTimeOffset RecordedAt { get; set; }

    public Guid? CarDocumentId { get; set; }
}
