using CarIngest.Api.Contracts;
using CarIngest.Infrastructure.Embedding;
using CarIngest.Infrastructure.Query;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace CarIngest.Api.Endpoints;

/// <summary>
/// Validation query — operator and automated-test use only.
/// </summary>
/// <remarks>
/// Explicitly not a consumer search contract. Relevance tuning, structured filters, pagination,
/// and facets are out of scope; its purpose is to make a silent failure in semantic conversion
/// impossible to mistake for success (FR-028, SC-011).
/// </remarks>
internal static class QueryEndpoints
{
    internal static RouteGroupBuilder MapQueryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/query", QueryAsync).WithName("validationQuery");
        return group;
    }

    private static async Task<Results<Ok<QueryResponse>, BadRequest<ProblemDetails>,
        UnprocessableEntity<ProblemDetails>, ProblemHttpResult>> QueryAsync(
        QueryRequest request,
        SimilarityRepository repository,
        ModelStampingEmbeddingGenerator generator,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 2000)
        {
            return TypedResults.BadRequest(Problems.Create(
                "invalid-text", "text must be between 1 and 2000 characters", 400,
                $"Received {request?.Text?.Length ?? 0} characters."));
        }

        // Required, not defaulted. pgvector would compare across models without complaint, so
        // the contract refuses to make the dangerous call the easy one (FR-029).
        if (string.IsNullOrWhiteSpace(request.ModelId) || string.IsNullOrWhiteSpace(request.ModelVersion))
        {
            return TypedResults.BadRequest(Problems.Create(
                "model-identity-required", "modelId and modelVersion are required", 400,
                "Similarity is only meaningful within one embedding model and version; " +
                "omitting either would silently mix vector spaces."));
        }

        var limit = request.Limit ?? 10;
        if (limit is < 1 or > 100)
        {
            return TypedResults.BadRequest(Problems.Create(
                "invalid-limit", "limit must be between 1 and 100", 400, $"Received limit={limit}."));
        }

        var hasEmbeddings = await repository
            .HasEmbeddingsForAsync(request.ModelId, request.ModelVersion, cancellationToken)
            .ConfigureAwait(false);

        if (!hasEmbeddings)
        {
            // 422, not an empty 200: "we hold nothing for that model" is a different answer
            // from "nothing is similar", and conflating them hides a misconfiguration.
            return TypedResults.UnprocessableEntity(Problems.Create(
                "unknown-model-identity", "No embeddings exist for that model identity", 422,
                $"No stored vectors were produced by '{request.ModelId}' version '{request.ModelVersion}'."));
        }

        if (generator is null)
        {
            return TypedResults.Problem(
                detail: "No embedding generator is configured, so the query text cannot be embedded.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Embedding generator unavailable");
        }

        StampedEmbedding embedded;
        try
        {
            embedded = await generator.GenerateAsync(request.Text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // The query text itself must be embedded before any comparison is possible, so an
            // unreachable generator is a 503 rather than an empty result.
            return TypedResults.Problem(
                detail: "The embedding generator could not be reached.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Embedding generator unavailable");
        }

        var hits = await repository
            .SearchAsync(embedded.Vector, request.ModelId, request.ModelVersion, limit, cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new QueryResponse(
            request.ModelId,
            request.ModelVersion,

            // Raw distance, ascending. Not a normalized "score" — that would imply a calibration
            // this feature has not established.
            [.. hits.Select(h => new QueryResult(h.DocumentId, h.SourceUrl, h.RetrievedAt, h.Distance))]));
    }
}
