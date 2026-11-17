using System.Net.Http.Headers;
using System.Text.Json;
using AnchorHub.Hub.Auth;

namespace AnchorHub.Hub.Graph;

/// <summary>
/// Read-only Graph queries against
/// <c>/me/onenote/pages?$search=…&amp;$expand=parentSection,parentNotebook</c>.
/// The access token is fetched silently from MSAL's DPAPI cache on
/// every call — never interactive — so a stale cache surfaces as an
/// explicit "sign in again" error rather than a surprise browser pop.
///
/// Graph's <c>$search</c> is approximate: a quoted phrase usually hits
/// URL substrings reliably, but a plain unquoted term is used as a
/// last-resort fuzzy fallback when the phrase form returns nothing.
/// </summary>
internal static class GraphLookupHelper
{
    private const string GraphBase   = "https://graph.microsoft.com/v1.0";
    private const int    MaxResults  = 25;

    // Shared HttpClient: handler pooling, no per-call socket churn.
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    public sealed record PageHit(
        string  PageId,
        string  Title,
        string? SectionName,
        string? NotebookName,
        string? ClientUrl,
        string? WebUrl,
        string  SearchTerm,
        string  MatchType);

    public static async Task<IReadOnlyList<PageHit>> FindPagesAsync(
        string clientId, string tenant, string query, CancellationToken ct)
    {
        var token = await GraphAuthHelper.GetSilentTokenAsync(clientId, tenant, ct);
        if (token is null) throw new InvalidOperationException("Not signed in. Open AnchorHub settings and sign in to Microsoft Graph.");

        // Pass 1: quoted phrase. URL substrings need exact-phrase matching.
        var phrase = query.Replace("\"", "");
        var hits = await SearchAsync(token.AccessToken, $"\"{phrase}\"", phrase, "exact", ct);
        if (hits.Count > 0) return hits;

        // Pass 2: unquoted (tokenised). Loses precision; mark as "related".
        return await SearchAsync(token.AccessToken, query, query, "related", ct);
    }

    private static async Task<IReadOnlyList<PageHit>> SearchAsync(
        string accessToken, string searchTerm, string searchTermBare, string matchType, CancellationToken ct)
    {
        var url = $"{GraphBase}/me/onenote/pages"
                + $"?$search={Uri.EscapeDataString(searchTerm)}"
                + $"&$expand=parentSection,parentNotebook"
                + $"&$top={MaxResults}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Graph returned {(int)resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var hits = new List<PageHit>();
        if (!doc.RootElement.TryGetProperty("value", out var arr)) return hits;

        foreach (var p in arr.EnumerateArray())
        {
            var id           = p.GetProperty("id").GetString() ?? "";
            var title        = p.TryGetProperty("title", out var t) ? t.GetString() : null;
            var sectionName  = ReadDisplayName(p, "parentSection");
            var notebookName = ReadDisplayName(p, "parentNotebook");
            var clientUrl    = ReadHref(p, "links", "oneNoteClientUrl");
            var webUrl       = ReadHref(p, "links", "oneNoteWebUrl");

            hits.Add(new PageHit(id, title ?? "(untitled)", sectionName, notebookName, clientUrl, webUrl, searchTermBare, matchType));
        }
        return hits;
    }

    private static string? ReadDisplayName(JsonElement page, string propertyName)
    {
        if (!page.TryGetProperty(propertyName, out var obj) || obj.ValueKind != JsonValueKind.Object) return null;
        return obj.TryGetProperty("displayName", out var n) ? n.GetString() : null;
    }

    private static string? ReadHref(JsonElement page, string container, string link)
    {
        if (!page.TryGetProperty(container, out var c) || c.ValueKind != JsonValueKind.Object) return null;
        if (!c.TryGetProperty(link, out var l)        || l.ValueKind != JsonValueKind.Object) return null;
        return l.TryGetProperty("href", out var h) ? h.GetString() : null;
    }
}
