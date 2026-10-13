using CarIngest.Core.Abstractions;
using CarIngest.Core.Configuration;
using CarIngest.Core.Policies;
using CarIngest.Core.Runs;
using CarIngest.Core.Sources;
using CarIngest.Infrastructure.Embedding;
using CarIngest.Infrastructure.Extraction;
using CarIngest.Infrastructure.Storage;
using CarIngest.Infrastructure.Storage.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarIngest.Infrastructure.Runs;

/// <summary>
/// Sequences fetch → extract → persist → embed for one run.
/// </summary>
/// <remarks>
/// <para>
/// Every per-address fault is caught <b>at the address boundary</b>. FR-022 requires that a
/// failing or changed target cannot fail the run for other targets, and the only way to
/// guarantee that is a catch around each address rather than around the run. The alternative —
/// trusting each extractor not to throw — fails the first time a site changes shape.
/// </para>
/// <para>
/// Exactly one <see cref="AddressOutcome"/> is written per address, which is what makes
/// SC-007's "determine the outcome of every single address" checkable rather than aspirational.
/// </para>
/// </remarks>
public sealed partial class IngestionPipeline(
    CarIngestDbContext context,
    IPageFetcher fetcher,
    IExtractorSelector extractors,
    IEmbeddingTextBuilder textBuilder,
    ModelStampingEmbeddingGenerator? embeddings,
    IOptionsMonitor<CarIngestOptions> options,
    ILogger<IngestionPipeline> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task ExecuteAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await context.IngestionRuns
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Run {runId} not found.");

        var source = await context.AddressSources
            .FirstAsync(s => s.Id == run.AddressSourceId, cancellationToken)
            .ConfigureAwait(false);

        var settings = options.CurrentValue;
        var store = new DocumentStore(context);

        try
        {
            var addresses = run.RetryOfRunId is { } retryOf
                ? await this.LoadFailedAddressesAsync(retryOf, cancellationToken).ConfigureAwait(false)
                : null;

            run.FilesRead = [.. AddressListReader.ListFiles(source.Location)];
            source.LastReadAt = this.timeProvider.GetUtcNow();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (addresses is not null)
            {
                foreach (var target in addresses)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await this.ProcessTargetAsync(run, target, settings, store, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                await foreach (var entry in AddressListReader
                    .ReadAsync(source.Location, cancellationToken)
                    .ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    run.AddressesRead++;

                    if (!entry.IsValid)
                    {
                        // A malformed line never becomes a target row; it is still counted and
                        // reported so the operator can fix the file (FR-003).
                        run.FailedCount++;
                        LogMalformedAddress(logger, entry.SourceFile, entry.LineNumber);
                        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var target = await this.UpsertTargetAsync(source.Id, entry, cancellationToken)
                        .ConfigureAwait(false);

                    await this.ProcessTargetAsync(run, target, settings, store, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            run.Status = RunStatus.Succeeded;
            run.CompletedAt = this.timeProvider.GetUtcNow();
            source.LastSuccessfulRunAt = run.CompletedAt;
        }
        catch (OperationCanceledException)
        {
            // Documents already stored stay stored and attributable.
            run.Status = RunStatus.Interrupted;
            run.CompletedAt = this.timeProvider.GetUtcNow();
            throw;
        }
        catch (Exception ex)
        {
            // A run-level failure (source unreadable, database down) is distinct from the
            // per-address failures handled below.
            run.Status = RunStatus.Failed;
            run.CompletedAt = this.timeProvider.GetUtcNow();
            LogRunFailed(logger, run.Id, ex);
        }
        finally
        {
            await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<List<IngestionTarget>> LoadFailedAddressesAsync(
        Guid priorRunId,
        CancellationToken cancellationToken)
    {
        // Only the failures. Re-fetching successes would spend politeness budget on pages we
        // already hold (FR-021).
        var targetIds = await context.AddressOutcomes
            .Where(o => o.IngestionRunId == priorRunId && o.Outcome == "failed")
            .Select(o => o.IngestionTargetId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return await context.IngestionTargets
            .Where(t => targetIds.Contains(t.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IngestionTarget> UpsertTargetAsync(
        Guid sourceId,
        AddressEntry entry,
        CancellationToken cancellationToken)
    {
        var url = entry.Url!.ToString();
        var existing = await context.IngestionTargets
            .FirstOrDefaultAsync(t => t.Url == url, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        var created = new IngestionTarget
        {
            Id = Guid.CreateVersion7(),
            AddressSourceId = sourceId,
            Url = url,
            Host = UrlNormalizer.HostKey(entry.Url!),
            DiscoveryMethod = Storage.Entities.DiscoveryMethod.OperatorFile,
            DiscoverySourceFile = entry.SourceFile,
            FirstSeenAt = this.timeProvider.GetUtcNow(),
        };

        context.IngestionTargets.Add(created);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    private async Task ProcessTargetAsync(
        IngestionRun run,
        IngestionTarget target,
        CarIngestOptions settings,
        DocumentStore store,
        CancellationToken cancellationToken)
    {
        if (run.RetryOfRunId is not null)
        {
            run.AddressesRead++;
        }

        try
        {
            // FR-008: a host with no recorded policy is not fetched at all.
            if (!settings.PolicyByHost.TryGetValue(target.Host, out var policy))
            {
                await this.RecordAsync(run, target, AddressOutcomeKind.SkippedByPolicy,
                    ReasonCode.NoHostPolicy,
                    $"No host access policy is configured for '{target.Host}'.",
                    null, cancellationToken).ConfigureAwait(false);
                return;
            }

            target.LastAttemptedAt = this.timeProvider.GetUtcNow();
            run.RequestCount++;

            var fetched = await fetcher.FetchAsync(
                new FetchRequest
                {
                    Url = new Uri(target.Url),
                    Policy = policy,
                    KnownETag = target.KnownETag,
                    KnownLastModified = target.KnownLastModified,
                },
                cancellationToken).ConfigureAwait(false);

            if (!fetched.Success)
            {
                if (fetched.ReasonCode == ReasonCode.PageGone)
                {
                    // Every card on the vanished page is marked unavailable; none is deleted.
                    await store.MarkUnavailableAsync(target.Id, this.timeProvider.GetUtcNow(), cancellationToken)
                        .ConfigureAwait(false);
                }

                await this.RecordAsync(run, target,
                    ReasonCode.OutcomeFor(fetched.ReasonCode!), fetched.ReasonCode,
                    fetched.Message, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (fetched.NotModified)
            {
                await this.RecordAsync(run, target, AddressOutcomeKind.Unchanged, null, null, null, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var extractor = extractors.Resolve(policy);
            var cards = extractor.Extract(fetched.Content!, new Uri(target.Url), policy);

            target.KnownETag = fetched.ETag;
            target.KnownLastModified = fetched.LastModified;

            if (cards.Count == 0 || cards.All(c => !c.Extraction.Success))
            {
                // Nothing on the page extracted. Report the first card's reason, which for a
                // single-card page is simply its reason.
                var firstFailure = cards.Count > 0
                    ? cards[0].Extraction
                    : ExtractionResult.Fail(ReasonCode.NotACarPage, "The page yielded no car cards.");

                await this.RecordAsync(run, target,
                    ReasonCode.OutcomeFor(firstFailure.ReasonCode!), firstFailure.ReasonCode,
                    firstFailure.Message, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            var retrievedAt = this.timeProvider.GetUtcNow();
            var storedAny = false;
            var changedAny = false;
            var failedCards = 0;
            Guid? firstDocumentId = null;
            string? cardFailureReason = null;
            string? cardFailureMessage = null;

            // A listing page can describe several cars. Each is persisted independently so one
            // malformed card cannot cost the others — the same isolation FR-022 requires
            // between addresses, applied within a page.
            foreach (var card in cards)
            {
                if (!card.Extraction.Success)
                {
                    failedCards++;
                    cardFailureReason ??= card.Extraction.ReasonCode;
                    cardFailureMessage ??= card.Extraction.Message;
                    continue;
                }

                var persisted = await store.PersistAsync(
                    target, card.CardKey, card.DetailUrl, card.Extraction.Fields!,
                    extractors.VersionFor(policy), retrievedAt, cancellationToken)
                    .ConfigureAwait(false);

                firstDocumentId ??= persisted.DocumentId;
                storedAny = true;
                changedAny |= persisted.Outcome == PersistOutcome.Stored;

                if (!persisted.NeedsEmbedding)
                {
                    continue;
                }

                var text = textBuilder.Build(card.Extraction.Fields!);

                if (embeddings is null)
                {
                    cardFailureReason ??= ReasonCode.EmbeddingUnavailable;
                    cardFailureMessage ??= "No embedding generator is configured; stored content is retained for retry.";
                    failedCards++;
                    continue;
                }

                try
                {
                    var stamped = await embeddings.GenerateAsync(text, cancellationToken).ConfigureAwait(false);
                    await store.AttachEmbeddingAsync(
                        persisted.DocumentId, stamped, text, this.timeProvider.GetUtcNow(), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
                {
                    cardFailureReason ??= ReasonCode.EmbeddingUnavailable;
                    cardFailureMessage ??= "Embedding generation failed; stored content is retained for retry.";
                    failedCards++;
                    LogEmbeddingFailed(logger, target.Host, ex);
                }
            }

            // One outcome per address, as FR-020 requires, summarising the page. A page that
            // stored something but also had card-level failures is reported as failed, because
            // "partly broken" must not read as success to an operator scanning outcomes.
            if (failedCards > 0)
            {
                await this.RecordAsync(run, target, AddressOutcomeKind.Failed,
                    cardFailureReason, cardFailureMessage is null
                        ? null
                        : $"{failedCards} of {cards.Count} card(s) failed. {cardFailureMessage}",
                    firstDocumentId, cancellationToken).ConfigureAwait(false);
                return;
            }

            var kind = storedAny && !changedAny
                ? AddressOutcomeKind.Unchanged
                : AddressOutcomeKind.Stored;

            await this.RecordAsync(run, target, kind, null, null, firstDocumentId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The address boundary. One target's unexpected fault cannot end the run (FR-022).
            await this.RecordAsync(run, target, AddressOutcomeKind.Failed,
                ReasonCode.FetchHttpError,
                "Unexpected failure while processing this address.",
                null, CancellationToken.None).ConfigureAwait(false);
            LogAddressFaulted(logger, target.Host, ex);
        }
    }

    private async Task RecordAsync(
        IngestionRun run,
        IngestionTarget target,
        AddressOutcomeKind kind,
        string? reasonCode,
        string? message,
        Guid? documentId,
        CancellationToken cancellationToken)
    {
        var outcome = kind switch
        {
            AddressOutcomeKind.Stored => "stored",
            AddressOutcomeKind.Unchanged => "unchanged",
            AddressOutcomeKind.SkippedByPolicy => "skipped-by-policy",
            _ => "failed",
        };

        switch (kind)
        {
            case AddressOutcomeKind.Stored: run.StoredCount++; break;
            case AddressOutcomeKind.Unchanged: run.UnchangedCount++; break;
            case AddressOutcomeKind.SkippedByPolicy: run.SkippedByPolicyCount++; break;
            default: run.FailedCount++; break;
        }

        var existing = await context.AddressOutcomes
            .FirstOrDefaultAsync(
                o => o.IngestionRunId == run.Id && o.IngestionTargetId == target.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.Outcome = outcome;
            existing.ReasonCode = reasonCode;
            existing.Message = Truncate(message);
            existing.AttemptCount++;
            existing.RecordedAt = this.timeProvider.GetUtcNow();
            existing.CarDocumentId = documentId;
        }
        else
        {
            context.AddressOutcomes.Add(new AddressOutcome
            {
                Id = Guid.CreateVersion7(),
                IngestionRunId = run.Id,
                IngestionTargetId = target.Id,
                Outcome = outcome,
                ReasonCode = reasonCode,
                Message = Truncate(message),
                AttemptCount = 1,
                RecordedAt = this.timeProvider.GetUtcNow(),
                CarDocumentId = documentId,
            });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bounds the diagnostic message. Messages are authored to carry field names and status
    /// only, and this is the backstop that keeps an unexpectedly long one out of the database.
    /// </summary>
    private static string? Truncate(string? message) =>
        message is { Length: > 480 } ? message[..480] + "…" : message;

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning,
        Message = "Rejected malformed address at {SourceFile}:{LineNumber}.")]
    private static partial void LogMalformedAddress(ILogger logger, string sourceFile, int lineNumber);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Error,
        Message = "Run {RunId} failed at the run level.")]
    private static partial void LogRunFailed(ILogger logger, Guid runId, Exception exception);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning,
        Message = "Embedding generation failed for a document on {Host}; content retained for retry.")]
    private static partial void LogEmbeddingFailed(ILogger logger, string host, Exception exception);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Error,
        Message = "Address on {Host} faulted unexpectedly; isolated to this address.")]
    private static partial void LogAddressFaulted(ILogger logger, string host, Exception exception);
}
