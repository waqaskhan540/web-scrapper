using CarIngest.Api.Endpoints;
using CarIngest.Api.Ingestion;
using CarIngest.Core.Abstractions;
using CarIngest.Core.Configuration;
using CarIngest.Core.Documents;
using CarIngest.Infrastructure.DependencyInjection;
using CarIngest.Infrastructure.Embedding;
using CarIngest.Infrastructure.Extraction;
using CarIngest.Infrastructure.Query;
using CarIngest.Infrastructure.Runs;
using CarIngest.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);

// Configuration is validated at startup: a host policy naming a non-allowlisted field must stop
// the process at boot rather than surface mid-run (data-model §3).
builder.Services.AddCarIngestOptions(builder.Configuration);

builder.Services.AddCarIngestStorage(builder.Configuration);
builder.Services.AddCarIngestFetching();

builder.Services.AddSingleton<IEmbeddingTextBuilder, EmbeddingTextBuilder>();
builder.Services.AddSingleton<JsonLdCarExtractor>();
builder.Services.AddSingleton<MicrodataCarExtractor>();

// Selection is by explicit host configuration, never an automatic fallback: absent JSON-LD must
// fail loudly rather than quietly switch to selector scraping (research D9).
builder.Services.AddSingleton<IExtractorSelector, ExtractorSelector>();
builder.Services.AddSingleton<RunDispatcher>();
// Ordered first: the configured sources must exist as rows before anything can reference
// them, and an orphaned 'running' row would otherwise block every future run on that source
// with a 409 nobody can clear (FR-026).
builder.Services.AddHostedService<AddressSourceSynchronizer>();
builder.Services.AddHostedService<RunReconciler>();
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddScoped<IngestionPipeline>();
builder.Services.AddScoped<SimilarityRepository>();

// Embedding provider. Coding against IEmbeddingGenerator means swapping Ollama for a hosted
// model is this registration and nothing else (research D5). The model-stamping decorator is
// the single choke point that puts FR-014's method+version on every vector.
var embeddingSection = builder.Configuration
    .GetSection($"{CarIngestOptions.SectionName}:Embedding");
var embeddingProvider = embeddingSection["Provider"] ?? "ollama";

if (string.Equals(embeddingProvider, "ollama", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton(provider =>
    {
        var options = provider.GetRequiredService<IOptions<CarIngestOptions>>().Value.Embedding;

        IEmbeddingGenerator<string, Embedding<float>> generator =
            new OllamaApiClient(options.Endpoint, options.ModelId);

        // ModelVersion should carry the resolved digest rather than the mutable tag. Until the
        // digest is read from the provider at startup, the configured id is recorded — the
        // distinction is left explicit here rather than silently assumed, because a provider
        // changing weights behind a stable tag would shift the whole corpus's vector space with
        // no error at all (research D5, R3).
        return new ModelStampingEmbeddingGenerator(generator, options.ModelId, options.ModelId);
    });
}
else
{
    // No generator configured. The pipeline stores the screened projection anyway and records
    // embedding-unavailable, so content is retained for retry without re-fetching (FR-017).
    builder.Services.AddSingleton<ModelStampingEmbeddingGenerator>(_ => null!);
}

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Every route under an explicit version segment from the first commit (gate III-2).
var v1 = app.MapGroup("/v1");
v1.MapRunEndpoints();
v1.MapDocumentEndpoints();
v1.MapQueryEndpoints();
v1.MapHealthEndpoints();

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed so WebApplicationFactory can host the app in contract tests.</summary>
public partial class Program;
