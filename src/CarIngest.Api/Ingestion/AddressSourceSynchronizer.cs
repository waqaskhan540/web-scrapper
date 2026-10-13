using CarIngest.Core.Configuration;
using CarIngest.Infrastructure.Storage;
using CarIngest.Infrastructure.Storage.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarIngest.Api.Ingestion;

/// <summary>
/// Reconciles the configured <c>CarIngest:Sources</c> into <see cref="AddressSource"/> rows at
/// startup.
/// </summary>
/// <remarks>
/// <para>
/// Configuration declares which folders are ingestable; the database holds a row per source so
/// that runs, outcomes, and per-source freshness have something stable to hang off. Without this
/// reconciliation a fresh database has no sources at all, so <c>POST /v1/runs</c> has no
/// <c>sourceId</c> to accept and the service is unusable despite being correctly configured.
/// </para>
/// <para>
/// Relative locations resolve against the host's content root, not the process working
/// directory. <c>dotnet run --project src/CarIngest.Api</c> leaves the working directory at the
/// shell's location, so <c>./targets</c> would otherwise mean something different depending on
/// where the command was typed.
/// </para>
/// <para>
/// Sources are never deleted here. Removing one from configuration stops new runs against it but
/// leaves its history intact — discarding a source row would cascade away every run and outcome
/// that referenced it, destroying the audit trail as a side effect of an edit.
/// </para>
/// </remarks>
public sealed partial class AddressSourceSynchronizer(
    IServiceScopeFactory scopeFactory,
    IOptions<CarIngestOptions> options,
    IHostEnvironment environment,
    ILogger<AddressSourceSynchronizer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CarIngestDbContext>();

        foreach (var configured in options.Value.Sources)
        {
            var location = ResolveLocation(configured.Location, environment.ContentRootPath);

            var existing = await context.AddressSources
                .FirstOrDefaultAsync(s => s.Location == location, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                continue;
            }

            context.AddressSources.Add(new AddressSource
            {
                Id = Guid.CreateVersion7(),
                Location = location,
            });

            LogRegistered(logger, location);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // A configured folder that does not exist is not fatal — a run against it reports zero
        // addresses read, which is distinguishable from a failure to start. But it is almost
        // always a mistake, so say so loudly at boot rather than letting the operator discover
        // it as a mysteriously empty run.
        foreach (var source in await context.AddressSources.ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Directory.Exists(source.Location))
            {
                LogMissingFolder(logger, source.Location);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static string ResolveLocation(string configured, string contentRoot) =>
        Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(contentRoot, configured));

    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Information,
        Message = "Registered address source {Location}.")]
    private static partial void LogRegistered(ILogger logger, string location);

    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Warning,
        Message = "Address source folder {Location} does not exist; runs against it will read zero addresses.")]
    private static partial void LogMissingFolder(ILogger logger, string location);
}
