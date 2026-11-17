namespace AnchorHub.Core;

/// <summary>
/// Extracts the article title from a Wikipedia URL so the lookup cascade can
/// search OneNote for the title alone (e.g. "Barcamp", "Albert Einstein").
/// Recognises every language subdomain (en, de, fr, …) plus the bare
/// <c>wikipedia.org</c> host. Underscores in the URL slug are replaced with
/// spaces, and percent-encoded characters are decoded so URLs like
/// <c>/wiki/Albert_Einstein</c> or <c>/wiki/Caf%C3%A9</c> become readable
/// search terms.
/// </summary>
public static class WikipediaUrlHelper
{
    public static string? ExtractArticleTitle(NormalizedUrl url)
    {
        if (!IsWikipediaHost(url.Host)) return null;

        var segments = new Uri(url.Normalized).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Article paths look like /wiki/<Title>. Anything else (Special:Search,
        // /w/index.php, language portal, …) has no useful title to extract.
        if (segments.Length < 2 ||
            !string.Equals(segments[0], "wiki", StringComparison.Ordinal))
            return null;

        var slug = Uri.UnescapeDataString(segments[1]);
        return slug.Replace('_', ' ');
    }

    /// <summary>
    /// Returns true when the host is <c>wikipedia.org</c> or any of its
    /// language subdomains (e.g. <c>en.wikipedia.org</c>,
    /// <c>de.wikipedia.org</c>, <c>simple.wikipedia.org</c>).
    /// </summary>
    public static bool IsWikipediaHost(string host)
    {
        return string.Equals(host, "wikipedia.org", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".wikipedia.org", StringComparison.OrdinalIgnoreCase);
    }
}
