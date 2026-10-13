using CarIngest.Infrastructure.Storage.Entities;
using CarIngest.Infrastructure.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CarIngest.Infrastructure.Tests.Storage;

/// <summary>
/// Provenance enforcement (FR-015, SC-004) and the run-concurrency constraint (FR-026).
/// </summary>
/// <remarks>
/// These assert at the <b>database</b> layer rather than in application validation. SC-004 calls
/// a document with incomplete provenance a defect, and an application-only check can be bypassed
/// by any future code path that writes directly. A NOT NULL column cannot.
/// </remarks>
[Collection(PostgresTestGroup.Name)]
public sealed class ProvenanceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly PostgresFixture fixture = fixture;

    public Task InitializeAsync() => this.fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(Guid SourceId, Guid TargetId)> SeedTargetAsync()
    {
        await using var context = this.fixture.CreateContext();

        var source = new AddressSource { Id = Guid.CreateVersion7(), Location = "/tmp/targets" };
        var target = new IngestionTarget
        {
            Id = Guid.CreateVersion7(),
            AddressSourceId = source.Id,
            Url = $"https://dealer.example/listing/{Guid.NewGuid():N}",
            Host = "dealer.example",
            DiscoveryMethod = DiscoveryMethod.OperatorFile,
            DiscoverySourceFile = "targets.txt",
            FirstSeenAt = DateTimeOffset.UtcNow,
        };

        context.AddressSources.Add(source);
        context.IngestionTargets.Add(target);
        await context.SaveChangesAsync(CancellationToken.None);
        return (source.Id, target.Id);
    }

    [Fact]
    public async Task DocumentWithFullProvenance_IsStored()
    {
        var (_, targetId) = await this.SeedTargetAsync();
        var now = DateTimeOffset.UtcNow;

        await using var context = this.fixture.CreateContext();
        context.CarDocuments.Add(new CarDocument
        {
            Id = Guid.CreateVersion7(),
            IngestionTargetId = targetId,
            Make = "Volkswagen",
            Model = "Golf",
            Year = 2018,
            ContentHash = [1, 2, 3],
            SourceUrl = "https://dealer.example/listing/1",
            RetrievedAt = now,
            ExtractorVersion = "jsonld-car/1.0.0",
            DiscoveryMethod = DiscoveryMethod.OperatorFile,
            LastVerifiedAt = now,
        });

        await context.SaveChangesAsync(CancellationToken.None);

        var stored = await context.CarDocuments.SingleAsync(CancellationToken.None);
        Assert.Equal("Volkswagen", stored.Make);

        // Stored and served as UTC (gate III-5).
        Assert.Equal(TimeSpan.Zero, stored.RetrievedAt.Offset);
    }

    [Theory]
    [InlineData("source_url")]
    [InlineData("extractor_version")]
    [InlineData("discovery_method")]
    public async Task DocumentWithNullTextProvenance_IsRejectedByTheDatabase(string column)
    {
        var (_, targetId) = await this.SeedTargetAsync();

        await using var context = this.fixture.CreateContext();

        // Raw SQL deliberately: this bypasses EF validation to prove the constraint lives in the
        // schema, which is what makes it unbypassable by future code.
        var sql = $"""
            INSERT INTO car_document
              (id, ingestion_target_id, make, model, year, availability, content_hash,
               source_url, retrieved_at, extractor_version, discovery_method, last_verified_at)
            VALUES
              (gen_random_uuid(), '{targetId}', 'VW', 'Golf', 2018, 'available', '\x01'::bytea,
               {(column == "source_url" ? "NULL" : "'https://dealer.example/1'")},
               now(),
               {(column == "extractor_version" ? "NULL" : "'jsonld-car/1.0.0'")},
               {(column == "discovery_method" ? "NULL" : "'operator-file'")},
               now());
            """;

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(
            () => context.Database.ExecuteSqlRawAsync(sql, CancellationToken.None));

        Assert.Equal("23502", ex.SqlState); // not_null_violation
        Assert.Contains(column, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecondActiveRunForOneSource_IsRejectedByThePartialUniqueIndex()
    {
        var (sourceId, _) = await this.SeedTargetAsync();

        await using var context = this.fixture.CreateContext();

        context.IngestionRuns.Add(new IngestionRun
        {
            Id = Guid.CreateVersion7(),
            AddressSourceId = sourceId,
            StartedAt = DateTimeOffset.UtcNow,
            Status = RunStatus.Running,
            TriggeredBy = TriggeredBy.Operator,
        });
        await context.SaveChangesAsync(CancellationToken.None);

        context.IngestionRuns.Add(new IngestionRun
        {
            Id = Guid.CreateVersion7(),
            AddressSourceId = sourceId,
            StartedAt = DateTimeOffset.UtcNow,
            Status = RunStatus.Running,
            TriggeredBy = TriggeredBy.Operator,
        });

        // FR-026 enforced by the database, so two concurrent operators cannot both win a race.
        var ex = await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync(CancellationToken.None));

        Assert.Equal("23505", ((Npgsql.PostgresException)ex.InnerException!).SqlState); // unique_violation
    }

    [Fact]
    public async Task CompletedRunsForOneSource_DoNotCollide()
    {
        var (sourceId, _) = await this.SeedTargetAsync();
        await using var context = this.fixture.CreateContext();

        for (var i = 0; i < 3; i++)
        {
            context.IngestionRuns.Add(new IngestionRun
            {
                Id = Guid.CreateVersion7(),
                AddressSourceId = sourceId,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                Status = RunStatus.Succeeded,
                TriggeredBy = TriggeredBy.Operator,
            });
        }

        // The index is filtered on status = 'running', so history is unconstrained.
        await context.SaveChangesAsync(CancellationToken.None);
        Assert.Equal(3, await context.IngestionRuns.CountAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(1899)]
    [InlineData(2200)]
    public async Task YearOutsideTheCheckConstraint_IsRejected(int year)
    {
        var (_, targetId) = await this.SeedTargetAsync();
        await using var context = this.fixture.CreateContext();

        context.CarDocuments.Add(new CarDocument
        {
            Id = Guid.CreateVersion7(),
            IngestionTargetId = targetId,
            Make = "VW",
            Model = "Golf",
            Year = year,
            ContentHash = [1],
            SourceUrl = "https://dealer.example/1",
            RetrievedAt = DateTimeOffset.UtcNow,
            ExtractorVersion = "jsonld-car/1.0.0",
            DiscoveryMethod = DiscoveryMethod.OperatorFile,
            LastVerifiedAt = DateTimeOffset.UtcNow,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync(CancellationToken.None));

        Assert.Equal("23514", ((Npgsql.PostgresException)ex.InnerException!).SqlState); // check_violation
    }

    [Fact]
    public async Task MileageWithoutUnit_IsRejected()
    {
        var (_, targetId) = await this.SeedTargetAsync();
        await using var context = this.fixture.CreateContext();

        context.CarDocuments.Add(new CarDocument
        {
            Id = Guid.CreateVersion7(),
            IngestionTargetId = targetId,
            Make = "VW",
            Model = "Golf",
            Year = 2018,
            MileageValue = 45000,
            MileageUnit = null, // the constraint under test
            ContentHash = [1],
            SourceUrl = "https://dealer.example/1",
            RetrievedAt = DateTimeOffset.UtcNow,
            ExtractorVersion = "jsonld-car/1.0.0",
            DiscoveryMethod = DiscoveryMethod.OperatorFile,
            LastVerifiedAt = DateTimeOffset.UtcNow,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync(CancellationToken.None));

        Assert.Equal("23514", ((Npgsql.PostgresException)ex.InnerException!).SqlState);
    }
}
