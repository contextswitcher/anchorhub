namespace AnchorHub.Core;

/// <summary>
/// Google Books volume pages live at books.google.&lt;tld&gt;/books and are
/// uniquely identified by the ?id=&lt;volumeId&gt; query parameter — the path
/// "/books" is shared by every Google Books link, across every locale TLD
/// (books.google.de, books.google.com, …). The generic UrlNormalizer strips
/// queries, so without this helper Pass 1 would search for
/// "books.google.de/books" and match every OneNote page that contains any
/// Google Books link (observed: a "Verschwörungstheorien" note matched as an
/// exact hit on an unrelated software-quality book).
///
/// The volume id (e.g. "vJ8pAQAAMAAJ") is opaque and highly distinctive, so
/// the canonical Pass-1 term is the bare id: it matches a saved link to the
/// same volume regardless of which locale TLD the link was captured under.
/// </summary>
public static class GoogleBooksUrlHelper
{
    /// <summary>
    /// For Google Books volume URLs, returns the volume id from ?id=&lt;id&gt;
    /// as the Pass-1 search term. Returns null for non-Books URLs or for
    /// /books paths without an id= parameter.
    /// </summary>
    public static string? CanonicalBookTerm(string? rawUrl)
    {
        if (!IsBooksHost(rawUrl, out var uri)) return null;

        var id = ExtractQueryParam(uri!.Query, "id");
        return string.IsNullOrEmpty(id) ? null : id;
    }

    /// <summary>
    /// Returns true for Google Books URLs whose normalised form has no useful
    /// identity — the "/books" path (and search/about pages under it) without
    /// an ?id=&lt;id&gt; parameter. The path token "books" alone matches every
    /// saved Google Books page. A volume URL with ?id=&lt;id&gt; is exempt —
    /// that has its own canonical search term.
    /// </summary>
    public static bool IsNoIdentityUrl(string? rawUrl)
    {
        if (!IsBooksHost(rawUrl, out var uri)) return false;
        return string.IsNullOrEmpty(ExtractQueryParam(uri!.Query, "id"));
    }

    private static bool IsBooksHost(string? rawUrl, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(rawUrl?.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (!parsed.Host.StartsWith("books.google.", StringComparison.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }

    private static string? ExtractQueryParam(string query, string key)
    {
        if (string.IsNullOrEmpty(query)) return null;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) continue;
            if (pair.AsSpan(0, idx).Equals(key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[(idx + 1)..]);
        }
        return null;
    }
}
