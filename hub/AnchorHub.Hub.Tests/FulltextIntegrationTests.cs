using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using AnchorHub.Hub.Http;
using AnchorHub.Hub.NativeMessaging;

namespace AnchorHub.Hub.Tests;

/// <summary>
/// End-to-end test of the fulltext realm: real HTTP listener, real
/// dispatcher, real StreamPair loopback between hub and a stub "browser"
/// that handles fetchFulltext / abortFulltext on the other end. No
/// OneNote / no actual browser involved.
/// </summary>
public class FulltextIntegrationTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// Wires up: HTTP listener + FulltextRealm + StdioExtensionDispatcher
    /// over a StreamPair loopback, plus a hub-side read loop that demuxes
    /// browser responses back into the dispatcher. The stub browser runs
    /// the caller-supplied <paramref name="browserBehaviour"/>.
    /// </summary>
    private sealed class HubUnderTest : IAsyncDisposable
    {
        public required HttpClient Client                { get; init; }
        public required CancellationTokenSource Cts      { get; init; }
        public required Task HttpServerTask              { get; init; }
        public required Task ReaderTask                  { get; init; }
        public required Task BrowserTask                 { get; init; }

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try { await Task.WhenAll(HttpServerTask, ReaderTask, BrowserTask); }
            catch { /* shutdown noise */ }
            Client.Dispose();
        }
    }

    private static async Task<HubUnderTest> StartAsync(
        Func<MessageProtocol, CancellationToken, Task> browserBehaviour,
        string token = "secret",
        TimeSpan? safetyValve = null)
    {
        var (hub, browser) = StreamPair.CreateLoopback();
        var writeLock = new SemaphoreSlim(1, 1);
        var dispatcher = new StdioExtensionDispatcher(hub, writeLock);

        var realm = safetyValve is null
            ? new FulltextRealm(token, dispatcher)
            : new FulltextRealm(token, dispatcher, safetyValve.Value);
        var realms = new Dictionary<string, IHttpRealm>
        {
            ["/v1/fulltext"] = realm,
            ["/v1/health"]   = realm,
        };
        var port  = FreePort();
        var ep    = new IPEndPoint(IPAddress.Loopback, port);
        var host  = new HttpListenerHost(ep, realms);
        var cts   = new CancellationTokenSource();

        var httpTask = Task.Run(() => host.RunAsync(cts.Token));

        // Hub read loop: route ext->hub (action present) to a no-op; route
        // responses (no action, with requestId) to dispatcher.
        var readerTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var msg = await hub.ReadMessageAsync(cts.Token);
                    if (msg is null) return;
                    if (msg.Value.TryGetProperty("action", out _))
                    {
                        // Tests don't exercise ext->hub; ignore.
                        continue;
                    }
                    if (msg.Value.TryGetProperty("requestId", out var rid) &&
                        rid.GetString() is { } requestId)
                    {
                        dispatcher.CompleteResponse(requestId, msg.Value);
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        var browserTask = Task.Run(() => browserBehaviour(browser, cts.Token));

        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout     = TimeSpan.FromSeconds(10),
        };

        for (var i = 0; i < 50; i++)
        {
            try { _ = await client.GetAsync("/__probe__"); break; }
            catch (HttpRequestException) { await Task.Delay(50); }
        }

        return new HubUnderTest
        {
            Client         = client,
            Cts            = cts,
            HttpServerTask = httpTask,
            ReaderTask     = readerTask,
            BrowserTask    = browserTask,
        };
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string body)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return req;
    }

    [Fact]
    public async Task Happy_path_fetchFulltext_round_trip_returns_path()
    {
        var browserSawAction = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var hub = await StartAsync(async (browser, ct) =>
        {
            try
            {
                var req = await browser.ReadMessageAsync(ct);
                if (req is null) return;
                browserSawAction.TrySetResult(req.Value);

                var requestId = req.Value.GetProperty("requestId").GetString();
                await browser.WriteMessageAsync(new
                {
                    requestId,
                    id   = "browser-id-1",
                    path = "C:\\stub\\10589877.pdf",
                }, ct);
            }
            catch (OperationCanceledException) { }
        });

        var r = await hub.Client.SendAsync(Authed(
            HttpMethod.Post, "/v1/fulltext",
            "{\"url\":\"https://ieeexplore.ieee.org/document/10589877\"}"));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("browser-id-1",           doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("C:\\stub\\10589877.pdf", doc.RootElement.GetProperty("path").GetString());

        var seen = await browserSawAction.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("fetchFulltext",
                     seen.GetProperty("action").GetString());
        Assert.Equal("https://ieeexplore.ieee.org/document/10589877",
                     seen.GetProperty("url").GetString());
        Assert.Equal("ieeexplore.ieee.org",
                     seen.GetProperty("publisherKey").GetString());
        Assert.Equal("ieee",
                     seen.GetProperty("contentScriptName").GetString());
    }

    [Fact]
    public async Task Silent_browser_triggers_safety_valve_and_abortFulltext()
    {
        // The browser receives fetchFulltext but never replies. With a
        // short safety-valve timeout, the realm cancels its dispatcher
        // call, which fires the abortFulltext companion. The browser
        // should see both the original fetchFulltext and the matching
        // abortFulltext; the HTTP client should see 503 busy.
        var sawFetch = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawAbort = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var hub = await StartAsync(
            browserBehaviour: async (browser, ct) =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var msg = await browser.ReadMessageAsync(ct);
                        if (msg is null) return;

                        var action    = msg.Value.GetProperty("action").GetString();
                        var requestId = msg.Value.GetProperty("requestId").GetString()!;

                        if (action == "fetchFulltext")
                        {
                            sawFetch.TrySetResult(requestId);
                            // Deliberately never reply.
                        }
                        else if (action == "abortFulltext")
                        {
                            sawAbort.TrySetResult(requestId);
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            },
            safetyValve: TimeSpan.FromMilliseconds(200));

        var r = await hub.Client.SendAsync(Authed(
            HttpMethod.Post, "/v1/fulltext",
            "{\"url\":\"https://ieeexplore.ieee.org/document/10589877\"}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("busy", doc.RootElement.GetProperty("error").GetString());

        var fetchId = await sawFetch.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var abortId = await sawAbort.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(fetchId, abortId);
    }
}
