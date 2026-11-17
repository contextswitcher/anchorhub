using System.Net;
using System.Net.Sockets;
using AnchorHub.Core;

namespace AnchorHub.Hub.NativeMessaging;

/// <summary>
/// Listen mode: serve the same MessageHandler over TCP that the stdio
/// host serves over Firefox native messaging. Used inside a Windows VM
/// when the Firefox-side daemon runs on Linux and forwards every
/// framed message over a localhost-forwarded port. Single connection
/// at a time — same backend instance is reused across reconnections.
/// </summary>
internal static class TcpListenerHost
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AnchorHub", "logs", $"host-{DateTime.UtcNow:yyyyMMdd}.log");

    private static void Log(string msg)
    {
        Console.Error.WriteLine($"[TCP] {DateTime.UtcNow:o} {msg}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.UtcNow:o} [TCP] {msg}\n");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[TCP] log-write failed: {ex.Message}"); }
    }

    /// <summary>
    /// Serve <paramref name="backend"/> over TCP at <paramref name="endpoint"/>.
    /// Caller owns the backend's lifetime. <paramref name="onListening"/> fires
    /// once with the bound endpoint (useful when port 0 is requested).
    /// </summary>
    public static async Task<int> RunAsync(
        IPEndPoint endpoint,
        IOneNoteBackend backend,
        string? token = null,
        Action<IPEndPoint>? onListening = null,
        CancellationToken cancellationToken = default)
    {
        Log($"TcpListenerHost starting on {endpoint}; auth={(token is null ? "open" : "token")}.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        TcpListener? listener = null;
        try
        {
            using var handler = new MessageHandler(backend);

            listener = new TcpListener(endpoint);
            listener.Start();
            var bound = (IPEndPoint)listener.LocalEndpoint;
            Log($"Listening on {bound}.");
            onListening?.Invoke(bound);

            while (!cts.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                Log($"Accepted connection from {client.Client.RemoteEndPoint}.");

                await using var stream = client.GetStream();
                var protocol = new MessageProtocol(stream, stream);

                try
                {
                    if (token is not null && !await ValidateHelloAsync(protocol, token, cts.Token))
                        continue; // ValidateHelloAsync logs + writes the failure response; close + accept next

                    while (!cts.IsCancellationRequested)
                    {
                        var msg = await protocol.ReadMessageAsync(cts.Token);
                        if (msg is null) { Log("EOF — peer disconnected."); break; }

                        var actionName = msg.Value.TryGetProperty("action", out var a) ? a.GetString() : "?";
                        Log($"→ action={actionName}");
                        var response = await handler.HandleAsync(msg.Value, cts.Token);
                        await protocol.WriteMessageAsync(response, cts.Token);
                        Log("← response sent.");
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    Log($"Connection error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("Cancellation requested.");
        }
        catch (Exception ex)
        {
            Log("Fatal: " + ex);
            return 2;
        }
        finally
        {
            try { listener?.Stop(); } catch { /* best effort */ }
        }

        Log("TcpListenerHost exiting normally.");
        return 0;
    }

    private static async Task<bool> ValidateHelloAsync(
        MessageProtocol protocol, string expectedToken, CancellationToken ct)
    {
        var hello = await protocol.ReadMessageAsync(ct);
        if (hello is null) { Log("Auth: EOF before hello."); return false; }

        var actionOk = hello.Value.TryGetProperty("action", out var act) && act.GetString() == "hello";
        var tokenOk  = hello.Value.TryGetProperty("token",  out var tok) && tok.GetString() == expectedToken;

        if (!actionOk || !tokenOk)
        {
            Log($"Auth failed (actionOk={actionOk}, tokenOk={tokenOk}).");
            try
            {
                await protocol.WriteMessageAsync(
                    new { ok = false, error = new { code = "AUTH_FAILED", message = "Invalid hello." } },
                    ct);
            }
            catch (Exception ex) when (ex is IOException or SocketException) { /* best effort */ }
            return false;
        }

        await protocol.WriteMessageAsync(new { ok = true }, ct);
        Log("Auth ok.");
        return true;
    }
}
