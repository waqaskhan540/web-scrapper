using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace CarIngest.Infrastructure.Storage;

/// <summary>
/// Design-time factory for `dotnet ef`. Reads the migrator connection string from the
/// environment and never falls back to the runtime one — migrations are applied by the elevated
/// identity, because the least-privilege runtime account cannot CREATE EXTENSION (research D14).
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CarIngestDbContext>
{
    public CarIngestDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("CarIngest__ConnectionStrings__Migrator")
            ?? "Host=localhost;Database=carringest;Username=postgres;Password=postgres";

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();

        var options = new DbContextOptionsBuilder<CarIngestDbContext>()
            .UseNpgsql(dataSource, o => o.UseVector())
            .Options;

        return new CarIngestDbContext(options);
    }
}
