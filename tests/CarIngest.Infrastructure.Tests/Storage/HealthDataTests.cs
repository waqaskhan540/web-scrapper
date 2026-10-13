using CarIngest.Infrastructure.Storage.Entities;
using CarIngest.Infrastructure.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CarIngest.Infrastructure.Tests.Storage;

/// <summary>
/// The data behind <c>GET /v1/health</c> (FR-024, gate II-5).
/// </summary>
/// <remarks>
/// <c>LastSuccessfulRunAt</c> is the whole point. A source that has quietly stopped producing
/// results looks identical to a healthy one from any other signal — the process is up, the
/// database answers, no errors are logged. This timestamp is what makes the difference visible
/// to monitoring without anyone reading logs.
/// </remarks>
[Collection(PostgresTestGroup.Name)]
public sealed class HealthDataTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly PostgresFixture fixture = fixture;
    private string folder = string.Empty;

    public async Task InitializeAsync()
    {
        await this.fixture.ResetAsync();
        this.folder = Directory.CreateTempSubdirectory("carringest-health").FullName;
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
    public async Task NewSource_HasNoSuccessfulRunTimestamp()
    {
        await using var context = this.fixture.CreateContext();
        context.AddressSources.Add(new AddressSource
        {
            Id = Guid.CreateVersion7(),
            Location = this.folder,
        });
        await context.SaveChangesAsync(CancellationToken.None);

        var source = await context.AddressSources.SingleAsync(CancellationToken.None);

        // Null, not epoch: "never succeeded" must be distinguishable from "succeeded long ago".
        Assert.Null(source.LastSuccessfulRunAt);
    }

    [Fact]
    public async Task SuccessfulRun_AdvancesTheSourceTimestamp()
    {
        var harness = new PipelineHarness(this.fixture, this.folder);
        harness.WriteTargets("https://dealer.example/listing/golf");

        var handler = PipelineHarness.Handler(configure: h =>
            h.Map("https://dealer.example/listing/golf",
                FixtureFiles.Read(Path.Combine("dealer.example", "valid-jsonld.html"))));

        await harness.RunAsync(await harness.StartRunAsync(), handler);

        await using var context = this.fixture.CreateContext();
        var source = await context.AddressSources.SingleAsync(CancellationToken.None);

        Assert.NotNull(source.LastSuccessfulRunAt);
        Assert.NotNull(source.LastReadAt);
    }

    [Fact]
    public async Task ActiveRun_IsDiscoverableForTheHealthReport()
    {
        var harness = new PipelineHarness(this.fixture, this.folder);
        var runId = await harness.StartRunAsync();

        await using var context = this.fixture.CreateContext();
        var active = await context.IngestionRuns
            .Where(r => r.Status == RunStatus.Running)
            .Select(r => r.Id)
            .SingleAsync(CancellationToken.None);

        Assert.Equal(runId, active);
    }

    [Fact]
    public async Task ReconcilingAnOrphanedRun_UnblocksTheSource()
    {
        // Without reconciliation an orphaned 'running' row blocks every future run on that
        // source with a 409 the operator cannot clear (FR-026).
        var harness = new PipelineHarness(this.fixture, this.folder);
        await harness.StartRunAsync();

        await using (var context = this.fixture.CreateContext())
        {
            var reconciled = await context.IngestionRuns
                .Where(r => r.Status == RunStatus.Running)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(r => r.Status, RunStatus.Interrupted)
                        .SetProperty(r => r.CompletedAt, DateTimeOffset.UtcNow),
                    CancellationToken.None);

            Assert.Equal(1, reconciled);
        }

        // A new run may now start, which the partial unique index would otherwise refuse.
        var secondRunId = await harness.StartRunAsync();
        Assert.NotEqual(Guid.Empty, secondRunId);

        await using var verify = this.fixture.CreateContext();
        Assert.Equal(1, await verify.IngestionRuns
            .CountAsync(r => r.Status == RunStatus.Interrupted, CancellationToken.None));
    }
}
