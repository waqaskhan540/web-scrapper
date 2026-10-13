namespace CarIngest.Api.Contracts;

// API-owned response types. No EF entity, scraper DTO, or database model ever appears on the
// wire (constitution gate III-1). Changing a field here is an API version decision (III-2).

public sealed record RunTotals(
    int AddressesRead,
    int Stored,
    int Unchanged,
    int SkippedByPolicy,
    int Failed,
    int RequestCount);

public sealed record RunSummary(
    Guid Id,
    Guid SourceId,
    string Status,
    string TriggeredBy,
    Guid? RetryOfRunId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<string> FilesRead,
    RunTotals Totals);

public sealed record AddressOutcomeItem(
    string Url,
    string Outcome,
    string? ReasonCode,
    string? Message,
    int AttemptCount,
    DateTimeOffset RecordedAt,
    Guid? DocumentId);

public sealed record OutcomePage(
    Guid RunId,
    IReadOnlyList<AddressOutcomeItem> Items,
    string? NextCursor);

public sealed record Mileage(int Value, string Unit);

/// <summary>
/// Amount is a string, not a JSON number: serializing currency as a double invites rounding
/// drift that only shows up in aggregate.
/// </summary>
public sealed record Price(string Amount, string Currency);

/// <summary>
/// Allowlisted fields only. There is no description, seller, or contact member anywhere in this
/// shape — v1 stores none, so none can be served (Principle I-4).
/// </summary>
public sealed record Vehicle(
    string Make,
    string Model,
    int Year,
    string? Trim,
    Mileage? Mileage,
    Price? Price,
    string? FuelType,
    string? Transmission,
    string? BodyStyle,
    string? Colour,
    string? Vin);

/// <summary>Fully required: a document cannot exist without provenance (FR-015).</summary>
public sealed record Provenance(
    string SourceUrl,
    DateTimeOffset RetrievedAt,
    string ExtractorVersion,
    string DiscoveryMethod,
    DateTimeOffset LastVerifiedAt);

/// <summary>Model identity only. The vector itself is never exposed.</summary>
public sealed record EmbeddingDescriptor(
    string ModelId,
    string ModelVersion,
    DateTimeOffset EmbeddedAt);

public sealed record CarDocumentResponse(
    Guid Id,
    Vehicle Vehicle,
    string Availability,
    string? DetectedLanguage,
    Provenance Provenance,
    IReadOnlyList<EmbeddingDescriptor> Embeddings);

public sealed record QueryResult(
    Guid DocumentId,
    string SourceUrl,
    DateTimeOffset RetrievedAt,
    double Distance);

public sealed record QueryResponse(
    string ModelId,
    string ModelVersion,
    IReadOnlyList<QueryResult> Results);

public sealed record SourceHealth(
    Guid SourceId,
    string Location,
    DateTimeOffset? LastSuccessfulRunAt,
    Guid? ActiveRunId);

public sealed record HealthResponse(
    string Status,
    IReadOnlyDictionary<string, string> Checks,
    IReadOnlyList<SourceHealth> Sources);

// Requests.

public sealed record StartRunRequest(Guid SourceId, Guid? RetryOfRunId);

/// <summary>
/// ModelId and ModelVersion are required, not optional. pgvector will compare vectors from
/// different models and return a plausible number with no error, so the contract refuses to let
/// a caller omit the discriminator — defaulting it would make the dangerous call the easy one
/// (FR-029).
/// </summary>
public sealed record QueryRequest(string Text, string ModelId, string ModelVersion, int? Limit);
