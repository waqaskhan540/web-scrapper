using CarIngest.Infrastructure.Storage;
using CarIngest.Infrastructure.Storage.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CarIngest.Api.Ingestion;

/// <summary>
/// Marks runs orphaned by a restart as interrupted.
/// </summary>
/// <remarks>
/// <para>
/// Without this, a process killed mid-run leaves a row in <c>running</c> forever — and because
/// FR-026 permits only one active run per source, that row would block every future run on that
/// source with a 409 the operator could not clear.
/// </para>
/// <para>
/// Documents already stored stay stored and attributable: the run is reclassified, never rolled
/// back, because the pages really were fetched and really were extracted.
/// </para>
/// </remarks>
public sealed partial class RunReconciler(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<RunReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CarIngestDbContext>();

        var orphaned = await context.IngestionRuns
            .Where(r => r.Status == RunStatus.Running)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.Status, RunStatus.Interrupted)
                    .SetProperty(r => r.CompletedAt, timeProvider.GetUtcNow()),
                cancellationToken)
            .ConfigureAwait(false);

        if (orphaned > 0)
        {
            LogReconciled(logger, orphaned);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Warning,
        Message = "Marked {Count} run(s) interrupted after restart; stored documents are unaffected.")]
    private static partial void LogReconciled(ILogger logger, int count);
}
