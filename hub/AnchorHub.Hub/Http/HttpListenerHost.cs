using System.Net;
using System.Text;

namespace AnchorHub.Hub.Http;

/// <summary>
/// Minimal loopback HTTP server bound to a single <see cref="IPEndPoint"/>.
/// Routes requests by path prefix to per-realm handlers; rejects
/// browser-initiated calls via an <c>Origin</c> check; rejects unauthorised
/// requests via a per-realm bearer-token check. Designed for
/// JabRef-fulltext and future loopback APIs; no CORS, no public exposure.
///
/// [impl-&gt;req~bxf.loopback-bind~1]
/// [impl-&gt;req~bxf.auth-bearer~1]
/// [impl-&gt;req~bxf.origin-check~1]
/// </summary>
internal sealed class HttpListenerHost
{
    /// <summary>
    /// All path prefixes the host knows about, whether enabled or not.
    /// A request whose path matches one of these but for which no realm
    /// is registered returns <c>401</c> (rather than <c>404</c>) so a
    /// client probing for a known API can tell "feature not enabled" from
    /// "wrong URL".
    /// </summary>
    private static readonly string[] KnownRealmPrefixes =
    {
        "/v1/fulltext",
        "/v1/health",
    };

    private readonly IPEndPoint _endpoint;
    private readonly Dictionary<string, IHttpRealm> _realms;

    /// <summary>
    /// Build a host whose route table maps each prefix in
    /// <paramref name="realms"/> to its handler. Multiple prefixes may
    /// point at the same realm (e.g. fulltext owns both
    /// <c>/v1/fulltext</c> and <c>/v1/health</c>).
    /// </summary>
    public HttpListenerHost(IPEndPoint endpoint, IDictionary<string, IHttpRealm> realms)
    {
        _endpoint = endpoint;
        _realms   = new Dictionary<string, IHttpRealm>(realms, StringComparer.Ordinal);
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://{_endpoint.Address}:{_endpoint.Port}/");
        listener.Start();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException)      { break; }

                _ = Task.Run(() => HandleContextSafelyAsync(context, ct), ct);
            }
        }
        finally
        {
            try { listener.Stop(); } catch { /* ignore */ }
        }
        return 0;
    }

    private async Task HandleContextSafelyAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            await HandleContextAsync(ctx, ct);
        }
        catch (Exception ex)
        {
            try
            {
                await RespondJsonAsync(ctx, 500,
                    $"{{\"error\":\"internal\",\"message\":{JsonEscape(ex.Message)}}}",
                    ct);
            }
            catch { /* response already partially written or peer gone */ }
        }
    }

    private async Task HandleContextAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";

        var matchedPrefix = KnownRealmPrefixes.FirstOrDefault(p =>
            path.Equals(p, StringComparison.Ordinal) ||
            path.StartsWith(p + "/", StringComparison.Ordinal));

        if (matchedPrefix is null)
        {
            await RespondJsonAsync(ctx, 404, "{\"error\":\"not-found\"}", ct);
            return;
        }

        // Loopback callers (JabRef, curl) do not set Origin. A browser-
        // initiated call would set it to the page's origin and is rejected
        // outright — this server is not a CORS target.
        var origin = ctx.Request.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin) &&
            !string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
        {
            await RespondJsonAsync(ctx, 403, "{\"error\":\"forbidden\"}", ct);
            return;
        }

        if (!_realms.TryGetValue(matchedPrefix, out var realm))
        {
            await RespondJsonAsync(ctx, 401, "{\"error\":\"unauthorized\"}", ct);
            return;
        }

        var auth = ctx.Request.Headers["Authorization"];
        if (string.IsNullOrEmpty(auth) ||
            !auth.StartsWith("Bearer ", StringComparison.Ordinal) ||
            !ConstantTimeEquals(auth.AsSpan("Bearer ".Length), realm.AuthToken))
        {
            await RespondJsonAsync(ctx, 401, "{\"error\":\"unauthorized\"}", ct);
            return;
        }

        await realm.HandleAsync(ctx, ct);
    }

    internal static async Task RespondJsonAsync(HttpListenerContext ctx, int status, string body, CancellationToken ct)
    {
        ctx.Response.StatusCode    = status;
        ctx.Response.ContentType   = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = bytes.Length;
        try
        {
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* ignore */ }
        }
    }

    private static bool ConstantTimeEquals(ReadOnlySpan<char> a, string b)
    {
        if (a.Length != b.Length) return false;
        var result = 0;
        for (var i = 0; i < a.Length; i++)
            result |= a[i] ^ b[i];
        return result == 0;
    }

    private static string JsonEscape(string s) =>
        System.Text.Json.JsonSerializer.Serialize(s);
}
