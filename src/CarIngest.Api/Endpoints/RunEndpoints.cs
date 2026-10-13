using CarIngest.Api.Contracts;
using CarIngest.Api.Ingestion;
using CarIngest.Infrastructure.Storage;
using CarIngest.Infrastructure.Storage.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CarIngest.Api.Endpoints;

internal static class RunEndpoints
{
    internal static RouteGroupBuilder MapRunEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/runs", StartRunAsync).WithName("startRun");
        group.MapGet("/runs/{id:guid}", GetRunAsync).WithName("getRun");
        group.MapGet("/runs/{id:guid}/outcomes", ListOutcomesAsync).WithName("listRunOutcomes");
        return group;
    }

    /// <summary>
    /// Queues a run and returns 202, not 200: a run over 10,000 addresses must not execute on a
    /// request thread (SC-002, FR-022).
    /// </summary>
    private static async Task<Results<Accepted<RunSummary>, NotFound<ProblemDetails>,
        Conflict<ProblemDetails>, UnprocessableEntity<ProblemDetails>>> StartRunAsync(
        StartRunRequest request,
        CarIngestDbContext context,
        RunDispatcher dispatcher,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var source = await context.AddressSources
            .FirstOrDefaultAsync(s => s.Id == request.SourceId, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            return TypedResults.NotFound(Problems.Create(
                "source-not-found", "No such address source", 404,
                $"Source '{request.SourceId}' is not configured."));
        }

        if (request.RetryOfRunId is { } retryOf)
        {
            var prior = await context.IngestionRuns
                .FirstOrDefaultAsync(r => r.Id == retryOf, cancellationToken)
                .ConfigureAwait(false);

            if (prior is null)
            {
                return TypedResults.NotFound(Problems.Create(
                    "run-not-found", "No such run", 404, $"Run '{retryOf}' does not exist."));
            }

            var failures = await context.AddressOutcomes
                .CountAsync(o => o.IngestionRunId == retryOf && o.Outcome == "failed", cancellationToken)
                .ConfigureAwait(false);

            if (failures == 0)
            {
                // 422 rather than queueing a no-op run: the operator asked for something that
                // cannot be done, and should hear so.
                return TypedResults.UnprocessableEntity(Problems.Create(
                    "nothing-to-retry", "That run has no failed addresses to retry", 422,
                    $"Run '{retryOf}' recorded no failures."));
            }
        }

        var run = new IngestionRun
        {
            Id = Guid.CreateVersion7(),
            AddressSourceId = source.Id,
            StartedAt = timeProvider.GetUtcNow(),
            Status = RunStatus.Running,
            RetryOfRunId = request.RetryOfRunId,
            TriggeredBy = request.RetryOfRunId is null
                ? TriggeredBy.Operator
                : TriggeredBy.Retry,
        };

        context.IngestionRuns.Add(run);

        var conflicted = false;
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The partial unique index rejected a second active run. Reporting 409 rather than
            // silently queueing means the operator learns their action did not take effect
            // (FR-026). Letting the database decide also closes the race that a prior
            // "SELECT then INSERT" check would leave open between two concurrent operators.
            conflicted = true;
        }

        if (conflicted)
        {
            context.ChangeTracker.Clear();
            return TypedResults.Conflict(Problems.Create(
                "run-already-active", "A run is already active for this source", 409,
                $"Source '{source.Id}' already has a run in progress."));
        }

        await dispatcher.EnqueueAsync(new RunRequest(run.Id), cancellationToken).ConfigureAwait(false);
        return TypedResults.Accepted($"/v1/runs/{run.Id}", Mappers.ToSummary(run));
    }

    /// <summary>23505 is unique_violation — here, the one-active-run-per-source index.</summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private static async Task<Results<Ok<RunSummary>, NotFound<ProblemDetails>>> GetRunAsync(
        Guid id,
        CarIngestDbContext context,
        CancellationToken cancellationToken)
    {
        var run = await context.IngestionRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return run is null
            ? TypedResults.NotFound(Problems.Create("run-not-found", "No such run", 404, $"Run '{id}' does not exist."))
            : TypedResults.Ok(Mappers.ToSummary(run));
    }

    /// <summary>
    /// Cursor paging, not offset: a 10,000-outcome run must be walkable without page boundaries
    /// shifting under the caller.
    /// </summary>
    private static async Task<Results<Ok<OutcomePage>, NotFound<ProblemDetails>, BadRequest<ProblemDetails>>>
        ListOutcomesAsync(
            Guid id,
            CarIngestDbContext context,
            CancellationToken cancellationToken,
            string? outcome = null,
            string? cursor = null,
            int limit = 100)
    {
        if (limit is < 1 or > 1000)
        {
            return TypedResults.BadRequest(Problems.Create(
                "invalid-limit", "limit must be between 1 and 1000", 400, $"Received limit={limit}."));
        }

        var runExists = await context.IngestionRuns
            .AnyAsync(r => r.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (!runExists)
        {
            return TypedResults.NotFound(Problems.Create(
                "run-not-found", "No such run", 404, $"Run '{id}' does not exist."));
        }

        var query = context.AddressOutcomes
            .AsNoTracking()
            .Include(o => o.IngestionTarget)
            .Where(o => o.IngestionRunId == id);

        if (!string.IsNullOrWhiteSpace(outcome))
        {
            query = query.Where(o => o.Outcome == outcome);
        }

        if (Cursor.TryDecode(cursor, out var after))
        {
            query = query.Where(o => o.Id.CompareTo(after) > 0);
        }

        // Ordering by the v7 GUID primary key gives a stable, monotonic cursor without an extra
        // sequence column, since v7 GUIDs are time-ordered.
        var rows = await query
            .OrderBy(o => o.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > limit;
        var page = hasMore ? rows.Take(limit).ToList() : rows;

        return TypedResults.Ok(new OutcomePage(
            id,
            [.. page.Select(o => Mappers.ToItem(o, o.IngestionTarget?.Url ?? string.Empty))],
            hasMore ? Cursor.Encode(page[^1].Id) : null));
    }
}
