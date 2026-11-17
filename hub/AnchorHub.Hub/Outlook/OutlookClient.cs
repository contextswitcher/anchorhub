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
        function Out($p, $s) { Write-Output ($p + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s))) }
        try {
            $req = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__REQ__')) | ConvertFrom-Json
            if ($req.action -eq 'capture') {
                try { $ol = [Runtime.InteropServices.Marshal]::GetActiveObject('Outlook.Application') }
                catch { throw 'Outlook is not running.' }
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
                $item = if ($req.storeId) { $ol.Session.GetItemFromID($req.entryId, $req.storeId) }
                        else              { $ol.Session.GetItemFromID($req.entryId) }
                $item.Display()
                $item.GetInspector.Activate()
                Out 'ok:' ''
            } else { throw 'unknown action' }
        } catch { Out 'err:' $_.ToString() }
        """;

    /// <summary>The selected (or open) Outlook item as JSON, with its anchor URI added.</summary>
    public static JsonObject Capture()
    {
        var item = JsonNode.Parse(Run(new { action = "capture" }))!.AsObject();
        item["anchor"] = new OutlookAnchor(
            (string)item["entryId"]!, (string?)item["storeId"], (string?)item["subject"]).ToUri();
        return item;
    }

    public static void Open(OutlookAnchor anchor) =>
        Run(new { action = "open", entryId = anchor.EntryId, storeId = anchor.StoreId });

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
