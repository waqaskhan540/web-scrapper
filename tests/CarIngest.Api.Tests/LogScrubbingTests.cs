using CarIngest.Api.Observability;
using Xunit;

namespace CarIngest.Api.Tests;

/// <summary>
/// Principle I-4 covers logs and responses, not only storage.
/// </summary>
/// <remarks>
/// Logging a raw response body on error is the commonest way a no-PII rule gets broken in
/// practice: the storage path is carefully designed, and then a debugging aid writes the whole
/// page to a log sink that nobody audits. Replacing markup wholesale rather than truncating it
/// matters because there is no safe prefix of an HTML document — the seller block could be
/// anywhere in it.
/// </remarks>
public sealed class LogScrubbingTests
{
    [Theory]
    [InlineData("<html><body>Call Jonathan on 07700 900461</body></html>")]
    [InlineData("<div class=\"seller\">jonathan.pemberton@seller-mailbox.example</div>")]
    [InlineData("Response was: <p>14 Alder Crescent</p>")]
    public void TextContainingMarkup_IsReplacedEntirely_NotTruncated(string candidate)
    {
        var scrubbed = LogScrubbing.Safe(candidate);

        Assert.Equal("[redacted: diagnostic text contained markup]", scrubbed);
        Assert.DoesNotContain("900461", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("Alder", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("jonathan", scrubbed, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Mandatory field(s) absent from the schema.org node: Year.")]
    [InlineData("Host returned 503.")]
    [InlineData("Declared 9000000 bytes exceeds the 5242880-byte cap.")]
    public void LegitimateDiagnosticText_PassesThroughUnchanged(string candidate)
    {
        // The messages the system actually emits name fields and statuses, so they must survive
        // scrubbing intact or the reason codes become useless to an operator (SC-007).
        Assert.Equal(candidate, LogScrubbing.Safe(candidate));
    }

    [Fact]
    public void OverlongText_IsTruncatedToTheCap()
    {
        var scrubbed = LogScrubbing.Safe(new string('a', LogScrubbing.MaxMessageLength * 3));

        Assert.True(scrubbed.Length <= LogScrubbing.MaxMessageLength + 16);
        Assert.EndsWith("…[truncated]", scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrBlankText_BecomesEmpty(string? candidate)
    {
        Assert.Equal(string.Empty, LogScrubbing.Safe(candidate));
    }

    [Fact]
    public void AngleBracketWithoutClosing_IsNotMistakenForMarkup()
    {
        // A guard that over-redacts gets disabled. "Price < 5000" is legitimate diagnostic text.
        const string text = "Value < 5000 was outside the plausible range.";

        Assert.Equal(text, LogScrubbing.Safe(text));
    }
}
