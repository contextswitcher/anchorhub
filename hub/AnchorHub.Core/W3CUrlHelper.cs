namespace AnchorHub.Core;

/// <summary>
/// Extracts the spec acronym from a w3.org URL so the lookup cascade can
/// fall back to e.g. "OWL", "RDF", "SPARQL". The first path segment is
/// taken (`w3.org/OWL/` → "OWL"); for `/TR/&lt;spec&gt;` the second segment
/// is preferred since "TR" alone is too generic.
/// </summary>
public static class W3CUrlHelper
{
    public static string? ExtractSpecName(NormalizedUrl url)
    {
        var host = url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? url.Host[4..] : url.Host;

        if (!string.Equals(host, "w3.org", StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = new Uri(url.Normalized).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0) return null;

        if (string.Equals(segments[0], "TR", StringComparison.Ordinal) && segments.Length >= 2)
            return segments[1];

        return segments[0];
    }
}
