using CarIngest.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace CarIngest.Infrastructure.Query;

public sealed record SimilarityHit(Guid DocumentId, string SourceUrl, DateTimeOffset RetrievedAt, double Distance);

/// <summary>
/// The single code path that issues a vector distance query.
/// </summary>
/// <remarks>
/// <para>
/// Sole-path-ness is the design, not an accident. FR-029 forbids comparing vectors across
/// embedding models, and that rule <b>cannot be enforced by the schema</b>: pgvector's
/// <c>&lt;=&gt;</c> operator will happily compute a distance between a vector from one model and
/// a vector from another and return a plausible-looking number with no error at all. So the
/// enforcement lives here, in one auditable place, with model identity as a
/// <b>required constructor-level parameter</b> of every query method rather than an optional
/// filter a caller might forget.
/// </para>
/// <para>
/// No ANN index exists on the embedding column: exact scan is ample at tens of thousands of
/// rows, and an HNSW index would add build cost and recall tuning for no measurable gain
/// (research D4).
/// </para>
/// </remarks>
public sealed class SimilarityRepository(CarIngestDbContext context)
{
    /// <summary>
    /// Top-K nearest by cosine distance, confined to one model identity.
    /// </summary>
    /// <param name="queryVector">
    /// Passed to EF as a <see cref="Vector"/> parameter rather than a raw array, so Npgsql
    /// parameterizes it instead of inlining a 768-element literal that would bloat the plan
    /// cache (research D3).
    /// </param>
    public async Task<IReadOnlyList<SimilarityHit>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        string modelId,
        string modelVersion,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelVersion);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);

        var parameter = new Vector(queryVector);

        // Projected to an anonymous type rather than straight into the result record: EF cannot
        // translate a custom constructor call combined with the navigation join, and fails at
        // runtime rather than at compile time.
        var rows = await context.CarDocumentEmbeddings
            .AsNoTracking()

            // The mandatory discriminator. Without it the ranking would silently mix vector
            // spaces and return numbers that look fine and mean nothing.
            .Where(e => e.ModelId == modelId && e.ModelVersion == modelVersion)
            .Select(e => new
            {
                e.CarDocumentId,
                e.CarDocument!.SourceUrl,
                e.CarDocument.RetrievedAt,

                // Projected explicitly: OrderBy alone would discard the value, and the caller
                // needs it to judge whether a match is meaningful.
                Distance = e.Embedding.CosineDistance(parameter),
            })
            .OrderBy(row => row.Distance)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(row =>
            new SimilarityHit(row.CarDocumentId, row.SourceUrl, row.RetrievedAt, row.Distance))];
    }

    /// <summary>
    /// Whether any vectors exist for a model identity. Lets the API answer "you asked for a
    /// model we have nothing for" (422) distinctly from "nothing is similar" (200, empty) —
    /// two very different things for an operator to read.
    /// </summary>
    public Task<bool> HasEmbeddingsForAsync(
        string modelId,
        string modelVersion,
        CancellationToken cancellationToken) =>
        context.CarDocumentEmbeddings
            .AsNoTracking()
            .AnyAsync(e => e.ModelId == modelId && e.ModelVersion == modelVersion, cancellationToken);
}
