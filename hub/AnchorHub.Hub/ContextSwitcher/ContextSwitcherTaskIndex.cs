using AnchorHub.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AnchorHub.Hub.ContextSwitcher;

/// A browser URL matched against a ContextSwitcher task. <see cref="TaskId"/>
/// is the task's stable id (relative path, `.md` stripped, forward slashes),
/// which the extension turns into a `contextswitcher://…/<id>` deep link.
internal sealed record ContextSwitcherMatch(string TaskId, string Title, string Status, string MatchedUrl);

/// Scans ContextSwitcher task files and answers "does this browser URL belong
/// to a task?".
///
/// ContextSwitcher (../ContextSwitcher) stores one task per Markdown file under
/// its tasks directory (default `~/.contextswitcher/tasks`), with YAML
/// frontmatter carrying the switching configuration. A task's `browser.urls`
/// list is the set of pages that belong to it, so a page whose URL appears in
/// that list *is* a ContextSwitcher task — the same way the OneNote adapter
/// treats a saved URL as an anchor.
///
/// The task id mirrors ContextSwitcher's own derivation
/// (`TaskFileParser.relativeId`): the file path relative to the tasks
/// directory, forward-slash separated, `.md` stripped (e.g. `jabref/fix-npe`).
/// Keeping the derivation identical is what lets the id round-trip into a
/// `contextswitcher://task/<id>` / `contextswitcher://switch/<id>` deep link
/// (planned interface, contextswitcher#47).
///
/// Pure file IO + YAML — no COM — so this compiles and runs on every daemon
/// target, including the plain net10.0 (Linux forwarder) build.
internal sealed class ContextSwitcherTaskIndex
{
    // ContextSwitcher's TaskRepository excludes these two reserved names from
    // the task set (a copy-from template and per-group config).
    private static readonly string[] ReservedFiles = { "TEMPLATE.md", "CONTEXTSWITCHER.md" };

    private readonly string _tasksDir;

    // IgnoreUnmatchedProperties: task frontmatter also carries tmux/terminal/
    // intellij/claude sections we don't model here.
    private readonly IDeserializer _yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public ContextSwitcherTaskIndex(string tasksDir) => _tasksDir = tasksDir;

    /// The first task whose `browser.urls` contains a URL that normalises to
    /// the same value as <paramref name="url"/>, or null when nothing matches
    /// or the tasks directory is absent. A single malformed task file is
    /// skipped rather than failing the whole lookup.
    public ContextSwitcherMatch? FindTaskForUrl(string url)
    {
        var target = UrlNormalizer.TryNormalize(url);
        if (target is null) return null;
        if (!Directory.Exists(_tasksDir)) return null;

        foreach (var file in EnumerateTaskFiles())
        {
            ContextSwitcherMatch? match;
            try
            {
                match = MatchFile(file, target.Normalized);
            }
            catch
            {
                continue;
            }
            if (match is not null) return match;
        }
        return null;
    }

    private ContextSwitcherMatch? MatchFile(string file, string normalizedTarget)
    {
        var frontmatter = ExtractFrontmatter(File.ReadAllText(file));
        if (string.IsNullOrWhiteSpace(frontmatter)) return null;

        var task = _yaml.Deserialize<TaskFrontmatter>(frontmatter);
        if (task?.Browser?.Urls is not { Count: > 0 } urls) return null;

        foreach (var candidate in urls)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var normalized = UrlNormalizer.TryNormalize(candidate);
            if (normalized is null) continue;
            if (!string.Equals(normalized.Normalized, normalizedTarget, StringComparison.Ordinal))
                continue;

            var id = RelativeId(file);
            return new ContextSwitcherMatch(id, task.Title ?? id, task.Status ?? "active", candidate);
        }
        return null;
    }

    // ContextSwitcher scans the tasks directory and its immediate subfolders
    // only (one level deep — a subfolder is a project group). Mirror that depth
    // so ids line up and we never wander into unrelated nested content.
    private IEnumerable<string> EnumerateTaskFiles()
    {
        foreach (var f in MdFilesIn(_tasksDir)) yield return f;
        foreach (var dir in SafeList(() => Directory.GetDirectories(_tasksDir)))
            foreach (var f in MdFilesIn(dir)) yield return f;
    }

    private static IEnumerable<string> MdFilesIn(string dir) =>
        SafeList(() => Directory.GetFiles(dir, "*.md"))
            .Where(f => !ReservedFiles.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));

    private static string[] SafeList(Func<string[]> list)
    {
        try { return list(); }
        catch { return Array.Empty<string>(); }
    }

    private string RelativeId(string file)
    {
        var relative = Path.GetRelativePath(_tasksDir, file).Replace('\\', '/');
        return relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? relative[..^3]
            : relative;
    }

    /// Returns the YAML text between the leading `---` fence and the next
    /// `---`, or null when the file has no frontmatter block. Mirrors
    /// ContextSwitcher's `TaskFileParser` (BOM + CRLF tolerant).
    private static string? ExtractFrontmatter(string content)
    {
        var text = content.Replace("\r\n", "\n");
        if (text.StartsWith('﻿')) text = text[1..];
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return null;

        var close = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (close < 0) return null;

        return text.Substring(4, close - 4 + 1);
    }

    private sealed class TaskFrontmatter
    {
        public string? Title { get; set; }
        public string? Status { get; set; }
        public BrowserSection? Browser { get; set; }
    }

    private sealed class BrowserSection
    {
        public List<string>? Urls { get; set; }
    }
}
