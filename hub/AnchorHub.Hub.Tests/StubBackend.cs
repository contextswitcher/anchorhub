using AnchorHub.Core;

namespace AnchorHub.Hub.Tests;

/// <summary>
/// Hand-rolled IOneNoteBackend stub for the loopback tests. Avoids
/// pulling in a mocking framework just for half a dozen no-op methods.
/// </summary>
internal sealed class StubBackend : IOneNoteBackend
{
    public Func<string, IReadOnlyList<PageMatch>> FindPagesImpl { get; set; } =
        _ => Array.Empty<PageMatch>();

    public IReadOnlyList<NotebookInfo> ListNotebooks() => Array.Empty<NotebookInfo>();
    public IReadOnlyList<PageMatch>    FindPages(string query, bool exact = true) => FindPagesImpl(query);
    public string                      AddPage(string sectionId, string title, string url) => "stub-page-id";
    public void                        NavigateTo(string pageId) { }
    public void                        FocusAndSearch(string pageId, string query) { }
    public void                        OpenSearch(string query) { }
    public void                        AppendBullet(string text, string url) { }
}
