namespace AnchorHub.Core;

/// <summary>
/// Extracts a host-anchored search term from Reddit URLs.
/// The bare subreddit / user name (e.g. "docker") is far too generic —
/// it would match every OneNote page that mentions Docker. Anchoring the
/// term to the host (e.g. "reddit.com/r/docker") restricts matches to
/// pages that actually link to that subreddit while still surfacing
/// related threads when the exact comment URL isn't recorded.
/// </summary>
public static class RedditUrlHelper
{
    private static readonly HashSet<string> RedditHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "reddit.com", "www.reddit.com", "old.reddit.com", "new.reddit.com",
    };

    /// <summary>
    /// Returns <c>reddit.com/r/&lt;subreddit&gt;</c> for /r/ URLs and
    /// <c>reddit.com/u/&lt;user&gt;</c> for /u/ URLs, or null for non-Reddit
    /// URLs / paths we don't recognise. Always uses the canonical
    /// <c>reddit.com</c> host regardless of the input subdomain
    /// (<c>www.</c>, <c>old.</c>, <c>new.</c>).
    /// </summary>
    public static string? ExtractTerm(NormalizedUrl url)
    {
        if (!RedditHosts.Contains(url.Host)) return null;

        var segments = new Uri(url.Normalized).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2) return null;

        var marker = segments[0];
        if (string.Equals(marker, "r", StringComparison.OrdinalIgnoreCase))
            return $"reddit.com/r/{segments[1]}";
        if (string.Equals(marker, "u", StringComparison.OrdinalIgnoreCase))
            return $"reddit.com/u/{segments[1]}";

        return null;
    }
}
