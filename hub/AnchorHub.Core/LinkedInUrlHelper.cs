namespace AnchorHub.Core;

/// <summary>
/// Extracts useful search terms from LinkedIn URLs so the lookup cascade
/// searches by person name or company slug rather than the useless host
/// "linkedin.com". Handles /in/{slug} personal profiles and
/// /company/{slug} company pages.
/// </summary>
public static class LinkedInUrlHelper
{
    private static readonly HashSet<string> LinkedInHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "linkedin.com", "www.linkedin.com",
    };

    /// <summary>
    /// Returns the person's name for a LinkedIn /in/ URL, e.g.
    /// "julia-hertel-2014a81ab" → "Julia Hertel".
    /// Returns null for non-LinkedIn URLs or unsupported paths.
    /// </summary>
    public static string? ExtractPersonName(NormalizedUrl url)
    {
        if (!LinkedInHosts.Contains(url.Host)) return null;

        var segments = new Uri(url.Normalized).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Only /in/<slug> — skip company, jobs, school, etc.
        if (segments.Length < 2 ||
            !string.Equals(segments[0], "in", StringComparison.OrdinalIgnoreCase))
            return null;

        var parts = segments[1].Split('-', StringSplitOptions.RemoveEmptyEntries);

        // LinkedIn appends a hash-like ID (contains digits) as the last token — drop it.
        if (parts.Length > 1 && parts[^1].Any(char.IsDigit))
            parts = parts[..^1];

        if (parts.Length == 0) return null;

        return string.Join(" ", parts.Select(p =>
            p.Length > 0 ? char.ToUpper(p[0]) + p[1..].ToLower() : p));
    }

    /// <summary>
    /// Returns (canonical company URL, slug) for a LinkedIn /company/ URL,
    /// or null if the URL isn't a company page. The first item is suitable
    /// as a host-anchored OneNote search term (substring-matching pages
    /// that link to any path under that company), the second is the bare
    /// slug for matching free-text mentions.
    /// </summary>
    /// <example>
    /// <c>https://www.linkedin.com/company/tragai/posts/?feedView=all</c> →
    /// <c>("https://www.linkedin.com/company/tragai", "tragai")</c>.
    /// </example>
    public static (string CompanyUrl, string Slug)? ExtractCompanyTerms(NormalizedUrl url)
    {
        if (!LinkedInHosts.Contains(url.Host)) return null;

        var segments = new Uri(url.Normalized).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2 ||
            !string.Equals(segments[0], "company", StringComparison.OrdinalIgnoreCase))
            return null;

        var slug = segments[1];
        return ($"https://www.linkedin.com/company/{slug}", slug);
    }
}
