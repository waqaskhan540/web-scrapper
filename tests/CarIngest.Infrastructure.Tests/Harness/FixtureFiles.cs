using System.Reflection;

namespace CarIngest.Infrastructure.Tests.Harness;

/// <summary>
/// Locates the repository-root <c>fixtures/</c> directory. Fixtures live at the root rather
/// than beside the tests because the infrastructure and API test projects share them.
/// </summary>
public static class FixtureFiles
{
    public static string Root { get; } = FindRoot();

    public static string Read(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return File.ReadAllText(Path.Combine(Root, relativePath));
    }

    /// <summary>
    /// The literal PII strings that must never reach a stored record, a response, or a log.
    /// </summary>
    public static IReadOnlyList<string> PiiGoldenList { get; } =
        Read(Path.Combine("dealer.example", "pii-golden-list.txt"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .ToArray();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository-root 'fixtures' directory from the test output path.");
    }
}
