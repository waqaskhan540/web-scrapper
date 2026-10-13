namespace CarIngest.Api.Observability;

/// <summary>
/// Guards against the commonest way a PII rule gets broken in practice: logging a raw response
/// body or a full parsed source object on error.
/// </summary>
/// <remarks>
/// Constitution Principle I-4 covers logs and responses as well as storage. Nothing in this
/// service may hand page content to a log sink, so diagnostic text passes through here, and
/// anything resembling page content is replaced rather than truncated — a truncated body is
/// still a body.
/// </remarks>
public static class LogScrubbing
{
    /// <summary>Longest diagnostic message permitted. Anything longer is suspect by construction.</summary>
    public const int MaxMessageLength = 512;

    public static string Safe(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        // Markup means a page body reached a diagnostic path. Replace it wholesale: there is no
        // safe prefix of an HTML document.
        if (message.Contains('<', StringComparison.Ordinal) &&
            message.Contains('>', StringComparison.Ordinal))
        {
            return "[redacted: diagnostic text contained markup]";
        }

        return message.Length <= MaxMessageLength
            ? message
            : message[..MaxMessageLength] + "…[truncated]";
    }
}
