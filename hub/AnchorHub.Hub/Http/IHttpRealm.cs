using System.Net;

namespace AnchorHub.Hub.Http;

/// <summary>
/// One route family ("realm") served by <see cref="HttpListenerHost"/>.
/// Each realm has its own bearer token and an HTTP handler that owns
/// request processing once the host has verified the prefix matches, the
/// Origin check passes, and the bearer token is correct. A single realm
/// may be registered at multiple prefixes (e.g. fulltext owns both
/// <c>/v1/fulltext</c> and <c>/v1/health</c>).
/// </summary>
internal interface IHttpRealm
{
    /// <summary>
    /// Bearer token clients must present in the <c>Authorization</c>
    /// header. Compared with a constant-time comparator.
    /// </summary>
    string AuthToken { get; }

    /// <summary>
    /// Process the request and write the response. The host has already
    /// rejected unauthorised, cross-origin, and prefix-unknown calls.
    /// </summary>
    Task HandleAsync(HttpListenerContext ctx, CancellationToken ct);
}
