using AngleSharp.Dom;

namespace CarIngest.Infrastructure.Extraction;

/// <summary>
/// Distinguishes "this page has no structured data" from "this page's content only exists after
/// script execution".
/// </summary>
/// <remarks>
/// The distinction matters to the operator, not to the code path: both are failures, but
/// <c>js-render-required</c> says "this host is unsupported by design" while
/// <c>jsonld-absent</c> says "this host needs a CSS extractor profile". Conflating them would
/// send someone looking for a selector bug that does not exist.
/// <para>
/// No headless browser is used. Playwright would add ~300MB of browser binaries, OS
/// dependencies, and hundreds of MB of RAM per page, and a hung browser would threaten the
/// fetch loop that FR-022 requires to stay isolated (research D12).
/// </para>
/// </remarks>
internal static class JsRenderDetector
{
    internal static bool LooksClientRendered(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // A <noscript> telling the reader they need JavaScript is the clearest signal a site
        // can give, and it is deliberate rather than inferred.
        var noscript = document.QuerySelectorAll("noscript")
            .Any(n => n.TextContent.Contains("javascript", StringComparison.OrdinalIgnoreCase));

        if (noscript)
        {
            return true;
        }

        // Otherwise: an empty-looking body alongside a mount point and inline script is the
        // standard single-page-app shape.
        var hasMountPoint = document.QuerySelectorAll(
            "#root, #app, #listing-root, [data-reactroot], [data-server-rendered]").Length > 0;

        var hasInlineScript = document.QuerySelectorAll("script:not([src])")
            .Any(s => s.TextContent.Trim().Length > 0);

        var visibleTextLength = document.Body?.TextContent
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim().Length ?? 0;

        return hasMountPoint && hasInlineScript && visibleTextLength < 200;
    }
}
