namespace AnchorHub.Core;

/// <summary>
/// Extracts useful search terms from code-forge URLs (github.com, gitlab.com,
/// codeberg.org) so the lookup cascade uses the org/project pair, the org
/// alone, and finally the bare project name instead of the generic host.
/// </summary>
public static class ForgeUrlHelper
{
    /// <summary>
    /// Maps every recognised host (including <c>www.</c> variants) to the
    /// canonical host string used when building search terms.
    /// </summary>
    private static readonly Dictionary<string, string> ForgeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["github.com"]       = "github.com",
        ["www.github.com"]   = "github.com",
        ["gitlab.com"]       = "gitlab.com",
        ["www.gitlab.com"]   = "gitlab.com",
        ["codeberg.org"]     = "codeberg.org",
        ["www.codeberg.org"] = "codeberg.org",
    };

    /// <summary>
    /// First-path-segment names that are reserved by the forge itself
    /// (e.g. <c>github.com/topics/git-analysis</c>, <c>github.com/marketplace/…</c>).
    /// They look like an org/project pair but aren't, so shortening would
    /// produce noisy search terms like <c>github.com/topics</c>.
    /// Hosts share the list because the names don't collide with valid
    /// usernames on any of the recognised forges.
    /// </summary>
    private static readonly HashSet<string> ReservedFirstSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "topics", "marketplace", "search", "explore", "settings",
        "login", "logout", "notifications", "about", "pricing",
        "features", "trending", "new", "signup", "help", "admin",
        "dashboard", "contact", "security", "pulls", "issues",
        "discussions", "sponsors", "organizations", "users",
        "codespaces", "collections", "site", "join",
    };

    /// <summary>
    /// File-extension whitelist for README files at repo root. Includes the
    /// common markup formats GitHub/GitLab/Codeberg render inline.
    /// </summary>
    private static readonly HashSet<string> ReadmeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "md", "markdown", "rst", "txt", "adoc", "asciidoc", "org",
    };

    /// <summary>
    /// Returns (canonical host, org, project) for a forge URL, or null for
    /// URLs whose host is not a recognised forge. <c>Org</c> and <c>Project</c>
    /// may be null if the URL doesn't have enough path segments, or if the
    /// first path segment is reserved by the forge (e.g. <c>/topics/…</c>) —
    /// in that case the lookup cascade enters the forge branch but no
    /// shortening pass runs, and host-only fallback is still skipped.
    /// </summary>
    public static (string Host, string? Org, string? Project)? ExtractTerms(NormalizedUrl url)
    {
        if (!ForgeHosts.TryGetValue(url.Host, out var host)) return null;

        var segments = new Uri(url.Normalized).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length >= 1 && ReservedFirstSegments.Contains(segments[0]))
            return (host, null, null);

        var org     = segments.Length >= 1 ? segments[0] : null;
        var project = segments.Length >= 2 ? segments[1] : null;
        return (host, org, project);
    }

    /// <summary>
    /// Returns true when the URL points to a path *inside* a forge repo —
    /// i.e. <c>&lt;org&gt;/&lt;project&gt;/&lt;something&gt;[/…]</c>. These deep links
    /// (specific issues, PRs, commits, files, …) get unique identity from
    /// the path beyond the repo root, so the broader forge fallback passes
    /// (<c>host/org/project</c>, <c>host/org</c>, bare project name) would
    /// surface every saved page from the same repo and mislead the user
    /// into thinking the specific item is in OneNote.
    ///
    /// Exception: "repo-identity" paths (default-branch tree root, README
    /// at repo root, wiki home) share identity with the repo root, so they
    /// return false and the fallback cascade runs as for the bare repo
    /// URL. See <see cref="IsRepoIdentityPath"/>.
    /// </summary>
    public static bool IsRepoSubpath(NormalizedUrl url)
    {
        if (!ForgeHosts.ContainsKey(url.Host)) return false;
        var segments = new Uri(url.Normalized).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || ReservedFirstSegments.Contains(segments[0])) return false;
        return !IsIdentityPath(segments);
    }

    /// <summary>
    /// Returns true when the URL is a forge deep link that semantically
    /// resolves to the repo's home view:
    /// <list type="bullet">
    /// <item><c>&lt;org&gt;/&lt;project&gt;/tree/&lt;branch&gt;</c> — default-branch tree root.</item>
    /// <item><c>&lt;org&gt;/&lt;project&gt;/blob/&lt;branch&gt;/README[.ext]</c> — README at repo root.</item>
    /// <item><c>&lt;org&gt;/&lt;project&gt;/wiki</c> or <c>…/wiki/Home</c> — wiki landing page.</item>
    /// </list>
    /// GitLab's <c>/-/</c> path marker is tolerated, so
    /// <c>gitlab.com/&lt;org&gt;/&lt;project&gt;/-/blob/&lt;branch&gt;/README.md</c>
    /// also qualifies.
    /// </summary>
    public static bool IsRepoIdentityPath(NormalizedUrl url)
    {
        if (!ForgeHosts.ContainsKey(url.Host)) return false;
        var segments = new Uri(url.Normalized).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || ReservedFirstSegments.Contains(segments[0])) return false;
        return IsIdentityPath(segments);
    }

    private static bool IsIdentityPath(string[] segments)
    {
        // Skip the optional GitLab "-" path marker between project and verb.
        var i = 2;
        if (segments[i] == "-" && i + 1 < segments.Length) i++;
        if (i >= segments.Length) return false;

        var verb = segments[i];
        var rest = segments.Length - i - 1;

        if (verb.Equals("tree", StringComparison.OrdinalIgnoreCase) && rest == 1)
            return true; // tree/<branch> — default tree root
        if (verb.Equals("blob", StringComparison.OrdinalIgnoreCase) && rest == 2)
            return IsReadmeFilename(segments[i + 2]);
        if (verb.Equals("wiki", StringComparison.OrdinalIgnoreCase))
            return rest == 0
                || (rest == 1 && segments[i + 1].Equals("Home", StringComparison.OrdinalIgnoreCase));
        return false;
    }

    private static bool IsReadmeFilename(string name)
    {
        var dot = name.IndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        if (!stem.Equals("README", StringComparison.OrdinalIgnoreCase)) return false;
        if (dot < 0) return true;
        return ReadmeExtensions.Contains(name[(dot + 1)..]);
    }

    /// <summary>
    /// Short / common-word project names that, searched bare (pass 4), match
    /// unrelated OneNote pages instead of the repo — e.g. <c>vale</c>,
    /// <c>utils</c>, <c>tools</c>. Length &lt; 5 is filtered separately, so
    /// this set only needs to list the common words of length 5+.
    /// </summary>
    private static readonly HashSet<string> CommonProjectNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "utils", "tools", "notes", "tests", "pages", "index", "react",
        "hooks", "world", "hello", "start", "setup", "build", "sample",
        "config", "server", "client", "common", "shared", "website",
        "example", "examples", "template", "templates", "playground",
    };

    /// <summary>
    /// Returns true when a forge project name is distinctive enough to search
    /// bare (pass 4) without matching unrelated notes. Names shorter than 5
    /// characters, or in <see cref="CommonProjectNames"/>, are treated as too
    /// generic — the host-anchored passes (2 and 3) still find a genuinely
    /// saved repo, so suppressing pass 4 only loses the "note mentions the
    /// project by name, no link" edge case for common names.
    /// </summary>
    public static bool IsDistinctiveProjectName(string? project) =>
        !string.IsNullOrEmpty(project)
        && project.Length >= 5
        && !CommonProjectNames.Contains(project);

    /// <summary>
    /// Returns the gist owner for a <c>gist.github.com/&lt;user&gt;/&lt;id&gt;</c>
    /// URL so the lookup cascade can fall back to all of that user's gists.
    /// Returns null for non-gist URLs.
    /// </summary>
    public static string? ExtractGistUser(NormalizedUrl url)
    {
        if (!string.Equals(url.Host, "gist.github.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var segments = new Uri(url.Normalized).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length >= 1 ? segments[0] : null;
    }
}
