using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using AnchorHub.Hub.Http;
using AnchorHub.Hub.NativeMessaging;

namespace AnchorHub.Hub.Tests;

public class FulltextRealmTests
{
    private sealed class StubDispatcher : IExtensionDispatcher
    {
        private readonly Func<string, IDictionary<string, object?>, CancellationToken, Task<JsonElement>> _handler;
        public StubDispatcher(Func<string, IDictionary<string, object?>, CancellationToken, Task<JsonElement>> h)
            => _handler = h;
        public Task<JsonElement> SendAsync(string action, IDictionary<string, object?> payload, CancellationToken ct)
            => _handler(action, payload, ct);
        public void CompleteResponse(string requestId, JsonElement message) { }
    }

    private static JsonElement Json(object o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<(Task<int> Server, CancellationTokenSource Cts, HttpClient Client)>
        StartFulltextHostAsync(string token, IExtensionDispatcher dispatcher)
    {
        var port  = FreePort();
        var ep    = new IPEndPoint(IPAddress.Loopback, port);
        var realm = new FulltextRealm(token, dispatcher);
        var realms = new Dictionary<string, IHttpRealm>
        {
            ["/v1/fulltext"] = realm,
            ["/v1/health"]   = realm,
        };
        var host = new HttpListenerHost(ep, realms);
        var cts  = new CancellationTokenSource();
        var task = Task.Run(() => host.RunAsync(cts.Token));
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

        return (task, cts, client);
    }

    private static HttpRequestMessage Auth(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static readonly IExtensionDispatcher NeverCalled = new StubDispatcher(
        (_, _, _) => throw new InvalidOperationException("dispatcher should not be called"));

    [Fact]
    public async Task Health_returns_200_with_protocol_version_1()
    {
        var (server, cts, client) = await StartFulltextHostAsync("secret", NeverCalled);
        try
        {
            var r = await client.SendAsync(Auth(HttpMethod.Get, "/v1/health", "secret"));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);

            var json = await r.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal("anchorhub", root.GetProperty("name").GetString());
            Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
        }
        finally { cts.Cancel(); await server; client.Dispose(); }
    }

    [Fact]
    public async Task Health_requires_token()
    {
        var (server, cts, client) = await StartFulltextHostAsync("secret", NeverCalled);
        try
        {
            var r = await client.GetAsync("/v1/health");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
        finally { cts.Cancel(); await server; client.Dispose(); }
    }

    [Fact]
    public async Task Fulltext_post_with_url_dispatches_to_extension()
    {
        var dispatched = new TaskCompletionSource<IDictionary<string, object?>>();
        var dispatcher = new StubDispatcher((action, payload, ct) =>
        {
            Assert.Equal("fetchFulltext", action);
            dispatched.SetResult(payload);
            return Task.FromResult(Json(new
            {
                id        = "test-id-1",
                path      = "C:\\fake\\path.pdf",
                sourceUrl = "https://ieeexplore.ieee.org/stampPDF/getPDF.jsp?arnumber=10589877",
            }));
        });
        var (server, cts, client) = await StartFulltextHostAsync("secret", dispatcher);
        try
        {
            var req = Auth(HttpMethod.Post, "/v1/fulltext", "secret");
            req.Content = new StringContent(
                "{\"url\":\"https://ieeexplore.ieee.org/document/10589877\"}",
                System.Text.Encoding.UTF8, "application/json");
            var r = await client.SendAsync(req);

            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var body = await r.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("test-id-1", doc.RootElement.GetProperty("id").GetString());
            Assert.Equal("C:\\fake\\path.pdf", doc.RootElement.GetProperty("path").GetString());

            var sent = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("https://ieeexplore.ieee.org/document/10589877", sent["url"]);
            Assert.Equal("ieeexplore.ieee.org", sent["publisherKey"]);
            Assert.Equal("ieee", sent["contentScriptName"]);
        }
        finally { cts.Cancel(); await server; client.Dispose(); }
    }

    [Fact]
    public async Task Fulltext_post_without_body_returns_400()
    {
        var (server, cts, client) = await StartFulltextHostAsync("secret", NeverCalled);
        try
        {
            var req = Auth(HttpMethod.Post, "/v1/fulltext", "secret");
            req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            var r = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        finally { cts.Cancel(); await server; client.Dispose(); }
    }

    [Fact]
    public async Task Fulltext_post_with_busy_error_returns_503()
    {
        var dispatcher = new StubDispatcher((_, _, _) =>
            Task.FromResult(Json(new { error = "busy", message = "queue full" })));
        var (server, cts, client) = await StartFulltextHostAsync("secret", dispatcher);
        try
        {
            var req = Auth(HttpMethod.Post, "/v1/fulltext", "secret");
            req.Content = new StringContent(
                "{\"url\":\"https://ieeexplore.ieee.org/document/10589877\"}",
                System.Text.Encoding.UTF8, "application/json");
            var r = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        }
        finally { cts.Cancel(); await server; client.Dispose(); }
    }

    [Fact]
    public async Task Fulltext_post_with_no_pdf_found_returns_no_adapter_when_generic()
    {
        var dispatcher = new StubDispatcher((_, _, _) =>
            Task.FromResult(Json(new { error = "no-pdf-found", message = "no link" })));
        var (server, cts, client) = await StartFulltextHostAsync("secret", dispatcher);
        try
        {
            // example.com matches only the generic adapter, not a publisher one.
            var req = Auth(HttpMethod.Post, "/v1/fulltext", "secret");
            req.Content = new StringContent(
                "{\"url\":\"https://example.com/some/paper.html\"}",
                System.Text.Encoding.UTF8, "application/json");
            var r = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

            var json = await r.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("no-adapter", doc.RootElement.GetProperty("error").GetString());
        }
        finally { cts.Cancel(); await server; client.Dispose(); }
    }

    [Fact]
    public async Task Fulltext_post_with_no_pdf_found_preserved_when_publisher_adapter_matched()
    {
        var dispatcher = new StubDispatcher((_, _, _) =>
            Task.FromResult(Json(new { error = "no-pdf-found", message = "no link" })));
        var (server, cts, client) = await StartFulltextHostAsync("secret", dispatcher);
        try
        {
            var req = Auth(HttpMethod.Post, "/v1/fulltext", "secret");
            req.Content = new StringContent(
                "{\"url\":\"https://ieeexplore.ieee.org/document/10589877\"}",
                System.Text.Encoding.UTF8, "application/json");
            var r = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

            var json = await r.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("no-pdf-found", doc.RootElement.GetProperty("error").GetString());
        }
        finally { cts.Cancel(); await server; client.Dispose(); }
    }

    [Fact]
    public async Task Fulltext_delete_returns_204()
    {
        var (server, cts, client) = await StartFulltextHostAsync("secret", NeverCalled);
        try
        {
            var r = await client.SendAsync(Auth(HttpMethod.Delete, "/v1/fulltext/abc123", "secret"));
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        }
        finally { cts.Cancel(); await server; client.Dispose(); }
    }

    [Fact]
    public async Task Safety_valve_returns_503_busy_when_extension_silent()
    {
        // Dispatcher that never completes — simulates extension going
        // unresponsive. With a tight safety-valve timeout, the realm
        // should return 503 busy rather than hang forever.
        var dispatcher = new StubDispatcher((_, _, ct) =>
        {
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        });

        var port  = FreePort();
        var ep    = new IPEndPoint(IPAddress.Loopback, port);
        var realm = new FulltextRealm("secret", dispatcher, TimeSpan.FromMilliseconds(200));
        var realms = new Dictionary<string, IHttpRealm>
        {
            ["/v1/fulltext"] = realm,
            ["/v1/health"]   = realm,
        };
        var host = new HttpListenerHost(ep, realms);
        var cts  = new CancellationTokenSource();
        var task = Task.Run(() => host.RunAsync(cts.Token));
        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout     = TimeSpan.FromSeconds(5),
        };

        // wait for listener
        for (var i = 0; i < 50; i++)
        {
            try { _ = await client.GetAsync("/__probe__"); break; }
            catch (HttpRequestException) { await Task.Delay(50); }
        }

        try
        {
            var req = Auth(HttpMethod.Post, "/v1/fulltext", "secret");
            req.Content = new StringContent(
                "{\"url\":\"https://ieeexplore.ieee.org/document/10589877\"}",
                System.Text.Encoding.UTF8, "application/json");
            var r = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);

            var json = await r.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("busy", doc.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            cts.Cancel();
            await task;
            client.Dispose();
        }
    }
}
