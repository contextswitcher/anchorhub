namespace AnchorHub.Core;

/// <summary>
/// Extracts a product-title search term from Amazon URLs of the form
/// <c>amazon.&lt;tld&gt;/&lt;Title-Words-Joined-By-Hyphens&gt;/dp/&lt;asin&gt;/…</c>.
/// The slug before <c>/dp/</c> is converted to spaces (e.g.
/// <c>Production-Workflow-Techniques-Frank-Leymann</c> →
/// <c>"Production Workflow Techniques Frank Leymann"</c>) so OneNote pages
/// referencing the title (and usually the author) can match.
/// </summary>
public static class AmazonUrlHelper
{
    public static string? ExtractProductTitle(NormalizedUrl url)
    {
        var host = url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? url.Host[4..] : url.Host;

        if (!host.StartsWith("amazon.", StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = new Uri(url.Normalized).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        var dpIdx = Array.FindIndex(segments,
            s => string.Equals(s, "dp", StringComparison.OrdinalIgnoreCase));
        if (dpIdx < 1) return null;

        var slug = segments[dpIdx - 1].Trim();
        if (slug.Length == 0) return null;

        return slug.Replace('-', ' ');
    }
}
