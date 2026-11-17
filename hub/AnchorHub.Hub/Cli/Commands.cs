using AnchorHub.Core;
using AnchorHub.Hub.NativeMessaging;

namespace AnchorHub.Hub.Cli;

internal static class Commands
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static int Lookup(string url)
    {
        var normalized = UrlNormalizer.TryNormalize(url);
        if (normalized is null)
        {
            Console.Error.WriteLine("Invalid or unsupported URL.");
            return 1;
        }

        using var client = new OneNoteClient();

        Console.WriteLine($"Normalised URL : {normalized.Normalized}");
        Console.WriteLine($"Host           : {normalized.Host}");
        Console.WriteLine();

        if (HostFallbackPolicy.IsSearchEngineHost(normalized) ||
            HostFallbackPolicy.IsNoIdentityHost(normalized) ||
            GoogleScholarUrlHelper.IsNoIdentityUrl(url))
        {
            Console.WriteLine("Host has no content identity (search engine / web-app shell / Scholar non-profile) — no lookup performed.");
            return 0;
        }

        // Pass 1 substitutes the Scholar canonical form when applicable
        // (see GoogleScholarUrlHelper for rationale).
        var pass1Term = GoogleScholarUrlHelper.CanonicalProfileTerm(url) ?? normalized.Normalized;
        Console.Write($"Pass 1 (exact)     searching \"{pass1Term}\" (phrase-quoted) ... ");
        var matches    = client.FindPages(pass1Term);
        Console.WriteLine($"{matches.Count} hit(s)");

        var matchType  = "exact";
        var searchTerm = pass1Term;

        if (matches.Count == 0)
        {
            var forge     = ForgeUrlHelper.ExtractTerms(normalized);
            var linkedIn  = LinkedInUrlHelper.ExtractPersonName(normalized);
            var reddit    = RedditUrlHelper.ExtractTerm(normalized);
            var appSlug   = AppCatalogUrlHelper.ExtractAppSlug(normalized);
            var idealo    = IdealoUrlHelper.ExtractProductName(normalized);
            var researchGate = ResearchGateUrlHelper.ExtractPersonName(normalized);
            var academiaEdu = AcademiaEduUrlHelper.ExtractPersonName(normalized);
            var wikiTitle = WikipediaUrlHelper.ExtractArticleTitle(normalized);

            if (forge.HasValue && ForgeUrlHelper.IsRepoSubpath(normalized))
            {
                Console.WriteLine("Forge deep link (issue/PR/commit/blob/…) — skipping helper fallback.");
            }
            else if (forge.HasValue)
            {
                var host = forge.Value.Host;
                if (!string.IsNullOrEmpty(forge.Value.Org) && !string.IsNullOrEmpty(forge.Value.Project))
                {
                    searchTerm = $"{host}/{forge.Value.Org}/{forge.Value.Project}";
                    Console.Write($"Pass 2 (forge-repo)  searching \"{searchTerm}\" ... ");
                    matches    = client.FindPages(searchTerm);
                    matchType  = "related";
                    Console.WriteLine($"{matches.Count} hit(s)");
                }
                if (matches.Count == 0 && !string.IsNullOrEmpty(forge.Value.Org))
                {
                    searchTerm = $"{host}/{forge.Value.Org}";
                    Console.Write($"Pass 3 (forge-org)   searching \"{searchTerm}\" ... ");
                    matches    = client.FindPages(searchTerm);
                    matchType  = "related";
                    Console.WriteLine($"{matches.Count} hit(s)");
                }
                if (matches.Count == 0 && !string.IsNullOrEmpty(forge.Value.Project))
                {
                    searchTerm = forge.Value.Project!;
                    Console.Write($"Pass 4 (forge-proj)  searching \"{searchTerm}\" ... ");
                    matches    = client.FindPages(searchTerm);
                    matchType  = "related";
                    Console.WriteLine($"{matches.Count} hit(s)");
                }
            }
            else if (linkedIn != null)
            {
                Console.Write($"Pass 2 (li-name)   searching \"{linkedIn}\" ... ");
                matches    = client.FindPages(linkedIn);
                matchType  = "related";
                searchTerm = linkedIn;
                Console.WriteLine($"{matches.Count} hit(s)");
            }
            else if (reddit != null)
            {
                Console.Write($"Pass 2 (subreddit) searching \"{reddit}\" ... ");
                matches    = client.FindPages(reddit);
                matchType  = "related";
                searchTerm = reddit;
                Console.WriteLine($"{matches.Count} hit(s)");
            }
            else if (appSlug != null)
            {
                Console.Write($"Pass 2 (app-slug)  searching \"{appSlug}\" ... ");
                matches    = client.FindPages(appSlug);
                matchType  = "related";
                searchTerm = appSlug;
                Console.WriteLine($"{matches.Count} hit(s)");
            }
            else if (idealo != null)
            {
                Console.Write($"Pass 2 (idealo)    searching \"{idealo}\" ... ");
                matches    = client.FindPages(idealo);
                matchType  = "related";
                searchTerm = idealo;
                Console.WriteLine($"{matches.Count} hit(s)");
            }
            else if (researchGate != null)
            {
                Console.Write($"Pass 2 (rg-name)   searching \"{researchGate}\" ... ");
                matches    = client.FindPages(researchGate);
                matchType  = "related";
                searchTerm = researchGate;
                Console.WriteLine($"{matches.Count} hit(s)");
            }
            else if (academiaEdu != null)
            {
                Console.Write($"Pass 2 (acad-name) searching \"{academiaEdu}\" ... ");
                matches    = client.FindPages(academiaEdu);
                matchType  = "related";
                searchTerm = academiaEdu;
                Console.WriteLine($"{matches.Count} hit(s)");
            }
            else if (wikiTitle != null)
            {
                Console.Write($"Pass 2 (wiki-title) searching \"{wikiTitle}\" ... ");
                matches    = client.FindPages(wikiTitle);
                matchType  = "related";
                searchTerm = wikiTitle;
                Console.WriteLine($"{matches.Count} hit(s)");
            }
            else if (!HostFallbackPolicy.ShouldSkipHostFallback(normalized))
            {
                Console.Write($"Pass 2 (host-only) searching \"{normalized.Host}\" ... ");
                matches    = client.FindPages(normalized.Host);
                matchType  = "host-only";
                searchTerm = normalized.Host;
                Console.WriteLine($"{matches.Count} hit(s)");
            }
            else
            {
                Console.WriteLine($"Pass 2 skipped — \"{normalized.Host}\" is in the no-host-fallback list.");
            }
        }

        Console.WriteLine();

        if (matches.Count == 0)
        {
            Console.WriteLine("No matches found.");
            return 0;
        }

        Console.WriteLine($"{matches.Count} match(es) [{matchType}] (search term: {searchTerm})\n");
        foreach (var m in matches)
        {
            Console.WriteLine($"  {m.Title}");
            Console.WriteLine($"    Notebook: {m.NotebookName}  /  Section: {m.SectionName}");
            Console.WriteLine($"    PageId: {m.PageId}");
            Console.WriteLine();
        }

        return 0;
    }

    // Free-text OneNote search by name (or any phrase). Mirrors the
    // `searchText` native-messaging action — no URL normalisation, no
    // host fallback, no helper passes. Useful for debugging the in-page
    // person-name scanner: when a marker is missing on a real page, run
    // `lookup-name "John Doe"` from the shell to verify whether OneNote
    // would have returned a hit at all.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static int LookupName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("Empty name.");
            return 1;
        }

        using var client = new OneNoteClient();
        Console.WriteLine($"Search term : {name}");
        Console.Write($"FindPages \"{name}\" (phrase-quoted) ... ");
        var matches = client.FindPages(name);
        Console.WriteLine($"{matches.Count} hit(s)\n");

        if (matches.Count == 0)
        {
            Console.WriteLine("No matches found.");
            return 0;
        }

        foreach (var m in matches)
        {
            Console.WriteLine($"  {m.Title}");
            Console.WriteLine($"    Notebook: {m.NotebookName}  /  Section: {m.SectionName}");
            Console.WriteLine($"    PageId: {m.PageId}");
            Console.WriteLine();
        }
        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static int SearchWsi(string url)
    {
        const string connStr =
            "Provider=Search.CollatorDSO.1;Extended Properties='Application=Windows'";

        var escaped = url.Replace("'", "''");
        var sql = $"""
            SELECT TOP 10
                   System.ItemUrl,
                   System.Title,
                   System.ItemPathDisplay,
                   System.ItemName,
                   System.Search.Contents
            FROM SYSTEMINDEX
            WHERE CONTAINS(System.Search.Contents, '"{escaped}"')
              AND System.ItemType = '.one'
            """;

        using var conn = new System.Data.OleDb.OleDbConnection(connStr);
        conn.Open();
        using var cmd = new System.Data.OleDb.OleDbCommand(sql, conn);
        using var reader = cmd.ExecuteReader();

        int count = 0;
        while (reader.Read())
        {
            count++;
            Console.WriteLine($"\n--- Result {count} ---");
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name  = reader.GetName(i);
                var value = reader.IsDBNull(i) ? "(null)" : reader.GetValue(i)?.ToString();
                Console.WriteLine($"  {name}: {value}");
            }
        }

        Console.WriteLine(count == 0
            ? "\nNo results found in Windows Search Index."
            : $"\n{count} result(s) found.");
        return 0;
    }

    public static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        Console.Error.WriteLine("Commands: lookup <url> | lookup-name <name…> | search-wsi <url>");
        return 1;
    }
}
