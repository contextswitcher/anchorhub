using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnchorHub.Core;

namespace AnchorHub.Hub.Outlook;

// Classic Outlook over COM, through a one-shot powershell.exe per call — the
// same Click-to-Run type-library workaround as OneNoteClient. The request is
// spliced into the script as Base64, so its content can never become code.
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
        try {
            $req = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__REQ__')) | ConvertFrom-Json
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

    private static string Run(object request)
    {
        var req    = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request));
        var script = PsScript.Replace("__REQ__", req);
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        {
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        using var ps = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start PowerShell process.");
        var line = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit();

        var ok = line.StartsWith("ok:", StringComparison.Ordinal);
        if (!ok && !line.StartsWith("err:", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected PowerShell output: {line}");
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(line[(ok ? 3 : 4)..]));
        return ok ? payload : throw new InvalidOperationException($"Outlook COM error: {payload}");
    }
}
