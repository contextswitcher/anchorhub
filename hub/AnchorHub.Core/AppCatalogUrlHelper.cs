namespace AnchorHub.Core;

/// <summary>
/// App/software catalog hosts whose URL paths end in a per-app slug
/// (e.g. <c>alternativeto.net/software/logseq/</c> → "logseq"). Lookup
/// uses that slug as a free-text search so OneNote pages about the same
/// app are surfaced even when the catalog URL itself isn't recorded.
/// </summary>
public static class AppCatalogUrlHelper
{
    // Each entry: catalog host → marker path segment that must appear
    // immediately before the app slug. Allows the marker to sit at any
    // depth (alternativeto.net/software/<slug>, but also
    // addons.mozilla.org/<lang>/firefox/addon/<slug>) — the rule is the
    // same: the second-to-last segment is the marker, the last is the slug.
    private static readonly Dictionary<string, string> Catalogs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alternativeto.net"]   = "software",
        ["noteapps.info"]       = "apps",
        ["addons.mozilla.org"]  = "addon",
    };

    public static string? ExtractAppSlug(NormalizedUrl url)
    {
        var host = url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? url.Host[4..] : url.Host;

        if (!Catalogs.TryGetValue(host, out var marker)) return null;

        var segments = new Uri(url.Normalized).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2) return null;
        if (!string.Equals(segments[^2], marker, StringComparison.OrdinalIgnoreCase)) return null;

        var slug = segments[^1].Trim();
        return slug.Length == 0 ? null : slug;
    }
}
