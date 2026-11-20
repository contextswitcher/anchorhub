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

    // Prints the selected Outlook item as one JSON line (with its anchor URI),
    // so other tools (ContextSwitcher, koppter) can store the link.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static int OutlookCapture()
    {
        Console.WriteLine(Outlook.OutlookClient.Capture().ToJsonString());
        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static int OutlookOpen(string uri) => WithAnchor(uri, Outlook.OutlookClient.Open);

    // One JSON line with the open To-Do List items of all stores; koppter
    // mixes them into its task list.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static int OutlookTodos()
    {
        Console.WriteLine(Outlook.OutlookClient.Todos().ToJsonString());
        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static int OutlookComplete(string uri) => WithAnchor(uri, Outlook.OutlookClient.Complete);

    // Reads the sync request from stdin: it can be far larger than a command line.
    // [impl->req~ank.outlook.blocks-sync~1]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static int OutlookBlocksSync()
    {
        string json;
        using (var reader = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false)))
            json = reader.ReadToEnd();
        KoppterSyncRequest req;
        try { req = KoppterBlocks.ParseSyncRequest(json); }
        catch (FormatException ex) { return Usage($"invalid request: {ex.Message}"); }

        var items = Outlook.OutlookClient.CalendarItems(req.Calendar, req.From, req.To, recurrences: false, onlyMarked: true);
        var plan = KoppterBlocks.Plan(items, req.From, req.To, req.Blocks);
        var result = Outlook.OutlookClient.Apply(req.Calendar, plan.ToDelete.Select(i => i.EntryId), plan.ToAdd);
        Console.WriteLine(new System.Text.Json.Nodes.JsonObject
        {
            ["added"] = result.Created,
            ["deleted"] = result.Deleted,
            ["kept"] = plan.Kept.Count + result.Skipped,
        }.ToJsonString());
        return 0;
    }

    // [impl->req~ank.outlook.blocks-clear~1]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static int OutlookBlocksClear(string[] args)
    {
        var dryRun = false;
        string? calendar = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dry-run") dryRun = true;
            else if (args[i] == "--calendar" && i + 1 < args.Length && args[i + 1].Length > 0) calendar = args[++i];
            else return Usage($"outlook-blocks-clear: unexpected argument {args[i]}");
        }
        var own = Outlook.OutlookClient.CalendarItems(calendar, null, null, recurrences: false, onlyMarked: true)
            .Where(KoppterBlocks.IsOwn).ToList();
        if (dryRun)
        {
            Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["count"] = own.Count }.ToJsonString());
            return 0;
        }
        var result = Outlook.OutlookClient.Apply(calendar, own.Select(i => i.EntryId), []);
        Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["deleted"] = result.Deleted }.ToJsonString());
        return 0;
    }

    // [impl->req~ank.outlook.busy~2]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static int OutlookBusy(string[] args)
    {
        string? fromArg = null, toArg = null, calendar = null;
        for (var i = 0; i < args.Length; i++)
        {
            var hasValue = i + 1 < args.Length && args[i + 1].Length > 0;
            if (args[i] == "--from" && hasValue) fromArg = args[++i];
            else if (args[i] == "--to" && hasValue) toArg = args[++i];
            else if (args[i] == "--calendar" && hasValue) calendar = args[++i];
            else return Usage($"outlook-busy: unexpected argument {args[i]}");
        }
        if (KoppterBlocks.ParseDate(fromArg) is not { } from || KoppterBlocks.ParseDate(toArg) is not { } to)
            return Usage("outlook-busy needs --from yyyy-MM-dd and --to yyyy-MM-dd");
        if (from >= to) return Usage("outlook-busy: --from must be before --to");
        var (items, colors) = Outlook.OutlookClient.BusyItems(calendar, from, to);
        Console.WriteLine(KoppterBlocks.Busy(items, from, to, colors).ToJsonString());
        return 0;
    }

    // Changes only the categories of one appointment (for a series: the master).
    // [impl->req~ank.outlook.sideline~1]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static int OutlookSideline(string[] args)
    {
        if (args.Length != 2 || (args[1] != "on" && args[1] != "off"))
            return Usage("outlook-sideline needs <anchor> on|off");
        if (OutlookAnchor.TryParse(args[0]) is not { } anchor)
            return Usage("not an anchor://outlook/message/... URI");
        var on = args[1] == "on";
        var item = Outlook.OutlookClient.ReadItem(anchor);
        if (item.Class != 26) return Refuse("the item is not an appointment");
        if (KoppterBlocks.IsOwn(item.BlockId, item.Categories, item.IsRecurring, item.MeetingStatus))
            return Refuse("the item is one of koppter's own blocks");
        var updated = OutlookCategories.SetSideline(item.Categories, on, item.Separator);
        if (updated != item.Categories) Outlook.OutlookClient.SetCategories(item, item.Categories, updated);
        Console.WriteLine(new System.Text.Json.Nodes.JsonObject { ["sideline"] = on }.ToJsonString());
        return 0;
    }

    private static int Refuse(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 2;
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }

    private static int WithAnchor(string uri, Action<OutlookAnchor> action)
    {
        var anchor = OutlookAnchor.TryParse(uri);
        if (anchor is null)
        {
            Console.Error.WriteLine("Not an anchor://outlook/message/... URI.");
            return 1;
        }
        action(anchor);
        return 0;
    }

    public static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        Console.Error.WriteLine("Commands: lookup <url> | lookup-name <name…> | search-wsi <url> | outlook-capture | outlook-open <anchor> | outlook-todos | outlook-complete <anchor> | outlook-blocks-sync (request on stdin) | outlook-blocks-clear [--dry-run] [--calendar <store>] | outlook-busy --from <date> --to <date> [--calendar <store>] | outlook-sideline <anchor> on|off");
        return 1;
    }
}
