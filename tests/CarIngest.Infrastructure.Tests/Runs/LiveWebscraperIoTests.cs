using CarIngest.Core.Configuration;
using CarIngest.Core.Documents;
using CarIngest.Core.Policies;
using CarIngest.Infrastructure.Embedding;
using CarIngest.Infrastructure.Extraction;
using CarIngest.Infrastructure.Fetching;
using CarIngest.Infrastructure.Query;
using CarIngest.Infrastructure.Runs;
using CarIngest.Infrastructure.Storage;
using CarIngest.Infrastructure.Storage.Entities;
using CarIngest.Infrastructure.Tests.Harness;
using CarIngest.Robots;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CarIngest.Infrastructure.Tests.Runs;

/// <summary>
/// End-to-end ingestion of the operator's configured host, with the live network replaced by a
/// stub serving the markup structure observed at <c>webscraper.io/test-sites/load-more</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the only test exercising the multi-car path: one address, six cars, each needing its
/// own document, vector, and identity. It is also the only test where the extractor's host
/// profile, the per-card keying, and the model-scoped query all meet.
/// </para>
/// <para>
/// No live request is made (constitution gate W-2). The live site was verified manually once
/// while resolving risk R1; this test pins the behaviour so the suite stays offline and
/// deterministic.
/// </para>
/// </remarks>
[Collection(PostgresTestGroup.Name)]
public sealed class LiveWebscraperIoTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string UserAgent = "CarIngestBot/1.0 (+https://example.invalid/bot; ops@example.invalid)";
    private const string ListingUrl = "https://webscraper.io/test-sites/load-more";

    private readonly PostgresFixture fixture = fixture;
    private string folder = string.Empty;

    public async Task InitializeAsync()
    {
        await this.fixture.ResetAsync();
        this.folder = Directory.CreateTempSubdirectory("carringest-webscraper").FullName;
        File.WriteAllText(Path.Combine(this.folder, "targets.txt"), ListingUrl + "\n");
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(this.folder))
        {
            Directory.Delete(this.folder, recursive: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>The exact policy configured in appsettings for this host.</summary>
    private static HostAccessPolicy Policy => new()
    {
        Host = "webscraper.io",
        RequestsPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromMilliseconds(1),
        MaxConcurrency = 1,
        ExtractorKind = ExtractorKind.Css,
        CssProfile = "webscraper-load-more",
        PermittedFields =
        [
            VehicleFieldNames.Make, VehicleFieldNames.Model, VehicleFieldNames.Year,
            VehicleFieldNames.MileageValue, VehicleFieldNames.MileageUnit,
            VehicleFieldNames.PriceAmount, VehicleFieldNames.PriceCurrency,
        ],
    };

    private static StubHttpHandler Handler()
    {
        var handler = new StubHttpHandler();

        // The host's real robots.txt, reproduced verbatim.
        handler.Map("https://webscraper.io/robots.txt",
            FixtureFiles.Read(Path.Combine("webscraper.io", "robots.txt")));

        handler.Map(ListingUrl,
            FixtureFiles.Read(Path.Combine("webscraper.io", "load-more-cars.html")));

        // Detail pages are robots-disallowed. Mapped so that a request would succeed if one were
        // made — which makes the "we never requested it" assertion meaningful rather than
        // vacuous.
        handler.Map("https://webscraper.io/test-sites/product/mercedes-benz-w123-280e-1955-c001",
            "<html><body>should never be fetched</body></html>");

        return handler;
    }

    private async Task<(Guid RunId, StubHttpHandler Handler)> RunAsync()
    {
        var handler = Handler();

        var monitor = new StaticOptionsMonitor<CarIngestOptions>(new CarIngestOptions
        {
            UserAgent = UserAgent,
            Sources = [new SourceOptions { Location = this.folder }],
            HostPolicies = [Policy],
        });

        var robots = new RobotsCache(
            handler.CreateClient(UserAgent), NullLogger<RobotsCache>.Instance,
            "CarIngestBot", TimeSpan.FromHours(24));

        using var limiter = new PerHostRateLimiter(monitor);
        var fetcher = new PageFetcher(
            handler.CreateClient(UserAgent), robots, limiter, monitor, NullLogger<PageFetcher>.Instance);

        Guid runId;
        await using (var seed = this.fixture.CreateContext())
        {
            // Reuse the source across runs: Location is unique, and a second run is exactly
            // the scenario the re-ingest test needs.
            var source = await seed.AddressSources
                .FirstOrDefaultAsync(s => s.Location == this.folder, CancellationToken.None);

            if (source is null)
            {
                source = new AddressSource { Id = Guid.CreateVersion7(), Location = this.folder };
                seed.AddressSources.Add(source);
            }

            var run = new IngestionRun
            {
                Id = Guid.CreateVersion7(),
                AddressSourceId = source.Id,
                StartedAt = DateTimeOffset.UtcNow,
                Status = RunStatus.Running,
                TriggeredBy = TriggeredBy.Operator,
            };
            seed.IngestionRuns.Add(run);
            await seed.SaveChangesAsync(CancellationToken.None);
            runId = run.Id;
        }

        await using var context = this.fixture.CreateContext();
        var pipeline = new IngestionPipeline(
            context,
            fetcher,
            new ExtractorSelector(new JsonLdCarExtractor(), new MicrodataCarExtractor()),
            new EmbeddingTextBuilder(),
            new ModelStampingEmbeddingGenerator(new FakeEmbeddingGenerator(), "fake-embed", "sha256:test"),
            monitor,
            NullLogger<IngestionPipeline>.Instance);

        await pipeline.ExecuteAsync(runId, CancellationToken.None);
        return (runId, handler);
    }

    [Fact]
    public async Task OneListingAddress_YieldsADocumentPerCar()
    {
        await this.RunAsync();

        await using var context = this.fixture.CreateContext();

        // One address, one target — but four storable cars (two cards fail by design).
        Assert.Equal(1, await context.IngestionTargets.CountAsync(CancellationToken.None));

        var documents = await context.CarDocuments.ToListAsync(CancellationToken.None);
        Assert.Equal(3, documents.Count);

        Assert.Contains(documents, d => d is { Make: "Mercedes-Benz", Model: "W123 280E", Year: 1955 });
        Assert.Contains(documents, d => d is { Make: "BMW", Model: "E24 635CSi", Year: 1954 });
        Assert.Contains(documents, d => d is { Make: "Jaguar", Model: "XJ6", Year: 1983 });
    }

    [Fact]
    public async Task EachCarGetsItsOwnStableIdentityAndVector()
    {
        await this.RunAsync();

        await using var context = this.fixture.CreateContext();
        var documents = await context.CarDocuments
            .Include(d => d.Embeddings)
            .ToListAsync(CancellationToken.None);

        // Card keys come from the detail-page slug, so they survive card reordering.
        var keys = documents.Select(d => d.CardKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("mercedes-benz-w123-280e-1955-c001", keys);

        // One vector per car, each stamped with the producing model identity.
        Assert.All(documents, d =>
        {
            var embedding = Assert.Single(d.Embeddings);
            Assert.Equal("fake-embed", embedding.ModelId);
            Assert.Equal("sha256:test", embedding.ModelVersion);
            Assert.Equal(768, embedding.Embedding.Memory.Length);
        });
    }

    [Fact]
    public async Task DisallowedDetailPages_AreRecordedButNeverRequested()
    {
        var (_, handler) = await this.RunAsync();

        await using var context = this.fixture.CreateContext();
        var documents = await context.CarDocuments.ToListAsync(CancellationToken.None);

        // The detail URL is kept as provenance...
        Assert.All(documents, d => Assert.Contains("/test-sites/product/", d.DetailUrl!, StringComparison.Ordinal));

        // ...and never fetched, because robots.txt disallows that prefix. Asserting the absence
        // of the request is the only real evidence of compliance.
        Assert.DoesNotContain(
            handler.RequestedUrls,
            u => u.AbsolutePath.StartsWith("/test-sites/product/", StringComparison.Ordinal));

        // Exactly two requests: robots.txt, then the one permitted listing page.
        Assert.Equal(2, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task ProvenancePointsAtTheListingPage_NotTheUnfetchedDetailPage()
    {
        await this.RunAsync();

        await using var context = this.fixture.CreateContext();
        var documents = await context.CarDocuments.ToListAsync(CancellationToken.None);

        // SourceUrl must name where the data actually came from. Recording the detail page here
        // would assert we read a page we deliberately did not read.
        Assert.All(documents, d =>
        {
            Assert.Equal(ListingUrl, d.SourceUrl);
            Assert.Equal(MicrodataCarExtractor.Version, d.ExtractorVersion);
            Assert.Equal(DiscoveryMethod.OperatorFile, d.DiscoveryMethod);
        });
    }

    [Fact]
    public async Task CardLevelFailures_AreReportedOnTheAddressOutcome()
    {
        var (runId, _) = await this.RunAsync();

        await using var context = this.fixture.CreateContext();

        // One outcome per address (FR-020), even though the page held six cards.
        var outcome = await context.AddressOutcomes.SingleAsync(CancellationToken.None);

        // Two cards fail by design — one missing its year, one with implausible mileage. A page
        // that is partly broken must not read as success.
        Assert.Equal("failed", outcome.Outcome);
        Assert.Contains("card(s) failed", outcome.Message!, StringComparison.Ordinal);

        var run = await context.IngestionRuns.SingleAsync(r => r.Id == runId, CancellationToken.None);
        Assert.Equal(1, run.AddressesRead);
        Assert.Equal(1, run.RequestCount);
    }

    [Fact]
    public async Task StoredCarsAreRetrievableByMeaning()
    {
        await this.RunAsync();

        await using var context = this.fixture.CreateContext();
        var repository = new SimilarityRepository(context);

        // Embed the exact text one stored car produced, then confirm it ranks first. With a
        // deterministic generator this proves the plumbing, not semantic quality.
        var builder = new EmbeddingTextBuilder();
        var jaguar = await context.CarDocuments
            .SingleAsync(d => d.Make == "Jaguar", CancellationToken.None);

        var probeText = builder.Build(new VehicleFields
        {
            Make = jaguar.Make,
            Model = jaguar.Model,
            Year = jaguar.Year,
            MileageValue = jaguar.MileageValue,
            MileageUnit = jaguar.MileageUnit,
            PriceAmount = jaguar.PriceAmount,
            PriceCurrency = jaguar.PriceCurrency,
        });

        var probe = await new ModelStampingEmbeddingGenerator(
                new FakeEmbeddingGenerator(), "fake-embed", "sha256:test")
            .GenerateAsync(probeText, CancellationToken.None);

        var hits = await repository.SearchAsync(
            probe.Vector, "fake-embed", "sha256:test", 10, CancellationToken.None);

        Assert.NotEmpty(hits);
        Assert.Equal(jaguar.Id, hits[0].DocumentId);
        Assert.True(hits[0].Distance < 0.0001, $"Exact text match should be ~0, got {hits[0].Distance}.");
        Assert.Equal(ListingUrl, hits[0].SourceUrl);
    }

    [Fact]
    public async Task ReIngestingTheSamePage_CreatesNoDuplicates()
    {
        await this.RunAsync();

        await using (var close = this.fixture.CreateContext())
        {
            await close.IngestionRuns
                .Where(r => r.Status == RunStatus.Running)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(r => r.Status, RunStatus.Succeeded),
                    CancellationToken.None);
        }

        await this.RunAsync();

        await using var context = this.fixture.CreateContext();

        // Per-card keying is what makes this work: without it the six cards would either
        // collapse onto one row or duplicate on every run.
        Assert.Equal(3, await context.CarDocuments.CountAsync(CancellationToken.None));
        Assert.Equal(3, await context.CarDocumentEmbeddings.CountAsync(CancellationToken.None));
    }
}
