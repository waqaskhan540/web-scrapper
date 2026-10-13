using CarIngest.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace CarIngest.Infrastructure.Tests.Harness;

/// <summary>
/// Starts a real Postgres with pgvector and applies the checked-in migrations.
/// </summary>
/// <remarks>
/// <para>
/// Runs the actual migrations rather than <c>EnsureCreated</c>, so these tests exercise the same
/// SQL that production deploys — including <c>CREATE EXTENSION vector</c>. A migration that
/// cannot apply fails the test run instead of surprising a deployment.
/// </para>
/// <para>
/// Works under podman as well as Docker. Podman's socket speaks the Docker API, but
/// Testcontainers looks for Docker's socket by default, so <c>DOCKER_HOST</c> must point at
/// podman's — see quickstart.md. Ryuk is left to the environment: under podman it generally
/// needs disabling via <c>TESTCONTAINERS_RYUK_DISABLED</c>, and this fixture disposes its own
/// container regardless.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Testcontainers 4.16.0 deprecated the parameterless builder: the image is now a
    // constructor argument. This corrects research R3, which had flagged the constructor-arg
    // form as probably inaccurate and treated .WithImage() as authoritative — the reverse is
    // true on the current version.
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("carringest")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private NpgsqlDataSource? dataSource;

    public string ConnectionString => this.container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await this.container.StartAsync().ConfigureAwait(false);

        // One data source for the whole fixture. Building one per context would create one
        // connection pool per context and exhaust Postgres's max_connections well before the
        // suite finished — the failure surfaces as an opaque "cannot open connection", not as
        // anything that points at pooling.
        var builder = new NpgsqlDataSourceBuilder(this.ConnectionString);
        builder.UseVector();
        this.dataSource = builder.Build();

        await using var context = this.CreateContext();
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (this.dataSource is not null)
        {
            await this.dataSource.DisposeAsync().ConfigureAwait(false);
        }

        await this.container.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A context over the container. Registers the vector type twice on purpose: once on the
    /// data source for Npgsql's type mapping and once on the EF provider — both are required
    /// (research D3).
    /// </summary>
    public CarIngestDbContext CreateContext()
    {
        var source = this.dataSource
            ?? throw new InvalidOperationException("Fixture not initialized.");

        var options = new DbContextOptionsBuilder<CarIngestDbContext>()
            .UseNpgsql(source, o => o.UseVector())
            .Options;

        return new CarIngestDbContext(options);
    }

    /// <summary>Clears all rows between tests without re-running migrations.</summary>
    public async Task ResetAsync()
    {
        await using var context = this.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE address_outcome, car_document_embedding, car_document,
                     ingestion_run, ingestion_target, address_source RESTART IDENTITY CASCADE;
            """).ConfigureAwait(false);
    }
}

/// <summary>
/// Shares one container across every storage test: starting Postgres per test class would add
/// minutes to the suite for no isolation benefit, since <see cref="PostgresFixture.ResetAsync"/>
/// truncates between tests.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
