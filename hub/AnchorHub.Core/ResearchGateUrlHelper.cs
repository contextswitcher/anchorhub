namespace AnchorHub.Core;

/// <summary>
/// Extracts the researcher's name from ResearchGate profile URLs of the
/// form <c>researchgate.net/profile/&lt;Name-Slug&gt;</c>. The slug uses
/// hyphen-separated tokens with a trailing numeric disambiguator
/// (e.g. <c>David-Benavides-7</c> → <c>"David Benavides"</c>).
/// </summary>
public static class ResearchGateUrlHelper
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "researchgate.net", "www.researchgate.net",
    };

    public static string? ExtractPersonName(NormalizedUrl url)
    {
        if (!Hosts.Contains(url.Host)) return null;

        var segments = new Uri(url.Normalized).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2 ||
            !string.Equals(segments[0], "profile", StringComparison.OrdinalIgnoreCase))
            return null;

        var parts = segments[1].Split('-', StringSplitOptions.RemoveEmptyEntries);

        // Drop trailing numeric disambiguator (e.g. "David-Benavides-7" → "7").
        if (parts.Length > 1 && parts[^1].All(char.IsDigit))
            parts = parts[..^1];

        if (parts.Length == 0) return null;

        return string.Join(" ", parts.Select(p =>
            p.Length > 0 ? char.ToUpper(p[0]) + p[1..].ToLower() : p));
    }
}
