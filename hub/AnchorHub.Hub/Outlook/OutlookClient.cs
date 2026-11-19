using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnchorHub.Core;

namespace AnchorHub.Hub.Outlook;

// Classic Outlook over COM, through a one-shot powershell.exe per call — the
// same Click-to-Run type-library workaround as OneNoteClient. The request goes
// to the script's stdin as one Base64 line: its content can never become code,
// and a large request (many calendar blocks) does not run into the 32k limit
// of the command line.
[SupportedOSPlatform("windows")]
internal static class OutlookClient
{
    private const string PsScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        function Out($p, $s) { Write-Output ($p + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s))) }
        function Running {
            try { [Runtime.InteropServices.Marshal]::GetActiveObject('Outlook.Application') }
            catch { throw 'Outlook is not running.' }
        }
        # Without a StoreID, the default store is tried first, then every other open store.
        function Resolve($ol, $entryId, $storeId) {
            if ($storeId) { return $ol.Session.GetItemFromID($entryId, $storeId) }
            try { return $ol.Session.GetItemFromID($entryId) } catch {}
            foreach ($store in $ol.Session.Stores) {
                try { return $ol.Session.GetItemFromID($entryId, $store.StoreID) } catch {}
            }
            throw 'No item with this EntryID in any open store.'
        }
        # The default calendar (olFolderCalendar) of the default store, or of the one store with this display name.
        function Calendar($ol, $name) {
            if (-not $name) { return $ol.Session.GetDefaultFolder(9) }
            $found = @()
            foreach ($store in $ol.Session.Stores) { if ([string]::Equals([string]$store.DisplayName, [string]$name)) { $found += $store } }
            if ($found.Count -eq 0) { throw "No store has the display name '$name'." }
            if ($found.Count -gt 1) { throw "More than one store has the display name '$name'." }
            return $found[0].GetDefaultFolder(9)
        }
        # Same predicate as KoppterBlocks.IsOwn in AnchorHub.Core; checked again here right before a Delete().
        function HasKoppterCategory($c) {
            if (-not $c) { return $false }
            foreach ($p in ([string]$c -split '[,;]')) { if ($p.Trim() -ieq 'koppter') { return $true } }
            return $false
        }
        function BlockId($it) {
            try { $p = $it.UserProperties.Find('KoppterBlockId'); if ($null -ne $p) { return [string]$p.Value } } catch {}
            return $null
        }
        function IsOwn($it) {
            if ($it.Class -ne 26) { return $false }                                    # olAppointment
            if ([string]::IsNullOrWhiteSpace((BlockId $it))) { return $false }
            if (-not (HasKoppterCategory $it.Categories)) { return $false }
            if ($it.IsRecurring) { return $false }
            return ($it.MeetingStatus -eq 0)                                            # olNonMeeting
        }
        function Fmt($d) { $d.ToString('yyyy-MM-ddTHH:mm', [Globalization.CultureInfo]::InvariantCulture) }
        function ParseTime($s) { [DateTime]::ParseExact($s, 'yyyy-MM-ddTHH:mm', [Globalization.CultureInfo]::InvariantCulture) }
        function Row($it) {
            [ordered]@{
                entryId       = $it.EntryID
                blockId       = (BlockId $it)
                categories    = $it.Categories
                isRecurring   = [bool]$it.IsRecurring
                meetingStatus = [int]$it.MeetingStatus
                start         = (Fmt $it.Start)
                end           = (Fmt $it.End)
                subject       = $it.Subject
                allDay        = [bool]$it.AllDayEvent
                busyStatus    = [int]$it.BusyStatus
            }
        }
        function Listed($list, $it, $onlyMarked) {
            try {
                if ($it.Class -eq 26 -and (-not $onlyMarked -or (HasKoppterCategory $it.Categories))) { [void]$list.Add((Row $it)) }
            } catch {}
        }
        try {
            $req = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Console]::In.ReadLine())) | ConvertFrom-Json
            if ($req.action -eq 'capture') {
                $ol = Running
                $win = $ol.ActiveWindow()
                $item = $null
                if ($win -and $win.Class -eq 35) { $item = $win.CurrentItem }                 # olInspector
                if (-not $item) {
                    $exp = $ol.ActiveExplorer()
                    if ($exp -and $exp.Selection.Count -gt 0) { $item = $exp.Selection.Item(1) }
                }
                if (-not $item) { throw 'No Outlook item is selected or open.' }
                if (-not $item.EntryID) { throw 'The item is not saved yet (no EntryID).' }
                $sender = $null; $received = $null
                try { $sender = $item.SenderName } catch {}
                try { $received = $item.ReceivedTime.ToString('o') } catch {}
                Out 'ok:' (@{
                    entryId  = $item.EntryID
                    storeId  = $item.Parent.StoreID
                    subject  = $item.Subject
                    sender   = $sender
                    received = $received
                } | ConvertTo-Json -Compress)
            } elseif ($req.action -eq 'open') {
                try { $ol = [Runtime.InteropServices.Marshal]::GetActiveObject('Outlook.Application') }
                catch { $ol = New-Object -ComObject 'Outlook.Application' }
                $item = Resolve $ol $req.entryId $req.storeId
                $item.Display()
                $item.GetInspector.Activate()
                Out 'ok:' ''
            } elseif ($req.action -eq 'complete') {
                $ol = Running
                $item = Resolve $ol $req.entryId $req.storeId
                if ($item.Class -eq 48) { $item.MarkComplete() }                      # olTask
                elseif ($item.IsMarkedAsTask) { $item.FlagStatus = 1 }                  # olFlagComplete
                else { throw 'The item is neither a task nor flagged.' }
                $item.Save()
                Out 'ok:' ''
            } elseif ($req.action -eq 'todos') {
                $ol = Running
                $list = New-Object System.Collections.ArrayList
                foreach ($store in $ol.Session.Stores) {
                    try { $folder = $store.GetDefaultFolder(28) } catch { continue }   # olFolderToDo
                    if (-not $folder) { continue }
                    $storeId = $store.StoreID; $storeName = $store.DisplayName
                    foreach ($it in $folder.Items) {
                        try {
                            $cls = $it.Class
                            $due = $null; $start = $null; $received = $null; $sender = $null
                            if ($cls -eq 48) {                                              # olTask
                                if ($it.Complete) { continue }
                                $kind = 'task'; $due = $it.DueDate; $start = $it.StartDate
                            } else {
                                if ($it.FlagStatus -eq 1) { continue }                      # olFlagComplete
                                $kind = if ($cls -eq 43) { 'mail' } else { 'other' }        # olMail
                                $due = $it.TaskDueDate; $start = $it.TaskStartDate
                                try { $received = $it.ReceivedTime.ToString('o') } catch {}
                                try { $sender = $it.SenderName } catch {}
                            }
                            [void]$list.Add([ordered]@{
                                entryId  = $it.EntryID
                                storeId  = $storeId
                                store    = $storeName
                                kind     = $kind
                                subject  = $it.Subject
                                due      = if ($due)   { $due.ToString('yyyy-MM-dd') }   else { $null }
                                start    = if ($start) { $start.ToString('yyyy-MM-dd') } else { $null }
                                received = $received
                                sender   = $sender
                            })
                        } catch {}
                    }
                }
                Out 'ok:' (ConvertTo-Json -InputObject @($list) -Compress -Depth 3)
            } elseif ($req.action -eq 'calendar-list') {
                $ol = Running
                $folder = Calendar $ol $req.calendar
                $list = New-Object System.Collections.ArrayList
                $items = $folder.Items
                if ($req.from) {
                    $items.Sort('[Start]')
                    $items.IncludeRecurrences = [bool]$req.recurrences
                    # Restrict takes dates in the user's locale; the window is widened by a
                    # day so that C# applies the exact window.
                    $from = (ParseTime $req.from).AddDays(-1).ToString('g')
                    $to   = (ParseTime $req.to).AddDays(1).ToString('g')
                    $r = $items.Restrict("[Start] < '$to' AND [End] > '$from'")
                    # With recurrences, Count is unreliable; GetFirst/GetNext is the documented way.
                    $it = $r.GetFirst()
                    while ($null -ne $it) { Listed $list $it $req.onlyMarked; $it = $r.GetNext() }
                } else {
                    foreach ($it in $items) { Listed $list $it $req.onlyMarked }
                }
                Out 'ok:' (ConvertTo-Json -InputObject @($list) -Compress -Depth 3)
            } elseif ($req.action -eq 'calendar-apply') {
                $ol = Running
                $folder = Calendar $ol $req.calendar
                $folderId = $folder.EntryID; $storeId = $folder.StoreID
                $deleted = New-Object System.Collections.ArrayList
                $errors = New-Object System.Collections.ArrayList
                $skipped = 0; $created = 0
                foreach ($id in $req.deletes) {
                    $it = $null
                    try { $it = $ol.Session.GetItemFromID($id, $storeId) } catch {}
                    try {
                        if ($null -eq $it -or -not (IsOwn $it) -or $it.Parent.EntryID -ne $folderId) { $skipped++; continue }
                        $row = [ordered]@{ entryId = $it.EntryID; start = (Fmt $it.Start); subject = $it.Subject }
                        $it.Delete()
                        [void]$deleted.Add($row)
                    } catch { [void]$errors.Add("delete ${id}: " + $_.ToString()) }
                }
                foreach ($b in $req.creates) {
                    try {
                        $a = $folder.Items.Add(1)                                          # olAppointmentItem
                        $a.Subject = $b.subject
                        $a.Start = ParseTime $b.start
                        $a.End = ParseTime $b.end
                        $a.Body = $b.body
                        $a.BusyStatus = 1                                                  # olTentative
                        $a.Sensitivity = 2                                                 # olPrivate
                        $a.ReminderSet = $false
                        $a.Categories = 'koppter'
                        $p = $a.UserProperties.Add('KoppterBlockId', 1, $false)            # olText, no folder field
                        $p.Value = $b.id
                        $a.Save()
                        $created++
                    } catch { [void]$errors.Add("create $($b.id): " + $_.ToString()) }
                }
                Out 'ok:' (ConvertTo-Json -Compress -Depth 3 -InputObject ([ordered]@{
                    deleted = @($deleted); skipped = $skipped; created = $created; errors = @($errors)
                }))
            } else { throw 'unknown action' }
        } catch { Out 'err:' $_.ToString() }
        """;

    /// <summary>The selected (or open) Outlook item as JSON, with its anchor URI added.</summary>
    // [impl->req~ank.outlook.capture~1]
    public static JsonObject Capture()
    {
        var item = JsonNode.Parse(Run(new { action = "capture" }))!.AsObject();
        item["anchor"] = new OutlookAnchor(
            (string)item["entryId"]!, (string?)item["storeId"], (string?)item["subject"]).ToUri();
        return item;
    }

    // [impl->req~ank.outlook.open~1]
    public static void Open(OutlookAnchor anchor) =>
        Run(new { action = "open", entryId = anchor.EntryId, storeId = anchor.StoreId });

    /// <summary>The open items of the To-Do List of every store, shaped by <see cref="OutlookTodos"/>.</summary>
    // [impl->req~ank.outlook.todos~1]
    public static JsonArray Todos() => OutlookTodos.Shape(JsonNode.Parse(Run(new { action = "todos" })));

    // [impl->req~ank.outlook.complete~1]
    public static void Complete(OutlookAnchor anchor) =>
        Run(new { action = "complete", entryId = anchor.EntryId, storeId = anchor.StoreId });

    /// <summary>
    /// The appointments of the target calendar: with <paramref name="from"/> and
    /// <paramref name="to"/>, those around that window (a day wider; callers
    /// apply the exact window), otherwise the whole folder. With
    /// <paramref name="onlyMarked"/>, only items with the koppter category.
    /// </summary>
    public static IReadOnlyList<CalendarItem> CalendarItems(
        string? calendar, DateTime? from, DateTime? to, bool recurrences, bool onlyMarked) =>
        KoppterBlocks.ParseItems(JsonNode.Parse(Run(new
        {
            action = "calendar-list",
            calendar,
            from = from is { } f ? KoppterBlocks.FormatTime(f) : null,
            to = to is { } t ? KoppterBlocks.FormatTime(t) : null,
            recurrences,
            onlyMarked,
        })));

    /// <summary>The outcome of <see cref="Apply"/>.</summary>
    public sealed record ApplyResult(int Deleted, int Skipped, int Created);

    /// <summary>
    /// Deletes the items with these EntryIDs — each only when the script, right
    /// before the Delete(), still finds it own and in the target calendar;
    /// otherwise it is skipped — and creates the blocks. Every deletion is logged.
    /// Throws after logging when a delete or create failed.
    /// </summary>
    // [impl->req~ank.outlook.blocks-sync~1]
    // [impl->req~ank.outlook.blocks-clear~1]
    public static ApplyResult Apply(string? calendar, IEnumerable<string> deletes, IEnumerable<KoppterBlock> creates)
    {
        var result = JsonNode.Parse(Run(new
        {
            action = "calendar-apply",
            calendar,
            deletes = deletes.ToArray(),
            creates = creates.Select(b => new
            {
                id = b.Id,
                start = KoppterBlocks.FormatTime(b.Start),
                end = KoppterBlocks.FormatTime(b.End),
                subject = b.Subject,
                body = b.Body,
            }).ToArray(),
        }))!.AsObject();
        var deleted = result["deleted"] as JsonArray ?? [];
        foreach (var d in deleted)
            Log($"outlook-blocks deleted entryId={(string?)d?["entryId"]} start={(string?)d?["start"]} subject={JsonSerializer.Serialize((string?)d?["subject"])}");
        var errors = (result["errors"] as JsonArray ?? []).Select(e => (string?)e).ToList();
        var created = (int?)result["created"] ?? 0;
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Outlook COM error after {deleted.Count} deleted and {created} created: {string.Join("; ", errors)}");
        return new ApplyResult(deleted.Count, (int?)result["skipped"] ?? 0, created);
    }

    // The same daily file as the native-messaging host, so a deletion can be traced.
    private static void Log(string message)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AnchorHub", "logs", $"host-{DateTime.UtcNow:yyyyMMdd}.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.UtcNow:o} {message}\n");
        }
        catch (Exception ex) { Console.Error.WriteLine($"log-write failed: {ex.Message}"); }
    }

    private static string Run(object request)
    {
        var req = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request));
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(PsScript)))
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        using var ps = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start PowerShell process.");
        ps.StandardInput.WriteLine(req);
        ps.StandardInput.Close();
        var line = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit();

        var ok = line.StartsWith("ok:", StringComparison.Ordinal);
        if (!ok && !line.StartsWith("err:", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected PowerShell output: {line}");
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(line[(ok ? 3 : 4)..]));
        return ok ? payload : throw new InvalidOperationException($"Outlook COM error: {payload}");
    }
}
