namespace AnchorHub.Core;

/// <summary>
/// Domains for which the generic host-only fallback lookup is suppressed.
/// Searching just "spiegel.de" in OneNote returns hundreds of unrelated
/// articles and is never useful. Group names are informational only.
/// </summary>
public static class HostFallbackPolicy
{
    // News sites — host alone matches every saved article, not this one.
    private static readonly HashSet<string> NewsSites = new(StringComparer.OrdinalIgnoreCase)
    {
        "spiegel.de",
        "zeit.de",
        "heise.de",
        "tagesschau.de",
        "golem.de",
    };

    // Aggregators / high-traffic portals with the same problem.
    private static readonly HashSet<string> Aggregators = new(StringComparer.OrdinalIgnoreCase)
    {
        "npmjs.com",
        "ctan.org",
        "researchgate.net",
        "alternativeto.net",
        "noteapps.info",
        "overleaf.com",
        "community.chocolatey.org",
        "addons.mozilla.org",
        "gist.github.com",
        // doi.org — every saved paper carries a DOI link; host-only matches all of them.
        "doi.org",
        // mdpi.com — every saved MDPI paper. Identity is in /<journal>/<vol>/<issue>/<id>.
        "mdpi.com",
        // lists.gi.de — Gesellschaft für Informatik HyperKitty mailing-list
        // archive. Every saved list / thread / message lives under this host;
        // identity is in /hyperkitty/list/<list-address>/[thread/<msgid>].
        "lists.gi.de",
        // idealo.de — host alone matches every product page the user ever
        // saved. Product identity comes from the slug; see IdealoUrlHelper.
        "idealo.de",
        "idealo.at",
        "idealo.fr",
        "idealo.it",
        "idealo.es",
        "idealo.co.uk",
    };

    // Blog hosters — one blog per <name>.<hoster> subdomain (and posts on
    // the apex). The hoster token is shared across all their blogs, as with
    // github.io, so host-only matches every saved blog there.
    private static readonly string[] BlogHosters =
        ["blogspot.com", "wordpress.com", "medium.com", "substack.com"];

    // Academic indexes — host alone matches every paper/author the user ever saved.
    private static readonly HashSet<string> AcademicSites = new(StringComparer.OrdinalIgnoreCase)
    {
        // dblp — canonical host plus officially-maintained mirrors.
        "dblp.org",
        "dblp.uni-trier.de",
        "dblp.dagstuhl.de",
        "orcid.org",
        "arxiv.org",
        "link.springer.com",
        "ieeexplore.ieee.org",
        "dl.acm.org",
        // Institutional research-information systems (FIS / CRIS portals).
        // Each entity URL is a unique publication/person/project; the bare
        // host matches every saved entry from that institution.
        "fis.uni-bamberg.de",
    };

    // Learning platforms (LMS, course portals) — host alone matches every
    // course/material page the user has saved from that platform.
    private static readonly HashSet<string> LearningPlatforms = new(StringComparer.OrdinalIgnoreCase)
    {
        "lernen.min.uni-hamburg.de",
        // O'Reilly online learning — host alone matches every saved book /
        // video / course. Identity is in /library/view/<slug>/<id>/.
        "learning.oreilly.com",
    };

    // Mapping portals — every saved place/way/node/relation lives under the
    // same host; identity is in the path (e.g. /way/12345, /node/678,
    // /search?query=...). Bare host matches every saved map page.
    private static readonly HashSet<string> MappingPortals = new(StringComparer.OrdinalIgnoreCase)
    {
        "openstreetmap.org",
    };

    // Web-app shells — the host serves a single-page application whose URL
    // is interface state (Gmail's #inbox, a calendar view), never a stable
    // identifier for any content. Skipped entirely (pass 1 included) so the
    // OneNote fuzzy search doesn't match unrelated saved pages on the path
    // tokens.
    private static readonly HashSet<string> WebAppShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "mail.google.com",     // Gmail
        "calendar.google.com", // Google Calendar
    };

    // Nextcloud instances — host alone matches every file/share/calendar
    // page the user has saved from that instance.
    private static readonly HashSet<string> NextcloudInstances = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbicloud.informatik.uni-hamburg.de",
    };

    // RFC-like specification portals — host alone matches every spec the
    // user saved (JEPs, RFCs, Python PEPs, etc.).
    private static readonly HashSet<string> SpecPortals = new(StringComparer.OrdinalIgnoreCase)
    {
        "openjdk.org",       // JEPs
        "rfc-editor.org",    // canonical RFC home
        "datatracker.ietf.org",
        "peps.python.org",   // Python PEPs
        "w3.org",            // W3C TR/REC/NOTE
    };

    // Social / share endpoints — every page on the web links to these via
    // share buttons (X, Facebook, …). Bare host matches every saved page
    // that ever had a share widget, not the actual post the user is on.
    private static readonly HashSet<string> SocialMedia = new(StringComparer.OrdinalIgnoreCase)
    {
        "x.com",
        "twitter.com",
        "facebook.com",
        "fb.com",
        "instagram.com",
        "threads.net",
        "tiktok.com",
        "pinterest.com",
        "youtube.com",
        "youtu.be",
        "reddit.com",
        "bsky.app",
        "mastodon.social",
        "t.me",
        "telegram.me",
        "wa.me",
        "whatsapp.com",
    };

    // Search engines — the host appears on every web page the user ever saved.
    private static readonly HashSet<string> SearchEngines = new(StringComparer.OrdinalIgnoreCase)
    {
        "bing.com",
        "duckduckgo.com",
        "ecosia.org",
        "startpage.com",
        "qwant.com",
        "yahoo.com",
        "search.brave.com",
        // Crossref's search UI — the whole subdomain is a result-page shell.
        // The query lives in ?q=, which normalisation strips, so every search
        // collapses to the bare root URL — a prefix of every saved
        // search.crossref.org link, producing false "exact" matches.
        "search.crossref.org",
        "kagi.com",
        "yandex.com",
        "yandex.ru",
        "baidu.com",
    };

    /// <summary>
    /// Returns true when the host-only fallback should be skipped for this URL.
    /// Strips a leading "www." before checking so both bare and www variants match.
    /// </summary>
    public static bool ShouldSkipHostFallback(NormalizedUrl url)
    {
        var host = StripWww(url.Host);

        // Google owns 100+ country TLDs (google.com, google.de, google.co.uk, …)
        // plus subdomains like scholar.google.com and maps.google.*. Each
        // citation/profile/place page has a unique path; the bare host
        // matches every saved Google page.
        if (host.StartsWith("google.", StringComparison.OrdinalIgnoreCase) ||
            host.Contains(".google.", StringComparison.OrdinalIgnoreCase))
            return true;

        // Amazon also has many country TLDs (amazon.com, amazon.de, amazon.co.uk, …).
        if (host.StartsWith("amazon.", StringComparison.OrdinalIgnoreCase))
            return true;

        // Academia.edu uses per-institution subdomains (<institution>.academia.edu);
        // host-only would match every profile the user has saved from that
        // institution. Skip the bare host and any subdomain.
        if (host.Equals("academia.edu", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".academia.edu", StringComparison.OrdinalIgnoreCase))
            return true;

        // LinkedIn — bare linkedin.com matches every saved LinkedIn page (every
        // /in/, /company/, /posts/, /feed/, /pub/, /dir/, /jobs/ URL). LinkedIn
        // also serves locale-specific subdomains (de.linkedin.com, fr.linkedin.com,
        // …) that are equally noisy. Identity lives in the path; the LinkedIn
        // helpers extract person/company names where possible, but for URL types
        // they don't recognise (e.g. /dir/, /pub/) we'd rather show "no match"
        // than a misleading host-only hit.
        if (host.Equals("linkedin.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".linkedin.com", StringComparison.OrdinalIgnoreCase))
            return true;

        // Wikipedia has a per-language subdomain (en/de/fr/…); host-only would
        // match every Wikipedia article the user has saved.
        if (WikipediaUrlHelper.IsWikipediaHost(url.Host))
            return true;

        // GitHub Pages serves one site per <user-or-org>.github.io subdomain
        // (plus the bare github.io apex). The host token "github.io" is shared
        // by every saved Pages site, so host-only matches all of them.
        if (host.Equals("github.io", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".github.io", StringComparison.OrdinalIgnoreCase))
            return true;

        // Blogs (blog.jetbrains.com, blogs.oracle.com) — the host matches every
        // saved post of that blog, not the one being read.
        if (host.StartsWith("blog.", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("blogs.", StringComparison.OrdinalIgnoreCase) ||
            BlogHosters.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                                 host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
            return true;

        return NewsSites.Contains(host)
            || Aggregators.Contains(host)
            || SearchEngines.Contains(host)
            || SocialMedia.Contains(host)
            || AcademicSites.Contains(host)
            || LearningPlatforms.Contains(host)
            || NextcloudInstances.Contains(host)
            || SpecPortals.Contains(host)
            || MappingPortals.Contains(host)
            || WebAppShells.Contains(host);
    }

    /// <summary>
    /// Returns true when the URL's host has no per-URL content identity
    /// (web-app shells like Gmail / Google Calendar). Callers skip *every*
    /// lookup pass for these hosts, returning an empty match list — distinct
    /// from <see cref="ShouldSkipHostFallback"/>, which only suppresses
    /// the host-only fallback pass while still letting pass 1 run.
    /// </summary>
    public static bool IsNoIdentityHost(NormalizedUrl url) =>
        WebAppShells.Contains(StripWww(url.Host));

    /// <summary>
    /// Returns true when the URL's host is a search engine (e.g. google.*,
    /// bing.com, duckduckgo.com). These hosts produce result-page URLs that
    /// are never useful as identifiers — searching the bare path
    /// (e.g. https://www.google.com/search) in OneNote would match every
    /// saved page that links to any Google search.
    /// </summary>
    public static bool IsSearchEngineHost(NormalizedUrl url)
    {
        var host = StripWww(url.Host);
        if (host.StartsWith("google.", StringComparison.OrdinalIgnoreCase))
            return true;
        return SearchEngines.Contains(host);
    }

    private static string StripWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
}
