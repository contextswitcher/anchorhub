using System.Collections.Concurrent;
using System.Text.Json;

namespace AnchorHub.Hub.NativeMessaging;

/// <summary>
/// Hub-to-extension request channel. The hub uses this to send actions
/// like <c>fetchFulltext</c> to the browser extension and await the
/// extension's response. Cancellation closes the pending request and
/// sends a best-effort <c>abortFulltext</c> companion message so the
/// extension can stop its in-flight work.
///
/// Implementations must also expose <see cref="CompleteResponse"/> so the
/// host's read loop can route incoming messages without an
/// <c>action</c> field (i.e. responses to hub-initiated requests) back to
/// the awaiting caller.
/// </summary>
internal interface IExtensionDispatcher
{
    /// <summary>
    /// Send an action to the extension and await its response. The
    /// dispatcher assigns the <c>requestId</c>; callers MUST NOT include
    /// one in <paramref name="payload"/>.
    /// </summary>
    Task<JsonElement> SendAsync(string action, IDictionary<string, object?> payload, CancellationToken ct);

    /// <summary>
    /// Called by the host's read loop when an incoming message has no
    /// <c>action</c> field. If the message's <c>requestId</c> matches an
    /// outstanding <see cref="SendAsync"/>, that call completes with the
    /// message. Otherwise the message is dropped.
    /// </summary>
    void CompleteResponse(string requestId, JsonElement message);
}

/// <summary>
/// Stdio-backed implementation: writes hub-initiated requests through a
/// shared <see cref="MessageProtocol"/> behind a write lock so the read
/// loop (which writes ext-initiated responses) and the HTTP-served
/// realms cannot interleave frames.
/// </summary>
internal sealed class StdioExtensionDispatcher : IExtensionDispatcher
{
    private readonly MessageProtocol _protocol;
    private readonly SemaphoreSlim _writeLock;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

    public StdioExtensionDispatcher(MessageProtocol protocol, SemaphoreSlim writeLock)
    {
        _protocol  = protocol;
        _writeLock = writeLock;
    }

    public async Task<JsonElement> SendAsync(string action, IDictionary<string, object?> payload, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        var msg = new Dictionary<string, object?>(payload, StringComparer.Ordinal)
        {
            ["action"]    = action,
            ["requestId"] = requestId,
        };

        await WriteAsync(msg, ct);

        await using var cancelReg = ct.Register(() =>
        {
            if (_pending.TryRemove(requestId, out var pending))
            {
                pending.TrySetCanceled(ct);
                _ = SendAbortAsync(requestId);
            }
        });

        try
        {
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    public void CompleteResponse(string requestId, JsonElement message)
    {
        if (_pending.TryRemove(requestId, out var tcs))
            tcs.TrySetResult(message);
    }

    private async Task WriteAsync(object payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _protocol.WriteMessageAsync(payload, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SendAbortAsync(string requestId)
    {
        try
        {
            // Best-effort: use CancellationToken.None and tolerate any write
            // failure (extension may already have closed the channel).
            await _writeLock.WaitAsync();
            try
            {
                await _protocol.WriteMessageAsync(
                    new Dictionary<string, object?>
                    {
                        ["action"]    = "abortFulltext",
                        ["requestId"] = requestId,
                    },
                    CancellationToken.None);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            // best effort
        }
    }
}
