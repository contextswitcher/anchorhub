using System.IO.Pipelines;
using System.Text.Json;
using AnchorHub.Hub.NativeMessaging;

namespace AnchorHub.Hub.Tests;

public class ExtensionDispatcherTests
{
    private static (StdioExtensionDispatcher Dispatcher, MessageProtocol PeerProtocol, Func<Task> Drain)
        Build()
    {
        // hub-side stream pair: writeStream is what dispatcher writes to;
        // peer reads from peerInput. We never read on the dispatcher side.
        var peerInputPipe = new Pipe();
        var peerOutputPipe = new Pipe(); // unused — dispatcher doesn't read.

        var hubProtocol  = new MessageProtocol(peerOutputPipe.Reader.AsStream(), peerInputPipe.Writer.AsStream());
        var peerProtocol = new MessageProtocol(peerInputPipe.Reader.AsStream(),  peerOutputPipe.Writer.AsStream());

        var dispatcher = new StdioExtensionDispatcher(hubProtocol, new SemaphoreSlim(1, 1));

        async Task Drain()
        {
            await peerInputPipe.Writer.CompleteAsync();
            await peerOutputPipe.Writer.CompleteAsync();
        }

        return (dispatcher, peerProtocol, Drain);
    }

    [Fact]
    public async Task Send_writes_action_and_requestId_to_extension()
    {
        var (dispatcher, peer, drain) = Build();

        var sendTask = dispatcher.SendAsync(
            "fetchFulltext",
            new Dictionary<string, object?>
            {
                ["url"]               = "https://ieeexplore.ieee.org/document/10589877",
                ["publisherKey"]      = "ieeexplore.ieee.org",
                ["contentScriptName"] = "ieee",
            },
            CancellationToken.None);

        var received = await peer.ReadMessageAsync(CancellationToken.None);
        Assert.NotNull(received);

        var msg = received!.Value;
        Assert.Equal("fetchFulltext", msg.GetProperty("action").GetString());
        Assert.False(string.IsNullOrEmpty(msg.GetProperty("requestId").GetString()));
        Assert.Equal("ieee", msg.GetProperty("contentScriptName").GetString());

        // Complete the pending send so the test doesn't leak the task.
        var requestId = msg.GetProperty("requestId").GetString()!;
        dispatcher.CompleteResponse(requestId,
            JsonDocument.Parse($"{{\"requestId\":\"{requestId}\",\"id\":\"abc\",\"path\":\"/tmp/x.pdf\"}}").RootElement);

        var result = await sendTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("abc",      result.GetProperty("id").GetString());
        Assert.Equal("/tmp/x.pdf", result.GetProperty("path").GetString());

        await drain();
    }

    [Fact]
    public async Task Cancellation_aborts_pending_request_and_sends_abort_message()
    {
        var (dispatcher, peer, drain) = Build();

        using var cts = new CancellationTokenSource();
        var sendTask = dispatcher.SendAsync(
            "fetchFulltext",
            new Dictionary<string, object?> { ["url"] = "https://x/" },
            cts.Token);

        var firstMessage = await peer.ReadMessageAsync(CancellationToken.None);
        Assert.NotNull(firstMessage);
        var requestId = firstMessage!.Value.GetProperty("requestId").GetString()!;
        Assert.Equal("fetchFulltext", firstMessage.Value.GetProperty("action").GetString());

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);

        var abort = await peer.ReadMessageAsync(CancellationToken.None);
        Assert.NotNull(abort);
        Assert.Equal("abortFulltext", abort!.Value.GetProperty("action").GetString());
        Assert.Equal(requestId,       abort!.Value.GetProperty("requestId").GetString());

        await drain();
    }

    [Fact]
    public async Task CompleteResponse_for_unknown_requestId_is_dropped_silently()
    {
        var (dispatcher, _, drain) = Build();

        // Should not throw.
        dispatcher.CompleteResponse("nonexistent",
            JsonDocument.Parse("{\"requestId\":\"nonexistent\"}").RootElement);

        await drain();
    }
}
