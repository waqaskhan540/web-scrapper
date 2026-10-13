using CarIngest.Core.Documents;
using CarIngest.Infrastructure.Embedding;
using CarIngest.Infrastructure.Query;
using CarIngest.Infrastructure.Storage.Entities;
using CarIngest.Infrastructure.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Xunit;

namespace CarIngest.Infrastructure.Tests.Query;

/// <summary>
/// FR-029: similarity must never cross embedding-model identity.
/// </summary>
/// <remarks>
/// This is the most important test in the query layer, because the failure it guards against is
/// <b>silent</b>. pgvector will compute a cosine distance between a vector from model A and a
/// vector from model B and return a perfectly well-formed number. Nothing errors, nothing logs,
/// and the ranking is meaningless. Only an explicit test can catch a regression here.
/// </remarks>
[Collection(PostgresTestGroup.Name)]
public sealed class ModelIsolationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly PostgresFixture fixture = fixture;

    public Task InitializeAsync() => this.fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> SeedDocumentAsync(string slug, string make, string model, int year)
    {
        await using var context = this.fixture.CreateContext();

        var source = await context.AddressSources.FirstOrDefaultAsync(CancellationToken.None);
        if (source is null)
        {
            source = new AddressSource { Id = Guid.CreateVersion7(), Location = "/tmp/targets" };
            context.AddressSources.Add(source);
        }

        var target = new IngestionTarget
        {
            Id = Guid.CreateVersion7(),
            AddressSourceId = source.Id,
            Url = $"https://dealer.example/listing/{slug}",
            Host = "dealer.example",
            DiscoveryMethod = DiscoveryMethod.OperatorFile,
            FirstSeenAt = DateTimeOffset.UtcNow,
        };

        var document = new CarDocument
        {
            Id = Guid.CreateVersion7(),
            IngestionTargetId = target.Id,
            Make = make,
            Model = model,
            Year = year,
            ContentHash = [1],
            SourceUrl = target.Url,
            RetrievedAt = DateTimeOffset.UtcNow,
            ExtractorVersion = "jsonld-car/1.0.0",
            DiscoveryMethod = DiscoveryMethod.OperatorFile,
            LastVerifiedAt = DateTimeOffset.UtcNow,
        };

        context.IngestionTargets.Add(target);
        context.CarDocuments.Add(document);
        await context.SaveChangesAsync(CancellationToken.None);
        return document.Id;
    }

    private async Task AttachAsync(Guid documentId, string text, string modelId, string modelVersion)
    {
        var generator = new ModelStampingEmbeddingGenerator(
            new FakeEmbeddingGenerator(), modelId, modelVersion);
        var stamped = await generator.GenerateAsync(text, CancellationToken.None);

        await using var context = this.fixture.CreateContext();
        var store = new Infrastructure.Storage.DocumentStore(context);
        await store.AttachEmbeddingAsync(documentId, stamped, text, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Fact]
    public async Task QueryConfinedToOneModelIdentity_NeverReturnsAnother()
    {
        var golf = await this.SeedDocumentAsync("golf", "Volkswagen", "Golf", 2018);
        var focus = await this.SeedDocumentAsync("focus", "Ford", "Focus", 2020);

        // The same two documents embedded under two different model identities.
        await this.AttachAsync(golf, "2018 Volkswagen Golf", "model-a", "v1");
        await this.AttachAsync(focus, "2020 Ford Focus", "model-a", "v1");
        await this.AttachAsync(golf, "2018 Volkswagen Golf", "model-b", "v1");
        await this.AttachAsync(focus, "2020 Ford Focus", "model-b", "v1");

        await using var context = this.fixture.CreateContext();
        Assert.Equal(4, await context.CarDocumentEmbeddings.CountAsync(CancellationToken.None));

        var repository = new SimilarityRepository(context);
        var probe = await new ModelStampingEmbeddingGenerator(
            new FakeEmbeddingGenerator(), "model-a", "v1")
            .GenerateAsync("2018 Volkswagen Golf", CancellationToken.None);

        var hits = await repository.SearchAsync(probe.Vector, "model-a", "v1", 100, CancellationToken.None);

        // Two documents exist, each with two vectors. A query scoped to model-a must see
        // exactly two hits — not four.
        Assert.Equal(2, hits.Count);
        Assert.Equal(golf, hits[0].DocumentId);
        Assert.True(hits[0].Distance < 0.0001, $"Exact text match should be ~0 distance, got {hits[0].Distance}.");
    }

    [Fact]
    public async Task UnknownModelIdentity_ReturnsNothing_RatherThanFallingBackToAnotherModel()
    {
        var golf = await this.SeedDocumentAsync("golf", "Volkswagen", "Golf", 2018);
        await this.AttachAsync(golf, "2018 Volkswagen Golf", "model-a", "v1");

        await using var context = this.fixture.CreateContext();
        var repository = new SimilarityRepository(context);
        var probe = await new ModelStampingEmbeddingGenerator(
            new FakeEmbeddingGenerator(), "model-a", "v1")
            .GenerateAsync("anything", CancellationToken.None);

        Assert.False(await repository.HasEmbeddingsForAsync("model-a", "v2", CancellationToken.None));

        // A version bump must not silently rank against the old corpus.
        var hits = await repository.SearchAsync(probe.Vector, "model-a", "v2", 10, CancellationToken.None);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task ModelVersionIsPartOfTheIdentity_NotJustTheModelId()
    {
        var golf = await this.SeedDocumentAsync("golf", "Volkswagen", "Golf", 2018);

        await this.AttachAsync(golf, "2018 Volkswagen Golf", "nomic-embed-text", "sha256:aaa");
        await this.AttachAsync(golf, "2018 Volkswagen Golf", "nomic-embed-text", "sha256:bbb");

        await using var context = this.fixture.CreateContext();

        // Same model id, different digests: two rows, because a silent weight change behind a
        // stable tag is exactly what the digest exists to distinguish (research R3).
        Assert.Equal(2, await context.CarDocumentEmbeddings.CountAsync(CancellationToken.None));

        var repository = new SimilarityRepository(context);
        var probe = await new ModelStampingEmbeddingGenerator(
            new FakeEmbeddingGenerator(), "nomic-embed-text", "sha256:aaa")
            .GenerateAsync("2018 Volkswagen Golf", CancellationToken.None);

        var hits = await repository.SearchAsync(
            probe.Vector, "nomic-embed-text", "sha256:aaa", 10, CancellationToken.None);

        Assert.Single(hits);
    }

    [Fact]
    public async Task ReAttachingIdenticalText_IsIdempotent()
    {
        var golf = await this.SeedDocumentAsync("golf", "Volkswagen", "Golf", 2018);

        await this.AttachAsync(golf, "2018 Volkswagen Golf", "model-a", "v1");
        await this.AttachAsync(golf, "2018 Volkswagen Golf", "model-a", "v1");
        await this.AttachAsync(golf, "2018 Volkswagen Golf", "model-a", "v1");

        await using var context = this.fixture.CreateContext();

        // The unique constraint on (document, model, version) plus the source-text hash make
        // re-embedding free rather than duplicative.
        Assert.Equal(1, await context.CarDocumentEmbeddings.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WrongDimensionEmbedding_IsRejectedRatherThanPadded()
    {
        var golf = await this.SeedDocumentAsync("golf", "Volkswagen", "Golf", 2018);

        var generator = new ModelStampingEmbeddingGenerator(
            new FakeEmbeddingGenerator(dimensions: 384), "small-model", "v1");
        var stamped = await generator.GenerateAsync("2018 Volkswagen Golf", CancellationToken.None);

        await using var context = this.fixture.CreateContext();
        var store = new Infrastructure.Storage.DocumentStore(context);

        // Zero-padding a 384-dim vector into a vector(768) column would be dimensionally valid
        // and would silently corrupt every distance involving it (research D7).
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AttachEmbeddingAsync(golf, stamped, "text", DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Contains("384", ex.Message, StringComparison.Ordinal);
        Assert.Contains("migration", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangedContent_DiscardsStaleVectors()
    {
        var golf = await this.SeedDocumentAsync("golf", "Volkswagen", "Golf", 2018);
        await this.AttachAsync(golf, "2018 Volkswagen Golf, GBP 12,000", "model-a", "v1");

        await using var context = this.fixture.CreateContext();
        var target = await context.IngestionTargets.SingleAsync(CancellationToken.None);
        var store = new Infrastructure.Storage.DocumentStore(context);

        var changed = new VehicleFields
        {
            Make = "Volkswagen",
            Model = "Golf",
            Year = 2018,
            PriceAmount = 10500.00m,
            PriceCurrency = "GBP",
        };

        var result = await store.PersistAsync(
            target, "page", null, changed, "jsonld-car/1.0.0", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(Infrastructure.Storage.PersistOutcome.Stored, result.Outcome);
        Assert.True(result.NeedsEmbedding);

        // A stale vector describes text that no longer exists; leaving it would return the old
        // car for a query about the new one.
        Assert.Equal(0, await context.CarDocumentEmbeddings.CountAsync(CancellationToken.None));
    }
}
