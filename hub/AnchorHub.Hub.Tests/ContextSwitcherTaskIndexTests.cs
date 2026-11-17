using AnchorHub.Hub.ContextSwitcher;

namespace AnchorHub.Hub.Tests;

public sealed class ContextSwitcherTaskIndexTests : IDisposable
{
    private readonly string _dir;

    public ContextSwitcherTaskIndexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anchorhub-cs-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteTask(string relativePath, string frontmatter, string body = "# Notes\n")
    {
        var file = Path.Combine(_dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $"---\n{frontmatter}\n---\n{body}");
    }

    [Fact]
    public void Matches_url_in_browser_urls_and_returns_task_id_from_filename()
    {
        WriteTask("fix-npe.md",
            "title: \"JabRef: fix groups NPE\"\n" +
            "status: active\n" +
            "browser:\n" +
            "  urls:\n" +
            "    - https://github.com/JabRef/jabref/pull/12345\n");

        var match = new ContextSwitcherTaskIndex(_dir)
            .FindTaskForUrl("https://github.com/JabRef/jabref/pull/12345");

        Assert.NotNull(match);
        Assert.Equal("fix-npe", match!.TaskId);
        Assert.Equal("JabRef: fix groups NPE", match.Title);
        Assert.Equal("active", match.Status);
        Assert.Equal("https://github.com/JabRef/jabref/pull/12345", match.MatchedUrl);
    }

    [Fact]
    public void Task_id_uses_forward_slashed_relative_path_for_grouped_tasks()
    {
        WriteTask("jabref/fix-npe.md",
            "browser:\n  urls:\n    - https://example.org/a\n");

        var match = new ContextSwitcherTaskIndex(_dir).FindTaskForUrl("https://example.org/a");

        Assert.NotNull(match);
        Assert.Equal("jabref/fix-npe", match!.TaskId);
        // Title falls back to the id when the frontmatter omits it.
        Assert.Equal("jabref/fix-npe", match.Title);
        Assert.Equal("active", match.Status);
    }

    [Fact]
    public void Matching_is_url_normalized_ignoring_trailing_slash_query_and_fragment()
    {
        WriteTask("t.md",
            "browser:\n  urls:\n    - https://Example.COM/Path/\n");

        var index = new ContextSwitcherTaskIndex(_dir);

        // Trailing slash + case in host normalise away; query/fragment discarded.
        Assert.NotNull(index.FindTaskForUrl("https://example.com/Path"));
        Assert.NotNull(index.FindTaskForUrl("https://example.com/Path?utm=x#frag"));
        // Path case is significant — a different path does not match.
        Assert.Null(index.FindTaskForUrl("https://example.com/other"));
    }

    [Fact]
    public void Returns_null_when_no_task_contains_the_url()
    {
        WriteTask("t.md", "browser:\n  urls:\n    - https://example.org/a\n");

        Assert.Null(new ContextSwitcherTaskIndex(_dir).FindTaskForUrl("https://nowhere.example/x"));
    }

    [Fact]
    public void Ignores_reserved_template_and_group_config_files()
    {
        WriteTask("TEMPLATE.md", "browser:\n  urls:\n    - https://example.org/a\n");
        WriteTask("CONTEXTSWITCHER.md", "browser:\n  urls:\n    - https://example.org/a\n");

        Assert.Null(new ContextSwitcherTaskIndex(_dir).FindTaskForUrl("https://example.org/a"));
    }

    [Fact]
    public void Task_without_browser_section_never_matches()
    {
        WriteTask("t.md", "title: no browsing\nstatus: active\n");

        Assert.Null(new ContextSwitcherTaskIndex(_dir).FindTaskForUrl("https://example.org/a"));
    }

    [Fact]
    public void A_malformed_task_file_does_not_break_lookup_of_the_others()
    {
        // No closing fence — unparseable frontmatter.
        File.WriteAllText(Path.Combine(_dir, "broken.md"), "---\nbrowser: [oops\nno closing fence");
        WriteTask("good.md", "browser:\n  urls:\n    - https://example.org/a\n");

        var match = new ContextSwitcherTaskIndex(_dir).FindTaskForUrl("https://example.org/a");

        Assert.NotNull(match);
        Assert.Equal("good", match!.TaskId);
    }

    [Fact]
    public void Preserves_non_active_status()
    {
        WriteTask("t.md",
            "status: suspended\nbrowser:\n  urls:\n    - https://example.org/a\n");

        var match = new ContextSwitcherTaskIndex(_dir).FindTaskForUrl("https://example.org/a");

        Assert.NotNull(match);
        Assert.Equal("suspended", match!.Status);
    }

    [Fact]
    public void Missing_tasks_directory_returns_null()
    {
        var absent = Path.Combine(_dir, "does-not-exist");
        Assert.Null(new ContextSwitcherTaskIndex(absent).FindTaskForUrl("https://example.org/a"));
    }

    [Fact]
    public void Non_http_urls_return_null()
    {
        WriteTask("t.md", "browser:\n  urls:\n    - https://example.org/a\n");

        Assert.Null(new ContextSwitcherTaskIndex(_dir).FindTaskForUrl("onenote:///x"));
    }
}
