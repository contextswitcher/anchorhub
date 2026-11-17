using System.Net;
using AnchorHub.Hub.Forwarding;
using AnchorHub.Hub.NativeMessaging;

namespace AnchorHub.Hub.Tests;

public class ForwardingHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RoundTrip_ListenerUp()
    {
        var ready = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(Timeout);

        var listenerTask = TcpListenerHost.RunAsync(
            new IPEndPoint(IPAddress.Loopback, 0),
            new StubBackend(),
            token: null,
            onListening: ep => ready.TrySetResult(ep),
            cancellationToken: cts.Token);

        var bound = await ready.Task.WaitAsync(Timeout);

        var (forwarderStdio, testStdio) = StreamPair.CreateLoopback();
        var forwarderTask = ForwardingHost.RunAsync(
            bound,
            token: null,
            client: forwarderStdio,
            cancellationToken: cts.Token);

        await testStdio.WriteMessageAsync(new { requestId = "r1", action = "status" }, cts.Token);
        var resp = await testStdio.ReadMessageAsync(cts.Token);

        Assert.NotNull(resp);
        Assert.True(resp!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal("r1", resp.Value.GetProperty("requestId").GetString());

        cts.Cancel();
        try { await Task.WhenAll(listenerTask, forwarderTask); } catch { /* expected on cancel */ }
    }

    [Fact]
    public async Task NoListener_SynthesisesDisconnected()
    {
        // Pick a port that's almost certainly not listening. Bind+release pattern
        // to get a known-free port, then connect to it (refused).
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var unreachable = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)probe.LocalEndpoint).Port);
        probe.Stop(); // port is now free; connection attempts will be refused

        using var cts = new CancellationTokenSource(Timeout);
        var (forwarderStdio, testStdio) = StreamPair.CreateLoopback();
        var forwarderTask = ForwardingHost.RunAsync(
            unreachable,
            token: null,
            client: forwarderStdio,
            cancellationToken: cts.Token);

        await testStdio.WriteMessageAsync(new { requestId = "r-disc", action = "status" }, cts.Token);
        var resp = await testStdio.ReadMessageAsync(cts.Token);

        Assert.NotNull(resp);
        Assert.False(resp!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal("DISCONNECTED", resp.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("r-disc",      resp.Value.GetProperty("requestId").GetString());

        cts.Cancel();
        try { await forwarderTask; } catch { /* expected on cancel */ }
    }
}
