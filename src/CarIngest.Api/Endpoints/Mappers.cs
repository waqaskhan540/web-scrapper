using System.Globalization;
using CarIngest.Api.Contracts;
using CarIngest.Infrastructure.Storage.Entities;

namespace CarIngest.Api.Endpoints;

/// <summary>
/// Maps entities to API-owned response records.
/// </summary>
/// <remarks>
/// This is the only place entities cross into responses, which is what keeps constitution gate
/// III-1 checkable: an architecture test can assert no EF type appears in a response shape, and
/// the compiler confines the conversion to this file.
/// </remarks>
internal static class Mappers
{
    internal static RunSummary ToSummary(IngestionRun run) => new(
        run.Id,
        run.AddressSourceId,
        run.Status,
        run.TriggeredBy,
        run.RetryOfRunId,
        run.StartedAt,
        run.CompletedAt,
        run.FilesRead,
        new RunTotals(
            run.AddressesRead,
            run.StoredCount,
            run.UnchangedCount,
            run.SkippedByPolicyCount,
            run.FailedCount,
            run.RequestCount));

    internal static AddressOutcomeItem ToItem(AddressOutcome outcome, string url) => new(
        url,
        outcome.Outcome,
        outcome.ReasonCode,
        outcome.Message,
        outcome.AttemptCount,
        outcome.RecordedAt,
        outcome.CarDocumentId);

    internal static CarDocumentResponse ToResponse(CarDocument document) => new(
        document.Id,
        new Vehicle(
            document.Make,
            document.Model,
            document.Year,
            document.Trim,
            document.MileageValue is { } mileage && document.MileageUnit is { } unit
                ? new Mileage(mileage, unit)
                : null,
            document.PriceAmount is { } price && document.PriceCurrency is { } currency

                // Fixed 2dp as a string: serializing currency as a JSON number invites rounding
                // drift that only shows up in aggregate.
                ? new Price(price.ToString("F2", CultureInfo.InvariantCulture), currency)
                : null,
            document.FuelType,
            document.Transmission,
            document.BodyStyle,
            document.Colour,
            document.Vin),
        document.Availability,
        document.DetectedLanguage,
        new Provenance(
            document.SourceUrl,
            document.RetrievedAt,
            document.ExtractorVersion,
            document.DiscoveryMethod,
            document.LastVerifiedAt),
        [.. document.Embeddings
            .Select(e => new EmbeddingDescriptor(e.ModelId, e.ModelVersion, e.EmbeddedAt))
            .OrderBy(e => e.ModelId, StringComparer.Ordinal)]);
}
