namespace AnchorHub.Core;

/// <summary>
/// Extracts a product-name search term from idealo product URLs of the form
/// <c>idealo.&lt;tld&gt;/preisvergleich/OffersOfProduct/&lt;id&gt;_-&lt;slug&gt;.html</c>.
/// Strips the numeric product id and trailing <c>.html</c>, then converts the
/// hyphenated slug to spaces (e.g.
/// <c>202837261_-mx-anywhere-3s-grafit-logitech</c> →
/// <c>"mx anywhere 3s grafit logitech"</c>) so OneNote pages referencing the
/// product (and brand) can match.
/// </summary>
public static class IdealoUrlHelper
{
    public static string? ExtractProductName(NormalizedUrl url)
    {
        var host = url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? url.Host[4..] : url.Host;

        if (!host.StartsWith("idealo.", StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = new Uri(url.Normalized).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Expect: /preisvergleich/OffersOfProduct/<id>_-<slug>.html
        if (segments.Length < 3) return null;
        if (!string.Equals(segments[^2], "OffersOfProduct", StringComparison.OrdinalIgnoreCase))
            return null;

        var leaf = segments[^1];
        if (leaf.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            leaf = leaf[..^5];

        // Drop the leading "<digits>_" or "<digits>_-" product-id prefix.
        var underscore = leaf.IndexOf('_');
        if (underscore >= 0 && underscore < leaf.Length - 1)
        {
            var afterId = leaf[(underscore + 1)..];
            if (afterId.StartsWith('-')) afterId = afterId[1..];
            leaf = afterId;
        }

        leaf = leaf.Trim();
        if (leaf.Length == 0) return null;

        return leaf.Replace('-', ' ');
    }
}
