using CarIngest.Api.Contracts;
using CarIngest.Infrastructure.Storage;
using CarIngest.Infrastructure.Storage.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CarIngest.Api.Endpoints;

internal static class HealthEndpoints
{
    internal static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/health", GetHealthAsync).WithName("getHealth");
        return group;
    }

    /// <summary>
    /// Health plus per-source freshness.
    /// </summary>
    /// <remarks>
    /// <c>lastSuccessfulRunAt</c> is the point of this endpoint, not an extra: a source that has
    /// quietly stopped producing results is the failure gate II-5 exists to catch, and surfacing
    /// it here makes it detectable by monitoring without anyone reading logs.
    /// </remarks>
    private static async Task<Results<Ok<HealthResponse>, JsonHttpResult<HealthResponse>>> GetHealthAsync(
        CarIngestDbContext context,
        CancellationToken cancellationToken)
    {
        var checks = new Dictionary<string, string>(StringComparer.Ordinal);

        var databaseHealthy = await CanReachDatabaseAsync(context, cancellationToken).ConfigureAwait(false);
        checks["database"] = databaseHealthy ? "healthy" : "unhealthy";

        var sources = new List<SourceHealth>();
        if (databaseHealthy)
        {
            var rows = await context.AddressSources
                .AsNoTracking()
                .Select(s => new
                {
                    s.Id,
                    s.Location,
                    s.LastSuccessfulRunAt,
                    ActiveRunId = context.IngestionRuns
                        .Where(r => r.AddressSourceId == s.Id && r.Status == RunStatus.Running)
                        .Select(r => (Guid?)r.Id)
                        .FirstOrDefault(),
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            sources.AddRange(rows.Select(r =>
                new SourceHealth(r.Id, r.Location, r.LastSuccessfulRunAt, r.ActiveRunId)));
        }

        var status = databaseHealthy ? "healthy" : "unhealthy";
        var response = new HealthResponse(status, checks, sources);

        // Degraded still returns 200; only unhealthy is a 503, so a monitor distinguishes
        // "needs attention" from "do not route traffic here".
        return databaseHealthy
            ? TypedResults.Ok(response)
            : TypedResults.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<bool> CanReachDatabaseAsync(
        CarIngestDbContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await context.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Npgsql.NpgsqlException)
        {
            return false;
        }
    }
}
