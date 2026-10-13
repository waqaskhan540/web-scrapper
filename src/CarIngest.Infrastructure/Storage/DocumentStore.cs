using CarIngest.Core.Documents;
using CarIngest.Infrastructure.Embedding;
using CarIngest.Infrastructure.Storage.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace CarIngest.Infrastructure.Storage;

/// <summary>What persisting a document did, so the caller can record the right outcome.</summary>
public enum PersistOutcome
{
    /// <summary>New document, or changed content replacing the old projection.</summary>
    Stored,

    /// <summary>Content hash matched; only LastVerifiedAt advanced.</summary>
    Unchanged,

    /// <summary>Page vanished; existing row marked unavailable rather than deleted.</summary>
    MarkedUnavailable,
}

public sealed record PersistResult(PersistOutcome Outcome, Guid DocumentId, bool NeedsEmbedding);

/// <summary>
/// Persists the screened projection and its vector.
/// </summary>
/// <remarks>
/// The projection is written <b>before</b> the embedding is generated, and the embedding is a
/// separate step. That ordering delivers FR-017 and SC-010: an embedding failure, or a later
/// extractor fix, can be retried from stored content without re-fetching the page — which
/// matters because re-fetching costs politeness budget against someone else's server.
/// </remarks>
public sealed class DocumentStore(CarIngestDbContext context)
{
    private readonly CarIngestDbContext context = context;

    /// <summary>
    /// Upserts the projection. Returns <see cref="PersistOutcome.Unchanged"/> when the canonical
    /// content hash matches, in which case no vector work is needed (FR-018, SC-008).
    /// </summary>
    public async Task<PersistResult> PersistAsync(
        IngestionTarget target,
        string cardKey,
        string? detailUrl,
        VehicleFields fields,
        string extractorVersion,
        DateTimeOffset retrievedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(cardKey);
        ArgumentNullException.ThrowIfNull(fields);

        var hash = CanonicalHash.Compute(fields);

        // Keyed by (address, card): one listing page describes several cars, so matching on the
        // address alone would collapse them onto one row.
        var existing = await this.context.CarDocuments
            .Include(d => d.Embeddings)
            .FirstOrDefaultAsync(
                d => d.IngestionTargetId == target.Id && d.CardKey == cardKey,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var created = new CarDocument
            {
                Id = Guid.CreateVersion7(),
                IngestionTargetId = target.Id,
                CardKey = cardKey,
                DetailUrl = detailUrl,
                Availability = Entities.Availability.Available,
                ContentHash = hash,
                SourceUrl = target.Url,
                RetrievedAt = retrievedAt,
                ExtractorVersion = extractorVersion,
                DiscoveryMethod = target.DiscoveryMethod,
                LastVerifiedAt = retrievedAt,
            };

            Apply(created, fields);
            this.context.CarDocuments.Add(created);
            await this.context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PersistResult(PersistOutcome.Stored, created.Id, NeedsEmbedding: true);
        }

        if (existing.ContentHash.AsSpan().SequenceEqual(hash))
        {
            // Nothing meaningful changed. Advance last-verified only — RetrievedAt still records
            // when this content was actually obtained.
            existing.LastVerifiedAt = retrievedAt;
            existing.Availability = Entities.Availability.Available;
            await this.context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // Re-embed only if a vector is somehow missing (e.g. a prior embedding failure).
            return new PersistResult(
                PersistOutcome.Unchanged,
                existing.Id,
                NeedsEmbedding: existing.Embeddings.Count == 0);
        }

        Apply(existing, fields);
        existing.DetailUrl = detailUrl;
        existing.ContentHash = hash;
        existing.RetrievedAt = retrievedAt;
        existing.LastVerifiedAt = retrievedAt;
        existing.ExtractorVersion = extractorVersion;
        existing.Availability = Entities.Availability.Available;

        // Content changed, so every stored vector describes text that no longer exists. Removing
        // them is not optional: leaving a stale vector would return the old car for a query
        // about the new one.
        this.context.CarDocumentEmbeddings.RemoveRange(existing.Embeddings);

        await this.context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new PersistResult(PersistOutcome.Stored, existing.Id, NeedsEmbedding: true);
    }

    /// <summary>
    /// Records a vanished page. The row is retained, never deleted — an operator needs to know
    /// the listing existed and when it disappeared (spec edge case).
    /// </summary>
    public async Task<int> MarkUnavailableAsync(
        Guid ingestionTargetId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        // Every card on a vanished page becomes unavailable, not just one. Rows are retained
        // either way — an operator needs to know the listings existed and when they went.
        var existing = await this.context.CarDocuments
            .Where(d => d.IngestionTargetId == ingestionTargetId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var document in existing)
        {
            document.Availability = Entities.Availability.Unavailable;
            document.LastVerifiedAt = observedAt;
        }

        if (existing.Count > 0)
        {
            await this.context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return existing.Count;
    }

    /// <summary>
    /// Attaches a vector, keyed by model identity. Idempotent on
    /// (document, model, version, source text).
    /// </summary>
    public async Task AttachEmbeddingAsync(
        Guid documentId,
        StampedEmbedding embedding,
        string sourceText,
        DateTimeOffset embeddedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embedding);

        if (embedding.Vector.Length != CarIngestDbContext.EmbeddingDimensions)
        {
            // Fail loudly rather than pad or truncate: a zero-padded vector is dimensionally
            // valid and would silently produce meaningless distances (research D7).
            throw new InvalidOperationException(
                $"Embedding model '{embedding.ModelId}' returned {embedding.Vector.Length} dimensions, " +
                $"but the column is vector({CarIngestDbContext.EmbeddingDimensions}). " +
                "Adopting a different-dimension model requires a new table via migration.");
        }

        var textHash = CanonicalHash.ComputeForText(sourceText);

        var existing = await this.context.CarDocumentEmbeddings
            .FirstOrDefaultAsync(
                e => e.CarDocumentId == documentId &&
                     e.ModelId == embedding.ModelId &&
                     e.ModelVersion == embedding.ModelVersion,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (existing.SourceTextHash.AsSpan().SequenceEqual(textHash))
            {
                return;
            }

            existing.Embedding = new Vector(embedding.Vector);
            existing.SourceTextHash = textHash;
            existing.EmbeddedAt = embeddedAt;
            await this.context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        this.context.CarDocumentEmbeddings.Add(new CarDocumentEmbedding
        {
            Id = Guid.CreateVersion7(),
            CarDocumentId = documentId,
            Embedding = new Vector(embedding.Vector),
            ModelId = embedding.ModelId,
            ModelVersion = embedding.ModelVersion,
            EmbeddedAt = embeddedAt,
            SourceTextHash = textHash,
        });

        await this.context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Apply(CarDocument document, VehicleFields fields)
    {
        document.Make = fields.Make;
        document.Model = fields.Model;
        document.Year = fields.Year;
        document.Trim = fields.Trim;
        document.MileageValue = fields.MileageValue;
        document.MileageUnit = fields.MileageUnit;
        document.PriceAmount = fields.PriceAmount;
        document.PriceCurrency = fields.PriceCurrency;
        document.FuelType = fields.FuelType;
        document.Transmission = fields.Transmission;
        document.BodyStyle = fields.BodyStyle;
        document.Colour = fields.Colour;
        document.Vin = fields.Vin;
        document.DetectedLanguage = fields.DetectedLanguage;
    }
}
