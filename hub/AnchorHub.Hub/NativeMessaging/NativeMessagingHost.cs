using System.Net;
using AnchorHub.Core;
using AnchorHub.Hub.Http;

namespace AnchorHub.Hub.NativeMessaging;

internal static class NativeMessagingHost
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AnchorHub", "logs", $"host-{DateTime.UtcNow:yyyyMMdd}.log");

    private static void Log(string msg)
    {
        Console.Error.WriteLine($"[ONL] {DateTime.UtcNow:o} {msg}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.UtcNow:o} {msg}\n");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[ONL] log-write failed: {ex.Message}"); }
    }

    public static Task<int> RunAsync() =>
        RunAsync(httpEndpoint: null, fulltextToken: null, fulltextTokenFilePath: null);

    public static async Task<int> RunAsync(
        IPEndPoint? httpEndpoint,
        string?     fulltextToken,
        string?     fulltextTokenFilePath)
    {
        Log("NativeMessagingHost starting.");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // OneNote COM bridge is best-effort: when OneNote isn't installed,
        // not running, or the PowerShell COM layer can't attach, the daemon
        // still needs to serve adapter-independent actions (graphSignIn).
        // On non-Windows builds the COM bridge doesn't exist at all — the
        // backend stays null and every OneNote-touching action returns
        // DISCONNECTED, which is exactly what the JabRef-fulltext + Graph
        // use case on macOS/Linux needs.
        IOneNoteBackend? backend = null;
#if WINDOWS
        try { backend = new OneNoteClient(); }
        catch (Exception ex) { Log($"OneNote backend unavailable: {ex.Message}. COM-backed actions will return DISCONNECTED."); }
#else
        Log("Non-Windows build: OneNote backend disabled; serving JabRef fulltext + Graph actions only.");
#endif

        var protocol  = MessageProtocol.Stdio();
        var writeLock = new SemaphoreSlim(1, 1);
        var dispatcher = new StdioExtensionDispatcher(protocol, writeLock);

        Task<int>? httpTask = null;
        var discoveryWritten = false;
        if (httpEndpoint is not null)
        {
            var realms = new Dictionary<string, IHttpRealm>();
            if (fulltextToken is not null)
            {
                var fulltextRealm = new FulltextRealm(fulltextToken, dispatcher);
                realms["/v1/fulltext"] = fulltextRealm;
                realms["/v1/health"]   = fulltextRealm;
            }
            var host = new HttpListenerHost(httpEndpoint, realms);
            httpTask = Task.Run(() => host.RunAsync(cts.Token));
            Log($"HTTP listener started on {httpEndpoint}; {realms.Count} realm prefix(es) registered.");

            // Advertise the fulltext provider to JabRef's discovery dir.
            // Only when the fulltext realm itself is enabled.
            if (fulltextToken is not null && fulltextTokenFilePath is not null)
            {
                try
                {
                    JabRefProviderRegistration.Write(httpEndpoint.Port, fulltextTokenFilePath);
                    discoveryWritten = true;
                    Log($"Wrote JabRef discovery file: {JabRefProviderRegistration.DiscoveryFilePath}");
                }
                catch (Exception ex)
                {
                    Log($"Failed to write JabRef discovery file: {ex.Message}");
                }
            }
        }

        try
        {
            using var _backend = backend as IDisposable;
            using var handler  = new MessageHandler(backend);
            Log("Ready. Entering message loop.");

            while (!cts.IsCancellationRequested)
            {
                var msg = await protocol.ReadMessageAsync(cts.Token);
                if (msg is null) { Log("EOF — extension disconnected."); break; }

                // Discriminate: ext-initiated requests carry an "action"
                // field. Responses to hub-initiated requests (e.g.
                // fetchFulltext) have only a "requestId" and result fields.
                if (msg.Value.TryGetProperty("action", out var actionEl))
                {
                    var actionName = actionEl.GetString();
                    Log($"→ action={actionName}");
                    var response = await handler.HandleAsync(msg.Value, cts.Token);
                    await WriteAsync(protocol, writeLock, response, cts.Token);
                    Log("← response sent.");
                }
                else if (msg.Value.TryGetProperty("requestId", out var ridEl) &&
                         ridEl.GetString() is { } requestId)
                {
                    dispatcher.CompleteResponse(requestId, msg.Value);
                    Log($"↩ response for {requestId} routed to dispatcher.");
                }
                else
                {
                    Log("Dropping malformed message (no action, no requestId).");
                }
            }
        }
        catch (Exception ex)
        {
            Log("Fatal: " + ex);
            return 2;
        }
        finally
        {
            cts.Cancel();
            if (httpTask is not null)
            {
                try { await httpTask; }
                catch (OperationCanceledException) { /* expected */ }
                catch (Exception ex) { Log($"HTTP listener shutdown error: {ex.Message}"); }
            }
            if (discoveryWritten)
            {
                JabRefProviderRegistration.Remove();
                Log("Removed JabRef discovery file.");
            }
        }

        Log("NativeMessagingHost exiting normally.");
        return 0;
    }

    private static async Task WriteAsync(MessageProtocol protocol, SemaphoreSlim writeLock, object payload, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await protocol.WriteMessageAsync(payload, ct);
        }
        finally
        {
            writeLock.Release();
        }
    }
}
