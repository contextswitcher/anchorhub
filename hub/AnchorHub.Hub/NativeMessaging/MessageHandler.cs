using System.Text.Json;
using AnchorHub.Core;
using AnchorHub.Hub.ContextSwitcher;

namespace AnchorHub.Hub.NativeMessaging;

internal sealed class MessageHandler : IDisposable
{
    // Nullable: actions that need the local OneNote COM bridge call
    // OneNoteRequired() to fail fast with DISCONNECTED. The Graph
    // sign-in action does not touch it, so the daemon can serve sign-in
    // even when OneNote isn't installed / running.
    private readonly IOneNoteBackend? _onenote;

    public MessageHandler(IOneNoteBackend? backend) => _onenote = backend;

    // Throwing accessor used by every OneNote-touching action method.
    // Caught by HandleAsync, which converts the exception into a clean
    // DISCONNECTED reply instead of an opaque INTERNAL one.
    private IOneNoteBackend Onenote =>
        _onenote ?? throw new OneNoteUnavailableException();

    private sealed class OneNoteUnavailableException : Exception
    {
        public OneNoteUnavailableException()
            : base("Local OneNote backend is not available in this daemon process.") { }
    }

    public async Task<object> HandleAsync(JsonElement msg, CancellationToken ct)
    {
        var requestId = msg.TryGetProperty("requestId", out var rid) ? rid.GetString() : null;
        var action    = msg.TryGetProperty("action",    out var act) ? act.GetString() : null;

        try
        {
            return action switch
            {
                "lookup"         => Lookup(requestId, msg),
                "searchText"     => SearchText(requestId, msg),
                "focus"          => Focus(requestId, msg),
                "openSearch"     => OpenSearch(requestId, msg),
                "appendBullet"   => AppendBullet(requestId, msg),
                "status"         => Status(requestId),
                "listNotebooks"  => ListNotebooks(requestId),
                "addPage"        => AddPage(requestId, msg),
                "graphSignIn"    => await GraphSignInAsync(requestId, msg, ct),
                "graphLookup"    => await GraphLookupAsync(requestId, msg, ct),
                "contextSwitcherLookup" => ContextSwitcherLookup(requestId, msg),
                _                => Error(requestId, "UNKNOWN_ACTION", $"Unknown action: {action}"),
            };
        }
        catch (OneNoteUnavailableException)
        {
            return Error(requestId, "DISCONNECTED", "Local OneNote backend is not available in this daemon process.");
        }
        catch (Exception ex)
        {
            return Error(requestId, "INTERNAL", ex.Message);
        }
    }

    // ── graph sign-in ─────────────────────────────────────────────────────────
    // OAuth 2.0 PKCE flow on a one-shot loopback listener. Cross-platform —
    // the helper is plain net8.0 code so this action works on Linux too.

    private static async Task<object> GraphSignInAsync(string? requestId, JsonElement msg, CancellationToken ct)
    {
        var clientId = msg.TryGetProperty("clientId", out var c) ? c.GetString() : null;
        var tenant   = msg.TryGetProperty("tenant",   out var t) ? t.GetString() : null;

        if (string.IsNullOrEmpty(clientId))
            return Error(requestId, "BAD_REQUEST", "Missing 'clientId'.");

        var tokens = await Auth.GraphAuthHelper.SignInAsync(clientId, tenant ?? "common", ct);
        return new
        {
            requestId,
            ok     = true,
            tokens = new
            {
                accessToken  = tokens.AccessToken,
                refreshToken = tokens.RefreshToken,
                expiresIn    = tokens.ExpiresIn,
                idToken      = tokens.IdToken,
                account      = tokens.Account,
            },
        };
    }

    // ── graph lookup ──────────────────────────────────────────────────────────
    // Queries /me/onenote/pages?$search=… with a silent token. Runs in
    // parallel with the local COM lookup; the extension merges results.

    private static async Task<object> GraphLookupAsync(string? requestId, JsonElement msg, CancellationToken ct)
    {
        var clientId = msg.TryGetProperty("clientId", out var c) ? c.GetString() : null;
        var tenant   = msg.TryGetProperty("tenant",   out var t) ? t.GetString() : null;
        var query    = msg.TryGetProperty("query",    out var q) ? q.GetString() : null;

        if (string.IsNullOrEmpty(clientId)) return Error(requestId, "BAD_REQUEST", "Missing 'clientId'.");
        if (string.IsNullOrEmpty(query))    return Error(requestId, "BAD_REQUEST", "Missing 'query'.");

        var hits = await Graph.GraphLookupHelper.FindPagesAsync(clientId, tenant ?? "common", query, ct);
        return new
        {
            requestId,
            ok      = true,
            matches = hits.Select(h => new
            {
                pageId       = h.PageId,
                title        = h.Title,
                sectionName  = h.SectionName,
                notebookName = h.NotebookName,
                clientUrl    = h.ClientUrl,
                webUrl       = h.WebUrl,
                searchTerm   = h.SearchTerm,
                matchType    = h.MatchType,
                source       = "graph",
            }).ToArray(),
        };
    }

    // ── contextswitcher lookup ──────────────────────────────────────────────────
    // Reports whether the current browser URL belongs to a ContextSwitcher task
    // (a URL in some task's browser.urls). Pure file IO, no OneNote — so it
    // works regardless of whether the COM bridge is available. The returned
    // taskId feeds a contextswitcher://…/<id> deep link in the extension.

    private static object ContextSwitcherLookup(string? requestId, JsonElement msg)
    {
        var url = msg.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(url))
            return Error(requestId, "BAD_REQUEST", "Missing 'url'.");

        var index = new ContextSwitcherTaskIndex(ContextSwitcherPaths.ResolveTasksDirectory());
        var match = index.FindTaskForUrl(url);
        return new
        {
            requestId,
            ok    = true,
            found = match is not null,
            task  = match is null ? null : new
            {
                taskId     = match.TaskId,
                title      = match.Title,
                status     = match.Status,
                matchedUrl = match.MatchedUrl,
            },
        };
    }

    // ── lookup ────────────────────────────────────────────────────────────────

    private object Lookup(string? requestId, JsonElement msg)
    {
        var url = msg.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(url))
            return Error(requestId, "BAD_REQUEST", "Missing 'url'.");

        var normalized = UrlNormalizer.TryNormalize(url);
        if (normalized is null)
            return Error(requestId, "BAD_REQUEST", "Unparseable URL.");

        // Search-engine result pages and web-app shells (Gmail, Calendar)
        // have no useful identity: the URL is a query or interface state
        // rather than a content identifier, so fuzzy OneNote search on the
        // path tokens would match unrelated saved pages. Skip pass 1 *and*
        // host-only fallback.
        if (HostFallbackPolicy.IsSearchEngineHost(normalized) ||
            HostFallbackPolicy.IsNoIdentityHost(normalized) ||
            GoogleScholarUrlHelper.IsNoIdentityUrl(url) ||
            GoogleBooksUrlHelper.IsNoIdentityUrl(url))
        {
            return new
            {
                requestId,
                ok      = true,
                total   = 0,
                matches = Array.Empty<object>(),
            };
        }

        // Pass 1: exact normalized URL — but for Scholar profile URLs the
        // ?user= query is the actual identity, so search for the canonical
        // "scholar.google.com/citations?user=<id>" form instead. Without
        // this override, Pass 1 would search the path "scholar.google.com/
        // citations" and match every saved Scholar profile.
        var pass1Term  = GoogleScholarUrlHelper.CanonicalProfileTerm(url)
                      ?? GoogleBooksUrlHelper.CanonicalBookTerm(url)
                      ?? normalized.Normalized;
        var matches    = Onenote.FindPages(pass1Term);
        var matchType  = "exact";
        var searchTerm = pass1Term;

        if (matches.Count == 0)
        {
            var forge        = ForgeUrlHelper.ExtractTerms(normalized);
            var gistUser     = ForgeUrlHelper.ExtractGistUser(normalized);
            var linkedIn     = LinkedInUrlHelper.ExtractPersonName(normalized);
            var liCompany    = LinkedInUrlHelper.ExtractCompanyTerms(normalized);
            var reddit       = RedditUrlHelper.ExtractTerm(normalized);
            var appSlug      = AppCatalogUrlHelper.ExtractAppSlug(normalized);
            var w3cSpec      = W3CUrlHelper.ExtractSpecName(normalized);
            var amazon       = AmazonUrlHelper.ExtractProductTitle(normalized);
            var idealo       = IdealoUrlHelper.ExtractProductName(normalized);
            var researchGate = ResearchGateUrlHelper.ExtractPersonName(normalized);
            var academiaEdu  = AcademiaEduUrlHelper.ExtractPersonName(normalized);
            var wikiTitle    = WikipediaUrlHelper.ExtractArticleTitle(normalized);

            if (gistUser != null)
            {
                // gist.github.com: pass 2 = gist.github.com/<user> — all gists
                // from this owner. Bare gist.github.com is never searched.
                searchTerm = $"gist.github.com/{gistUser}";
                matches    = Onenote.FindPages(searchTerm);
                matchType  = "related";
            }
            else if (forge.HasValue && ForgeUrlHelper.IsRepoSubpath(normalized))
            {
                // Forge deep link (issue, PR, commit, blob, tree, …): Pass 1
                // already searched the exact URL. No fallback — the broader
                // forge passes (host/org/project, host/org, project) match
                // every saved page from the same repo, which is misleading
                // for a specific issue/PR/file. Either the exact URL is in
                // OneNote or it isn't.
            }
            else if (forge.HasValue)
            {
                // Forge URLs (github.com, gitlab.com, codeberg.org):
                //   pass 2 = <host>/<org>/<project>
                //   pass 3 = <host>/<org>
                //   pass 4 = <project>          (bare repo name, distinctive only)
                // Passes 2/3 phrase-anchor the org/project with the host so a
                // generic name like "hakimel" alone doesn't match unrelated pages.
                // Pass 4 is the last resort for notes that mention the project
                // by name without linking to it — but only for distinctive
                // project names (see IsDistinctiveProjectName), since a bare
                // common word like "vale" matches many unrelated notes. Bare
                // host is never searched.
                var host = forge.Value.Host;
                if (!string.IsNullOrEmpty(forge.Value.Org) && !string.IsNullOrEmpty(forge.Value.Project))
                {
                    searchTerm = $"{host}/{forge.Value.Org}/{forge.Value.Project}";
                    matches    = Onenote.FindPages(searchTerm);
                    matchType  = "related";
                }
                if (matches.Count == 0 && !string.IsNullOrEmpty(forge.Value.Org))
                {
                    searchTerm = $"{host}/{forge.Value.Org}";
                    matches    = Onenote.FindPages(searchTerm);
                    matchType  = "related";
                }
                if (matches.Count == 0 && ForgeUrlHelper.IsDistinctiveProjectName(forge.Value.Project))
                {
                    searchTerm = forge.Value.Project!;
                    matches    = Onenote.FindPages(searchTerm);
                    matchType  = "related";
                }
            }
            else if (linkedIn != null)
            {
                // LinkedIn /in/ profile: pass 2 = person name — skip host-only.
                matches    = Onenote.FindPages(linkedIn);
                matchType  = "related";
                searchTerm = linkedIn;
            }
            else if (liCompany.HasValue)
            {
                // LinkedIn /company/<slug>: pass 2 = canonical company URL
                // (host-anchored substring), pass 3 = bare slug.
                // Bare linkedin.com and the parent /company path are never
                // searched — both match every saved company page.
                searchTerm = liCompany.Value.CompanyUrl;
                matches    = Onenote.FindPages(searchTerm);
                matchType  = "related";
                if (matches.Count == 0)
                {
                    searchTerm = liCompany.Value.Slug;
                    matches    = Onenote.FindPages(searchTerm);
                }
            }
            else if (reddit != null)
            {
                // Reddit: pass 2 = subreddit or username — skip host-only.
                matches    = Onenote.FindPages(reddit);
                matchType  = "related";
                searchTerm = reddit;
            }
            else if (appSlug != null)
            {
                // App catalogs (alternativeto, noteapps, …): pass 2 = app slug
                // (e.g. "logseq"). Catalog host alone is useless, so this is
                // the only fallback attempted.
                matches    = Onenote.FindPages(appSlug);
                matchType  = "related";
                searchTerm = appSlug;
            }
            else if (w3cSpec != null)
            {
                // w3.org: pass 2 = spec acronym (e.g. "OWL") — bare host
                // is excluded from host-only fallback.
                matches    = Onenote.FindPages(w3cSpec);
                matchType  = "related";
                searchTerm = w3cSpec;
            }
            else if (amazon != null)
            {
                // amazon.<tld>: pass 2 = title slug joined with spaces
                // (e.g. "Production Workflow Techniques Frank Leymann").
                matches    = Onenote.FindPages(amazon);
                matchType  = "related";
                searchTerm = amazon;
            }
            else if (idealo != null)
            {
                // idealo.<tld>: pass 2 = product-name slug joined with spaces
                // (e.g. "mx anywhere 3s grafit logitech"). Bare host is
                // excluded from host-only fallback.
                matches    = Onenote.FindPages(idealo);
                matchType  = "related";
                searchTerm = idealo;
            }
            else if (researchGate != null)
            {
                // researchgate.net/profile/<Slug>: pass 2 = researcher name
                // (e.g. "David Benavides"). Bare researchgate.net is
                // excluded from host-only fallback.
                matches    = Onenote.FindPages(researchGate);
                matchType  = "related";
                searchTerm = researchGate;
            }
            else if (academiaEdu != null)
            {
                // <institution>.academia.edu/<CamelCaseName>: pass 2 = name
                // with spaces (e.g. "Francisco Benitez"). academia.edu and
                // its subdomains are excluded from host-only fallback.
                matches    = Onenote.FindPages(academiaEdu);
                matchType  = "related";
                searchTerm = academiaEdu;
            }
            else if (wikiTitle != null)
            {
                // *.wikipedia.org/wiki/<Title>: pass 2 = article title
                // (e.g. "Barcamp", "Albert Einstein"). Bare wikipedia.org
                // is excluded from host-only fallback.
                matches    = Onenote.FindPages(wikiTitle);
                matchType  = "related";
                searchTerm = wikiTitle;
            }
            else if (!HostFallbackPolicy.ShouldSkipHostFallback(normalized))
            {
                // Generic: host-only fallback.
                matches    = Onenote.FindPages(normalized.Host);
                matchType  = "host-only";
                searchTerm = normalized.Host;

                // Last resort: domain core (e.g. docs.slipshow.org → "slipshow")
                // so URLs whose subdomain isn't in any OneNote page still
                // surface notes about the project itself. First try phrase
                // search; if still empty, retry with tokenised (unquoted)
                // search — catches CamelCase / compound-word occurrences like
                // "OneMoreaddin" that phrase search misses when the surrounding
                // page text doesn't contain the literal core string.
                if (matches.Count == 0)
                {
                    var core = DomainCoreHelper.ExtractDomainCore(normalized.Host);
                    if (!string.IsNullOrEmpty(core))
                    {
                        matches    = Onenote.FindPages(core);
                        matchType  = "related";
                        searchTerm = core;

                        if (matches.Count == 0)
                            matches = Onenote.FindPages(core, exact: false);
                    }
                }
            }
        }

        var total = matches.Count;
        var limit = 9; // send up to 9; popup shows "and N more" only when total >= 10
        return new
        {
            requestId,
            ok = true,
            total,
            matches = matches.Take(limit).Select(m => new
            {
                pageId       = m.PageId,
                title        = m.Title,
                notebookName = m.NotebookName,
                sectionName  = m.SectionName,
                matchType,
                searchTerm,
            }),
        };
    }

    // ── searchText ────────────────────────────────────────────────────────────

    // Free-text OneNote search. Used for fallbacks where the search term comes
    // from page DOM scraping (e.g. dblp ORCID/author name) rather than a URL.
    private object SearchText(string? requestId, JsonElement msg)
    {
        var term = msg.TryGetProperty("term", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(term))
            return Error(requestId, "BAD_REQUEST", "Missing 'term'.");

        var matches = Onenote.FindPages(term);
        var total   = matches.Count;
        var limit   = 9;
        return new
        {
            requestId,
            ok = true,
            total,
            matches = matches.Take(limit).Select(m => new
            {
                pageId       = m.PageId,
                title        = m.Title,
                notebookName = m.NotebookName,
                sectionName  = m.SectionName,
                matchType    = "related",
                searchTerm   = term,
            }),
        };
    }

    // ── focus ─────────────────────────────────────────────────────────────────

    private object Focus(string? requestId, JsonElement msg)
    {
        var pageId = msg.TryGetProperty("pageId", out var p) ? p.GetString() : null;
        if (string.IsNullOrEmpty(pageId))
            return Error(requestId, "BAD_REQUEST", "Missing 'pageId'.");

        // searchTerm is already the exact string that was sent to OneNote's
        // search when the match was produced (see req~ank.match.search-term~1):
        // a normalised URL for exact/host-only, or free text for related
        // matches (LinkedIn name, app slug, dblp ORCID/name, …). Use it as-is.
        var query = msg.TryGetProperty("query", out var q) ? q.GetString() : null;

        if (!string.IsNullOrWhiteSpace(query))
            Onenote.FocusAndSearch(pageId, query);
        else
            Onenote.NavigateTo(pageId);

        return new { requestId, ok = true };
    }

    // ── openSearch ────────────────────────────────────────────────────────────

    private object OpenSearch(string? requestId, JsonElement msg)
    {
        var url = msg.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (string.IsNullOrEmpty(url))
            return Error(requestId, "BAD_REQUEST", "Missing 'query'.");

        var normalized = UrlNormalizer.TryNormalize(url);
        var searchTerm = normalized?.Normalized ?? url;

        Onenote.OpenSearch(searchTerm);
        return new { requestId, ok = true };
    }

    // ── appendBullet ──────────────────────────────────────────────────────────

    private object AppendBullet(string? requestId, JsonElement msg)
    {
        var text = msg.TryGetProperty("text", out var t) ? t.GetString() : null;
        var url  = msg.TryGetProperty("url",  out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(url))
            return Error(requestId, "BAD_REQUEST", "Missing 'text' or 'url'.");

        Onenote.AppendBullet(text, url);
        return new { requestId, ok = true };
    }

    // ── listNotebooks ─────────────────────────────────────────────────────────

    private object ListNotebooks(string? requestId)
    {
        var notebooks = Onenote.ListNotebooks();
        return new
        {
            requestId,
            ok = true,
            notebooks = notebooks.Select(nb => new
            {
                notebookId   = nb.NotebookId,
                notebookName = nb.NotebookName,
                sections     = nb.Sections.Select(s => new
                {
                    sectionId   = s.SectionId,
                    sectionName = s.SectionName,
                }),
            }),
        };
    }

    // ── addPage ───────────────────────────────────────────────────────────────

    private object AddPage(string? requestId, JsonElement msg)
    {
        var sectionId = msg.TryGetProperty("sectionId", out var s) ? s.GetString() : null;
        var title     = msg.TryGetProperty("title",     out var t) ? t.GetString() : null;
        var url       = msg.TryGetProperty("url",       out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(sectionId) || string.IsNullOrEmpty(url))
            return Error(requestId, "BAD_REQUEST", "Missing sectionId or url.");
        var pageId = Onenote.AddPage(sectionId, title ?? url, url);
        return new { requestId, ok = true, pageId };
    }

    // ── status ────────────────────────────────────────────────────────────────

    private static object Status(string? requestId) => new { requestId, ok = true };

    // ── helpers ───────────────────────────────────────────────────────────────

    private static object Error(string? requestId, string code, string message) => new
    {
        requestId,
        ok = false,
        error = new { code, message },
    };

    public void Dispose()
    {
        if (_onenote is IDisposable d) d.Dispose();
    }
}
