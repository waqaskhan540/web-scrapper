using System.Buffers.Text;
using System.Text;
using CarIngest.Api.Observability;
using Microsoft.AspNetCore.Mvc;

namespace CarIngest.Api.Endpoints;

/// <summary>RFC 9457 problem details.</summary>
internal static class Problems
{
    private const string BaseUri = "https://carringest.invalid/problems/";

    /// <summary>
    /// Builds a problem response. <paramref name="detail"/> passes through log scrubbing: an
    /// error path is exactly where a response body tends to acquire page content, and
    /// Principle I-4 covers responses as well as storage.
    /// </summary>
    internal static ProblemDetails Create(string type, string title, int status, string detail) => new()
    {
        Type = BaseUri + type,
        Title = title,
        Status = status,
        Detail = LogScrubbing.Safe(detail),
    };
}

/// <summary>Opaque pagination cursor over a time-ordered GUID.</summary>
internal static class Cursor
{
    internal static string Encode(Guid id) =>
        Base64Url.EncodeToString(id.ToByteArray());

    internal static bool TryDecode(string? cursor, out Guid id)
    {
        id = Guid.Empty;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var bytes = Base64Url.DecodeFromChars(cursor);
            if (bytes.Length != 16)
            {
                return false;
            }

            id = new Guid(bytes);
            return true;
        }
        catch (FormatException)
        {
            // A malformed cursor is treated as "start from the beginning" rather than an error:
            // it is opaque to callers, so there is nothing actionable to report.
            return false;
        }
    }
}
