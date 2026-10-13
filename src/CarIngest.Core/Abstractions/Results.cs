using CarIngest.Core.Documents;

namespace CarIngest.Core.Abstractions;

/// <summary>
/// Outcome of one fetch. Carries page text only long enough to be extracted — raw bytes are
/// never persisted (research D8).
/// </summary>
public sealed record FetchResult
{
    public required bool Success { get; init; }

    /// <summary>Set only on success. Discarded after extraction; never stored, never logged.</summary>
    public string? Content { get; init; }

    public string? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    /// <summary>True when a conditional request returned 304 — nothing to re-parse.</summary>
    public bool NotModified { get; init; }

    /// <summary>Set when <see cref="Success"/> is false. A closed-set code from ReasonCode.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>Field names and status only — never page content (Principle I-4).</summary>
    public string? Message { get; init; }

    public static FetchResult Ok(string content, string? etag, DateTimeOffset? lastModified) =>
        new() { Success = true, Content = content, ETag = etag, LastModified = lastModified };

    public static FetchResult Unmodified() => new() { Success = true, NotModified = true };

    public static FetchResult Fail(string reasonCode, string? message = null) =>
        new() { Success = false, ReasonCode = reasonCode, Message = message };
}

/// <summary>Outcome of one extraction. Either a complete projection or a typed failure.</summary>
public sealed record ExtractionResult
{
    public required bool Success { get; init; }

    public VehicleFields? Fields { get; init; }

    public string? ReasonCode { get; init; }

    /// <summary>Names the missing or invalid field. Never quotes page content.</summary>
    public string? Message { get; init; }

    public static ExtractionResult Ok(VehicleFields fields) =>
        new() { Success = true, Fields = fields };

    public static ExtractionResult Fail(string reasonCode, string message) =>
        new() { Success = false, ReasonCode = reasonCode, Message = message };
}
