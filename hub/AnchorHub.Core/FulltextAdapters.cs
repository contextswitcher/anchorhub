namespace AnchorHub.Core;

/// <summary>
/// Registry that picks the right <see cref="IFulltextAdapter"/> for a
/// given (resolved) publisher URL. Publisher-specific adapters are tried in
/// registration order; <see cref="GenericFulltextAdapter"/> is the final
/// fallback and always matches.
/// </summary>
public static class FulltextAdapters
{
    private static readonly IReadOnlyList<IFulltextAdapter> PublisherSpecific = new IFulltextAdapter[]
    {
        new IeeeFulltextAdapter(),
    };

    private static readonly IFulltextAdapter Fallback = new GenericFulltextAdapter();

    /// <summary>
    /// Returns the first publisher-specific adapter whose <c>Matches</c>
    /// returns true, or the generic fallback adapter otherwise. Never
    /// returns null.
    /// </summary>
    public static IFulltextAdapter Pick(NormalizedUrl url)
    {
        foreach (var adapter in PublisherSpecific)
        {
            if (adapter.Matches(url))
                return adapter;
        }
        return Fallback;
    }

    /// <summary>
    /// True when a publisher-specific adapter matches; false when only the
    /// generic fallback would. Useful for distinguishing
    /// <c>no-adapter</c> vs <c>no-pdf-found</c> error responses.
    /// </summary>
    public static bool HasPublisherAdapter(NormalizedUrl url)
    {
        foreach (var adapter in PublisherSpecific)
        {
            if (adapter.Matches(url))
                return true;
        }
        return false;
    }
}
