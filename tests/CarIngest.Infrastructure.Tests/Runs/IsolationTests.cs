using CarIngest.Core.Runs;
using CarIngest.Infrastructure.Storage.Entities;
using CarIngest.Infrastructure.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CarIngest.Infrastructure.Tests.Runs;

/// <summary>
/// FR-022 / SC-009: one target's failure must not fail the others.
/// </summary>
/// <remarks>
/// Site layout changes without warning, so a broken extractor is routine rather than
/// exceptional. If a single malformed page could end a 10,000-address run, every run would be
/// hostage to the least reliable host in the list.
/// </remarks>
[Collection(PostgresTestGroup.Name)]
public sealed class IsolationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly PostgresFixture fixture = fixture;
    private string folder = string.Empty;

    public async Task InitializeAsync()
    {
        await this.fixture.ResetAsync();
        this.folder = Directory.CreateTempSubdirectory("carringest-isolation").FullName;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(this.folder))
        {
            Directory.Delete(this.folder, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task OneBrokenHost_DoesNotPreventOtherHostsFromStoring()
    {
        var harness = new PipelineHarness(this.fixture, this.folder);

        // Two hosts: one serves valid JSON-LD, one is unreachable entirely.
        harness.WriteTargets(
            "https://dealer.example/listing/golf",
            "https://broken.example/listing/1",
            "https://dealer.example/listing/corolla");

        var handler = PipelineHarness.Handler(
            extraHosts: ["broken.example"],
            configure: h =>
            {
                h.Map("https://dealer.example/listing/golf",
                    FixtureFiles.Read(Path.Combine("dealer.example", "valid-jsonld.html")));
                h.Map("https://dealer.example/listing/corolla",
                    FixtureFiles.Read(Path.Combine("dealer.example", "multiple-jsonld-blocks.html")));
                h.MapThrow("https://broken.example/listing/1", new HttpRequestException("connection refused"));
                h.MapThrow("https://broken.example/robots.txt", new HttpRequestException("connection refused"));
            });

        var runId = await harness.StartRunAsync();
        await harness.RunAsync(runId, handler);

        await using var context = this.fixture.CreateContext();
        var run = await context.IngestionRuns.SingleAsync(r => r.Id == runId, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(3, run.AddressesRead);

        // The two healthy addresses stored despite the third host being dead.
        Assert.Equal(2, await context.CarDocuments.CountAsync(CancellationToken.None));
        Assert.Equal(2, run.StoredCount);

        // The dead host is reported, not swallowed. Unreachable robots.txt means fail-safe
        // disallow, which is a policy skip rather than a fetch failure.
        var broken = await context.AddressOutcomes
            .Include(o => o.IngestionTarget)
            .SingleAsync(o => o.IngestionTarget!.Host == "broken.example", CancellationToken.None);

        Assert.Equal(ReasonCode.RobotsUnavailable, broken.ReasonCode);
    }

    [Fact]
    public async Task EveryAddressGetsExactlyOneOutcome_EvenWhenSomeFail()
    {
        var harness = new PipelineHarness(this.fixture, this.folder);
        harness.WriteTargets(
            "https://dealer.example/listing/golf",
            "https://dealer.example/listing/broken",
            "https://dealer.example/listing/absent");

        var handler = PipelineHarness.Handler(configure: h =>
        {
            h.Map("https://dealer.example/listing/golf",
                FixtureFiles.Read(Path.Combine("dealer.example", "valid-jsonld.html")));
            h.Map("https://dealer.example/listing/broken",
                FixtureFiles.Read(Path.Combine("dealer.example", "missing-required-field.html")));
            h.Map("https://dealer.example/listing/absent",
                FixtureFiles.Read(Path.Combine("dealer.example", "jsonld-absent.html")));
        });

        var runId = await harness.StartRunAsync();
        await harness.RunAsync(runId, handler);

        await using var context = this.fixture.CreateContext();

        // SC-007 depends on this exactly-once property holding regardless of outcome mix.
        var outcomes = await context.AddressOutcomes.ToListAsync(CancellationToken.None);
        Assert.Equal(3, outcomes.Count);
        Assert.Equal(3, outcomes.Select(o => o.IngestionTargetId).Distinct().Count());

        var reasons = outcomes.Select(o => o.ReasonCode).ToList();
        Assert.Contains(ReasonCode.SchemaDrift, reasons);
        Assert.Contains(ReasonCode.JsonLdAbsent, reasons);
        Assert.Contains(null, reasons); // the stored one
    }
}
