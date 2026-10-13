using CarIngest.Core.Abstractions;
using CarIngest.Core.Configuration;
using CarIngest.Core.Documents;
using CarIngest.Core.Policies;
using CarIngest.Core.Runs;
using CarIngest.Infrastructure.Embedding;
using CarIngest.Infrastructure.Extraction;
using CarIngest.Infrastructure.Fetching;
using CarIngest.Infrastructure.Runs;
using CarIngest.Infrastructure.Storage.Entities;
using CarIngest.Infrastructure.Tests.Harness;
using CarIngest.Robots;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CarIngest.Infrastructure.Tests.Runs;

/// <summary>
/// End-to-end US1: a folder of addresses becomes attributable car documents in Postgres.
/// </summary>
/// <remarks>
/// Runs the real pipeline, the real extractor, and the real store against a real database. Only
/// the network is stubbed, so the parts that could plausibly differ in production — EF mapping,
/// constraints, change detection — are genuinely exercised.
/// </remarks>
[Collection(PostgresTestGroup.Name)]
public sealed class IngestionPipelineTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string UserAgent = "CarIngestBot/1.0 (+https://example.invalid; ops@example.invalid)";
    private readonly PostgresFixture fixture = fixture;
    private string folder = string.Empty;

    public async Task InitializeAsync()
    {
        await this.fixture.ResetAsync();
        this.folder = Directory.CreateTempSubdirectory("carringest-pipeline").FullName;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(this.folder))
        {
            Directory.Delete(this.folder, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static HostAccessPolicy Policy => new()
    {
        Host = "dealer.example",
        RequestsPerPeriod = 10,
        ReplenishmentPeriod = TimeSpan.FromMilliseconds(1),
        MaxConcurrency = 1,
        PermittedFields = [.. VehicleFieldNames.All],
    };

    private async Task<Guid> SeedSourceAsync()
    {
        await using var context = this.fixture.CreateContext();
        var source = new AddressSource { Id = Guid.CreateVersion7(), Location = this.folder };
        context.AddressSources.Add(source);
        await context.SaveChangesAsync(CancellationToken.None);
        return source.Id;
    }

    private async Task<Guid> StartRunAsync(Guid sourceId, Guid? retryOf = null)
    {
        await using var context = this.fixture.CreateContext();
        var run = new IngestionRun
        {
            Id = Guid.CreateVersion7(),
            AddressSourceId = sourceId,
            StartedAt = DateTimeOffset.UtcNow,
            Status = RunStatus.Running,
            RetryOfRunId = retryOf,
            TriggeredBy = retryOf is null ? TriggeredBy.Operator : TriggeredBy.Retry,
        };
        context.IngestionRuns.Add(run);
        await context.SaveChangesAsync(CancellationToken.None);
        return run.Id;
    }

    private async Task RunPipelineAsync(
        Guid runId,
        StubHttpHandler handler,
        bool withEmbeddings = true)
    {
        var settings = new CarIngestOptions
        {
            UserAgent = UserAgent,
            Sources = [new SourceOptions { Location = this.folder }],
            HostPolicies = [Policy],
        };

        var monitor = new StaticOptionsMonitor<CarIngestOptions>(settings);
        var robots = new RobotsCache(
            handler.CreateClient(UserAgent), NullLogger<RobotsCache>.Instance,
            "CarIngestBot", TimeSpan.FromHours(24));

        using var limiter = new PerHostRateLimiter(monitor);
        var fetcher = new PageFetcher(
            handler.CreateClient(UserAgent), robots, limiter, monitor, NullLogger<PageFetcher>.Instance);

        await using var context = this.fixture.CreateContext();
        var pipeline = new IngestionPipeline(
            context,
            fetcher,
            new ExtractorSelector(new JsonLdCarExtractor(), new MicrodataCarExtractor()),
            new EmbeddingTextBuilder(),
            withEmbeddings
                ? new ModelStampingEmbeddingGenerator(
                    new FakeEmbeddingGenerator(), "fake-embed", "sha256:test")
                : null,
            monitor,
            NullLogger<IngestionPipeline>.Instance);

        await pipeline.ExecuteAsync(runId, CancellationToken.None);
    }

    private void WriteTargets(params string[] urls) =>
        File.WriteAllText(Path.Combine(this.folder, "targets.txt"), string.Join('\n', urls) + "\n");

    private static StubHttpHandler Handler(string robotsBody = "User-agent: *\nDisallow: /private/\n")
    {
        var handler = new StubHttpHandler();
        handler.Map("https://dealer.example/robots.txt", robotsBody);
        return handler;
    }

    [Fact]
    public async Task ValidAddresses_BecomeAttributableDocuments()
    {
        var sourceId = await this.SeedSourceAsync();
        this.WriteTargets(
            "https://dealer.example/listing/golf",
            "https://dealer.example/listing/corolla");

        var handler = Handler()
            .Map("https://dealer.example/listing/golf",
                FixtureFiles.Read(Path.Combine("dealer.example", "valid-jsonld.html")))
            .Map("https://dealer.example/listing/corolla",
                FixtureFiles.Read(Path.Combine("dealer.example", "multiple-jsonld-blocks.html")));

        var runId = await this.StartRunAsync(sourceId);
        await this.RunPipelineAsync(runId, handler);

        await using var context = this.fixture.CreateContext();
        var run = await context.IngestionRuns.SingleAsync(r => r.Id == runId, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(2, run.AddressesRead);
        Assert.Equal(["targets.txt"], run.FilesRead);

        var documents = await context.CarDocuments.ToListAsync(CancellationToken.None);
        Assert.Equal(2, documents.Count);

        // SC-004: incomplete provenance is a defect, so assert every component is populated.
        Assert.All(documents, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.SourceUrl));
            Assert.NotEqual(default, d.RetrievedAt);
            Assert.Equal(JsonLdCarExtractor.Version, d.ExtractorVersion);
            Assert.Equal(DiscoveryMethod.OperatorFile, d.DiscoveryMethod);
            Assert.NotEqual(default, d.LastVerifiedAt);
            Assert.NotEmpty(d.ContentHash);
        });

        Assert.Contains(documents, d => d is { Make: "Volkswagen", Model: "Golf", Year: 2018 });
        Assert.Contains(documents, d => d is { Make: "Toyota", Model: "Corolla", Year: 2021 });

        // One outcome per address makes SC-007 checkable.
        var outcomes = await context.AddressOutcomes.ToListAsync(CancellationToken.None);
        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o =>
        {
            Assert.Equal("stored", o.Outcome);
            Assert.Null(o.ReasonCode);
        });
        Assert.Equal(2, run.StoredCount);

        // Every vector records the model identity that produced it (FR-014).
        var embeddings = await context.CarDocumentEmbeddings.ToListAsync(CancellationToken.None);
        Assert.Equal(2, embeddings.Count);
        Assert.All(embeddings, e =>
        {
            Assert.Equal("fake-embed", e.ModelId);
            Assert.Equal("sha256:test", e.ModelVersion);
            Assert.Equal(768, e.Embedding.Memory.Length);
            Assert.NotEmpty(e.SourceTextHash);
        });
    }

    [Fact]
    public async Task WithoutAnEmbeddingGenerator_ProjectionIsStillStored_AndRetryable()
    {
        // FR-017 / SC-010: the screened projection is persisted before embedding, so an
        // embedding outage does not cost a re-fetch when it clears.
        var sourceId = await this.SeedSourceAsync();
        this.WriteTargets("https://dealer.example/listing/golf");

        var handler = Handler().Map("https://dealer.example/listing/golf",
            FixtureFiles.Read(Path.Combine("dealer.example", "valid-jsonld.html")));

        await this.RunPipelineAsync(await this.StartRunAsync(sourceId), handler, withEmbeddings: false);

        await using var context = this.fixture.CreateContext();

        // The document exists with full provenance...
        var document = await context.CarDocuments.SingleAsync(CancellationToken.None);
        Assert.Equal("Volkswagen", document.Make);
        Assert.NotEmpty(document.ContentHash);

        // ...but no vector, and the address is reported as retryable rather than silently fine.
        Assert.Empty(await context.CarDocumentEmbeddings.ToListAsync(CancellationToken.None));
        var outcome = await context.AddressOutcomes.SingleAsync(CancellationToken.None);
        Assert.Equal("failed", outcome.Outcome);
        Assert.Equal(ReasonCode.EmbeddingUnavailable, outcome.ReasonCode);
    }

    [Fact]
    public async Task RunTotals_AccountForEveryAddress()
    {
        var sourceId = await this.SeedSourceAsync();
        this.WriteTargets(
            "https://dealer.example/listing/golf",      // stored
            "https://dealer.example/private/listing/1", // robots-disallowed
            "https://dealer.example/listing/broken",    // schema-drift
            "https://other.example/listing/1",          // no-host-policy
            "not-a-url");                               // malformed

        var handler = Handler()
            .Map("https://dealer.example/listing/golf",
                FixtureFiles.Read(Path.Combine("dealer.example", "valid-jsonld.html")))
            .Map("https://dealer.example/listing/broken",
                FixtureFiles.Read(Path.Combine("dealer.example", "missing-required-field.html")));

        var runId = await this.StartRunAsync(sourceId);
        await this.RunPipelineAsync(runId, handler);

        await using var context = this.fixture.CreateContext();
        var run = await context.IngestionRuns.SingleAsync(r => r.Id == runId, CancellationToken.None);

        Assert.Equal(5, run.AddressesRead);

        // The invariant the contract promises: every address is accounted for exactly once.
        Assert.Equal(
            run.AddressesRead,
            run.StoredCount + run.UnchangedCount + run.SkippedByPolicyCount + run.FailedCount);

        var byReason = await context.AddressOutcomes
            .ToDictionaryAsync(o => o.ReasonCode ?? "none", o => o.Outcome, CancellationToken.None);

        Assert.Equal("skipped-by-policy", byReason[ReasonCode.RobotsDisallowed]);
        Assert.Equal("skipped-by-policy", byReason[ReasonCode.NoHostPolicy]);
        Assert.Equal("failed", byReason[ReasonCode.SchemaDrift]);

        // A failing address did not stop the others (FR-022).
        Assert.Single(await context.CarDocuments.ToListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SchemaDriftMessage_NamesTheField_AndCarriesNoPageContent()
    {
        var sourceId = await this.SeedSourceAsync();
        this.WriteTargets("https://dealer.example/listing/broken");

        var handler = Handler().Map("https://dealer.example/listing/broken",
            FixtureFiles.Read(Path.Combine("dealer.example", "missing-required-field.html")));

        await this.RunPipelineAsync(await this.StartRunAsync(sourceId), handler);

        await using var context = this.fixture.CreateContext();
        var outcome = await context.AddressOutcomes.SingleAsync(CancellationToken.None);

        Assert.Equal(ReasonCode.SchemaDrift, outcome.ReasonCode);
        Assert.Contains("Year", outcome.Message!, StringComparison.Ordinal);

        // Principle I-4 covers stored diagnostics too: no markup may reach the database.
        Assert.DoesNotContain('<', outcome.Message!);
    }

    [Fact]
    public async Task ReIngestingUnchangedContent_CreatesNoDuplicate()
    {
        var sourceId = await this.SeedSourceAsync();
        this.WriteTargets("https://dealer.example/listing/golf");
        var html = FixtureFiles.Read(Path.Combine("dealer.example", "valid-jsonld.html"));

        await this.RunPipelineAsync(
            await this.StartRunAsync(sourceId),
            Handler().Map("https://dealer.example/listing/golf", html));

        DateTimeOffset firstRetrievedAt;
        await using (var context = this.fixture.CreateContext())
        {
            var document = await context.CarDocuments.SingleAsync(CancellationToken.None);
            firstRetrievedAt = document.RetrievedAt;

            // The first run left the run in a terminal state, so a second may start.
            await context.IngestionRuns
                .Where(r => r.Status == RunStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Succeeded), CancellationToken.None);
        }

        await this.RunPipelineAsync(
            await this.StartRunAsync(sourceId),
            Handler().Map("https://dealer.example/listing/golf", html));

        await using var verify = this.fixture.CreateContext();

        // SC-008: zero duplicates.
        var documents = await verify.CarDocuments.ToListAsync(CancellationToken.None);
        Assert.Single(documents);

        // RetrievedAt records when this content was obtained; only LastVerifiedAt advances.
        Assert.Equal(firstRetrievedAt, documents[0].RetrievedAt);
        Assert.True(documents[0].LastVerifiedAt >= firstRetrievedAt);
    }

    [Fact]
    public async Task ChangedContent_ReplacesTheProjection()
    {
        var sourceId = await this.SeedSourceAsync();
        this.WriteTargets("https://dealer.example/listing/golf");
        var html = FixtureFiles.Read(Path.Combine("dealer.example", "valid-jsonld.html"));

        await this.RunPipelineAsync(
            await this.StartRunAsync(sourceId),
            Handler().Map("https://dealer.example/listing/golf", html));

        await using (var context = this.fixture.CreateContext())
        {
            await context.IngestionRuns
                .Where(r => r.Status == RunStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Succeeded), CancellationToken.None);
        }

        // Price drop — a meaningful change, so the stored projection must be replaced.
        var repriced = html.Replace("\"12000.00\"", "\"10500.00\"", StringComparison.Ordinal);

        await this.RunPipelineAsync(
            await this.StartRunAsync(sourceId),
            Handler().Map("https://dealer.example/listing/golf", repriced));

        await using var verify = this.fixture.CreateContext();
        var document = await verify.CarDocuments.SingleAsync(CancellationToken.None);

        Assert.Equal(10500.00m, document.PriceAmount);
    }

    [Fact]
    public async Task EmptySource_IsASuccessfulRunReportingZero()
    {
        var sourceId = await this.SeedSourceAsync();

        await this.RunPipelineAsync(await this.StartRunAsync(sourceId), Handler());

        await using var context = this.fixture.CreateContext();
        var run = await context.IngestionRuns.SingleAsync(CancellationToken.None);

        // Distinguishable from a run that failed to start.
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(0, run.AddressesRead);
        Assert.NotNull(run.CompletedAt);
    }

    [Fact]
    public async Task VanishedPage_MarksDocumentUnavailable_WithoutDeletingIt()
    {
        var sourceId = await this.SeedSourceAsync();
        this.WriteTargets("https://dealer.example/listing/golf");
        var html = FixtureFiles.Read(Path.Combine("dealer.example", "valid-jsonld.html"));

        await this.RunPipelineAsync(
            await this.StartRunAsync(sourceId),
            Handler().Map("https://dealer.example/listing/golf", html));

        await using (var context = this.fixture.CreateContext())
        {
            await context.IngestionRuns
                .Where(r => r.Status == RunStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Succeeded), CancellationToken.None);
        }

        var gone = Handler();
        gone.MapStatus("https://dealer.example/listing/golf", System.Net.HttpStatusCode.NotFound);
        await this.RunPipelineAsync(await this.StartRunAsync(sourceId), gone);

        await using var verify = this.fixture.CreateContext();
        var document = await verify.CarDocuments.SingleAsync(CancellationToken.None);

        // Retained, not deleted: the operator needs to know it existed and when it vanished.
        Assert.Equal(Availability.Unavailable, document.Availability);
    }

    [Fact]
    public async Task RetryRun_ProcessesOnlyThePriorFailures()
    {
        var sourceId = await this.SeedSourceAsync();
        this.WriteTargets(
            "https://dealer.example/listing/golf",
            "https://dealer.example/listing/broken");

        var html = FixtureFiles.Read(Path.Combine("dealer.example", "valid-jsonld.html"));
        var firstRunId = await this.StartRunAsync(sourceId);

        await this.RunPipelineAsync(firstRunId, Handler()
            .Map("https://dealer.example/listing/golf", html)
            .Map("https://dealer.example/listing/broken",
                FixtureFiles.Read(Path.Combine("dealer.example", "missing-required-field.html"))));

        await using (var context = this.fixture.CreateContext())
        {
            await context.IngestionRuns
                .Where(r => r.Status == RunStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Succeeded), CancellationToken.None);
        }

        // The broken listing now returns valid data.
        var retryHandler = Handler()
            .Map("https://dealer.example/listing/broken", html)
            .Map("https://dealer.example/listing/golf", html);

        var retryRunId = await this.StartRunAsync(sourceId, retryOf: firstRunId);
        await this.RunPipelineAsync(retryRunId, retryHandler);

        await using var verify = this.fixture.CreateContext();
        var retryRun = await verify.IngestionRuns.SingleAsync(r => r.Id == retryRunId, CancellationToken.None);

        // Only the one prior failure, not both addresses (FR-021).
        Assert.Equal(1, retryRun.AddressesRead);
        Assert.Equal(TriggeredBy.Retry, retryRun.TriggeredBy);

        // The already-stored document was not re-fetched.
        Assert.False(retryHandler.WasRequested("https://dealer.example/listing/golf"));
    }
}
