using System.Text;

namespace CarIngest.Robots;

/// <summary>
/// RFC 9309 §2.2.3 path matching: '*' matches any sequence including empty, '$' anchors
/// end-of-path, comparison is case-sensitive, and percent-encoding is normalized first.
/// </summary>
internal static class PathMatcher
{
    /// <summary>
    /// Uppercases percent-escape hex digits so %2f and %2F compare equal, and guarantees a
    /// leading slash. Does not decode: decoding would make %2F indistinguishable from a real
    /// path separator and change which rules match.
    /// </summary>
    internal static string NormalizePath(string path)
    {
        if (path.Length == 0)
        {
            return "/";
        }

        var builder = new StringBuilder(path.Length);
        if (path[0] != '/')
        {
            builder.Append('/');
        }

        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '%' && i + 2 < path.Length &&
                Uri.IsHexDigit(path[i + 1]) && Uri.IsHexDigit(path[i + 2]))
            {
                builder.Append('%')
                       .Append(char.ToUpperInvariant(path[i + 1]))
                       .Append(char.ToUpperInvariant(path[i + 2]));
                i += 2;
                continue;
            }

            builder.Append(path[i]);
        }

        return builder.ToString();
    }

    internal static bool Matches(string pattern, string normalizedPath)
    {
        var anchored = pattern.EndsWith('$');
        var effective = anchored ? pattern[..^1] : pattern;
        return IsMatch(NormalizePath(effective), normalizedPath, anchored);
    }

    /// <summary>
    /// Backtracking glob match with prefix semantics.
    /// </summary>
    /// <remarks>
    /// The terminating condition is <b>pattern</b> exhaustion, not path exhaustion: an
    /// unanchored robots rule matches a prefix, so "/private" must match
    /// "/private/deep/path". Requiring the path to be fully consumed would silently under-block
    /// every directory rule — the most dangerous possible bug in this file, since it turns a
    /// Disallow into an allow. '$' is the only thing that demands full consumption.
    /// Iterative backtracking so a pattern of many wildcards cannot blow the stack.
    /// </remarks>
    private static bool IsMatch(string pattern, string path, bool anchored)
    {
        int p = 0, s = 0, starPattern = -1, starPath = -1;

        while (true)
        {
            if (p == pattern.Length)
            {
                if (!anchored || s == path.Length)
                {
                    return true;
                }

                // Anchored with path left over: let an earlier '*' absorb more and retry.
                if (starPattern < 0)
                {
                    return false;
                }

                p = starPattern + 1;
                s = ++starPath;
                continue;
            }

            if (pattern[p] == '*')
            {
                starPattern = p++;
                starPath = s;
                continue;
            }

            if (s < path.Length && pattern[p] == path[s])
            {
                p++;
                s++;
                continue;
            }

            if (starPattern < 0 || starPath >= path.Length)
            {
                return false;
            }

            p = starPattern + 1;
            s = ++starPath;
        }
    }
}
