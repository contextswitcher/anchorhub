namespace AnchorHub.Core;

/// <summary>
/// Catch-all fulltext adapter that matches any URL. Used when no publisher-
/// specific adapter recognises the host. The browser-side content script
/// (<c>adapters/generic.js</c>) scans the loaded page for any plausible PDF
/// link (href ending in <c>.pdf</c>, paths containing <c>/pdf/</c> or
/// <c>/fulltext/</c>, anchors with <c>type="application/pdf"</c>, etc.).
/// </summary>
public sealed class GenericFulltextAdapter : IFulltextAdapter
{
    public string ContentScriptName => "generic";

    public bool Matches(NormalizedUrl url) => true;

    public string PublisherKey(NormalizedUrl url) => url.Host;

    public string DocumentUrl(NormalizedUrl url) => url.Normalized;
}
