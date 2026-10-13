using CarIngest.Api.Contracts;
using CarIngest.Infrastructure.Storage;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CarIngest.Api.Endpoints;

internal static class DocumentEndpoints
{
    internal static RouteGroupBuilder MapDocumentEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/documents/{id:guid}", GetDocumentAsync).WithName("getDocument");
        return group;
    }

    private static async Task<Results<Ok<CarDocumentResponse>, NotFound<ProblemDetails>>> GetDocumentAsync(
        Guid id,
        CarIngestDbContext context,
        CancellationToken cancellationToken)
    {
        var document = await context.CarDocuments
            .AsNoTracking()
            .Include(d => d.Embeddings)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            .ConfigureAwait(false);

        // The response carries model identity for each embedding but never the vector itself:
        // serving 768 floats would commit the dimension as a public contract.
        return document is null
            ? TypedResults.NotFound(Problems.Create(
                "document-not-found", "No such document", 404, $"Document '{id}' does not exist."))
            : TypedResults.Ok(Mappers.ToResponse(document));
    }
}
