namespace AnchorHub.Core;

/// <summary>
/// Adapter for fetching a paper's fulltext PDF from a specific publisher's
/// website via the browser extension. The C# side decides which adapter to
/// use, the publisher URL to open, and the per-publisher throttling key;
/// the actual DOM scraping happens in the extension's content script named
/// <see cref="ContentScriptName"/>.
/// </summary>
public interface IFulltextAdapter
{
    /// <summary>
    /// Returns true when this adapter handles the given (resolved) publisher
    /// URL. Publisher-specific adapters match on host plus a path pattern;
    /// the generic fallback adapter matches everything.
    /// </summary>
    bool Matches(NormalizedUrl url);

    /// <summary>
    /// Stable key used by the extension-side throttle to cap concurrency
    /// per publisher (default cap: 1 in-flight tab per key). Typically the
    /// host of the publisher URL.
    /// </summary>
    string PublisherKey(NormalizedUrl url);

    /// <summary>
    /// URL the browser extension should open in a new tab to start the
    /// fetch. Adapters MAY rewrite the input (e.g. canonicalising query
    /// strings) but typically return the input unchanged.
    /// </summary>
    string DocumentUrl(NormalizedUrl url);

    /// <summary>
    /// File name (without extension or directory) of the content script
    /// in the extension's <c>adapters/</c> directory, e.g. <c>"ieee"</c>
    /// for <c>adapters/ieee.js</c>.
    /// </summary>
    string ContentScriptName { get; }
}
