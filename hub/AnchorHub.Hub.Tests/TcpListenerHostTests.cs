using System.Net;
using System.Net.Sockets;
using AnchorHub.Hub.NativeMessaging;

namespace AnchorHub.Hub.Tests;

public class TcpListenerHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OpenMode_StatusRoundTrip()
    {
        await using var fixture = await ListenerFixture.StartAsync(token: null);

        var resp = await fixture.SendAsync(new { requestId = "r1", action = "status" });

        Assert.True(resp.GetProperty("ok").GetBoolean());
        Assert.Equal("r1", resp.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task TokenMode_ValidHello_ThenStatus()
    {
        const string token = "deadbeef";
        await using var fixture = await ListenerFixture.StartAsync(token: token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, fixture.Port, fixture.Cts.Token);
        var stream = client.GetStream();
        var proto  = new MessageProtocol(stream, stream);

        // hello
        await proto.WriteMessageAsync(new { action = "hello", token }, fixture.Cts.Token);
        var ack = await proto.ReadMessageAsync(fixture.Cts.Token);
        Assert.NotNull(ack);
        Assert.True(ack!.Value.GetProperty("ok").GetBoolean());

        // real request
        await proto.WriteMessageAsync(new { requestId = "r1", action = "status" }, fixture.Cts.Token);
        var resp = await proto.ReadMessageAsync(fixture.Cts.Token);
        Assert.NotNull(resp);
        Assert.True(resp!.Value.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task TokenMode_InvalidHello_AuthFailedThenClose()
    {
        await using var fixture = await ListenerFixture.StartAsync(token: "expected");

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, fixture.Port, fixture.Cts.Token);
        var stream = client.GetStream();
        var proto  = new MessageProtocol(stream, stream);

        await proto.WriteMessageAsync(new { action = "hello", token = "wrong" }, fixture.Cts.Token);
        var ack = await proto.ReadMessageAsync(fixture.Cts.Token);
        Assert.NotNull(ack);
        Assert.False(ack!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal("AUTH_FAILED", ack.Value.GetProperty("error").GetProperty("code").GetString());

        // Listener closes the connection on AUTH_FAILED — next read returns null (EOF).
        var followUp = await proto.ReadMessageAsync(fixture.Cts.Token);
        Assert.Null(followUp);
    }

    [Fact]
    public async Task TokenMode_RealActionInsteadOfHello_AuthFailed()
    {
        await using var fixture = await ListenerFixture.StartAsync(token: "expected");

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, fixture.Port, fixture.Cts.Token);
        var stream = client.GetStream();
        var proto  = new MessageProtocol(stream, stream);

        // Skip hello entirely; send a normal action straight away.
        await proto.WriteMessageAsync(new { requestId = "r1", action = "status" }, fixture.Cts.Token);
        var ack = await proto.ReadMessageAsync(fixture.Cts.Token);
        Assert.NotNull(ack);
        Assert.False(ack!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal("AUTH_FAILED", ack.Value.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class ListenerFixture : IAsyncDisposable
    {
        public required int Port { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task ListenerTask { get; init; }

        public static async Task<ListenerFixture> StartAsync(string? token)
        {
            var ready = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cts   = new CancellationTokenSource(Timeout);
            var task  = TcpListenerHost.RunAsync(
                new IPEndPoint(IPAddress.Loopback, 0),
                new StubBackend(),
                token: token,
                onListening: ep => ready.TrySetResult(ep),
                cancellationToken: cts.Token);

            var bound = await ready.Task.WaitAsync(Timeout);
            return new ListenerFixture
            {
                Port = bound.Port,
                Cts = cts,
                ListenerTask = task,
            };
        }

        public async Task<System.Text.Json.JsonElement> SendAsync(object request)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Port, Cts.Token);
            var stream = client.GetStream();
            var proto  = new MessageProtocol(stream, stream);

            await proto.WriteMessageAsync(request, Cts.Token);
            var resp = await proto.ReadMessageAsync(Cts.Token);
            return resp ?? throw new InvalidOperationException("Listener returned EOF.");
        }

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try { await ListenerTask; } catch { /* expected on cancel */ }
            Cts.Dispose();
        }
    }
}
