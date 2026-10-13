using CarIngest.Core.Runs;

namespace CarIngest.Core.Sources;

/// <summary>One line read from an address-list file: either a usable address or a rejection.</summary>
public sealed record AddressEntry
{
    public required string SourceFile { get; init; }

    public required int LineNumber { get; init; }

    public required string RawValue { get; init; }

    public Uri? Url { get; init; }

    public string? ReasonCode { get; init; }

    public bool IsValid => this.Url is not null;
}

/// <summary>
/// Streams addresses from the plain-text files in a source folder.
/// </summary>
/// <remarks>
/// <para>
/// Streams rather than materializes. SC-002 requires a 10,000-address run to complete without
/// exhausting memory, and reading every file into a list first would defeat that for no benefit.
/// </para>
/// <para>
/// A malformed line is yielded as a rejection rather than thrown, so the rest of the file is
/// still processed (FR-003). One bad line must not cost an operator the other 999 addresses.
/// </para>
/// </remarks>
public static class AddressListReader
{
    public const string FilePattern = "*.txt";

    /// <summary>
    /// Enumerates every entry across every list file, de-duplicated by normalized URL
    /// within and across files (FR-004).
    /// </summary>
    public static async IAsyncEnumerable<AddressEntry> ReadAsync(
        string folder,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        if (!Directory.Exists(folder))
        {
            // An absent folder yields zero addresses. The caller reports a successful run with
            // nothing read, which is distinguishable from a run that failed to start.
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(folder, FilePattern).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lineNumber = 0;

            using var reader = new StreamReader(file);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lineNumber++;
                var trimmed = line.Trim();

                // Blank and comment lines are not entries at all, not even rejections.
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var fileName = Path.GetFileName(file);

                if (!UrlNormalizer.TryNormalize(trimmed, out var url))
                {
                    yield return new AddressEntry
                    {
                        SourceFile = fileName,
                        LineNumber = lineNumber,
                        RawValue = trimmed,
                        ReasonCode = ReasonCode.MalformedAddress,
                    };
                    continue;
                }

                if (!seen.Add(url.ToString()))
                {
                    // Already yielded this run; retrieving it twice would be impolite and would
                    // produce a duplicate outcome row.
                    continue;
                }

                yield return new AddressEntry
                {
                    SourceFile = fileName,
                    LineNumber = lineNumber,
                    RawValue = trimmed,
                    Url = url,
                };
            }
        }
    }

    public static IReadOnlyList<string> ListFiles(string folder) =>
        Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, FilePattern)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.Ordinal)]
            : [];
}
