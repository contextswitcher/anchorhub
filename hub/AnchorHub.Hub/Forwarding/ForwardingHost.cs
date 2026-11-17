using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AnchorHub.Hub.NativeMessaging;

namespace AnchorHub.Hub.Forwarding;

/// <summary>
/// Forward mode: relay framed messages between Firefox (stdio) and a
/// remote daemon running in --listen mode (typically inside a Windows
/// VM). When the listener is unreachable, synthesize a DISCONNECTED
/// response per request so the popup flips to the "not connected"
/// icon promptly without waiting for a network timeout.
/// </summary>
internal static class ForwardingHost
{
    private const int ConnectTimeoutMs = 500;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AnchorHub", "logs", $"host-{DateTime.UtcNow:yyyyMMdd}.log");

    private static void Log(string msg)
    {
        Console.Error.WriteLine($"[FWD] {DateTime.UtcNow:o} {msg}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.UtcNow:o} [FWD] {msg}\n");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[FWD] log-write failed: {ex.Message}"); }
    }

    public static async Task<int> RunAsync(
        IPEndPoint remote,
        string? token = null,
        MessageProtocol? client = null,
        CancellationToken cancellationToken = default)
    {
        Log($"ForwardingHost starting; relaying client to {remote}; auth={(token is null ? "open" : "token")}.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var stdio = client ?? MessageProtocol.Stdio();
        TcpClient? tcp = null;
        MessageProtocol? remoteProto = null;

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var msg = await stdio.ReadMessageAsync(cts.Token);
                if (msg is null) { Log("EOF — extension disconnected."); break; }

                // Lazy (re)connect — every request gets a chance to find the
                // listener back, which keeps the popup's status pings cheap to
                // recover from.
                if (remoteProto is null)
                {
                    (tcp, remoteProto) = await TryConnectAsync(remote, token, cts.Token);
                }

                if (remoteProto is null)
                {
                    await WriteDisconnectedAsync(stdio, msg.Value, cts.Token);
                    continue;
                }

                try
                {
                    await remoteProto.WriteMessageAsync(msg.Value, cts.Token);
                    var resp = await remoteProto.ReadMessageAsync(cts.Token);
                    if (resp is null)
                    {
                        Log("Listener EOF; treating as disconnected.");
                        DisposeRemote(ref tcp, ref remoteProto);
                        await WriteDisconnectedAsync(stdio, msg.Value, cts.Token);
                        continue;
                    }
                    await stdio.WriteMessageAsync(resp.Value, cts.Token);
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    Log($"Forward error: {ex.Message}; treating as disconnected.");
                    DisposeRemote(ref tcp, ref remoteProto);
                    await WriteDisconnectedAsync(stdio, msg.Value, cts.Token);
                }
            }
        }
        catch (OperationCanceledException) { Log("Cancellation requested."); }
        catch (Exception ex)
        {
            Log("Fatal: " + ex);
            return 2;
        }
        finally
        {
            tcp?.Dispose();
        }

        Log("ForwardingHost exiting normally.");
        return 0;
    }

    private static async Task<(TcpClient?, MessageProtocol?)> TryConnectAsync(
        IPEndPoint remote, string? token, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(remote.Address, remote.Port, ct)
                .AsTask().WaitAsync(TimeSpan.FromMilliseconds(ConnectTimeoutMs), ct);
            var stream = tcp.GetStream();
            var proto  = new MessageProtocol(stream, stream);
            Log($"Connected to {remote}.");

            if (token is not null && !await SendHelloAsync(proto, token, ct))
            {
                tcp.Dispose();
                return (null, null);
            }

            return (tcp, proto);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException)
        {
            // OperationCanceledException from WaitAsync's timeout (not from ct itself)
            // is treated the same as a refused connection.
            Log($"Connect failed: {ex.GetType().Name}: {ex.Message}");
            tcp.Dispose();
            return (null, null);
        }
    }

    private static async Task<bool> SendHelloAsync(
        MessageProtocol proto, string token, CancellationToken ct)
    {
        try
        {
            await proto.WriteMessageAsync(new { action = "hello", token }, ct);
            var ack = await proto.ReadMessageAsync(ct);
            if (ack is null)
            {
                Log("Auth: listener closed before ack.");
                return false;
            }
            var ok = ack.Value.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            if (!ok)
            {
                var err = ack.Value.TryGetProperty("error", out var e) ? e.ToString() : "(no error body)";
                Log($"Auth rejected by listener: {err}");
                return false;
            }
            Log("Auth ok.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            Log($"Auth handshake failed: {ex.Message}");
            return false;
        }
    }

    private static void DisposeRemote(ref TcpClient? tcp, ref MessageProtocol? proto)
    {
        try { tcp?.Dispose(); } catch { /* best effort */ }
        tcp = null;
        proto = null;
    }

    private static async Task WriteDisconnectedAsync(
        MessageProtocol stdio, JsonElement request, CancellationToken ct)
    {
        var requestId = request.TryGetProperty("requestId", out var rid) ? rid.GetString() : null;
        var response = new
        {
            requestId,
            ok    = false,
            error = new
            {
                code    = "DISCONNECTED",
                message = "AnchorHub listener unreachable.",
            },
        };
        await stdio.WriteMessageAsync(response, ct);
    }
}
