using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AnchorHub.Core;

namespace AnchorHub.Hub.NativeMessaging;

// Bridges to a persistent PowerShell process that holds the OneNote COM object.
// .NET's COM interop cannot access Click-to-Run Office type libraries (they live in a
// registry hive only visible to Office processes), but PowerShell's COM layer can.
// The PS process reads one-line JSON requests from stdin and writes one Base64-encoded
// UTF-8 line per response to stdout, keeping startup overhead to a single hit.
[SupportedOSPlatform("windows")]
internal sealed class OneNoteClient : IOneNoteBackend, IDisposable
{
    private readonly Process _ps;
    private bool _disposed;

    private readonly OneMoreIndex _oneMore = new(
        OneMoreIndex.DefaultPath, OneMoreIndex.DefaultSettingsPath, OneMoreInstalledDefault);
    private Dictionary<string, PageMatch>? _pages;   // current hierarchy, keyed by page ID
    private DateTime _pagesLoadedAt;

    private const string PsScript = """
        $ErrorActionPreference = 'Stop'
        try { $app = [Runtime.InteropServices.Marshal]::GetActiveObject('OneNote.Application') }
        catch { $app = New-Object -ComObject 'OneNote.Application' }

        Add-Type -TypeDefinition "using System; using System.Runtime.InteropServices; public static class Win32 { [DllImport(`"user32.dll`")] public static extern bool SetForegroundWindow(IntPtr hWnd); [DllImport(`"user32.dll`")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); }"

        while ($true) {
            $line = [Console]::ReadLine()
            if ($null -eq $line) { break }
            try {
                $req = $line | ConvertFrom-Json
                if ($req.action -eq 'findPages') {
                    $xml = ''
                    # By default wrap in quotes for exact-phrase matching (same as Windows Search
                    # CONTAINS syntax). Without quotes OneNote tokenises on ://, ., / etc. and
                    # misses URL matches. Callers can pass exact=false to use the tokenised form
                    # for a last-resort fuzzy fallback (e.g. domain core "onemoreaddin" matching a
                    # OneNote page that contains the string as part of a CamelCase word).
                    $exact = $true
                    if ($req.PSObject.Properties.Name -contains 'exact') { $exact = [bool]$req.exact }
                    $term = if ($exact) { '"' + $req.query.Replace('"', '""') + '"' } else { $req.query }
                    $app.FindPages('', $term, [ref]$xml)
                    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($xml))
                    Write-Output "ok:$b64"
                } elseif ($req.action -eq 'navigateTo') {
                    $app.NavigateTo($req.pageId, '')
                    Write-Output 'ok:'
                } elseif ($req.action -eq 'focusAndSearch') {
                    $app.NavigateTo($req.pageId, '')
                    Start-Sleep -Milliseconds 800
                    $proc = Get-Process -Name 'ONENOTE' -ErrorAction SilentlyContinue |
                            Select-Object -First 1
                    if ($proc -and $proc.MainWindowHandle -ne 0) {
                        $hwnd = $proc.MainWindowHandle
                        [void][Win32]::ShowWindow($hwnd, 9)
                        [void][Win32]::SetForegroundWindow($hwnd)
                        Start-Sleep -Milliseconds 400
                        $shell = New-Object -ComObject 'WScript.Shell'
                        $shell.SendKeys('^f')
                        Start-Sleep -Milliseconds 300
                        $shell.SendKeys('"' + $req.query.Replace('"', '""') + '"')
                    }
                    Write-Output 'ok:'
                } elseif ($req.action -eq 'openSearch') {
                    $proc = Get-Process -Name 'ONENOTE' -ErrorAction SilentlyContinue |
                            Select-Object -First 1
                    if ($proc -and $proc.MainWindowHandle -ne 0) {
                        $hwnd = $proc.MainWindowHandle
                        [void][Win32]::ShowWindow($hwnd, 9)          # SW_RESTORE
                        [void][Win32]::SetForegroundWindow($hwnd)
                        Start-Sleep -Milliseconds 600
                        $shell = New-Object -ComObject 'WScript.Shell'
                        $shell.SendKeys('^e')                        # open OneNote search
                        Start-Sleep -Milliseconds 400
                        $shell.SendKeys($req.query)
                    }
                    Write-Output 'ok:'
                } elseif ($req.action -eq 'appendBullet') {
                    $proc = Get-Process -Name 'ONENOTE' -ErrorAction SilentlyContinue |
                            Select-Object -First 1
                    if ($proc -and $proc.MainWindowHandle -ne 0) {
                        $hwnd = $proc.MainWindowHandle
                        [void][Win32]::ShowWindow($hwnd, 9)          # SW_RESTORE
                        [void][Win32]::SetForegroundWindow($hwnd)
                        Start-Sleep -Milliseconds 600
                        # SendKeys: { } first (their own escape), then the other specials.
                        $escape = {
                            param($s)
                            $s = $s -replace '\{', '{{}'
                            $s = $s -replace '\}', '{}}'
                            $s = $s -replace '([+^%~()\[\]])', '{$1}'
                            return $s
                        }
                        $shell = New-Object -ComObject 'WScript.Shell'
                        $shell.SendKeys((& $escape $req.text))
                        Start-Sleep -Milliseconds 100
                        $shell.SendKeys('{ENTER}{TAB}')
                        Start-Sleep -Milliseconds 100
                        $shell.SendKeys((& $escape $req.url))
                    }
                    Write-Output 'ok:'
                } elseif ($req.action -eq 'listPages') {
                    $xml = ''
                    $app.GetHierarchy('', 4, [ref]$xml)              # 4 = hsPages
                    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($xml))
                    Write-Output "ok:$b64"
                } elseif ($req.action -eq 'listNotebooks') {
                    $xml = ''
                    $app.GetHierarchy('', 3, [ref]$xml)              # 3 = hsSections
                    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($xml))
                    Write-Output "ok:$b64"
                } elseif ($req.action -eq 'addPage') {
                    $pageId = ''
                    $app.CreateNewPage($req.sectionId, [ref]$pageId, 0)
                    $ns       = 'http://schemas.microsoft.com/office/onenote/2013/onenote'
                    $safeId   = [System.Security.SecurityElement]::Escape($pageId)
                    $safeTitle = $req.title -replace ']]>', ']]]]><![CDATA[>'
                    $safeUrl   = $req.url   -replace ']]>', ']]]]><![CDATA[>'
                    $pageXml  = '<?xml version="1.0" encoding="utf-8"?>' +
                                '<one:Page xmlns:one="' + $ns + '" ID="' + $safeId + '">' +
                                '<one:Title><one:OE><one:T><![CDATA[' + $safeTitle + ']]></one:T></one:OE></one:Title>' +
                                '<one:Outline><one:OEChildren><one:OE>' +
                                '<one:T><![CDATA[' + $safeUrl + ']]></one:T>' +
                                '</one:OE></one:OEChildren></one:Outline></one:Page>'
                    $app.UpdatePageContent($pageXml, [DateTime]::MinValue, 45, $false)
                    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($pageId))
                    Write-Output "ok:$b64"
                } elseif ($req.action -eq 'resolveLink') {
                    # The page an onenote: link names: the section by its path (or the
                    # section-id), then the page whose own hyperlink carries the page-id.
                    $ns  = 'http://schemas.microsoft.com/office/onenote/2013/onenote'
                    $xml = ''
                    $app.GetHierarchy('', 3, [ref]$xml)              # 3 = hsSections
                    $doc = [xml]$xml
                    $nsm = New-Object Xml.XmlNamespaceManager($doc.NameTable)
                    $nsm.AddNamespace('one', $ns)
                    $found = ''
                    foreach ($sec in $doc.SelectNodes('//one:Section', $nsm)) {
                        if ($found) { break }
                        if ($sec.isInRecycleBin -eq 'true') { continue }
                        $path = [Uri]::UnescapeDataString([string]$sec.path)
                        $byId = $req.sectionGuid -and ([string]$sec.ID).ToLower().StartsWith('{' + $req.sectionGuid + '}')
                        if (-not $byId -and $path -ne $req.sectionPath) { continue }
                        $px = ''
                        $app.GetHierarchy($sec.ID, 4, [ref]$px)      # 4 = hsPages
                        $pdoc = [xml]$px
                        $pnsm = New-Object Xml.XmlNamespaceManager($pdoc.NameTable)
                        $pnsm.AddNamespace('one', $ns)
                        foreach ($p in $pdoc.SelectNodes('//one:Page', $pnsm)) {
                            if ($p.isInRecycleBin -eq 'true') { continue }
                            $link = ''
                            $app.GetHyperlinkToObject($p.ID, '', [ref]$link)
                            if ($link.ToLower().Contains('page-id={' + $req.pageGuid + '}')) { $found = $p.ID; break }
                        }
                    }
                    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($found))
                    Write-Output "ok:$b64"
                } elseif ($req.action -eq 'getPage') {
                    $xml = ''
                    $app.GetPageContent($req.pageId, [ref]$xml, 0, 2) # piBasic, xs2013
                    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($xml))
                    Write-Output "ok:$b64"
                } elseif ($req.action -eq 'updatePage') {
                    $app.UpdatePageContent($req.xml, [DateTime]::MinValue, 2, $false)
                    Write-Output 'ok:'
                } elseif ($req.action -eq 'deleteContent') {
                    $app.DeletePageContent($req.pageId, $req.objectId, [DateTime]::MinValue, $false)
                    Write-Output 'ok:'
                } else {
                    Write-Output 'err:unknown action'
                }
            } catch {
                $msg = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_.ToString()))
                Write-Output "err:$msg"
            }
        }
        """;

    public OneNoteClient()
    {
        // Write the PS script to a temp file so we don't have to deal with
        // -Command vs -File stdin semantics or BOM issues.
        var scriptPath = Path.Combine(Path.GetTempPath(),
            $"AnchorHub_{Environment.ProcessId}.ps1");
        File.WriteAllText(scriptPath, PsScript, new UTF8Encoding(false));

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -File \"{scriptPath}\"")
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = false,
            UseShellExecute = false,
            CreateNoWindow  = true,
            StandardInputEncoding  = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        _ps = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start PowerShell process.");

        // Script file is read immediately at startup; clean up after a short delay.
        Task.Delay(2000).ContinueWith(_ =>
        {
            try { File.Delete(scriptPath); } catch { /* best effort */ }
        });
    }

    /// <summary>
    /// Returns all notebooks and their sections.
    /// </summary>
    public IReadOnlyList<NotebookInfo> ListNotebooks()
    {
        var xml = SendRequest(new { action = "listNotebooks" });
        return ParseNotebooksXml(xml);
    }

    /// <summary>
    /// Creates a new page in <paramref name="sectionId"/> with <paramref name="title"/>
    /// and a line containing <paramref name="url"/>. Returns the new page ID.
    /// </summary>
    public string AddPage(string sectionId, string title, string url)
        => SendRequest(new { action = "addPage", sectionId, title, url });

    /// <summary>
    /// Searches for pages whose text contains <paramref name="query"/> and
    /// returns structured matches. OneMore's local full-text index is asked
    /// first; when it is absent or has no hit, OneNote's own search is used
    /// (it also sees pages OneMore has not indexed yet). For OneNote search,
    /// <paramref name="exact"/> = true sends the query quoted (phrase match);
    /// otherwise OneNote's tokenised search is used.
    /// </summary>
    public IReadOnlyList<PageMatch> FindPages(string query, bool exact = true)
    {
        var ids = _oneMore.FindPageIds(query);
        if (ids.Count > 0)
        {
            var hits = ResolvePages(ids);
            if (hits.Count > 0) return hits;
        }
        var xml = SendRequest(new { action = "findPages", query, exact });
        return ParseXml(xml);
    }

    // OneMore's installer records whether indexing starts enabled; OneMore reads
    // the same value when Settings.xml has no explicit choice.
    private static bool OneMoreInstalledDefault()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\River\OneMoreAddIn");
            return key?.GetValue("TextIndex") as string == "1";
        }
        catch (Exception) { return false; }
    }

    // Maps index page IDs to current pages (names from the live hierarchy).
    // IDs OneNote no longer knows — OneNote reissues them when a notebook is
    // reopened, until OneMore catches up — are dropped.
    private List<PageMatch> ResolvePages(IReadOnlyList<string> ids)
    {
        // ponytail: whole-hierarchy reload, at most once a minute on an unknown ID
        if (_pages is null ||
            (ids.Any(id => !_pages.ContainsKey(id)) &&
             DateTime.UtcNow - _pagesLoadedAt > TimeSpan.FromMinutes(1)))
        {
            _pages = new Dictionary<string, PageMatch>();
            foreach (var p in ParseXml(SendRequest(new { action = "listPages" })))
                _pages[p.PageId] = p;
            _pagesLoadedAt = DateTime.UtcNow;
        }
        return ids.Where(_pages.ContainsKey).Select(id => _pages[id]).ToList();
    }

    /// <summary>The ID of the page an <c>onenote:</c> link names, or null when OneNote does not know it.</summary>
    public string? ResolveLink(OneNoteLinkParts parts)
    {
        if (parts.PageGuid is null) return null;
        var id = SendRequest(new { action = "resolveLink", sectionPath = parts.SectionPath, sectionGuid = parts.SectionGuid, pageGuid = parts.PageGuid });
        return id.Length > 0 ? id : null;
    }

    /// <summary>The page XML (2013 schema, without binary data), or null when the page is gone.</summary>
    public string? TryGetPage(string pageId)
    {
        try { return SendRequest(new { action = "getPage", pageId }); }
        catch (InvalidOperationException) { return null; }
    }

    public void UpdatePage(string xml) => SendRequest(new { action = "updatePage", xml });

    public void DeleteContent(string pageId, string objectId) => SendRequest(new { action = "deleteContent", pageId, objectId });

    /// <summary>
    /// Opens the specified page in the OneNote desktop app.
    /// </summary>
    public void NavigateTo(string pageId)
    {
        SendRequest(new { action = "navigateTo", pageId });
    }

    /// <summary>
    /// Navigates to <paramref name="pageId"/>, then opens the find-on-page bar
    /// (Ctrl+F) pre-filled with <paramref name="query"/> so the user can see
    /// exactly where the URL appears on the page.
    /// </summary>
    public void FocusAndSearch(string pageId, string query)
    {
        SendRequest(new { action = "focusAndSearch", pageId, query });
    }

    /// <summary>
    /// Brings OneNote to the foreground and opens its search box pre-filled with
    /// <paramref name="query"/> (Ctrl+E).
    /// </summary>
    public void OpenSearch(string query)
    {
        SendRequest(new { action = "openSearch", query });
    }

    /// <summary>
    /// Brings OneNote to the foreground and types
    /// <paramref name="text"/>, then Enter+Tab, then <paramref name="url"/> at
    /// the current caret. The user is responsible for placing the caret on
    /// the desired OneNote page beforehand.
    /// </summary>
    public void AppendBullet(string text, string url)
    {
        SendRequest(new { action = "appendBullet", text, url });
    }

    // ── IPC ──────────────────────────────────────────────────────────────────

    private string SendRequest(object request)
    {
        var json = JsonSerializer.Serialize(request);
        _ps.StandardInput.WriteLine(json);
        _ps.StandardInput.Flush();

        var response = _ps.StandardOutput.ReadLine()
            ?? throw new InvalidOperationException("PowerShell process closed unexpectedly.");

        if (response.StartsWith("err:", StringComparison.Ordinal))
        {
            var errB64 = response[4..];
            var errMsg = errB64.Length > 0
                ? Encoding.UTF8.GetString(Convert.FromBase64String(errB64))
                : "unknown error";
            throw new InvalidOperationException($"OneNote COM error: {errMsg}");
        }

        // "ok:<base64-payload>" — payload is empty string for void methods
        var b64 = response[3..];
        return b64.Length > 0
            ? Encoding.UTF8.GetString(Convert.FromBase64String(b64))
            : "";
    }

    // ── Notebook XML parsing ─────────────────────────────────────────────────

    private static IReadOnlyList<NotebookInfo> ParseNotebooksXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];
        var doc = XDocument.Parse(xml);
        var ns  = doc.Root?.Name.Namespace ?? XNamespace.None;
        var results = new List<NotebookInfo>();
        foreach (var nb in doc.Root?.Elements(ns + "Notebook") ?? [])
        {
            var sections = new List<SectionInfo>();
            CollectSections(nb, sections, ns);
            results.Add(new NotebookInfo(
                nb.Attribute("ID")?.Value   ?? "",
                nb.Attribute("name")?.Value ?? "",
                sections));
        }
        return results;
    }

    private static void CollectSections(XElement node, List<SectionInfo> sections, XNamespace ns)
    {
        foreach (var s in node.Elements(ns + "Section"))
            sections.Add(new SectionInfo(
                s.Attribute("ID")?.Value   ?? "",
                s.Attribute("name")?.Value ?? ""));
        foreach (var sg in node.Elements(ns + "SectionGroup"))
            CollectSections(sg, sections, ns);
    }

    // ── Page XML parsing ──────────────────────────────────────────────────────

    private static IReadOnlyList<PageMatch> ParseXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];

        var doc = XDocument.Parse(xml);
        var ns  = doc.Root?.Name.Namespace ?? XNamespace.None;

        var results = new List<PageMatch>();
        foreach (var nb in doc.Root?.Elements(ns + "Notebook") ?? [])
            CollectPages(nb, nb.Attribute("name")?.Value ?? "", results, ns);

        return results;
    }

    private static void CollectPages(XElement node, string notebookName,
        List<PageMatch> results, XNamespace ns)
    {
        foreach (var section in node.Elements(ns + "Section"))
        {
            var sectionName = section.Attribute("name")?.Value ?? "";
            foreach (var page in section.Elements(ns + "Page"))
                results.Add(new PageMatch(
                    PageId:       page.Attribute("ID")?.Value   ?? "",
                    Title:        page.Attribute("name")?.Value ?? "(untitled)",
                    SectionName:  sectionName,
                    NotebookName: notebookName));
        }
        foreach (var sg in node.Elements(ns + "SectionGroup"))
            CollectPages(sg, notebookName, results, ns);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _ps.StandardInput.Close(); } catch { /* best effort */ }
        try { _ps.Kill(); }              catch { /* best effort */ }
        _ps.Dispose();
    }
}
