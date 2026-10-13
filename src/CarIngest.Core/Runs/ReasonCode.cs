using System.Collections.Frozen;

namespace CarIngest.Core.Runs;

/// <summary>The outcome of attempting one address within one run.</summary>
public enum AddressOutcomeKind
{
    Stored,
    Unchanged,
    SkippedByPolicy,
    Failed,
}

/// <summary>
/// The closed set of machine-readable reasons (spec FR-020). Closed rather than free-text
/// because SC-007 requires an operator to distinguish "nothing matched" from "extraction is
/// broken" — which an ad-hoc message string cannot support.
/// </summary>
public static class ReasonCode
{
    // Skipped by policy — the host, or its robots.txt, declined automated access.
    public const string RobotsDisallowed = "robots-disallowed";
    public const string RobotsUnavailable = "robots-unavailable";
    public const string NoHostPolicy = "no-host-policy";
    public const string AuthRequired = "auth-required";

    // Failed — fetching.
    public const string MalformedAddress = "malformed-address";
    public const string FetchTimeout = "fetch-timeout";
    public const string FetchHttpError = "fetch-http-error";
    public const string RateLimitedPersistently = "rate-limited-persistently";
    public const string ContentTooLarge = "content-too-large";
    public const string PageGone = "page-gone";

    // Failed — extraction.
    public const string JsonLdAbsent = "jsonld-absent";
    public const string JsonLdMalformed = "jsonld-malformed";
    public const string SchemaDrift = "schema-drift";
    public const string ValidationRange = "validation-range";
    public const string JsRenderRequired = "js-render-required";
    public const string NotACarPage = "not-a-car-page";

    // Failed — downstream.
    public const string EmbeddingUnavailable = "embedding-unavailable";

    private static readonly FrozenDictionary<string, AddressOutcomeKind> Map = new Dictionary<string, AddressOutcomeKind>(StringComparer.Ordinal)
    {
        [RobotsDisallowed] = AddressOutcomeKind.SkippedByPolicy,
        [RobotsUnavailable] = AddressOutcomeKind.SkippedByPolicy,
        [NoHostPolicy] = AddressOutcomeKind.SkippedByPolicy,
        [AuthRequired] = AddressOutcomeKind.SkippedByPolicy,
        [MalformedAddress] = AddressOutcomeKind.Failed,
        [FetchTimeout] = AddressOutcomeKind.Failed,
        [FetchHttpError] = AddressOutcomeKind.Failed,
        [RateLimitedPersistently] = AddressOutcomeKind.Failed,
        [ContentTooLarge] = AddressOutcomeKind.Failed,
        [PageGone] = AddressOutcomeKind.Failed,
        [JsonLdAbsent] = AddressOutcomeKind.Failed,
        [JsonLdMalformed] = AddressOutcomeKind.Failed,
        [SchemaDrift] = AddressOutcomeKind.Failed,
        [ValidationRange] = AddressOutcomeKind.Failed,
        [JsRenderRequired] = AddressOutcomeKind.Failed,
        [NotACarPage] = AddressOutcomeKind.Failed,
        [EmbeddingUnavailable] = AddressOutcomeKind.Failed,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static FrozenSet<string> All { get; } = Map.Keys.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsKnown(string code) => Map.ContainsKey(code);

    public static AddressOutcomeKind OutcomeFor(string code) =>
        Map.TryGetValue(code, out var kind)
            ? kind
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown reason code.");
}
