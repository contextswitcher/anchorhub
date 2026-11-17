using System.Text.RegularExpressions;

namespace AnchorHub.Core;

/// <summary>
/// Fulltext adapter for IEEE Xplore document pages
/// (<c>https://ieeexplore.ieee.org/document/&lt;arnumber&gt;</c>). The PDF
/// link on these pages is rendered client-side, so the actual locator lives
/// in <c>adapters/ieee.js</c> on the extension side.
/// </summary>
public sealed class IeeeFulltextAdapter : IFulltextAdapter
{
    private const string IeeeHost = "ieeexplore.ieee.org";

    private static readonly Regex DocumentPath =
        new(@"^/document/\d+/?$", RegexOptions.Compiled);

    public string ContentScriptName => "ieee";

    public bool Matches(NormalizedUrl url)
    {
        if (!string.Equals(StripWww(url.Host), IeeeHost, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(url.Normalized, UriKind.Absolute, out var uri))
            return false;

        return DocumentPath.IsMatch(uri.AbsolutePath);
    }

    public string PublisherKey(NormalizedUrl url) => IeeeHost;

    public string DocumentUrl(NormalizedUrl url) => url.Normalized;

    private static string StripWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
}
