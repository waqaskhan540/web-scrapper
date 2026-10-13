using CarIngest.Core.Configuration;
using CarIngest.Core.Documents;
using CarIngest.Core.Policies;
using CarIngest.Infrastructure.Embedding;
using CarIngest.Infrastructure.Extraction;
using CarIngest.Infrastructure.Fetching;
using CarIngest.Infrastructure.Runs;
using CarIngest.Infrastructure.Storage.Entities;
using CarIngest.Robots;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CarIngest.Infrastructure.Tests.Harness;

/// <summary>
/// Runs the real pipeline against the real database with only the network stubbed.
/// </summary>
/// <remarks>
/// Deliberately assembles the production types rather than mocks, so EF mapping, database
/// constraints, change detection, and robots enforcement are all genuinely exercised. The
/// stubbed network is the only seam.
/// </remarks>
public sealed class PipelineHarness(PostgresFixture fixture, string folder)
{
    private const string UserAgent = "CarIngestBot/1.0 (+https://example.invalid; ops@example.invalid)";

    public Guid SourceId { get; private set; }

    public void WriteTargets(params string[] urls) =>
        File.WriteAllText(Path.Combine(folder, "targets.txt"), string.Join('\n', urls) + "\n");

    public static StubHttpHandler Handler(
        IEnumerable<string>? extraHosts = null,
        Action<StubHttpHandler>? configure = null,
        string robotsBody = "User-agent: *\nDisallow: /private/\n")
    {
        var handler = new StubHttpHandler();
        handler.Map("https://dealer.example/robots.txt", robotsBody);

        foreach (var host in extraHosts ?? [])
        {
            handler.Map($"https://{host}/robots.txt", robotsBody);
        }

        configure?.Invoke(handler);
        return handler;
    }

    public async Task<Guid> StartRunAsync(Guid? retryOf = null)
    {
        await using var context = fixture.CreateContext();

        if (this.SourceId == Guid.Empty)
        {
            var source = new AddressSource { Id = Guid.CreateVersion7(), Location = folder };
            context.AddressSources.Add(source);
            await context.SaveChangesAsync(CancellationToken.None);
            this.SourceId = source.Id;
        }

        var run = new IngestionRun
        {
            Id = Guid.CreateVersion7(),
            AddressSourceId = this.SourceId,
            StartedAt = DateTimeOffset.UtcNow,
            Status = RunStatus.Running,
            RetryOfRunId = retryOf,
            TriggeredBy = retryOf is null ? TriggeredBy.Operator : TriggeredBy.Retry,
        };

        context.IngestionRuns.Add(run);
        await context.SaveChangesAsync(CancellationToken.None);
        return run.Id;
    }

    public async Task RunAsync(Guid runId, StubHttpHandler handler, bool withEmbeddings = true)
    {
        var policies = new List<HostAccessPolicy>();

        foreach (var host in (string[])["dealer.example", "broken.example"])
        {
            policies.Add(new HostAccessPolicy
            {
                Host = host,
                RequestsPerPeriod = 10,
                ReplenishmentPeriod = TimeSpan.FromMilliseconds(1),
                MaxConcurrency = 1,
                PermittedFields = [.. VehicleFieldNames.All],
            });
        }

        var monitor = new StaticOptionsMonitor<CarIngestOptions>(new CarIngestOptions
        {
            UserAgent = UserAgent,
            Sources = [new SourceOptions { Location = folder }],
            HostPolicies = policies,
        });

        var robots = new RobotsCache(
            handler.CreateClient(UserAgent), NullLogger<RobotsCache>.Instance,
            "CarIngestBot", TimeSpan.FromHours(24));

        using var limiter = new PerHostRateLimiter(monitor);
        var fetcher = new PageFetcher(
            handler.CreateClient(UserAgent), robots, limiter, monitor, NullLogger<PageFetcher>.Instance);

        await using var context = fixture.CreateContext();
        var pipeline = new IngestionPipeline(
            context,
            fetcher,
            new ExtractorSelector(new JsonLdCarExtractor(), new MicrodataCarExtractor()),
            new EmbeddingTextBuilder(),
            withEmbeddings
                ? new ModelStampingEmbeddingGenerator(new FakeEmbeddingGenerator(), "fake-embed", "sha256:test")
                : null,
            monitor,
            NullLogger<IngestionPipeline>.Instance);

        await pipeline.ExecuteAsync(runId, CancellationToken.None);
    }

    /// <summary>
    /// Marks any active run terminal so a subsequent run may start. Mirrors what
    /// <c>RunReconciler</c> does at startup; FR-026 permits only one active run per source.
    /// </summary>
    public async Task CloseActiveRunsAsync()
    {
        await using var context = fixture.CreateContext();
        await context.IngestionRuns
            .Where(r => r.Status == RunStatus.Running)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, RunStatus.Succeeded),
                CancellationToken.None);
    }
}
