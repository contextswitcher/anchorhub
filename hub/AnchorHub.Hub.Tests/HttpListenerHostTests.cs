using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using AnchorHub.Hub.Http;

namespace AnchorHub.Hub.Tests;

public class HttpListenerHostTests
{
    private sealed class EchoRealm : IHttpRealm
    {
        public EchoRealm(string token) => AuthToken = token;
        public string AuthToken { get; }

        public async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            var body = System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}");
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body, ct);
            ctx.Response.Close();
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<(Task<int> Server, CancellationTokenSource Cts, HttpClient Client, Uri Base)>
        StartAsync(IDictionary<string, IHttpRealm>? realms = null)
    {
        var port = FreePort();
        var ep   = new IPEndPoint(IPAddress.Loopback, port);
        var host = new HttpListenerHost(ep, realms ?? new Dictionary<string, IHttpRealm>());
        var cts  = new CancellationTokenSource();
        var task = Task.Run(() => host.RunAsync(cts.Token));
        var baseUri = new Uri($"http://127.0.0.1:{port}/");
        var client  = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(5) };

        // Give HttpListener a moment to start accepting.
        for (var i = 0; i < 50; i++)
        {
            try
            {
                var probe = await client.GetAsync("/__probe__");
                _ = probe.StatusCode; // any response means listener is up
                break;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(50);
            }
        }

        return (task, cts, client, baseUri);
    }

    [Fact]
    public async Task Unknown_path_returns_404()
    {
        var (server, cts, client, _) = await StartAsync();
        try
        {
            var r = await client.GetAsync("/some/unknown/path");
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }
        finally
        {
            cts.Cancel();
            await server;
            client.Dispose();
        }
    }

    [Fact]
    public async Task Known_prefix_without_realm_returns_401()
    {
        var (server, cts, client, _) = await StartAsync();
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/v1/fulltext/health");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "anything");
            var r = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
        finally
        {
            cts.Cancel();
            await server;
            client.Dispose();
        }
    }

    [Fact]
    public async Task Missing_token_returns_401()
    {
        var realm = new EchoRealm("secret");
        var realms = new Dictionary<string, IHttpRealm> { ["/v1/fulltext"] = realm };
        var (server, cts, client, _) = await StartAsync(realms);
        try
        {
            var r = await client.GetAsync("/v1/fulltext/health");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
        finally
        {
            cts.Cancel();
            await server;
            client.Dispose();
        }
    }

    [Fact]
    public async Task Wrong_token_returns_401()
    {
        var realm = new EchoRealm("secret");
        var realms = new Dictionary<string, IHttpRealm> { ["/v1/fulltext"] = realm };
        var (server, cts, client, _) = await StartAsync(realms);
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/v1/fulltext/health");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
            var r = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
        finally
        {
            cts.Cancel();
            await server;
            client.Dispose();
        }
    }

    [Fact]
    public async Task Correct_token_reaches_realm()
    {
        var realm = new EchoRealm("secret");
        var realms = new Dictionary<string, IHttpRealm> { ["/v1/fulltext"] = realm };
        var (server, cts, client, _) = await StartAsync(realms);
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/v1/fulltext/health");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
            var r = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var body = await r.Content.ReadAsStringAsync();
            Assert.Contains("\"ok\":true", body);
        }
        finally
        {
            cts.Cancel();
            await server;
            client.Dispose();
        }
    }

    [Fact]
    public async Task Cross_origin_request_returns_403()
    {
        var realm = new EchoRealm("secret");
        var realms = new Dictionary<string, IHttpRealm> { ["/v1/fulltext"] = realm };
        var (server, cts, client, _) = await StartAsync(realms);
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/v1/fulltext/health");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
            req.Headers.Add("Origin", "https://evil.example.com");
            var r = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }
        finally
        {
            cts.Cancel();
            await server;
            client.Dispose();
        }
    }

    [Fact]
    public async Task Origin_null_is_allowed()
    {
        var realm = new EchoRealm("secret");
        var realms = new Dictionary<string, IHttpRealm> { ["/v1/fulltext"] = realm };
        var (server, cts, client, _) = await StartAsync(realms);
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/v1/fulltext/health");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
            req.Headers.Add("Origin", "null");
            var r = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
        finally
        {
            cts.Cancel();
            await server;
            client.Dispose();
        }
    }
}
