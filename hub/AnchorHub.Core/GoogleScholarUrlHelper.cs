namespace AnchorHub.Core;

/// <summary>
/// Google Scholar profile pages live at scholar.google.&lt;tld&gt;/citations and
/// are uniquely identified by the ?user= query parameter — the path alone
/// is shared by every Scholar profile. The generic UrlNormalizer strips
/// queries, so without this helper Pass 1 would search for
/// "scholar.google.com/citations" and match every Scholar profile saved
/// in OneNote.
///
/// This helper returns a canonical Pass-1 search term that includes the
/// user= parameter (and drops ephemeral parameters like hl=).
/// </summary>
public static class GoogleScholarUrlHelper
{
    /// <summary>
    /// For Scholar profile URLs, returns the canonical search term
    /// "scholar.google.com/citations?user=&lt;id&gt;". Returns null for
    /// non-Scholar URLs or for /citations paths without a user= parameter.
    /// The host TLD is normalised to .com — Scholar redirects all locales
    /// to scholar.google.com, so saved pages share the .com form regardless
    /// of which locale the user originally browsed from.
    /// </summary>
    public static string? CanonicalProfileTerm(string? rawUrl)
    {
        if (!Uri.TryCreate(rawUrl?.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        var host = uri.Host.ToLowerInvariant();
        if (!host.StartsWith("scholar.google.", StringComparison.Ordinal)) return null;
        if (!uri.AbsolutePath.Equals("/citations", StringComparison.OrdinalIgnoreCase)) return null;

        var userId = ExtractQueryParam(uri.Query, "user");
        if (string.IsNullOrEmpty(userId)) return null;

        return $"scholar.google.com/citations?user={userId}";
    }

    /// <summary>
    /// Returns true for Scholar URLs whose normalised form has no useful
    /// identity — paper search result pages (/scholar?q=…), individual
    /// citation deep links (/citations?view_op=view_citation&amp;…), label
    /// taxonomy pages (/citations?mauthors=label:…), etc. The path tokens
    /// alone match every saved Scholar page, producing false positives on
    /// every link in an author's publication list. A profile URL with
    /// ?user=&lt;id&gt; is exempt — that has its own canonical search term.
    /// </summary>
    public static bool IsNoIdentityUrl(string? rawUrl)
    {
        if (!Uri.TryCreate(rawUrl?.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host.ToLowerInvariant();
        if (!host.StartsWith("scholar.google.", StringComparison.Ordinal)) return false;

        // Author profile root (/citations?user=<id>) has identity.
        if (uri.AbsolutePath.Equals("/citations", StringComparison.OrdinalIgnoreCase))
        {
            var userId = ExtractQueryParam(uri.Query, "user");
            var citationFor = ExtractQueryParam(uri.Query, "citation_for_view");
            // The per-paper deep link (citation_for_view=<id>:<token>) is
            // shared structurally but is per-paper and not worth searching
            // by path tokens. Only the profile-root form (user= without
            // citation_for_view) is treated as having identity.
            if (!string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(citationFor))
                return false;
        }

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
