using CarIngest.Core.Runs;
using CarIngest.Core.Sources;
using Xunit;

namespace CarIngest.Core.Tests;

public sealed class AddressListParsingTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("carringest-addresses").FullName;

    public void Dispose() => Directory.Delete(this.folder, recursive: true);

    private void WriteFile(string name, string content) =>
        File.WriteAllText(Path.Combine(this.folder, name), content);

    private async Task<List<AddressEntry>> ReadAllAsync()
    {
        var entries = new List<AddressEntry>();
        await foreach (var entry in AddressListReader.ReadAsync(this.folder))
        {
            entries.Add(entry);
        }

        return entries;
    }

    [Fact]
    public async Task BlankAndCommentLines_AreIgnoredEntirely()
    {
        WriteFile("a.txt", """
            # a leading comment
            https://dealer.example/listing/1

              # an indented comment

            https://dealer.example/listing/2
            """);

        var entries = await this.ReadAllAsync();

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.True(e.IsValid));
    }

    [Fact]
    public async Task MalformedLine_IsReportedIndividually_AndDoesNotAbortTheFile()
    {
        WriteFile("a.txt", """
            https://dealer.example/listing/1
            not-a-url
            ftp://dealer.example/listing/2
            /relative/path
            https://dealer.example/listing/3
            """);

        var entries = await this.ReadAllAsync();

        var valid = entries.Where(e => e.IsValid).ToList();
        var rejected = entries.Where(e => !e.IsValid).ToList();

        // The two valid addresses after the bad lines must still be read (FR-003).
        Assert.Equal(2, valid.Count);
        Assert.Equal(3, rejected.Count);
        Assert.All(rejected, e => Assert.Equal(ReasonCode.MalformedAddress, e.ReasonCode));

        // The rejection names where it came from, so an operator can fix the file.
        Assert.All(rejected, e => Assert.Equal("a.txt", e.SourceFile));
        Assert.Contains(rejected, e => e.LineNumber == 2);
    }

    [Fact]
    public async Task DuplicateWithinOneFile_IsYieldedOnce()
    {
        WriteFile("a.txt", """
            https://dealer.example/listing/1
            https://dealer.example/listing/1
            """);

        var entries = await this.ReadAllAsync();

        Assert.Single(entries);
    }

    [Fact]
    public async Task DuplicateAcrossFiles_IsYieldedOnce()
    {
        WriteFile("a.txt", "https://dealer.example/listing/1\n");
        WriteFile("b.txt", "https://dealer.example/listing/1\n");

        var entries = await this.ReadAllAsync();

        Assert.Single(entries);
    }

    [Fact]
    public async Task AllFilesInTheFolder_AreRead()
    {
        WriteFile("a.txt", "https://dealer.example/listing/1\n");
        WriteFile("b.txt", "https://dealer.example/listing/2\n");
        WriteFile("c.txt", "https://dealer.example/listing/3\n");

        var entries = await this.ReadAllAsync();

        Assert.Equal(3, entries.Count);
        Assert.Equal(3, AddressListReader.ListFiles(this.folder).Count);
    }

    [Fact]
    public async Task AbsentFolder_YieldsNothing_RatherThanThrowing()
    {
        var entries = new List<AddressEntry>();
        await foreach (var entry in AddressListReader.ReadAsync(Path.Combine(this.folder, "nope")))
        {
            entries.Add(entry);
        }

        // A successful run reporting zero addresses, distinguishable from a failure to start.
        Assert.Empty(entries);
    }

    [Fact]
    public async Task FragmentDiffersOnly_IsTreatedAsTheSameAddress()
    {
        WriteFile("a.txt", """
            https://dealer.example/listing/1
            https://dealer.example/listing/1#gallery
            """);

        var entries = await this.ReadAllAsync();

        // The fragment never reaches the server, so these are the same page.
        Assert.Single(entries);
    }

    [Fact]
    public async Task QueryStringIsPreserved_SoListingIdsRemainDistinct()
    {
        WriteFile("a.txt", """
            https://dealer.example/listing?id=1
            https://dealer.example/listing?id=2
            """);

        var entries = await this.ReadAllAsync();

        // Stripping the query would collapse every listing on this host into one address.
        Assert.Equal(2, entries.Count);
    }

    [Theory]
    [InlineData("https://Dealer.Example/listing/1", "https://dealer.example/listing/1")]
    [InlineData("HTTPS://dealer.example/listing/1", "https://dealer.example/listing/1")]
    public void HostAndScheme_AreLowercased(string input, string expected)
    {
        Assert.True(UrlNormalizer.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized.ToString());
    }

    [Fact]
    public void PathCase_IsPreserved_BecauseRobotsMatchingIsCaseSensitive()
    {
        Assert.True(UrlNormalizer.TryNormalize("https://dealer.example/Listing/ABC", out var normalized));

        // Lowercasing the path would change which robots.txt rules apply to this address.
        Assert.Equal("/Listing/ABC", normalized.AbsolutePath);
    }

    [Theory]
    [InlineData("https://dealer.example/x", "dealer.example")]
    [InlineData("http://localhost:8099/x", "localhost:8099")]
    [InlineData("https://dealer.example:443/x", "dealer.example")]
    public void HostKey_IncludesThePortOnlyWhenNonDefault(string url, string expected)
    {
        Assert.True(UrlNormalizer.TryNormalize(url, out var normalized));
        Assert.Equal(expected, UrlNormalizer.HostKey(normalized));
    }
}
