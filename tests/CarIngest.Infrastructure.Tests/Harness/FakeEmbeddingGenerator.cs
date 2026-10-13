using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace CarIngest.Infrastructure.Tests.Harness;

/// <summary>
/// Deterministic embedding generator for tests.
/// </summary>
/// <remarks>
/// <para>
/// Deterministic so a test can assert that identical text yields an identical vector, and that
/// different text does not. Derived from a hash of the input, which gives stable but
/// well-separated vectors without reaching for a real model — no test may touch the network
/// (constitution gate W-2).
/// </para>
/// <para>
/// It is <b>not</b> semantically meaningful: these vectors prove plumbing, model-identity
/// isolation, and idempotence. They cannot prove retrieval quality, which needs a real model.
/// </para>
/// </remarks>
public sealed class FakeEmbeddingGenerator(int dimensions = 768, string modelId = "fake-embed")
    : IEmbeddingGenerator<string, Embedding<float>>
{
    public int CallCount { get; private set; }

    public string ModelId { get; } = modelId;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var results = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            this.CallCount++;
            results.Add(new Embedding<float>(this.Vectorize(value)));
        }

        return Task.FromResult(results);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private ReadOnlyMemory<float> Vectorize(string text)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var vector = new float[dimensions];

        for (var i = 0; i < dimensions; i++)
        {
            // Spread the 32 hash bytes across the full width deterministically.
            var b = seed[i % seed.Length];
            var mix = (byte)(b ^ (i * 31 % 251));
            vector[i] = (mix / 255f) - 0.5f;
        }

        // L2-normalize so cosine distance behaves as it would with a real model.
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < dimensions; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }
}
