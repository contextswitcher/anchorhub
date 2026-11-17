using System.Net;
using System.Text;
using System.Text.Json;
using AnchorHub.Core;
using AnchorHub.Hub.NativeMessaging;

namespace AnchorHub.Hub.Http;

/// <summary>
/// HTTP realm that serves the JabRef fulltext-fetch endpoints described in
/// <c>docs/requirements/browser-extension-fulltext.md</c>:
/// <c>GET /v1/health</c>, <c>POST /v1/fulltext</c>,
/// <c>DELETE /v1/fulltext/{id}</c>.
///
/// The actual fetch dispatches via <see cref="IExtensionDispatcher"/> to
/// the browser extension; the realm is a thin orchestrator that resolves
/// the DOI to a publisher URL, picks the right adapter, and waits for the
/// extension's reply. No internal queue — the extension owns admission
/// control and reports <c>busy</c> when its queue overflows.
///
/// [impl-&gt;req~bxf.health~1]
/// [impl-&gt;req~bxf.fetch~1]
/// [impl-&gt;req~bxf.fetch-errors~1]
/// [impl-&gt;req~bxf.cleanup~1]
/// [impl-&gt;req~bxf.safety-valve~1]
/// [impl-&gt;req~bxf.cancellation~1]
/// </summary>
internal sealed class FulltextRealm : IHttpRealm
{
    private const string Body_Health =
        "{\"ok\":true,\"name\":\"anchorhub\",\"protocolVersion\":1}";

    private readonly IExtensionDispatcher _dispatcher;
    private readonly TimeSpan _safetyValveTimeout;

    private TimeSpan SafetyValveTimeout => _safetyValveTimeout;

    public string AuthToken { get; }

    public FulltextRealm(string authToken, IExtensionDispatcher dispatcher)
        : this(authToken, dispatcher, TimeSpan.FromSeconds(90)) { }

    // Test-visible ctor: lets tests shorten the safety-valve timeout.
    internal FulltextRealm(string authToken, IExtensionDispatcher dispatcher, TimeSpan safetyValveTimeout)
    {
        AuthToken           = authToken;
        _dispatcher         = dispatcher;
        _safetyValveTimeout = safetyValveTimeout;
    }

    public async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var path   = ctx.Request.Url?.AbsolutePath ?? "/";
        var method = ctx.Request.HttpMethod;

        if (path == "/v1/health" && method == "GET")
        {
            await RespondAsync(ctx, 200, Body_Health, ct);
            return;
        }

        if ((path == "/v1/fulltext" || path == "/v1/fulltext/") && method == "POST")
        {
            await HandleFulltextPostAsync(ctx, ct);
            return;
        }

        if (path.StartsWith("/v1/fulltext/", StringComparison.Ordinal) && method == "DELETE")
        {
            // Per spec, DELETE is optional cleanup; provider relies on a
            // TTL sweep regardless. Phase 1 acknowledges DELETE without
            // doing anything — the extension and ankd both expire cached
            // files on their own timers.
            await RespondAsync(ctx, 204, string.Empty, ct);
            return;
        }

        await RespondAsync(ctx, 405, "{\"error\":\"method-not-allowed\"}", ct);
    }

    private async Task HandleFulltextPostAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        FulltextRequest? body;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
            var raw = await reader.ReadToEndAsync(ct);
            body = JsonSerializer.Deserialize<FulltextRequest>(raw, JsonOpts);
        }
        catch (JsonException ex)
        {
            await RespondAsync(ctx, 400,
                $"{{\"error\":\"bad-request\",\"message\":{JsonEscape(ex.Message)}}}",
                ct);
            return;
        }

        if (body is null || (string.IsNullOrWhiteSpace(body.Doi) && string.IsNullOrWhiteSpace(body.Url)))
        {
            await RespondAsync(ctx, 400,
                "{\"error\":\"bad-request\",\"message\":\"at least one of doi or url required\"}",
                ct);
            return;
        }

        var resolvedUrl = await ResolveAsync(body, ct);
        if (resolvedUrl is null)
        {
            await RespondAsync(ctx, 404,
                "{\"error\":\"not-reachable\",\"message\":\"could not resolve DOI/URL\"}",
                ct);
            return;
        }

        var normalized = UrlNormalizer.TryNormalize(resolvedUrl);
        if (normalized is null)
        {
            await RespondAsync(ctx, 400,
                "{\"error\":\"bad-request\",\"message\":\"resolved URL is not a valid http(s) URL\"}",
                ct);
            return;
        }

        var adapter = FulltextAdapters.Pick(normalized);
        var hasSpecific = FulltextAdapters.HasPublisherAdapter(normalized);

        // Connection-close cancels the dispatcher; the extension owns the
        // queue, so no throttle bookkeeping happens here. A safety-valve
        // timer (90 s) caps the wait if the extension goes silent — the
        // dispatcher's abortFulltext companion message fires automatically
        // when the linked CTS triggers cancellation.
        JsonElement extResponse;
        using var safetyValve = new CancellationTokenSource(SafetyValveTimeout);
        using var linkedCts   = CancellationTokenSource.CreateLinkedTokenSource(ct, safetyValve.Token);
        try
        {
            extResponse = await _dispatcher.SendAsync(
                "fetchFulltext",
                new Dictionary<string, object?>
                {
                    ["url"]               = adapter.DocumentUrl(normalized),
                    ["publisherKey"]      = adapter.PublisherKey(normalized),
                    ["contentScriptName"] = adapter.ContentScriptName,
                },
                linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                // Client closed the connection; no response to send.
                return;
            }
            // Safety-valve timeout — extension never replied. Surface as
            // busy so JabRef treats it as a soft miss (no retry).
            await RespondAsync(ctx, 503,
                "{\"error\":\"busy\",\"message\":\"extension did not respond within timeout\"}",
                ct);
            return;
        }

        // Extension reports outcomes inline. Map them to HTTP status codes
        // per the spec.
        if (TryReadString(extResponse, "error", out var errorCode))
        {
            var message = TryReadString(extResponse, "message", out var m) ? m : "";
            // If the publisher adapter didn't match and the generic
            // fallback also produced no PDF, surface no-adapter rather
            // than the extension's generic no-pdf-found.
            if (errorCode == "no-pdf-found" && !hasSpecific)
                errorCode = "no-adapter";

            var status = errorCode switch
            {
                "busy"          => 503,
                "no-pdf-found"  => 404,
                "no-adapter"    => 404,
                "auth-required" => 404,
                "not-reachable" => 404,
                "timeout"       => 504,
                _               => 500,
            };
            await RespondAsync(ctx, status,
                $"{{\"error\":{JsonEscape(errorCode)},\"message\":{JsonEscape(message)}}}",
                ct);
            return;
        }

        // Success: { id, path, sourceUrl? } — the extension's "id" is the
        // download bookkeeping id; we echo it so DELETE can target it
        // later.
        var idStr      = TryReadString(extResponse, "id",        out var i) ? i : Guid.NewGuid().ToString("N");
        var pathStr    = TryReadString(extResponse, "path",      out var p) ? p : null;
        var sourceUrl  = TryReadString(extResponse, "sourceUrl", out var s) ? s : null;

        if (string.IsNullOrEmpty(pathStr))
        {
            await RespondAsync(ctx, 500,
                "{\"error\":\"internal-error\",\"message\":\"extension returned success without a file path\"}",
                ct);
            return;
        }

        var ok = new Dictionary<string, object?>
        {
            ["id"]   = idStr,
            ["path"] = pathStr,
        };
        if (sourceUrl is not null) ok["sourceUrl"] = sourceUrl;

        await RespondJsonAsync(ctx, 200, ok, ct);
    }

    /// <summary>
    /// Resolves the request to the publisher URL the extension should
    /// open. If only <c>url</c> is given, returns it directly. If
    /// <c>doi</c> is given, follows <c>https://doi.org/&lt;doi&gt;</c>
    /// redirects to the final landing page.
    /// </summary>
    private static async Task<string?> ResolveAsync(FulltextRequest req, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(req.Doi))
        {
            try
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
                {
                    Timeout = TimeSpan.FromSeconds(15),
                };
                using var resp = await http.GetAsync(
                    $"https://doi.org/{req.Doi.Trim()}",
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);
                return resp.RequestMessage?.RequestUri?.ToString();
            }
            catch
            {
                return null;
            }
        }

        return req.Url?.Trim();
    }

    private static async Task RespondAsync(HttpListenerContext ctx, int status, string body, CancellationToken ct)
    {
        ctx.Response.StatusCode  = status;
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = bytes.Length;
        try
        {
            if (bytes.Length > 0)
                await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        }
        finally
        {
            try { ctx.Response.Close(); } catch { /* ignore */ }
        }
    }

    private static Task RespondJsonAsync(HttpListenerContext ctx, int status, object payload, CancellationToken ct) =>
        RespondAsync(ctx, status, JsonSerializer.Serialize(payload, JsonOpts), ct);

    private static bool TryReadString(JsonElement el, string name, out string value)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.String &&
            v.GetString() is { } s)
        {
            value = s;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static string JsonEscape(string s) => JsonSerializer.Serialize(s);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed class FulltextRequest
    {
        public string? Doi { get; set; }
        public string? Url { get; set; }
    }
}
