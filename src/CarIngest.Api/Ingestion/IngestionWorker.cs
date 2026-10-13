using System.Threading.Channels;
using CarIngest.Infrastructure.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CarIngest.Api.Ingestion;

/// <summary>A request to execute one run, queued for the background worker.</summary>
public sealed record RunRequest(Guid RunId);

/// <summary>
/// Bounded queue between the API and the worker.
/// </summary>
/// <remarks>
/// Bounded, not unbounded: a run over 10,000 addresses must not be able to accumulate unbounded
/// work in memory (SC-002). The capacity is small because at most one run per source may be
/// active anyway (FR-026), so a deep queue would only hide a problem.
/// </remarks>
public sealed class RunDispatcher
{
    private readonly Channel<RunRequest> channel = Channel.CreateBounded<RunRequest>(
        new BoundedChannelOptions(capacity: 16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    public ValueTask EnqueueAsync(RunRequest request, CancellationToken cancellationToken) =>
        this.channel.Writer.WriteAsync(request, cancellationToken);

    public IAsyncEnumerable<RunRequest> ReadAllAsync(CancellationToken cancellationToken) =>
        this.channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Executes queued runs off the request path.
/// </summary>
/// <remarks>
/// The isolation FR-022 requires: a run that faults, hangs, or takes an hour cannot affect the
/// API's ability to serve requests, because it never executes on a request thread. Each run gets
/// its own DI scope so one run's DbContext cannot be corrupted by another's.
/// </remarks>
public sealed partial class IngestionWorker(
    RunDispatcher queue,
    IServiceScopeFactory scopeFactory,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogWorkerStarted(logger);

        await foreach (var request in queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<IngestionPipeline>();
                await pipeline.ExecuteAsync(request.RunId, stoppingToken).ConfigureAwait(false);
                LogRunCompleted(logger, request.RunId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown. The pipeline has already marked the run interrupted.
                break;
            }
            catch (Exception ex)
            {
                // Never let one run's failure stop the worker; the next queued run must still run.
                LogRunFaulted(logger, request.RunId, ex);
            }
        }
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Ingestion worker started.")]
    private static partial void LogWorkerStarted(ILogger logger);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information, Message = "Run {RunId} completed.")]
    private static partial void LogRunCompleted(ILogger logger, Guid runId);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Error, Message = "Run {RunId} faulted in the worker.")]
    private static partial void LogRunFaulted(ILogger logger, Guid runId, Exception exception);
}
