namespace AnchorHub.Core;

/// <summary>
/// Extracts the "core" domain name (second-level domain) from a host so the
/// lookup cascade can fall back to e.g. <c>docs.slipshow.org</c> →
/// "slipshow". Used as a last resort when neither the full URL nor the
/// full host match anything in OneNote.
/// </summary>
public static class DomainCoreHelper
{
    /// <summary>
    /// Returns the second-to-last dot-separated segment of <paramref name="host"/>
    /// (after stripping a leading <c>www.</c>). Returns null when extraction
    /// would be misleading: too few segments, or the segment is shorter than
    /// 4 characters (which catches generic short SLDs in compound TLDs like
    /// <c>co.uk</c>, <c>com.au</c>, <c>org.uk</c>).
    /// </summary>
    public static string? ExtractDomainCore(string host)
    {
        if (string.IsNullOrEmpty(host)) return null;

        var stripped = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? host[4..] : host;

        var parts = stripped.Split('.');
        if (parts.Length < 2) return null;

        var core = parts[^2];
        return core.Length < 4 ? null : core;
    }
}
