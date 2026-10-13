using System.Reflection;
using System.Text.RegularExpressions;
using CarIngest.Core.Documents;
using Xunit;

namespace CarIngest.Core.Tests;

/// <summary>
/// Structural guards for constitutional invariants.
/// </summary>
/// <remarks>
/// These assert properties of the codebase itself rather than of its behaviour, because the
/// invariants they protect are the kind a well-meaning future change erodes quietly: adding a
/// convenience "force" flag, returning an entity from an endpoint to save a mapper, blocking on
/// an async call to simplify a signature. Each is individually reasonable and collectively
/// fatal to a gate.
/// </remarks>
public sealed class ArchitectureTests
{
    private static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static IEnumerable<string> SourceFiles(string project) =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains("Migrations", StringComparison.Ordinal));

    [Fact]
    public void CoreProject_TakesNoIoOrFrameworkDependencies()
    {
        // Principle II-4 requires fetch/extract/persist to be separable stages. Keeping Core
        // free of I/O packages is what makes that separation structural rather than a
        // convention someone can drift away from.
        var csproj = File.ReadAllText(
            Path.Combine(RepositoryRoot, "src", "CarIngest.Core", "CarIngest.Core.csproj"));

        foreach (var forbidden in (string[])
            ["EntityFrameworkCore", "Npgsql", "AngleSharp", "Pgvector", "AspNetCore", "Testcontainers"])
        {
            Assert.DoesNotContain(forbidden, csproj, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NoTypeAnywhereExposesARobotsBypass()
    {
        // Principle I-1 is non-overridable in production. The capability must not exist, so
        // that no configuration flag, test seam, or "just this once" parameter can enable it.
        var offenders = new List<string>();
        var pattern = new Regex(
            @"\b(bypassRobots|ignoreRobots|skipRobots|forceRobots|disableRobots|robotsOverride|allowDisallowed)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (var project in (string[])
            ["CarIngest.Core", "CarIngest.Robots", "CarIngest.Infrastructure", "CarIngest.Api"])
        {
            foreach (var file in SourceFiles(project))
            {
                if (pattern.IsMatch(File.ReadAllText(file)))
                {
                    offenders.Add(Path.GetRelativePath(RepositoryRoot, file));
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void ApiResponseContracts_ReferenceNoPersistenceType()
    {
        // Gate III-1: responses are served from API-owned types, never entities. A leaked entity
        // makes every future schema change a silent breaking API change.
        var contracts = Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot, "src", "CarIngest.Api", "Contracts"), "*.cs");

        foreach (var file in contracts)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("CarIngest.Infrastructure.Storage.Entities", text, StringComparison.Ordinal);
            Assert.DoesNotContain("DbSet", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Pgvector", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoProjectUsesEnsureCreated()
    {
        // Gate III-3: schema comes from checked-in migrations only. EnsureCreated would create
        // the schema at runtime from the model snapshot, bypassing the migration history
        // entirely — and it silently diverges from what production deploys.
        var offenders = new List<string>();

        foreach (var project in (string[])
            ["CarIngest.Core", "CarIngest.Robots", "CarIngest.Infrastructure", "CarIngest.Api"])
        {
            foreach (var file in SourceFiles(project))
            {
                if (File.ReadAllText(file).Contains("EnsureCreated", StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetRelativePath(RepositoryRoot, file));
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void ProjectionRecord_RemainsFreeOfPersonalDataFields()
    {
        // The same assertion PiiContainmentTests makes behaviourally, stated structurally so a
        // widened record fails even if nobody updates the fixture.
        var forbidden = new[]
        {
            "Description", "Seller", "Contact", "Phone", "Telephone",
            "Email", "Address", "Vendor", "Owner", "Name",
        };

        var properties = typeof(VehicleFields).GetProperties().Select(p => p.Name).ToArray();

        foreach (var term in forbidden)
        {
            Assert.DoesNotContain(properties, p => p.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void NoBlockingOnAsyncOutsideEntryPoints()
    {
        // Gate T-4. A single .Result in a fetch or extraction path can deadlock the worker under
        // load, and the symptom (a run that never finishes) points nowhere near the cause.
        // Negative lookbehind for Outcome.Result: Polly's Outcome<T> exposes a Result property
        // that has nothing to do with blocking on a Task. A guard that cries wolf gets
        // suppressed, so it has to be precise about what it forbids — and when our own code
        // tripped it with a record property named Result, the property was renamed rather than
        // the guard widened.
        var pattern = new Regex(
            @"(?<!Outcome)\.(Result\b|Wait\(\)|GetAwaiter\(\)\.GetResult\(\))",
            RegexOptions.CultureInvariant);
        var offenders = new List<string>();

        foreach (var project in (string[])
            ["CarIngest.Core", "CarIngest.Robots", "CarIngest.Infrastructure", "CarIngest.Api"])
        {
            foreach (var file in SourceFiles(project))
            {
                var text = File.ReadAllText(file);

                // The two extractors each block once on AngleSharp's async parse, behind the
                // synchronous ICarExtractor contract. Permitted because it is pure CPU work on
                // an in-memory string with no I/O to deadlock against — the content was already
                // fetched. An explicit allowlist rather than a relaxed rule, so adding a third
                // extractor forces the same deliberate decision.
                if (Path.GetFileName(file) is "JsonLdCarExtractor.cs" or "MicrodataCarExtractor.cs")
                {
                    continue;
                }

                if (pattern.IsMatch(text))
                {
                    offenders.Add(Path.GetRelativePath(RepositoryRoot, file));
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryRoute_SitsUnderAVersionSegment()
    {
        // Gate III-2: an unversioned route cannot be changed later without breaking callers.
        var program = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "CarIngest.Api", "Program.cs"));

        Assert.Contains("MapGroup(\"/v1\")", program, StringComparison.Ordinal);

        // No endpoint may be mapped directly onto the app, bypassing the version group.
        Assert.DoesNotContain("app.MapGet(", program, StringComparison.Ordinal);
        Assert.DoesNotContain("app.MapPost(", program, StringComparison.Ordinal);
    }
}
