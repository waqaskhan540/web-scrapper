using Microsoft.Extensions.AI;

namespace CarIngest.Infrastructure.Embedding;

/// <summary>A vector together with the model identity that produced it.</summary>
public sealed record StampedEmbedding(ReadOnlyMemory<float> Vector, string ModelId, string ModelVersion);

/// <summary>
/// Produces embeddings and stamps the model identity onto every result.
/// </summary>
/// <remarks>
/// <para>
/// A single choke point for FR-014, deliberately. Stamping at call sites would mean every future
/// call site has to remember, and the one that forgets produces a vector with no recorded
/// provenance — which cannot be detected later, because a dimensionally valid vector passes
/// through pgvector's distance operators without complaint.
/// </para>
/// <para>
/// <see cref="ModelVersion"/> carries the resolved digest rather than the mutable tag. A
/// provider that silently changes weights behind a stable name would otherwise shift the entire
/// corpus's vector space with no error at all (research D5, R3).
/// </para>
/// </remarks>
public sealed class ModelStampingEmbeddingGenerator
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> inner;
    private readonly string modelId;
    private readonly string modelVersion;

    public ModelStampingEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> inner,
        string modelId,
        string modelVersion)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelVersion);

        this.inner = inner;
        this.modelId = modelId;
        this.modelVersion = modelVersion;
    }

    public string ModelId => this.modelId;

    public string ModelVersion => this.modelVersion;

    public async Task<StampedEmbedding> GenerateAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var embedding = await this.inner
            .GenerateAsync([text], options: null, cancellationToken)
            .ConfigureAwait(false);

        var first = embedding.FirstOrDefault()
            ?? throw new InvalidOperationException("Embedding generator returned no result.");

        return new StampedEmbedding(first.Vector, this.modelId, this.modelVersion);
    }

    public async Task<IReadOnlyList<StampedEmbedding>> GenerateAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        if (texts.Count == 0)
        {
            return [];
        }

        var generated = await this.inner
            .GenerateAsync(texts, options: null, cancellationToken)
            .ConfigureAwait(false);

        return [.. generated.Select(e => new StampedEmbedding(e.Vector, this.modelId, this.modelVersion))];
    }
}
