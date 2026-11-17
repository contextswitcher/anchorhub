namespace AnchorHub.Core;

/// <summary>
/// A OneNote page that represents a single literature reference, parsed from
/// the page's metadata table per <c>feat~citation-page-metadata~1</c>.
/// </summary>
public sealed record CitationPage(
    string Title,
    string CitationKey,
    IReadOnlyList<CitationIdentifier> Identifiers)
{
    /// <summary>
    /// The first identifier row, treated as the canonical/primary identifier
    /// per <c>req~citation-page.identifier-row~2</c>.
    /// </summary>
    public CitationIdentifier Primary => Identifiers[0];
}
