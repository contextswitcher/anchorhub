namespace AnchorHub.Core;

/// <summary>
/// Surface area exposed to MessageHandler for talking to OneNote. The
/// production implementation (OneNoteClient) wraps the PowerShell COM
/// bridge and is Windows-only; tests and the future TCP-listen mode
/// use the same interface.
/// </summary>
public interface IOneNoteBackend
{
    IReadOnlyList<NotebookInfo> ListNotebooks();

    /// <summary>
    /// Searches OneNote for pages whose text contains <paramref name="query"/>.
    /// When <paramref name="exact"/> is true (default), the query is sent as a
    /// quoted phrase — the entire string must appear contiguously in the page.
    /// When false, OneNote's tokenised search is used, matching the query as a
    /// token regardless of surrounding text (catches CamelCase / compound-word
    /// occurrences that phrase search misses).
    /// </summary>
    IReadOnlyList<PageMatch>    FindPages(string query, bool exact = true);
    string                      AddPage(string sectionId, string title, string url);
    void                        NavigateTo(string pageId);
    void                        FocusAndSearch(string pageId, string query);
    void                        OpenSearch(string query);
    void                        AppendBullet(string text, string url);
}

public sealed record PageMatch(
    string  PageId,
    string  Title,
    string? SectionName,
    string? NotebookName);

public sealed record NotebookInfo(
    string NotebookId,
    string NotebookName,
    IReadOnlyList<SectionInfo> Sections);

public sealed record SectionInfo(
    string SectionId,
    string SectionName);
