using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace CarIngest.Api.Tests;

/// <summary>
/// CI regression guard: no PII pattern may appear in any API response shape or in the
/// repository's committed fixtures outside the one fixture built to carry it.
/// </summary>
/// <remarks>
/// This is the backstop for the four-layer gate I-4 defence. The other three layers test the
/// code that exists today; this one fails the build when someone adds a response field, a new
/// fixture, or a committed capture that reintroduces personal data.
/// </remarks>
public sealed class PiiGoldenListTests
{
    private static string RepositoryRoot { get; } = FindRoot();

    private static string FindRoot()
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

    [Fact]
    public void NoResponseContract_DeclaresAPersonalDataField()
    {
        // Reflection over the declared types, not a text scan of the source: a comment
        // explaining that no seller field exists would otherwise fail its own test.
        var forbidden = new[]
        {
            "Description", "Seller", "Telephone", "Phone",
            "Email", "StreetAddress", "PostalCode", "Vendor", "Owner",
        };

        var contractTypes = typeof(CarIngest.Api.Contracts.CarDocumentResponse).Assembly
            .GetTypes()
            .Where(t => t.Namespace == "CarIngest.Api.Contracts" && t is { IsClass: true, IsPublic: true })
            .ToArray();

        Assert.NotEmpty(contractTypes);

        var offenders = new List<string>();
        foreach (var type in contractTypes)
        {
            foreach (var property in type.GetProperties())
            {
                if (forbidden.Any(term => property.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    offenders.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void OnlyTheDedicatedPiiFixture_ContainsPiiPatterns()
    {
        // Every other committed fixture must be clean. A recorded live capture would trip this,
        // which is the point: research D15 forbids them precisely because they carry real
        // seller data into git history where deletion does not remove it.
        var fixtures = Path.Combine(RepositoryRoot, "fixtures");
        var phone = new Regex(@"\b07\d{3}\s?\d{6}\b|\+44\s?7\d{3}\s?\d{6}\b", RegexOptions.CultureInvariant);
        var email = new Regex(@"\b[\w.+-]+@[\w-]+\.[\w.-]+\b", RegexOptions.CultureInvariant);

        var allowed = new[] { "seller-pii-present.html", "pii-golden-list.txt" };
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(fixtures, "*.*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (allowed.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);

            // The bot's own contact address is required by FR-007, so it is not a finding.
            text = text.Replace("ops@example.invalid", string.Empty, StringComparison.Ordinal);

            if (phone.IsMatch(text) || email.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(RepositoryRoot, file));
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void ThePiiFixtureActuallyContainsPii_SoTheGuardIsNotVacuous()
    {
        // If the fixture were ever emptied, every containment test would pass trivially. This
        // asserts the test subject still has teeth.
        var fixture = File.ReadAllText(Path.Combine(
            RepositoryRoot, "fixtures", "dealer.example", "seller-pii-present.html"));

        var goldenList = File.ReadAllLines(Path.Combine(
                RepositoryRoot, "fixtures", "dealer.example", "pii-golden-list.txt"))
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
            .ToArray();

        Assert.NotEmpty(goldenList);
        Assert.All(goldenList, secret =>
            Assert.Contains(secret, fixture, StringComparison.OrdinalIgnoreCase));
    }
}
