# AnchorHub Requirements

## URL Matching

### URL normalisation
`req~ank.match.normalise~1`

Before any comparison, a raw URL is normalised:

- scheme is lowercased
- host is lowercased
- default ports (80 for http, 443 for https) are removed
- path is kept as-is except that a trailing `/` is removed from non-root paths
- query string and fragment are discarded

Example: `https://Example.com/page/?ref=1` → `https://example.com/page`

Needs: impl

### Exact match — pass 1
`req~ank.match.exact~1`

OneNote is searched for the normalised URL (scheme + host + path).
A result is tagged `matchType = "exact"`.

Needs: impl

### Forge-aware fallback — pass 2 / 3 / 4
`req~ank.match.forge-fallback~7`

For URLs whose host is a recognised code forge — `github.com`,
`gitlab.com`, or `codeberg.org` (with or without the `www.` prefix) —
if pass 1 returns no results:

**Forge deep links** (`<host>/<org>/<repo>/<…>`, e.g. specific issues,
PRs, commits, source files, wiki pages, discussions) skip every fallback
pass. Identity comes from the path beyond the repo root, so the
broader passes below would surface every saved page from the same
repo and mislead the user into thinking the specific item is in
OneNote. Either the exact URL is in OneNote or it isn't.

**Repo-identity deep links** are an exception: paths that semantically
resolve to the repo's home view share identity with the bare repo URL,
so the fallback cascade runs as for the repo root. The recognised
forms are:

- `<host>/<org>/<repo>/blob/<branch>/README[.ext]` — README at repo
  root, with an extension of `.md`, `.markdown`, `.rst`, `.txt`,
  `.adoc`, `.asciidoc`, `.org`, or no extension. Filename match is
  case-insensitive.
- `<host>/<org>/<repo>/tree/<branch>` — default-branch tree root, no
  further path.
- `<host>/<org>/<repo>/wiki` or `<host>/<org>/<repo>/wiki/Home` — wiki
  landing page.

GitLab's `/-/` path marker between repo and verb is tolerated, so
`gitlab.com/<org>/<repo>/-/blob/<branch>/README.md` also qualifies.

**Forge repo-level URLs** (`<host>/<org>/<repo>` and `<host>/<org>`):

- Pass 2 searches `<host>/<org>/<repo>` (e.g. `github.com/hakimel/reveal.js`,
  `gitlab.com/gitlab-org/gitlab`, `codeberg.org/forgejo/forgejo`).
- Pass 3 searches `<host>/<org>` (e.g. `github.com/hakimel`).
- Pass 4 searches the bare repository name (e.g. `reveal.js`) so that
  notes which mention the project by name without linking to it still
  surface. Pass 4 runs **only for distinctive repository names** — a
  name of at least 5 characters that is not a common developer word
  (`vale`, `core`, `utils`, `tools`, …). A bare common word matches
  many unrelated notes (e.g. `vale` surfaced 18 unrelated pages), which
  is worse than showing no match; passes 2 and 3 still find a genuinely
  saved repo. The blocklist lives in `ForgeUrlHelper.CommonProjectNames`
  and the test in `ForgeUrlHelper.IsDistinctiveProjectName`.
- Passes 2 and 3 phrase-anchor the org/repo to the host, so a generic
  name like `hakimel` doesn't match unrelated pages that happen to
  mention it. Pass 4 is the last resort and accepts that imprecision.
- No host-only pass is performed; the bare host (`github.com`,
  `gitlab.com`, `codeberg.org`) matches every saved link to that
  forge and is not a useful fallback.
- Forge-internal paths whose first segment is reserved by the forge
  (e.g. `github.com/topics/git-analysis`, `github.com/marketplace/…`,
  `gitlab.com/explore/…`, `codeberg.org/explore/…`) are *not*
  shortened. They look like an org/project pair but aren't, so passes
  2–4 are skipped and the lookup returns no results. The list of
  reserved first segments lives in `ForgeUrlHelper.ReservedFirstSegments`.

Results from these passes are tagged `matchType = "related"`.

Needs: impl

### W3C spec fallback
`req~ank.match.w3c-fallback~1`

For URLs whose host is `w3.org` (or `www.w3.org`), if pass 1 returns
no results, pass 2 searches OneNote for the spec acronym extracted
from the path:

- `w3.org/<spec>/…` → `<spec>` (e.g. `w3.org/OWL/` → `OWL`).
- `w3.org/TR/<spec>/…` → `<spec>` (e.g. `w3.org/TR/owl2-overview/`
  → `owl2-overview`); the leading `TR` segment is too generic to
  search on its own.

Bare `w3.org` is never searched — it would match every saved W3C
link. Results are tagged `matchType = "related"`.

Needs: impl

### Amazon product-title fallback
`req~ank.match.amazon-fallback~1`

For URLs whose host begins with `amazon.` (any country TLD), if
pass 1 returns no results and the path follows the form
`/<Title-Slug>/dp/<asin>/…`, pass 2 searches OneNote for the title
slug with hyphens converted to spaces (e.g.
`/Production-Workflow-Techniques-Frank-Leymann/dp/0130217530` →
`Production Workflow Techniques Frank Leymann`).

The slug typically includes both title and author tokens; OneNote's
phrase-matching means the search hits pages that mention the full
phrase. If only part of the title is recorded in OneNote, the
fallback may miss — refining the slug split is left as future work.

Bare `amazon.<tld>` is never searched. Results are tagged
`matchType = "related"`.

Needs: impl

### Academia.edu person-name fallback
`req~ank.match.academia-fallback~1`

For URLs whose host is `academia.edu` or any subdomain
(`<institution>.academia.edu`), if pass 1 returns no results and the
path is a single CamelCase segment, pass 2 searches OneNote for the
researcher's name with spaces inserted at lowercase→uppercase
boundaries (Unicode-aware):

- `aguapedia.academia.edu/FranciscoBenitez` → `Francisco Benitez`
- `harvard.academia.edu/JaneSmith` → `Jane Smith`
- `www.academia.edu/JoséGarcía` → `José García`

The slug must contain at least one lowercase letter — purely uppercase
single-segment paths (`/SEARCH`, `/HELP`) are likely reserved routes,
not profiles, and are skipped.

Bare `academia.edu` and any institution subdomain are excluded from
the host-only fallback (host alone matches every saved profile from
that institution). Results are tagged `matchType = "related"`.

Needs: impl

### ResearchGate person-name fallback
`req~ank.match.researchgate-fallback~1`

For URLs whose host is `researchgate.net` or `www.researchgate.net`,
if pass 1 returns no results and the path is
`/profile/<Name-Slug>`, pass 2 searches OneNote for the researcher's
name extracted from the slug:

1. Take the segment after `/profile/`.
2. Split on `-`.
3. If the last token is all digits, drop it (ResearchGate's trailing
   disambiguator).
4. Title-case each token and join with spaces.

Examples:

- `/profile/David-Benavides-7` → `David Benavides`
- `/profile/Mary-Anne-Smith-12` → `Mary Anne Smith`

Bare `researchgate.net` is excluded from host-only fallback (already
in the aggregator skip list). Results are tagged
`matchType = "related"`.

Needs: impl

### Idealo product-name fallback
`req~ank.match.idealo-fallback~1`

For URLs whose host begins with `idealo.` (any country TLD), if
pass 1 returns no results and the path follows the form
`/preisvergleich/OffersOfProduct/<id>_-<slug>.html`, pass 2 searches
OneNote for the slug with the leading `<id>_-` (or `<id>_`) product-id
prefix and trailing `.html` stripped, hyphens converted to spaces (e.g.
`202837261_-mx-anywhere-3s-grafit-logitech.html` →
`mx anywhere 3s grafit logitech`).

Bare `idealo.<tld>` is never searched (host alone matches every saved
product page). Idealo URLs whose path does not follow the
`/preisvergleich/OffersOfProduct/…` form (search pages, manufacturer
pages, …) get no fallback — the URL itself is the only check.
Results from the slug pass are tagged `matchType = "related"`.

Needs: impl

### GitHub gist fallback
`req~ank.match.gist-fallback~1`

For URLs whose host is `gist.github.com` (e.g.
`gist.github.com/<user>/<gist-id>`), if pass 1 returns no results:

- Pass 2 searches `gist.github.com/<user>` to surface other gists from
  the same author.
- The cascade stops there. Bare `gist.github.com` is never searched —
  it would match every saved gist link.

Results are tagged `matchType = "related"`.

Needs: impl

### Reddit subreddit/user fallback
`req~ank.match.reddit-fallback~2`

For URLs whose host is `reddit.com`, `www.reddit.com`, `old.reddit.com`, or
`new.reddit.com`, if pass 1 returns no results:

- For `/r/<subreddit>[/…]` paths, pass 2 searches the host-anchored
  term `reddit.com/r/<subreddit>` (e.g. `reddit.com/r/docker`). The
  bare subreddit name on its own (e.g. `docker`) would match every
  OneNote page that mentions Docker, so the host anchor is required.
- For `/u/<user>[/…]` paths, pass 2 searches `reddit.com/u/<user>`
  (host-anchored for the same reason).
- No host-only pass is performed; `reddit.com` alone is not a useful term.

Results are tagged `matchType = "related"`.

Needs: impl

### LinkedIn person-name fallback
`req~ank.match.linkedin-fallback~3`

For URLs whose host is `linkedin.com` or `www.linkedin.com`, if pass 1
returns no results:

**Personal profile (`/in/<slug>`):**

Pass 2 searches OneNote using the person's name extracted from the URL slug:

1. Take the first path segment after `/in/` (e.g. `julia-hertel-2014a81ab`).
2. Split on `-`.
3. If the last token contains any digit, discard it (LinkedIn's trailing hash ID).
4. Title-case the remaining tokens and join with spaces (e.g. `Julia Hertel`).

**Company page (`/company/<slug>[/…]`):**

- Pass 2 searches the canonical company URL
  `https://www.linkedin.com/company/<slug>` (host-anchored substring,
  matches any saved page under the same company regardless of trailing
  segments like `/posts`, `/about`, `/people`, …).
- Pass 3 searches the bare slug (e.g. `tragai`) so notes that mention
  the company by name without linking to it still surface.
- The parent path `linkedin.com/company` and the bare host
  `linkedin.com` are never searched — both match every saved company
  page.

**Any other LinkedIn URL** (`/posts/`, `/feed/`, `/pulse/`, `/dir/`,
`/pub/`, `/jobs/`, locale-specific subdomains like `de.linkedin.com`,
…) has no helper-derived term and goes straight to "no match".

No host-only pass is performed for *any* LinkedIn URL — bare
`linkedin.com` (and any `*.linkedin.com` subdomain) matches every
saved LinkedIn page, which is never a useful identification. Results
from the helper passes are tagged `matchType = "related"`.

Needs: impl

### App-catalog slug fallback
`req~ank.match.app-catalog-fallback~2`

For URLs whose host is an app/software catalog and whose path ends in
`…/{marker}/{slug}` (the marker segment sits immediately before the
slug), if pass 1 returns no results, pass 2 searches OneNote for the
slug alone (e.g. `logseq`, `web-highlights-pdf-web-highlig`).

Catalog hosts and their marker segments:

| Host | Marker segment | Example path |
|---|---|---|
| `alternativeto.net`  | `software` | `/software/<slug>`                |
| `noteapps.info`      | `apps`     | `/apps/<slug>`                    |
| `addons.mozilla.org` | `addon`    | `/<lang>/firefox/addon/<slug>`    |

No host-only pass is performed; the catalog host alone matches every
saved app and is not a useful fallback term. Catalog URLs whose path
does not end in `…/{marker}/{slug}` (e.g. `alternativeto.net/news/…`,
`addons.mozilla.org/<lang>/firefox/`) are ignored by this rule and
follow the generic host-fallback policy (which suppresses these
hosts as well).

Results are tagged `matchType = "related"`.

Needs: impl

### dblp person-page fallback
`req~ank.match.dblp-fallback~2`

For URLs whose host is `dblp.org` *or* one of its officially-maintained
mirrors — `dblp.uni-trier.de` (Trier) or `dblp.dagstuhl.de` (Dagstuhl) —
and whose path starts with `/pid/` (dblp person pages), if pass 1
returns no results:

1. Pass 2 scrapes the person's ORCID from the page DOM
   (`a[href*="orcid.org/"]`) and looks up the canonical
   `https://orcid.org/<orcid>` URL in OneNote.
2. Pass 3 scrapes the person's display name from the page header
   (`span[itemprop="name"]`) and runs a free-text OneNote search for it
   (e.g. `Barbara König`).

Both passes are tagged `matchType = "related"`. No host-only pass is
performed; all three dblp hostnames plus `orcid.org` are excluded from
the host-only fallback (the host alone matches every saved publication
or author).

Needs: impl

### Wikipedia article-title fallback
`req~ank.match.wikipedia-fallback~1`

For URLs whose host is `wikipedia.org` or any language subdomain
(`en.wikipedia.org`, `de.wikipedia.org`, `simple.wikipedia.org`, …)
and whose path is `/wiki/<Title>`, if pass 1 returns no results,
pass 2 searches OneNote for the article title.

The title is extracted from the slug by URL-decoding percent-escapes
and replacing underscores with spaces:

- `/wiki/Barcamp` → `Barcamp`
- `/wiki/Albert_Einstein` → `Albert Einstein`
- `/wiki/Caf%C3%A9` → `Café`

Bare `wikipedia.org` (and any of its language subdomains) is excluded
from the host-only fallback. Results are tagged `matchType = "related"`.

Needs: impl

### Google Scholar profile canonicalisation
`req~ank.match.scholar-profile~1`

For Scholar profile URLs of the form
`scholar.google.<tld>/citations?user=<id>[&hl=…]`, Pass 1 substitutes
the canonical search term `scholar.google.com/citations?user=<id>` for
the normalised URL. The `?user=<id>` query parameter is the actual
identity of a Scholar profile — the path `/citations` alone is shared
by every profile on Scholar — and the generic URL normaliser strips
queries, so without this override Pass 1 would match every saved
Scholar profile.

The canonical term drops every other query parameter (notably `hl=`,
the locale) and folds the host back to `scholar.google.com` regardless
of which TLD the user originally browsed from, since Scholar redirects
all locales to the .com host.

Results are tagged `matchType = "exact"`. The canonical term is also
used as the `searchTerm` field returned to the extension, so subsequent
Ctrl+F calls highlight the saved URL on the OneNote page.

Bare `scholar.google.com` is excluded from the host-only fallback (see
`req~ank.match.host-fallback~2` — covered by the generic Google-host
suppression).

Needs: impl

### Google Scholar non-profile URL exclusion
`req~ank.match.scholar-no-identity~1`

Scholar URLs that are not author profiles — paper search result pages
(`/scholar?q=…`, `/scholar?cluster=…`), individual citation deep links
(`/citations?view_op=view_citation&user=<id>&citation_for_view=<id>:<token>`),
label/taxonomy pages (`/citations?mauthors=label:<topic>`), and the
host root — have no useful identity. Their normalised forms reduce to
shared path tokens (`scholar.google.com/scholar`,
`scholar.google.com/citations`) that match every saved Scholar page
under OneNote's fuzzy substring search, producing a false-positive
exact match on every link in an author's publication list.

For such URLs, both pass 1 and the host-only fallback are skipped and
the lookup returns an empty match list. The author-profile form
covered by `req~ank.match.scholar-profile~1`
(`/citations?user=<id>` with no `citation_for_view`) is the only
exempt shape.

Needs: impl

### Google Books volume canonicalisation
`req~ank.match.books-volume~1`

For Google Books volume URLs of the form
`books.google.<tld>/books?id=<volumeId>[&…]`, Pass 1 substitutes the
bare volume id `<volumeId>` for the normalised URL. The `?id=<volumeId>`
query parameter is the actual identity of a book — the path `/books`
alone is shared by every Google Books link, across every locale TLD
(`books.google.de`, `books.google.com`, …) — and the generic URL
normaliser strips queries, so without this override Pass 1 searches
`books.google.<tld>/books` and matches every OneNote page that contains
any Google Books link (observed: a "Verschwörungstheorien" note matched
as an exact hit on an unrelated software-quality book result).

The volume id is opaque and highly distinctive, so searching it bare
matches a saved link to the same volume regardless of the locale TLD it
was captured under. Results are tagged `matchType = "exact"`; the id is
also the `searchTerm` field returned to the extension.

### Google Books non-volume URL exclusion
`req~ank.match.books-no-identity~1`

Google Books URLs under `books.google.<tld>` without an `?id=<volumeId>`
parameter — the bare `/books` path, in-book search pages, `/books/about`
pages, and the host root — have no useful identity. Their normalised
forms reduce to the shared `/books` path token that matches every saved
Google Books page. For such URLs, both Pass 1 and the host-only fallback
are skipped and the lookup returns an empty match list. A volume URL
with `?id=<volumeId>` (covered by `req~ank.match.books-volume~1`) is the
only exempt shape.

Needs: impl

### Search-engine URL exclusion
`req~ank.match.search-engine-skip~2`

When the active tab's host is a known search engine (any `google.*`
country TLD, `bing.com`, `duckduckgo.com`, `ecosia.org`, `startpage.com`,
`qwant.com`, `yahoo.com`, `search.brave.com`, `search.crossref.org`,
`kagi.com`, `yandex.com`, `yandex.ru`, `baidu.com`), no OneNote lookup
is performed. The URL
produced by the engine (e.g. `https://www.google.com/search?q=…`) is
not a unique identifier; matching its normalised form
(`https://www.google.com/search`) would surface every saved page that
contains any Google-search link.

Both pass 1 and the host-only fallback are skipped; the icon shows the
no-match state.

Needs: impl

### Web-app shell URL exclusion
`req~ank.match.web-app-shell-skip~1`

Some hosts serve a single-page application whose URL is interface
state (mailbox folder, calendar view) rather than a content identifier.
The normalised URL strips the `#fragment` that distinguishes one
interface state from another (`mail.google.com/mail/u/0/#inbox` →
`https://mail.google.com/mail/u/0`), so all pass-1 lookups for these
hosts would collapse onto a single non-identifying string and fuzzy-
match unrelated saved pages on the path tokens.

When the active tab's host is a known web-app shell, no OneNote lookup
is performed; both pass 1 and the host-only fallback are skipped and
the icon shows the no-match state. The list lives in
`HostFallbackPolicy.WebAppShells` and currently covers
`mail.google.com` (Gmail) and `calendar.google.com` (Google Calendar).

Needs: impl

### Host-only fallback — pass 2
`req~ank.match.host-fallback~2`

For URLs not handled by a more specific helper (forge, gist, LinkedIn,
Reddit, app catalog, w3.org, Amazon, dblp/ORCID, …), if pass 1 returns
no results, OneNote is searched again using only the host. Results
from this pass are tagged `matchType = "host-only"`. Pass 2 is skipped
when pass 1 returns at least one result.

Needs: impl

### Social media — no host-only fallback
`req~ank.match.social-no-fallback~1`

Share buttons embedded on virtually every web page point at social
networks (`x.com/intent/tweet`, `facebook.com/sharer/sharer.php`,
`reddit.com/submit`, …). Bare social-media hosts therefore match
every saved page that ever carried a share widget — not the post the
user is actually on. The host-only fallback is suppressed for known
social-network hosts.

The list lives in `HostFallbackPolicy.SocialMedia` and currently
covers `x.com`, `twitter.com`, `facebook.com`, `fb.com`,
`instagram.com`, `threads.net`, `tiktok.com`, `pinterest.com`,
`youtube.com`, `youtu.be`, `reddit.com`, `bsky.app`,
`mastodon.social`, `t.me`, `telegram.me`, `wa.me`, `whatsapp.com`.

Needs: impl

### Learning platforms — no host-only fallback
`req~ank.match.learning-no-fallback~1`

Learning platforms (LMS, course portals, online-book services) serve
every saved book / video / course / material page under a single
hostname. Identity lives in the path (e.g.
`learning.oreilly.com/library/view/<slug>/<id>/`), so the bare host
matches every saved page from the platform. The host-only fallback is
suppressed for known learning-platform hosts.

The list lives in `HostFallbackPolicy.LearningPlatforms` and is
extended on demand. Currently:

- `lernen.min.uni-hamburg.de`
- `learning.oreilly.com`

Needs: impl

### Mapping portals — no host-only fallback
`req~ank.match.mapping-no-fallback~1`

Mapping portals like OpenStreetMap serve every place, way, node,
relation, and search under a single hostname (`openstreetmap.org/way/…`,
`/node/…`, `/relation/…`, `/search?query=…`). Identity lives entirely
in the path, so the bare host matches every saved map page. The
host-only fallback is suppressed for known mapping-portal hosts.

The list lives in `HostFallbackPolicy.MappingPortals` and currently
covers `openstreetmap.org`.

Needs: impl

### Nextcloud instances — no host-only fallback
`req~ank.match.nextcloud-no-fallback~1`

Self-hosted Nextcloud instances host every kind of saved page (files,
shares, calendar entries, deck cards, …) under a single hostname.
Searching the bare host returns hundreds of unrelated results. For this
reason, the host-only fallback is suppressed for known Nextcloud hosts.

The list lives in `HostFallbackPolicy.NextcloudInstances` and is
extended on demand. Currently:

- `fbicloud.informatik.uni-hamburg.de`

Needs: impl

### Blogs — no host-only fallback
`req~ank.match.blog-no-fallback~4`

A blog host (`blog.jetbrains.com`, `blogs.oracle.com`) matches every
saved post of that blog, not the post being read. The host-only
fallback (and with it the domain-core fallback) is suppressed for any
host whose first label, after an optional `www.`, is `blog` or `blogs`,
and for the blog hosters `blogspot.com`, `wordpress.com`, `medium.com`,
`substack.com`, `ghost.io` and `hashnode.dev` and any of their subdomains
(their shared hoster token would also match every other blog there).

Needs: impl

### GitHub Pages — no host-only fallback
`req~ank.match.github-pages-no-fallback~1`

GitHub Pages serves one independent site per
`<user-or-org>.github.io` subdomain (plus the bare `github.io` apex).
OneNote's tokenised search splits the host on dots, so a host-only
search for `aeondesktop.github.io` matches the shared `github.io`
token in every saved GitHub Pages URL — returning a sea of unrelated
hits. The host-only fallback (and, by inclusion in the exclusion
list, the domain-core fallback whose core would be `github`) is
therefore suppressed for the bare `github.io` host and any
`*.github.io` subdomain.

Needs: impl

### Domain-core last-resort fallback
`req~ank.match.domain-core-fallback~2`

When pass 1 (exact URL) and the generic host-only fallback both
return no results, OneNote is searched once more using the
domain core: the second-to-last dot-separated segment of the host
(e.g. `docs.slipshow.org` → `slipshow`, `slipshow.org` → `slipshow`).

This catches the common case where the user has a OneNote page
about the project itself (titled e.g. "Slipshow") even though the
specific subdomain URL isn't recorded anywhere.

The domain core is searched twice: first as a quoted phrase
(default behaviour), then — if still empty — as a tokenised
(unquoted) query. The second pass catches CamelCase / compound-word
occurrences such as `onemoreaddin.com` → "onemoreaddin" matching a
page that contains "OneMoreaddin" embedded in a word, where the
quoted phrase search returns nothing.

The fallback is suppressed when the extracted core would be shorter
than 4 characters — this filters compound TLDs like `co.uk` or
`com.au` whose second-to-last segment is the registry SLD, not the
project name.

Skipped entirely for hosts already in the host-fallback exclusion
list. Results are tagged `matchType = "related"`.

Needs: impl

### Search term recorded per match
`req~ank.match.search-term~2`

Every match object carries the exact string that was submitted to OneNote's
search engine (`searchTerm`): the normalised URL for exact matches, the host
for host-only matches, and a free-text term (e.g. a person name, app slug,
or canonical identifier URL) for related matches.
Subsequent operations (find-on-page, Ctrl+E) use this recorded term verbatim,
without re-normalisation; non-URL search terms must round-trip unchanged.

Needs: impl

### OneMore full-text index preferred
`req~ank.match.onemore-index~2`

Every OneNote text search (URL, host, and free-text passes alike) first
queries the full-text index of the OneMore add-in
(`%LOCALAPPDATA%\OneMore\OneMore.db`, FTS5 trigram) as a
case-insensitive substring match. Hits are mapped to the pages currently
in OneNote's hierarchy; index entries whose page ID OneNote no longer
knows are dropped.

OneNote's own search is used instead when search indexing is turned
off in OneMore (its `Settings.xml` setting, or — when unset — OneMore's
installer default), the database is absent or unreadable, the query is shorter than 3 characters, or the index yields
no current page — so pages OneMore has not indexed yet are still found.

Needs: impl

## Page-embedded citations

### BibTeX-on-page detection
`req~ank.match.bibtex-on-page~4`

Pages that render a BibTeX entry inside a `<pre>` / `<pre><code>`
block (typical case: a "Citation" section in a GitHub repository
README) cause the extension to look up the entry's identifiers as
if the user were on the cited resource itself, regardless of the
hosting page's own URL.

Detection:

- The content of every `<pre>` and `<pre><code>` element is scanned
  for top-level `@<entryType>{<citationKey>, … }` blocks. Brace
  balancing is used to find the entry close; nested braces inside
  field values are tolerated.
- Each entry's fields are parsed (`key = {value}` or
  `key = "value"`). The relevant fields are `url`, `doi`, `eprint`,
  and `archivePrefix`. Field names are matched case-insensitively.
- The exact source text spanned by each entry is also captured
  verbatim so the popup can render the entry as it appears on the
  page.

Lookups performed for every detected entry:

- **OneNote** — `entry.url` is looked up verbatim via the normal
  pipeline (same passes as a tab navigation: normalisation, exact,
  fallbacks). Results are tagged `matchType = "exact"` with
  `searchTerm = entry.url`.
- **JabRef** — `entry.doi`, `entry.eprint`, and `entry.url` are
  collected across all entries; each value becomes its own Search.g4
  expression (`doi =~i "…"`, `url =~i "…"`, `eprint =~i "…"`). All
  expressions are sent in a single `/libraries/query` POST under the
  `queries` list; the endpoint returns a `results` list in the same
  order. The match lists are flattened and de-duplicated by
  `libraryId` + `citationKey`. `=~i` (contains) matches a DOI whether
  it is stored bare or `https://doi.org/`-prefixed.

UI:

- **Popup** — a dedicated popup section titled "Reference" shows
  each detected entry's verbatim BibTeX source in a monospaced
  block, followed by OneNote and JabRef match rows. The section
  is suppressed when no BibTeX is detected.
- **In-page bubble** — for each `<pre>` block carrying a BibTeX
  entry, the content script injects a status marker floated to the
  top-right of the block (visually beside the `@<entryType>{…,`
  line). The marker's status (green / teal / grey) reflects the
  OneNote lookup of the entry's `url` field (or
  `https://arxiv.org/abs/<eprint>` when `url` is absent). Clicking
  the marker opens the popup against that URL.

Detection runs only when at least one adapter is enabled
(`req~ank.settings.adapter-toggle~1`); the URL / DOI / eprint sub
queries respect each adapter's enable flag independently.

Needs: impl

## Toolbar Icon

### Colour palette and shape encoding
`req~ank.icon.colour-palette~5`

Icon colours are taken from Paul Tol's colour-blind-safe
[bright and vibrant qualitative schemes](https://sronpersonalpages.nl/~pault/).

**Design principle: shape encodes the lookup phase, colour encodes
the kind of result.** Shape gives colour-blind users a second
discriminator and separates "still working" from "completed".

- **Solid ship's anchor** (full canvas size) — a result has come back.
  Colour says which: green = exact match, teal = host-only / related,
  orange = daemon not connected.
- **Thin-stroke grey anchor** (full canvas size) — lookup completed, no
  OneNote match. Same grey hue as the searching anchor; stroke weight
  and size alone distinguish the lookup phase (thin vs small solid
  vs full solid anchor). Keeps link-dense pages quiet — no "red
  sea" of negatives when most links aren't anchored.
- **Small solid grey anchor** (~60% of canvas, centred) — lookup in
  progress. Smaller-than-result footprint so a result icon visibly
  "enlarges" from the same centre when the answer lands.

Traffic-light intuition still applies for the result icons: green >
teal > orange. When several states could apply at once the more
diagnostic state wins.

| State | Icon |
|---|---|
| Exact match | mint anchor, `#44BB99` |
| Host-only match | olive anchor, `#AAAA00` |
| Not connected | orange anchor, `#EE7733` |
| No match | thin grey anchor, `#BBBBBB` |
| Searching | small grey anchor, `#BBBBBB` |

The mint+olive pair is intentionally drawn from Tol's light scheme:
the previous bright-green (`#228833`) + teal (`#009988`) pair was hard
to tell apart at 16px since both read as "darker green". Mint and
olive sit clearly apart in hue (green-cyan vs yellow-green).

Needs: impl

### Exact-match icon
`req~ank.icon.exact~4`

When at least one exact match exists for the active tab's URL the icon is
shown in the exact-match colour (`#44BB99`, mint).
The tooltip reads "In OneNote: \<title\>" for a single match, or
"In OneNote (\<n\> pages)" for multiple.

Needs: impl

### Host-only icon
`req~ank.icon.host-only~3`

When no exact match exists but at least one host-only match exists, the icon
is shown in the host-only colour (`#AAAA00`, olive).
The tooltip reads "Same site in OneNote: \<title\>" or
"Same site in OneNote (\<n\> pages)".

Needs: impl

### Disconnected icon
`req~ank.icon.disconnected~2`

When the native messaging host is not reachable, all tabs show the
not-connected colour (`#EE7733`, orange).

Needs: impl

### No-match icon
`req~ank.icon.no-match~6`

When the host is connected but no match is found, the icon shows a
**hollow grey ring** (`#BBBBBB` outline on transparent fill, full
canvas size — `notfound-*.png`). Same grey hue as the searching
square; shape alone (hollow ring vs small filled square vs full
filled circle) distinguishes the lookup phase. Keeps link-dense pages
visually quiet by avoiding a sea of saturated negatives.

Needs: impl

### Searching icon
`req~ank.icon.searching~3`

While a lookup is in progress (between page-navigation / tab-switch
and the first result), the icon shows a **small centred gray anchor**
(`#BBBBBB` solid, ~60% of canvas, `default-*.png`). The smaller
footprint means a result icon (solid / thin anchor) visibly
"enlarges" from the same centre when the answer lands.
The tooltip reads "AnchorHub — searching…".

Needs: impl

### Match-count badge
`req~ank.icon.count-badge~2`

When at least one OneNote match exists for the active tab's URL the
icon shows a count badge bucketed as follows:

| Total matches | Badge text |
|---|---|
| 1     | `1`   |
| 2–4   | `2+`  |
| 5–9   | `5+`  |
| ≥ 10  | `10+` |

The badge uses the exact-match colour (`#44BB99`, mint) with black text.
The OneNote count badge takes priority over the JabRef indicator
(`J` for in-library, `!` for offline); when there are no OneNote
matches the previous JabRef-only badge logic applies. JabRef state
remains visible in the tooltip.

Needs: impl

### Paused badge
`req~ank.icon.paused~1`

"Pause AnchorHub" in the toolbar icon's context menu suspends all
per-tab lookups. While paused every tab shows the no-match icon with a
red (`#CC3311`) `OFF` badge in white text and the tooltip "AnchorHub —
paused". The badge survives lookups still in flight at pause time and
is restored on browser start when the paused flag persists. "Resume
AnchorHub" clears it and re-runs the active tab's lookup.

Needs: impl

## Send Selection to OneNote

### Append selection as a bullet
`feat~append-selection-bullet~1`

The user can append the selected page text and the current URL to
the active OneNote page. Two trigger paths share the same handler:

- **Keyboard shortcut** `Ctrl+Alt+O` (rebindable in
  `about:addons` → AnchorHub → Manage Extension Shortcuts).
- **Right-click context menu on the selection** → "Send to OneNote
  with URL".

The hub brings OneNote to the foreground and types the selection
text, then `Enter`+`Tab`, then the URL at the current caret. OneNote
auto-bullets, so the result is the selection as one bullet with the
URL nested as a child bullet:

```
Selected text
    https://example.com/path
```

The user is responsible for placing the OneNote caret on the desired
page before triggering the action — the hub does not navigate,
choose a page, or save state. Special SendKeys characters
(`{ } + ^ % ~ ( ) [ ]`) in the selection text are escaped before
sending.

Browser-action middle-click is not used because WebExtension
toolbar buttons do not surface middle-click events when a popup is
configured.

Needs: impl

### Look up selection in adapters
`feat~selection-lookup-menu~2`

While the user has text selected, the AnchorHub root context-menu
item pre-populates with hits from every enabled adapter (OneNote
COM, Microsoft Graph, JabRef). The submenu shows:

- the "Send to OneNote with URL" append action;
- a separator;
- up to eight result rows merged across adapters.

Each row carries a source-specific icon:

- no prefix — OneNote local (COM)
- `☁` — OneNote cloud (Microsoft Graph)
- `📚` — JabRef library entry

Clicking a result row:

- **OneNote (COM)** focuses the page in OneNote and runs Ctrl+F with
  the search term that produced the hit;
- **OneNote (Graph)** opens the page's `clientUrl` (desktop OneNote
  protocol) or `webUrl` in a new tab;
- **JabRef** copies the citation key to the clipboard (the content
  script performs the copy because the service worker cannot reach
  `navigator.clipboard`).

The content script forwards every selection change to the background
on a 300 ms debounce, and again synchronously on `contextmenu`
(capture phase) so a fast right-click after selection still
populates the menu before Firefox renders it.

JabRef routing is content-driven; the selection becomes one Search.g4
expression sent in the `/libraries/query` `queries` list:

- text matching `^10\.\d{4,}/\S+$` — `doi =~i "<sel>"`;
- text matching `^https?://` — `url =~i "<sel>"`;
- otherwise — a free-text expression matching `author`, `title`,
  `keywords`, `abstract` with the case-insensitive `=~i` operator.

Selections shorter than 2 or longer than 80 characters reset the
menu to "select text to look up" without contacting any adapter.
Late-returning results from an outdated selection are dropped by
sequence number so they never overwrite menu items for a newer
selection.

Needs: impl

## Popup

### Sections
`req~ank.popup.sections~1`

The popup displays exact matches under the heading "Exact matches" and
host-only matches under "Same site".
A section is omitted when it contains no results.

Needs: impl

### Per-section find-status cue
`req~ank.popup.find-status~1`

Each adapter section (OneNote, JabRef) carries a header with the
adapter name and a status chip — green "found" when the section has at
least one result, gray "not found" when the lookup completed with
none. The whole section block also gets a coloured left accent and
faint background tint matching the chip (green = found, gray = empty),
so the user can tell at a glance which adapter holds the page without
reading the rows. The OneNote header reads `Name: "<name>"` in name
mode. A JabRef section that is offline shows no chip (the "JabRef
offline" line states the reason) and no tint.

Needs: impl

### Per-section cap
`req~ank.popup.cap~2`

Each section shows at most 9 match rows without an overflow row.
Only when the total result count for that section exceeds 9 is an overflow
row appended (the rows beyond 9 are not transmitted by the hub).
Do **not** show an overflow row when the total is 9 or fewer — even if the
hub payload happens to contain fewer items than the total.

Needs: impl

### Overflow row
`req~ank.popup.overflow-row~1`

The overflow row reads "and \<n\> more… (open in OneNote)".
Clicking it brings OneNote to the foreground and opens the global search
(Ctrl+E) pre-filled with the section's `searchTerm`.

Needs: impl

### Per-match search-term tooltip
`req~ank.popup.match-tooltip~1`

Each match row in the popup carries a browser tooltip
(`Matched: <searchTerm>`) so the user can see at a glance which
OneNote search produced this match. Useful for diagnosing surprising
matches (e.g. host-only fallback or related-match free-text terms).

Needs: impl

### Refresh button
`req~ank.popup.refresh~1`

The popup footer contains a "↺ Refresh" button.
Clicking it clears the URL cache entry for the active tab and re-runs the
lookup, then re-renders the popup with the fresh results.

Needs: impl

### Paused state
`req~ank.popup.paused~1`

While AnchorHub is paused the popup shows a single "✗ Disabled" line
instead of the adapter sections, so no "not found" / "offline" chip is
reported for a lookup that never ran. Copy link is hidden and Refresh
is disabled.

Needs: impl

## In-page overlay

### Marker status — JabRef-aware
`req~ank.overlay.marker-status~1`

Every in-page status marker (all scanners) is resolved against both
adapters. The hub looks the marker's URL up in OneNote and, in the
same batched call, in JabRef — a `doi.org` URL matches JabRef's `doi`
field, an `arxiv.org` URL its `eprint` field, a publisher URL that
embeds a DOI in its path (e.g. Springer `/book/10.1007/…`) its `doi`
or `url` field, and any other URL its `url` field.

The marker's icon and tooltip reflect the combined result:

- exact OneNote match — green icon, tooltip `In OneNote (N)`;
- same-site / related OneNote match — pale green icon, tooltip
  `Same site / related in OneNote (N)`;
- no OneNote match but a JabRef hit — green icon, tooltip
  `In JabRef (N)`;
- no match in either — grey icon, tooltip `Not found`.

When OneNote matched, a JabRef hit is appended to the tooltip as
`, also in JabRef (N)`. For aggregated markers (one marker, several
candidate URLs) the JabRef hit ranks above a OneNote same-site match
but below a OneNote exact match when picking the winning URL.

### GitHub topics / search status markers
`req~ank.overlay.github-markers~3`

On `https://github.com/topics/<topic>` and the GitHub repository search
results page (`https://github.com/search?…&type=repositories`), each
repository card receives a 16×16 status marker injected to the left of
the star button. The marker indicates whether the repo's URL is already
referenced in OneNote:

- green (exact icon) — repo URL appears in a OneNote page
- pale green (host icon) — same site / forge-related match (org or
  project name match)
- grey "not found" icon — no OneNote reference
- default checkbox icon — lookup in progress

The marker is anchored on the star `<form action="…/star">` (or
`/unstar` for already-starred repos), since the form action carries
`/owner/repo` directly and is the most stable identifier on the page.
The repo URL is `https://github.com/<owner>/<repo>`. A
`MutationObserver` re-scans for new cards as Turbo navigates between
pages.

On a repository's own project page the star/unstar form belongs to the
repo header, where a marker beside the star button reads far from the
repo it describes (GitHub renders the Watch/Fork/Star controls in a
right-aligned actions bar). In that case the marker is instead appended
to the repo-name heading row (`strong[itemprop="name"]`), so it reads
"&lt;owner&gt; / &lt;repo&gt; &lt;visibility&gt; ●".

The repo-page case is detected by matching the star form's
`/owner/repo` against the header repo-name link
(`strong[itemprop="name"] > a[href="/owner/repo"]`) — **not** by
whether the form sits inside `#repository-container-header`. GitHub
moved the star control out of that container into a separate sticky
actions bar, so a containment test places the marker far to the right
by the star button. A repo page renders both a star and an unstar form;
only the first yields a marker so the heading is not double-marked.

Needs: impl

### GitHub profile-sidebar status markers
`req~ank.overlay.github-profile-markers~1`

On `https://github.com/<user>` (and any `?tab=…` variant of the same
profile), each external link in the user's profile sidebar
(`ul.vcard-details > li > a[href]`) receives a status marker injected
*after* the link text. This covers the homepage, LinkedIn, BlueSky,
Twitter / X, Mastodon, YouTube and any other social link the user has
added to their profile.

The lookup URL is the link's literal absolute href; the hub's existing
per-site fallbacks (LinkedIn person-name, forge, host-only, etc.) then
apply normally. Self-links to the profile page itself are skipped.

Same status colour scheme as the GitHub repo markers; click opens the
same embedded popup overlay.

Needs: impl

### GitHub repository-list status markers
`req~ank.overlay.github-repo-list-markers~1`

On a user's or organisation's "Repositories" listing
(`https://github.com/<owner>`, `?tab=repositories`, and
`https://github.com/orgs/<org>/repositories`), each repository card
receives a status marker. These cards have no inline star `<form>`
(unlike topic/search cards), so the repo name is a plain
`a[itemprop="name codeRepository"]` link; the marker is injected
*after* that link so the row reads "<repo> <Public> ●".

The lookup URL is `https://github.com/<owner>/<repo>` derived from the
link's href. Markers are lazy (looked up on scroll into view) so a
profile with hundreds of repos does not fire hundreds of lookups at
once. Same status colour scheme as the other GitHub markers; click
opens the same embedded popup overlay.

Needs: impl

### Springer journal-masthead status marker
`req~ank.overlay.springer-masthead-marker~1`

On `link.springer.com` journal pages, the masthead journal title
(`a[data-test="journal-heading-href"]`, e.g. "Software and Systems
Modeling") links to the journal home (`/journal/<id>`). The masthead
sits *outside* `<main>`, so the generic content-link scanner never
marks it — only body-prose links (submission guidelines text, etc.)
get bubbles, all far down the page. A marker is appended after the
journal-heading title so the journal itself gets a bubble at the top of
every journal sub-page (submission-guidelines, articles, updates, …).
The breadcrumb copy of the same link lives in a `<nav>` and stays
skipped; the marker is anchored on the heading link only.

The scanner is invoked directly from the dispatcher, **not** registered
in `HOST_HANDLERS`: registering it would switch the generic content
scanner to the narrow container allowlist (dropping `<main>`), removing
the body-prose bubbles that already work on these pages. The masthead
marker is looked up eagerly (a single marker, wanted immediately) rather
than lazily.

Needs: impl

### ResearchGate lab-page status markers
`req~ank.overlay.researchgate-markers~1`

On `https://www.researchgate.net/lab/<lab-slug>`, each lab member's
profile link receives a marker injected after the link text. The
lookup URL is the link's literal absolute href (including any
`?_sg=…&_tp=…` tracking query); the hub's `UrlNormalizer` strips
the query so OneNote pages recorded with the canonical
`/profile/<slug>` URL still match. Per-slug deduplication ensures one
marker per member even when the card has multiple links to the
same profile.

Needs: impl

### AlternativeTo software-page status markers
`req~ank.overlay.alternativeto-markers~1`

On `https://alternativeto.net/software/<slug>/` (and any sub-paths
under that app's section), each alternative app linked from the page
receives a status marker. Markers are anchored on links of the form
`/software/<other-slug>/about/` and the canonical lookup URL is
`https://alternativeto.net/software/<other-slug>/`.

Deduplication: the page typically renders both an icon link and a
title link to the same slug; only the first link encountered receives
a marker. The page's own slug is skipped (so breadcrumb / nav
self-links don't get markers).

Same status colour scheme as the GitHub markers; click opens the same
embedded popup overlay.

Needs: impl

### Google search-result status markers
`req~ank.overlay.google-markers~1`

On `https://www.google.<TLD>/search` (every Google country-code TLD —
`google.com`, `google.de`, `google.co.uk`, etc.), each organic search
result receives a status marker appended inside its `<h3>` title so
the bubble sits inline next to the result text.

The lookup URL is the destination URL of the result. Modern Google
serves direct hrefs; legacy `/url?q=…` redirect wrappers are
unwrapped so the canonical destination is what gets searched.

Ad blocks and Knowledge-Graph cards also contain `<h3>` titles and
will receive markers — accepted as a known false-positive surface
in exchange for a robust, ad-detection-free selector. Click opens
the same embedded popup overlay; the marker's click handler
`preventDefault`s so the result link doesn't navigate.

Needs: impl

### LinkedIn post status markers
`req~ank.overlay.linkedin-markers~1`

On any `linkedin.com` page that renders feed posts (the home feed,
single-post pages, profile activity, company posts), each post
container — identified by `data-urn="urn:li:activity:<N>"` — receives
two markers:

1. **Author marker.** Placed after the first `/in/<slug>` link with
   visible text inside the post (the avatar link and the name link
   point at the same slug; we pick the textual one so the tooltip
   shows the human-readable name). The lookup URL is the canonical
   `https://www.linkedin.com/in/<slug>`; the hub's `LinkedInUrlHelper`
   falls back to a person-name search so OneNote pages that mention
   the author in plain text still match.
2. **Post marker.** Placed before the post's `…` control-menu button
   (located via `button[aria-label*="actions"|"control menu"|"more
   options"]`). The lookup URL is the synthesised permalink
   `https://www.linkedin.com/feed/update/<urn>/`, which mirrors the
   "Copy link to post" entry in that menu.

Deduplication is per individual link / button via `SEEN_ATTR`, not per
post container, so a re-scan after LinkedIn lazy-renders the missing
half still injects the second marker.

Same status colour scheme as the GitHub markers; click opens the same
embedded popup overlay.

Needs: impl

### MDPI author hover-card aggregated markers
`req~ank.overlay.mdpi-aggregated~1`

On `www.mdpi.com` article pages, each author is wrapped in
`<span class="inlineblock">` containing a trigger
`<div class="profile-card-drop">` (the visible name) and a hidden
`<div class="f-dropdown profile-card-content">` popup with four
platform-profile buttons (SciProfiles, Scilit, Preprints.org,
Google Scholar).

A single status marker is injected right after the trigger (i.e. inline
next to the author name). The four button URLs in the popup are looked
up together, and the marker shows the **most fitting** result across
them — exact > host-only > none. The marker's click target is
re-pointed to whichever URL produced the winning match, so opening the
overlay surfaces the relevant OneNote pages.

Per-button markers inside the popup are suppressed (each button is
flagged `SEEN_ATTR` so the generic content-container scanner skips it)
— their state is fragmented and only visible on hover, while the
aggregated marker is always visible alongside the author name.

Needs: impl

### arXiv paper status markers
`req~ank.overlay.arxiv-markers~1`

On `arxiv.org` (the LaTeXML HTML paper view, `arxiv.org/html/<id>`),
two kinds of status marker are injected:

1. **Paper marker.** Appended to the `<h1 class="ltx_title_document">`
   title, after the title text. The arXiv id is taken from the page
   path (`/abs/`, `/html/`, or `/pdf/` segment, version suffix
   stripped). Looked up against two URLs — `https://arxiv.org/abs/<id>`
   and the arXiv DOI `https://doi.org/10.48550/arXiv.<id>` — and shows
   the most fitting result.
2. **Reference markers.** Each bibliography entry
   (`<li class="ltx_bibitem">`) gets a marker appended after the
   reference title (`span.ltx_bib_title`), so the dot lands after the
   trailing period. Lookup URLs are aggregated from: the entry's arXiv
   id, parsed from an `arXiv:NNNN.NNNNN` mention in the entry text
   (`https://arxiv.org/abs/<id>`); and every `doi.org` / `dx.doi.org`
   anchor under the entry's `External Links` (`span.ltx_bib_links`),
   normalised to `https://doi.org/<doi>`. Entries with neither an
   arXiv id nor a DOI get no marker.

The consumed DOI anchors are flagged `SEEN_ATTR` so the generic
content-link scanner does not also mark them. Same status colour
scheme as the GitHub markers; click opens the embedded popup overlay.

Needs: impl

### Viewport-gated lookup for content-link scanner
`req~ank.overlay.lazy-content-link-lookup~1`

Link-dense pages (e.g. `github.com/jnv/lists` has ~3000 anchors inside
`.markdown-body`, awesome-lists, long forge READMEs) would otherwise
queue thousands of OneNote lookups against the daemon's serial COM
bridge — the queue runs minutes deep and visibly stalls midway down
the page (markers below the cutoff stay in the searching state).

The generic content-link scanner observes each placed marker with an
`IntersectionObserver`. The lookup is deferred until the marker enters
either the viewport itself or a pre-load band one viewport-height tall
*below* the viewport. On intersect the marker's task is pushed onto
the shared lookup queue and the observer stops watching it.

The pre-load band makes slow scrolling feel instantaneous — by the
time a marker enters real view its lookup has already started or
completed. Initial load only spends OneNote requests on the first
viewport plus the next-viewport pre-load (≈ 60–100 markers on
typical content), not the entire document.

Host-specific scanners (Google search results, GitHub repo cards,
LinkedIn posts, …) keep eager lookup — list pages of those types
have bounded counts and the user wants every row to settle without
needing to scroll.

Needs: impl

### Sibling-marker dedup for non-exact matches (content-link scanner only)
`req~ank.overlay.dedup-non-exact~2`

When several links on the same page resolve to the same OneNote
search-context, the page would otherwise gain N identical teal
markers — one per link, all pointing to the same set of saved pages.
For non-exact (host-only / related) results the **generic
content-link scanner** keeps only the **first** marker per
`(matchType, searchTerm)` pair: the hub annotates each non-exact
result with a `dedupKey`, the in-page marker dispatcher records the
marker that first claims a key, and subsequent markers with the same
key are removed from the DOM.

Exact matches and "no match" results are never deduplicated — every
exact URL is its own item, and grey "not in OneNote" markers carry
no context to collapse against.

Host-specific scanners (Google search results, GitHub repo cards,
LinkedIn posts, mdpi.com author cards, …) place one marker per
list-item and **opt out of dedup**. Every list row is its own
result and deserves per-item feedback even when two rows share a
host-only fallback context — a missing marker on one search result
because another result for the same domain matched first would feel
like a bug, not a feature.

The dedup map keys are checked against marker connectivity at use
time: if the original marker has been torn down (SPA navigation, DOM
re-render), the next marker for the same key takes over rather than
being silently suppressed.

Needs: impl

### Content-container link markers (any host)
`req~ank.overlay.content-link-markers~7`

On any page, every text-bearing `http(s)://` link inside a curated set
of "content" containers receives a status marker placed inline after
the link text. Anchors with no visible text (icon-only links such as
mailto envelopes, ORCID badges, social-share strips, …) are skipped:
without anchor text, the marker has nothing to be "about", and these
anchors cluster heavily in article-wrapped metadata headers (e.g.
`mdpi.com` author rows pack 4+ icons each).

Pagination, breadcrumb, and other in-page navigation anchors are
skipped — they are in-site navigation, not pasted reference URLs, so a
"no match" bubble next to each page number / Prev / Next is always
noise. Detection is structural: an ancestor `<nav>` or
`[role="navigation"]` (the semantic case used by most modern sites);
or an ancestor with a class containing `pagination` / `pagenav`, or a
whole-class `pager` (covers Bootstrap 3 pagers, WordPress
`wp-pagenavi`, and similar non-semantic markup); or `rel="next"` /
`"prev"` / `"first"` / `"last"` on the anchor itself (set by many
CMSs even when the link is not inside `<nav>`).

Login screens and modal action rows are likewise skipped — a marker
next to a "Passwort vergessen?" / "Forgot password?" link or beside a
dialog's OK/Cancel buttons is UI chrome, never a pasted reference URL.
Detection is structural: the anchor shares a `<form>` / `<dialog>` /
`[role="dialog"]` / `[aria-modal="true"]` ancestor with an
`input[type="password"]` (login form); or the anchor's parent also
holds a button (`<button>`, `[role="button"]`, `input[type=button|
submit]`) whose label matches a known action verb (OK, Cancel,
Abbrechen, Close, Save, Speichern, Sign in, Anmelden, Continue, Weiter,
Confirm, Delete, … — English + German). Links whose target host begins
with a known auth subdomain label (`login.`, `signin.`, `accounts.`,
`auth.`, `sso.`, `oauth.` — e.g. `https://login.tailscale.com/`) are
skipped regardless of where they sit, since a sign-in destination is
never a pasted reference URL.

The container allowlist also includes `.author__urls` — the canonical
sidebar list used by the Jekyll academicpages and minimal-mistakes
themes (widely used for academic GitHub Pages personal sites). The
sidebar gathers a researcher's external profile links (Email, Google
Scholar, ORCID, ResearchGate, LinkedIn, …) as a labelled list; each
link is exactly the kind of "where to find me" pointer the marker is
designed to surface.

The container allowlist is:

- `<article>` and `[role="article"]`
- `.markdown-body` (GitHub issues, PRs, READMEs, gists, comments)
- `.comment-body`
- `.s-prose` (Stack Exchange)
- `.md` (Reddit)
- `[itemprop="articleBody"]` (schema.org-marked article bodies)
- `.author__urls` (Jekyll academicpages / minimal-mistakes sidebar
  profile links — Email, Google Scholar, ORCID, ResearchGate,
  LinkedIn, …)
- `.ce-bodytext` (TYPO3 CMS content-element body text — used by many
  German university sites like HTWK, KIT-old, etc.; always wraps
  prose+links with no nav/buttons, so safe to scan in both main
  content and right-side "Links" sidebars)

In addition, on hosts that *don't* have a dedicated host-specific
scanner (i.e. not in `HOST_HANDLERS` or `HOST_PATTERN_HANDLERS`), the
allowlist is extended with `<main>` and `[role="main"]` — the HTML5
main-content landmark — plus `#page-content` and `.bp-page-content`
which the TYPO3 BootstrapPackage extension uses on sites that predate
HTML5 landmarks (many German university and Gesellschaft-für-Informatik
sub-sites). Some academic CMS pages (e.g. KIT) use
`<section class="content-wrap" role="main">` rather than `<main>`, so
multiple selectors are needed.

On rich apps with their own scanners (github.com PR/issue lists,
linkedin.com feed, google search, …) `<main>` wraps the entire UI
including filter chips, label tags, and toolbar links — including it
would produce marker spam over every UI control, so it is suppressed.
The dedicated scanners + the narrow prose containers above already
cover the relevant signal on those hosts.

The allowlist is intentionally narrow: marking *every* anchor on a
page injects bubbles into nav, footer, sidebar and related-content
rails, which becomes visually overwhelming. Restricting to recognised
content containers keeps the bubble density tolerable while still
covering the cases that motivated the feature — pasted reference URLs
in issue comments, blog post bodies, and Q&A answers.

Self-links (target origin + path + query equal to the current page)
are skipped — the toolbar icon already shows that state. Nested
content containers are walked once via the outermost match.

Site-specific scanners (GitHub `/star`, AlternativeTo, ResearchGate,
LinkedIn, …) run first and own `SEEN_ATTR` for the elements they
touch; the generic scanner defers to them on overlap. Same status
colour scheme as the GitHub markers; click opens the same embedded
popup overlay.

Needs: impl

### Google Scholar — no host-only markers
`req~ank.overlay.scholar-no-host-only~1`

On Google Scholar result pages (`scholar.google.<tld>`) the content
scanner marks the outbound paper / PDF links. These point at many
publisher and repository hosts (`mit.edu`, `academia.edu`,
`elsevier.com`, …), and a host-only ("same site / related") fallback
there matches every unrelated paper the user ever saved from that same
host — pure noise on a search-results page.

In-page markers on Scholar pages therefore downgrade a `host-only`
result to `none` (grey "not found" icon). Exact matches — the paper
itself is already in OneNote — are unaffected and still show the green
marker. The suppression is client-side (the daemon has no knowledge of
the referring page); host detection is `scholar.google.<tld>`.

Needs: impl

### Person-name status markers (any host)
`req~ank.overlay.person-name-markers~1`

On any page, the content script scans for plain-text person names that may
already be referenced in OneNote and injects a marker next to each one
*only when OneNote actually has a hit*. Misses stay invisible — the
false-positive risk on text-only name detection is too high to surface
"not found" markers.

Two structural patterns are recognised:

1. **Heading-anchored lists.** Headings (`h1`-`h6`) whose text matches
   `organizers? | committees? | chairs? | speakers? | authors? | members |
   team | faculty | reviewers? | editors? | presenters? | panel |
   panellists? | keynotes? | invited talks? | advisory | board | people`
   anchor a search of every following sibling list/table (until the next
   heading at equal-or-higher level) for items whose text starts with a
   capitalised name (2-4 tokens, optional connecting particle
   `de/van/von/…`, with affiliation in parens / after a comma stripped).
   A small stop-list rejects geographic and boilerplate phrases
   (`United States`, `Best Paper`, `Czech Republic`, …). The container
   must yield ≥2 candidate names — a single capitalised phrase is more
   likely to be a city or section header than a person.

2. **Italic with affiliation.** `<em>` / `<i>` whose entire text matches
   `<Name> (<Affiliation>)` — strong enough as a pattern to skip the
   heading anchor.

Items that already carry a URL marker (`.onl-marker`) are skipped: the
host-specific URL scanner has the more precise canonical lookup. The
hub's `searchText` action performs the OneNote search; only matches with
total ≥ 1 are surfaced. Click opens the embedded popup with
`?name=<name>&embedded=1` — name mode hides the JabRef section *and* the
"Copy link" button (no canonical URL applies) and labels the result
section "Pages mentioning this name".

Needs: impl

### Marker-click opens popup overlay
`req~ank.overlay.iframe-popup~1`

Clicking a status marker opens the toolbar popup (`popup.html`) inside
an iframe hosted in a closed shadow DOM injected into the page. The
iframe URL carries `?url=<repo-url>&title=<owner/repo>&embedded=1`, and
the popup uses the supplied URL/title for all lookups, adds, and
"open in OneNote" actions instead of querying the active tab. The
JabRef section is hidden in embedded mode (no DOI detection runs for
synthetic URLs in v1).

The overlay closes on outside click or Escape. All popup features —
match list, "Open in OneNote", refresh, "+ Add" — work against the
synthetic URL.

The marker's click handler is registered in the **capture** phase and
calls `stopImmediatePropagation()`. SPA frameworks (e.g. Angular on
`ieeexplore.ieee.org`) attach a capture-phase document click listener
for routing that calls `stopPropagation()`; a bubble-phase handler on
the marker would never fire (the hover title still shows, so it reads
as "nothing happens" on click). Capturing on the marker itself wins
that race.

Needs: impl

### Bulk URL lookup
`req~ank.overlay.bulk-lookup~1`

The content script issues a single `bulkLookup` runtime message
containing all collected repo URLs. The background processes them with
a small concurrency cap (4 in-flight lookups) and returns a map
`{url → {matchType, total}}` where `matchType` is collapsed to
`"exact"`, `"host-only"`, or `"none"`. Cache hits do not contact the
hub.

Needs: impl

## Page Navigation

### Navigate and find
`req~ank.nav.focus-and-search~1`

Clicking a match row navigates OneNote to that page and opens the find-on-page
bar (Ctrl+F) pre-filled with the match's `searchTerm`, so the user immediately
sees where the link appears on the page.

Needs: impl

### Quoted Ctrl+F search term
`req~ank.nav.quoted-search~1`

The search term sent to OneNote's find-on-page bar (Ctrl+F via SendKeys) is
wrapped in double-quote characters so that OneNote performs an exact-phrase
match. Without quotes, OneNote tokenises URLs on `://`, `.`, and `/`, causing
dozens of false-positive highlights. Any literal double-quote inside the term
is escaped by doubling it.

Needs: impl

## Caching

### Cache duration
`req~ank.cache.ttl~1`

Lookup results are cached in memory per URL for 24 hours.

Needs: impl

### Instant display from cache
`req~ank.cache.instant~1`

When the user switches to a tab whose URL is present in the cache, the icon
and matches are updated immediately from the cached result without a new
OneNote query.

Needs: impl

### Searching state
`req~ank.cache.searching-state~1`

When the active tab's URL is not yet in the cache the icon is set to the
no-match grey and the tooltip reads "AnchorHub — searching…" while
the lookup is in progress. This applies both on tab switch and on page
navigation within the active tab.

Needs: impl

### Popup waits for in-flight lookup
`req~ank.cache.popup-wait~1`

When the popup is opened while a lookup is in progress, the popup blocks
on "Loading…" until the lookup completes, then renders the result.
The popup never shows "Not found" for a lookup that is still running.

Needs: impl

### Popup lookup after background restart
`req~ank.cache.popup-cold-start~1`

The browser suspends the extension's background worker when it is idle,
which discards all per-tab lookup state. When the popup is opened for a
tab that has no stored lookup state and no lookup in flight, the
background runs the full lookup set for that tab (OneNote, DOI/JabRef,
BibTeX) and renders its result, instead of reporting "Not found in
OneNote" and "JabRef offline".

A tab whose lookup ran and produced no matches is distinct from a tab
that was never looked up; only the latter triggers the lookup.

Needs: impl

### Cache survives background restarts
`req~ank.cache.session-persistence~1`

The URL and free-text-name lookup caches are mirrored into the browser's
session storage, so they survive a suspend/restart of the background
worker within the same browser session. Restored entries are subject to
the same 24 hour TTL (`req~ank.cache.ttl~1`) and are discarded when the
browser closes; entries written by the running worker take precedence
over restored ones. Where session storage is unavailable, the caches
remain in-memory-only.

Needs: impl

### Bounded memory use
`req~ank.cache.bounded~1`

Extension memory use stays bounded over long browser sessions:

- The in-memory lookup caches (URL, name, ContextSwitcher) are capped at
  a fixed entry count; the oldest entries are evicted when the cap is
  exceeded.
- A request to the hub that receives no response within 30 seconds is
  failed and its pending state released, so hung requests do not retain
  their callers indefinitely. All pending requests are failed when the
  native port disconnects.
- In-page marker scanning is paused while the tab is hidden and resumes
  (with one catch-up scan) when the tab becomes visible.
- Markers detached from the page by SPA re-renders are dropped from the
  lazy-lookup bookkeeping instead of being retained.

Needs: impl

## Copy link

### Copy-link button when URL not in OneNote
`req~ank.copy.button~1`

When the URL the popup is rendered against has no **exact** match in
OneNote, a single "Copy link" button is shown — both in the toolbar
popup (where the URL is the active tab's URL) and in the embedded
in-page overlay (where the URL is the synthetic URL passed via
`?url=…`).

Clicking the button writes that URL to the system clipboard via
`navigator.clipboard.writeText`. The button label briefly changes to
"Copied" for ~1.5 s before reverting. On clipboard-permission failure
the label reads "Copy failed".

The button replaces the earlier "+ Add" / notebook-section dropdown
flow, which the user did not use; the underlying hub `addPage` action
remains available for CLI use and future features.

Needs: impl

## Page Types

AnchorHub recognises several "page types" — single OneNote pages
that represent one well-defined object (a publication, a software
product, a person, …), each with a typed metadata table that the hub
parses. The concept is modelled after [Capacities](https://capacities.io)'s
object types: a controlled vocabulary of labels per type, with
per-type validation and per-type cross-referencing into OneNote.

Each type is detected by the presence of one or more canonical labels
in the first column of a two-column table. The table-location and
label-normalisation rules in `req~citation-page.metadata-table~1` and
`req~citation-page.label-normalisation~1` apply uniformly to all
page types.

Currently defined types:

| Type | Marker label(s) | Section |
|---|---|---|
| Citation page | `citationkey`, `identifier` | Citation Pages |
| Person page   | `orcid`, `linkedin`         | Person Pages   |
| Software page | `homepage`                  | Software Pages |

Marker precedence: when a metadata table contains markers from more
than one type, the order above wins (citation > person > software).
This resolves the `homepage` overlap — a person page may declare a
`homepage` row for the person's own site without being misclassified
as a software page.

## Citation Pages

A OneNote page can be marked as a "citation page" — a single page that
represents one publication, paper, book, or other reference — by including
a metadata table that AnchorHub parses to resolve the reference
against JabRef and external sources.

### Citation page metadata
`feat~citation-page-metadata~1`

A OneNote page describing a literature reference uses the page title as
the reference's display title and provides a metadata table that
AnchorHub parses to resolve the reference against JabRef and external
sources.

Needs: req

### Metadata table location
`req~citation-page.metadata-table~1`

The metadata table is the first two-column OneNote table on the page
whose first column contains either a `citationkey` row or an `identifier`
row. The presence of such a table marks the page as a citation page.

Covers:
- `feat~citation-page-metadata~1`

Needs: impl

### Label normalisation
`req~citation-page.label-normalisation~1`

Row labels in the first column are matched case-insensitively, with
leading and trailing whitespace stripped, and an optional trailing colon
removed. So `Citationkey`, `CITATIONKEY`, ` citationkey `, and
`citationkey:` all match the canonical label `citationkey`.

Covers:
- `feat~citation-page-metadata~1`

Needs: impl

### Citation key row
`req~citation-page.citationkey-row~2`

The metadata table SHALL contain exactly one row whose label is
`citationkey`. The value cell contains the JabRef citation key of the
referenced entry, trimmed of surrounding whitespace.

Covers:
- `feat~citation-page-metadata~1`

Needs: impl

### Identifier row
`req~citation-page.identifier-row~2`

The metadata table SHALL contain at least one row whose label is
`identifier`. Each value cell is a typed identifier of the form
`<scheme>:<value>` where `<scheme>` is one of `doi`, `arxiv`, `isbn`,
`url` (matched case-insensitively, normalised to lowercase). The first
identifier row is treated as the canonical/primary identifier;
subsequent ones are alternates (e.g. a book may carry both `isbn:` and
`doi:`).

Rationale: JabRef citation keys are not stable across libraries or over
time. The typed identifier provides a JabRef-independent, durable
reference.

Covers:
- `feat~citation-page-metadata~1`

Needs: impl

### Required rows
`req~citation-page.required-rows~2`

A citation page SHALL contain both a `citationkey` row and at least one
`identifier` row. Pages that contain a metadata table (per
`req~citation-page.metadata-table~1`) but are missing either requirement
SHALL be reported as validation errors. Pages with no metadata table at
all are not citation pages and SHALL NOT be flagged.

Covers:
- `feat~citation-page-metadata~1`

Needs: impl

### Identifier-based lookup
`req~citation-page.identifier-lookup~1`

When the active tab's URL maps to a known identifier scheme,
AnchorHub SHALL resolve the URL to that identifier and return any
citation page whose `identifier` row matches as an `exact` match (in
addition to, and ranked above, any direct URL match for the same page).

URL → identifier mapping:

| URL pattern | Identifier |
|---|---|
| `https://doi.org/<x>` (and `dx.doi.org`) | `doi:<x>` |
| `https://arxiv.org/abs/<x>` (and `/pdf/<x>`) | `arxiv:<x>` |
| Any other URL | `url:<normalised URL>` |

Comparison is case-insensitive on the scheme and case-sensitive on the
value (DOIs are case-insensitive in the spec but commonly stored
verbatim; matching the stored form avoids false negatives for URL
identifiers).

Rationale: a citation page may be the canonical landing for a paper
even when no OneNote page contains the publisher URL itself; the
identifier row provides a stable join key.

Covers:
- `feat~citation-page-metadata~1`

Needs: impl

### Identifier extracted from page content
`req~citation-page.identifier-extraction~1`

In addition to URL-derived identifiers
(`req~citation-page.identifier-lookup~1`), AnchorHub SHALL extract
identifiers from the active tab's page content/DOM and use them for
citation-page lookup. Extraction sources:

| Identifier | Source |
|---|---|
| DOI | `<meta name="citation_doi">`, `<meta name="dc.identifier">`, `<meta name="prism.doi">`, or visible `doi.org/<x>` links |
| arXiv | `<meta name="citation_arxiv_id">`, or visible `arxiv.org/abs/<x>` links |
| ISBN | `<meta name="citation_isbn">`, or visible ISBN-10 / ISBN-13 patterns near the literal "ISBN" |

Each extracted identifier is looked up against citation pages' `identifier`
rows the same way as URL-derived ones, and surfaced as an `exact` match.
Extraction is performed by the extension via a content script (consistent
with the existing DOI detection in `req~ank.match.exact~1`'s pipeline).

Covers:
- `feat~citation-page-metadata~1`

Needs: impl

### Validation reporting
`req~citation-page.validation-reporting~1`

Validation errors produced while parsing citation pages are written to
the hub log file (`%APPDATA%\AnchorHub\logs\host-YYYYMMDD.log`) and
listed by a diagnostic CLI command (`lint-citation-pages`) that walks
all OneNote pages and prints per-page error summaries.

Covers:
- `feat~citation-page-metadata~1`

Needs: impl

## Software Pages

A OneNote page describing a single software product / app uses the
page title as the product name and provides a metadata table whose
rows are typed URL roles. Software pages are looked up when the user
visits any of the URLs they declare.

### Software page metadata
`feat~software-page-metadata~1`

A software page contains a two-column metadata table where the first
column is a URL role and the second column is the corresponding URL.

| Role            | Constraint |
|---|---|
| `homepage`      | Exactly one row. The canonical product URL. Marker label that identifies the page as a software page. |
| `alternativeto` | Zero or one row. Must point to `alternativeto.net`. |
| `github`        | Zero or one row. Must point to `github.com`. |
| `other`         | Zero or more rows. Any other URL associated with the product (review sites, store listings, alternative catalogs). |

The presence of a `homepage` row marks the page as a software page,
**unless** the table also contains a person-page marker (`orcid` or
`linkedin`), in which case the person-page rules apply per the marker
precedence in `## Page Types`. A page that mixes citation-page markers
(`citationkey`/`identifier`) with software-page markers is reported
as a validation error.

AnchorHub uses these URLs for cross-page lookup: visiting any of
them in the browser surfaces the software page as an `exact` match,
parallel to `req~citation-page.identifier-lookup~1`. Example fields
for a Logseq software page:

| Role            | Value |
|---|---|
| `homepage`      | `https://logseq.com/` |
| `alternativeto` | `https://alternativeto.net/software/logseq/` |
| `github`        | `https://github.com/logseq/logseq` |
| `other`         | `https://noteapps.info/apps/logseq` |

Needs: req

## Person Pages

A OneNote page describing a single person uses the page title as the
person's display name and provides a metadata table with typed URL
roles for the person's online presence. Person pages are looked up
when the user visits any of the URLs they declare.

### Person page metadata
`feat~person-page-metadata~1`

A person page contains a two-column metadata table where the first
column is a URL role and the second column is the corresponding URL.

| Role       | Constraint |
|---|---|
| `orcid`    | Zero or one row. The person's ORCID URL (e.g. `https://orcid.org/0000-0002-4193-2889`). Marker label. |
| `linkedin` | Zero or one row. The person's LinkedIn `/in/` profile URL. Marker label. |
| `homepage` | Zero or one row. The person's personal/academic homepage. |
| `github`   | Zero or one row. The person's GitHub user URL (e.g. `https://github.com/<username>`). |

A person page MUST contain at least one of `orcid` or `linkedin` —
these are the marker labels. (`homepage` and `github` alone are
ambiguous because they also appear on software pages.)

AnchorHub uses these URLs for cross-page lookup: visiting any of
them in the browser surfaces the person page as an `exact` match,
parallel to `req~citation-page.identifier-lookup~1`. A page that
mixes citation-page markers (`citationkey`/`identifier`) with
person-page markers is reported as a validation error.

Example fields for an academic researcher's person page:

| Role       | Value |
|---|---|
| `orcid`    | `https://orcid.org/0000-0002-4193-2889` |
| `linkedin` | `https://www.linkedin.com/in/barbara-koenig/` |
| `homepage` | `https://www.uni-due.de/theoinf/people/koenig/` |
| `github`   | `https://github.com/bkoenig` |

Needs: req

## Page Templates

### OneNote templates
`feat~page-templates~1`

The repo ships one OneNote template (`*.one` file) per page type
(citation, person, software). Each template lays out the metadata
table with the canonical labels in the first column and placeholder
cells in the second column for the user to fill in. Templates are
copied once into OneMore's `Templates/` folder; thereafter the user
inserts a fresh typed page via OneMore's *Insert Template* menu.

Rationale: typed pages are seeded occasionally rather than created
daily, validation already lives hub-side, and a template carries
zero ongoing maintenance burden (no script, no fork, no build).
Schema migrations to existing pages are handled manually — the
trade-off is explicitly accepted in
[ADR 0005](../decisions/0005-typed-page-authoring.yaml).

Templates supplied:

| Template file                              | Page type     | Marker label(s)        |
|---|---|---|
| `templates/onenote/citation-page.one`      | Citation page | `citationkey`, `identifier` |
| `templates/onenote/person-page.one`        | Person page   | `orcid`, `linkedin`         |
| `templates/onenote/software-page.one`      | Software page | `homepage`                  |

Needs: req

## Transport

The daemon (`ankd`) supports running in three transport modes so the
same codebase serves both Windows-native installs and Linux installs
that reach OneNote in a Windows VM. See
[ADR 0006](../decisions/0006-linux-port-via-vm-relay.md) and
[`plan-linux-port.md`](../plan-linux-port.md) for context.

### Transport modes
`req~ank.transport.modes~1`

The daemon binary supports three startup modes, selected by argv:

- **direct** (default on Windows): reads framed messages from stdin
  and writes responses to stdout; dispatches each message to the local
  OneNote backend. This is the only mode used by the Windows-native
  install and matches the daemon's behaviour from before the Linux
  port.
- **forward** (`--forward=<host:port>`): reads framed messages from
  stdin, forwards them verbatim over TCP to a daemon running in
  `--listen` mode, and writes the framed responses back to stdout. No
  message is parsed locally except `status` (and any other action
  whose response is needed to render the disconnected icon state). No
  OneNote backend is used.
- **listen** (`--listen=<host:port>`): accepts a single TCP
  connection at a time and dispatches each framed message to the local
  OneNote backend. Used inside the Windows VM that hosts OneNote.

Default selection: on Windows, when no transport flag is given, the
daemon runs in direct mode. On non-Windows platforms, a transport flag
is required.

Needs: req

### Wire format
`req~ank.transport.framing~1`

The on-the-wire format on TCP between forward-mode and listen-mode
daemons is identical to the format used over stdio between the Firefox
extension and a direct-mode daemon: 4-byte little-endian length
prefix followed by a UTF-8 JSON object. A single TCP connection
carries an interleaved sequence of request and response frames. This
allows the forward-mode daemon to be a pure byte-forwarder without
parsing payloads.

Needs: req

### Authentication
`req~ank.transport.auth~2`

A daemon in `--listen` mode that is configured with a shared token
accepts only connections that present a matching token as the first
framed message after connect. The token is a random 32-byte hex
string, generated by the installer and written to a file path passed
as `--token-file=<path>` on both ends.

The first message from the forward-mode daemon is

```json
{ "action": "hello", "token": "<hex>" }
```

The listener then responds:

- on a valid hello — `{ "ok": true }`, after which the connection
  enters the normal request/response loop.
- on a missing or mismatched hello — `{ "ok": false, "error": {
  "code": "AUTH_FAILED", "message": "..." } }`, then closes the
  connection without dispatching any further action.

The forwarder treats an `AUTH_FAILED` response (or a closed connection
during the handshake) the same as a connection failure: drops back to
the disconnected state and retries on the next request.

Open mode: when neither side is configured with `--token-file`, the
hello frame is skipped and the connection enters request/response
mode immediately. A protocol mismatch (one side configured with a
token, the other not) results in connection rejection and is the
expected behaviour — an authenticated listener must reject
unauthenticated peers.

Rationale: TCP listeners are bound to localhost and exposed across the
host/guest boundary via QEMU port forwarding, so an attacker on the
local machine could otherwise impersonate the forwarder. The token
adds defence in depth without the operational cost of TLS for a
localhost link.

Needs: req

### Disconnected state
`req~ank.transport.disconnected-state~2`

When a forward-mode daemon cannot reach the listen-mode daemon (TCP
connect failure, broken pipe, missing response), it answers locally
with

```json
{ "ok": false, "error": { "code": "DISCONNECTED", "message": "..." } }
```

preserving the request's `requestId`, so the popup can render the
orange "not connected" icon without waiting for a network timeout.
The forwarder retries the TCP connection lazily on the next request:
every request gets a chance to find the listener back, with a short
(500 ms) connect timeout that keeps recovery cheap.

The extension flips the toolbar icon to orange on the first
`DISCONNECTED` response and clears it on the first `ok:true`
response. While the flag is set, per-tab icon updates and the
"searching" gray-square base are both suppressed in favour of orange,
so navigation during an outage doesn't paint over the disconnected
state. Detection latency is bounded by the next user-driven request
(navigation, hover scan, popup interaction); a periodic status ping
is a possible future refinement if lazy detection proves insufficient.

Needs: req

## JabRef Integration

### Fulltext PDF fetch
`req~ank.jabref.fulltext-fetch~1`

**Status: disabled.** The daemon no longer auto-enables the fulltext
realm or writes the JabRef discovery file; that functionality is being
built in the JabRef browser-extension plugin instead. Re-enable by
compiling with `JABREF_FULLTEXT_AUTOENABLE` defined.

AnchorHub acts as a provider for JabRef's fulltext-PDF fetch pipeline:
when JabRef cannot retrieve a paper's PDF directly (paywall, anti-bot
defences, institutional SSO), it asks AnchorHub, which uses the user's
already-authenticated browser session to obtain the PDF.

AnchorHub implements the provider side of the **Browser-Extension
Fulltext Provider Protocol v1** (see
`docs/requirements/browser-extension-fulltext.md`, a verbatim copy of
the canonical file maintained in the JabRef repository under MIT). The
protocol is provider-agnostic; AnchorHub is one possible implementation,
JabRef-Browser-Extension (AGPL-3.0) may be another. JabRef itself ships
a single, vendor-neutral consumer named `BrowserExtensionFulltextFetcher`.
This requirement therefore captures AnchorHub-side **derived obligations**
on top of the protocol's `req~bxf.*~1` clauses; cross-repo tracing back
to those protocol identifiers happens through the local copy of the
protocol file.

Daemon-side requirements:

- A new mode `--http-listen=host:port` starts a loopback HTTP server
  bound to `127.0.0.1`. Route families ("realms") are enabled by
  passing per-realm token-file flags. Phase 1 ships one realm enabled
  by `--fulltext-token-file=<path>`, serving `GET /v1/health`,
  `POST /v1/fulltext`, and `DELETE /v1/fulltext/{id}`. Realms whose
  flag is omitted return `401` for all their routes regardless of
  token. Every request requires `Authorization: Bearer <token>`; the
  server rejects any request whose `Origin` header is set to anything
  other than absent or `null`.
- At startup the daemon writes a provider discovery file at
  `%APPDATA%\JabRef\fulltext-providers\anchorhub.json` (Windows) or the
  platform equivalent under JabRef's config dir. The file declares
  `name`, `displayName`, `port`, `tokenFile`, `protocolVersion: 1`. On
  graceful shutdown the file is removed.
- `POST /v1/fulltext` accepts `{ doi?, url? }` (at least one required).
  The daemon resolves the DOI to a publisher URL by following
  `https://doi.org/<doi>` redirects, selects a per-publisher
  `IFulltextAdapter` (phase 1: `IeeeFulltextAdapter` for
  `ieeexplore.ieee.org`), and dispatches a `fetchFulltext` action to
  the browser extension over native messaging. If no per-publisher
  adapter matches, a generic adapter runs that scans the loaded page
  for any plausible PDF link. Success response carries `{ id, path,
  sourceUrl? }`; `path` points at a readable PDF on local disk. On
  missing adapter and missing generic-fallback PDF, the daemon returns
  `404 { error: "no-adapter" }` or `404 { error: "no-pdf-found" }`.
- The daemon does not queue requests; it forwards each to the
  extension and holds the HTTP connection open until the extension
  responds. A safety-valve timer (~90 s of no progress) and an
  extension-reported `busy` error produce `503 busy`; JabRef does not
  retry `503`. When the HTTP client closes the connection, the daemon
  sends an `abortFulltext` message to the extension with the original
  correlation id so the browser tab and any in-progress download are
  aborted.

Extension-side requirements:

- A `fetchFulltext` native-messaging handler opens a tab on the
  resolved publisher URL via `tabs.create({ active: false })`, runs the
  per-publisher content adapter (`adapters/<key>.js`) or the generic
  fallback adapter, and downloads the located PDF via the
  `browser.downloads` API. On adapter miss, a single retry runs with
  `{ active: true }` to cover lazy-rendered content; the tab is closed
  after the download completes or the adapter ultimately fails.
- The extension maintains a FIFO queue with a global cap of 3
  concurrent fetches and a per-publisher (host) cap of 1. Queue depth
  beyond 50 returns `{ error: "busy" }`. An `abortFulltext` message
  removes queued entries or closes in-flight tabs.

Scope and assumptions:

- AnchorHub's PDF fetch relies on the cookies of the browser hosting
  its extension. If the user is logged into the publisher in a
  different browser, the fetch silently returns a subscription-preview
  page that the adapter then fails to extract a PDF from, surfacing as
  `404 no-pdf-found`. This is documented; no auth-state detection
  ships in phase 1.
- Phase 1 enables the fulltext realm only in the Windows-direct
  install. The Linux forwarder install does **not** pass
  `--fulltext-token-file` and does **not** write a discovery file, so
  JabRef sees no AnchorHub provider on Linux. Cross-host fulltext is
  deferred to a later phase that adds a raw-bytes streaming response
  variant.

Rationale: ADR 0007.

Needs: req

### macOS native build
`req~ank.platform.macos~1`

The daemon runs natively on macOS (Apple Silicon, `osx-arm64`) with no
OneNote and no Windows VM. The `net8.0` (non-`net8.0-windows`) build omits
the OneNote COM bridge entirely and starts the native-messaging host with a
**null** OneNote backend: every OneNote-touching action returns
`DISCONNECTED`, while the JabRef fulltext realm and Microsoft Graph
sign-in — both platform-neutral `net8.0` code — are served normally. macOS
is therefore a JabRef-fulltext-provider host, not an anchor-lookup host.

Because native-messaging manifests cannot carry CLI arguments, the
Chrome-spawned daemon auto-enables the fulltext realm on
`127.0.0.1:17893` when the default token file exists at the .NET
`ApplicationData` path (`~/.config/AnchorHub/fulltext.token` on macOS). The
installer (`install/install-mac.sh`) publishes the self-contained
single-file `ankd`, writes that token file, registers the Chrome native
messaging manifest under
`~/Library/Application Support/Google/Chrome/NativeMessagingHosts/`, and
stages the extension (with the fixed Chrome `key`) for Load-unpacked. The
discovery file is written to
`~/Library/Application Support/JabRef/fulltext-providers/anchorhub.json`
(the macOS branch of the shared discovery-path resolver).

Scope: Apple Silicon and Chrome only in this phase; Firefox/Safari and
Intel macs are out of scope. Like the Linux forwarder, the macOS path
ships without end-to-end exercise on real hardware until validated on a
Mac.

Needs: req

## ContextSwitcher Integration

ContextSwitcher (../ContextSwitcher, github.com/contextswitcher/
contextswitcher) is a desktop task-switcher whose tasks are Markdown
files with YAML frontmatter under `~/.contextswitcher/tasks`. A task's
`browser:` section lists the `urls` that belong to it. AnchorHub treats
such a URL as an anchor the same way it treats a URL saved in OneNote:
being on the page means "this page is a ContextSwitcher task", and the
user can jump back into the app.

### Task match by browser URL
`req~ank.contextswitcher.task-match~1`

When the ContextSwitcher adapter is enabled, the daemon answers a
`contextSwitcherLookup` action carrying the current URL by scanning the
ContextSwitcher tasks directory and reporting the first task whose
`browser.urls` contains a URL that normalises (per
`req~ank.match.normalise~1`) to the same value.

- The tasks directory is resolved the way ContextSwitcher itself does:
  the `tasksDir` key of `~/.contextswitcher/settings.yaml`, or
  `~/.contextswitcher/tasks` when the file is absent. The
  `ANCHORHUB_CS_TASKS_DIR` environment variable overrides both.
- Only the tasks directory and its immediate subfolders are scanned
  (a subfolder is a ContextSwitcher project group); the reserved
  `TEMPLATE.md` and `CONTEXTSWITCHER.md` files are ignored.
- The reported `taskId` is the file path relative to the tasks
  directory, forward-slash separated with `.md` stripped
  (e.g. `jabref/fix-npe`) — identical to ContextSwitcher's own id
  derivation, so it round-trips into a deep link.
- The reply carries `found`, and when found the task's `taskId`,
  `title`, `status`, and the `matchedUrl`. A single malformed task
  file is skipped rather than failing the whole lookup.

The action is pure file IO (no OneNote COM), so it is served by every
daemon build, including the plain net8.0 forwarder target.

Needs: impl, utest

### Deep links into ContextSwitcher
`req~ank.contextswitcher.deeplink~1`

When the current page is part of a ContextSwitcher task, the popup shows
a ContextSwitcher section with the task title, id, and status, and two
actions that hand a deep link to the OS protocol handler (planned
ContextSwitcher interface, contextswitcher#47):

- **Switch to task** → `contextswitcher://switch/<id>` (runs the task's
  switch).
- **Show in app** → `contextswitcher://task/<id>` (focus + select the
  task).

Task ids may contain `/` for group folders; the slashes are preserved
and each path segment is percent-encoded. When the page is not part of
any task, the section shows a "not part of a ContextSwitcher task"
state; when the adapter is disabled or the daemon is unreachable, the
section is hidden.

Needs: impl

## Outlook Integration

Classic desktop Outlook, reached over COM (Windows only). An Outlook item
gets a stable anchor URI that other tools — ContextSwitcher tasks, koppter
task notes — store and later open again.

### Capture an Outlook item as an anchor
`req~ank.outlook.capture~1`

`ankd outlook-capture` prints one JSON line describing the Outlook item
the user is looking at: the item of the active inspector window, else the
first selected item of the active explorer. The line carries `entryId`,
`storeId`, `subject`, `sender`, `received` (ISO 8601) and `anchor`:
`anchor://outlook/message/<EntryID>?x-store=<StoreID>&title=<subject>&v=1`,
each value percent-encoded. When Outlook is not running or nothing is
selected, the command exits non-zero with the reason on stderr.

Needs: impl, utest

### Open an Outlook anchor
`req~ank.outlook.open~1`

`ankd outlook-open <anchor>` opens the item an
`anchor://outlook/message/…` URI names in its own Outlook window, starting
Outlook when it is not running. The item is resolved by EntryID within the
store named by `x-store`, or across all stores when `x-store` is absent.

Needs: impl, utest

## Settings

### Adapter enable toggles
`req~ank.settings.adapter-toggle~2`

The extension exposes an options page that lets the user enable or
disable each adapter independently. The OneNote, JabRef, and
ContextSwitcher adapters default to enabled; the OneNote (Microsoft
Graph) adapter defaults to disabled (it needs Azure AD configuration
and sign-in first).

When an adapter is enabled, the extension performs its normal work:
OneNote lookups via the native daemon, JabRef lookups via the
localhost HTTP API, ContextSwitcher task matching via the native
daemon. When an adapter is disabled, the extension
skips all corresponding work:

- **OneNote disabled** — the extension does not connect to the
  native messaging host, performs no URL or name lookups, and shows
  a neutral toolbar icon with the title "AnchorHub — OneNote
  disabled". In-page status markers and the "Send to OneNote with
  URL" context-menu / keyboard action are no-ops while disabled.
- **JabRef disabled** — the extension does not poll
  `http://localhost:23119`, performs no DOI detection, suppresses
  the JabRef popup section, and shows no `J` badge or
  "JabRef offline" indicator.
- **ContextSwitcher disabled** — the extension performs no
  `contextSwitcherLookup` and the popup's ContextSwitcher section is
  hidden. Because ContextSwitcher shares the native host, disabling
  OneNote alone no longer disconnects the port while ContextSwitcher
  is enabled.

Toggling either setting takes effect immediately: the background
script reacts to the `adapterSettings` change without an extension
reload, refreshes the active tab's state, and disconnects or
re-connects the native host as needed.

Needs: impl

## Browser Support

### Single-source manifest and codebase
`req~ank.browser.single-source~1`

The extension ships a single `extension/manifest.json` and a single
`extension/background.js` that work in both Firefox (121+) and any
modern Chromium browser (Chrome, Edge, Brave, Vivaldi). The dual
support is achieved by:

- declaring **both** `background.scripts` (Firefox prefers it) and
  `background.service_worker` (Chrome's only option) — each browser
  uses its own and ignores the other key;
- injecting a `key` field with a fixed RSA public key into the staged
  Chrome copy at install time (see `install-chrome.ps1`) so Chrome
  derives a stable extension ID
  (`bhnaeoglhfolnnkciokfdinoghhjlfeh`) on every machine, which the
  host manifest's `allowed_origins` references. The source
  `extension/manifest.json` omits the field so Firefox does not warn
  about an unexpected property when loading the same directory as a
  temporary add-on;
- aliasing the WebExtensions namespace at the top of every script
  (`background.js`, `popup.js`, `content/overlay.js`, `offscreen.js`)
  with `if (typeof browser === "undefined") globalThis.browser = chrome;`
  — Chrome's `chrome.*` is promise-based since MV3, so the rest of
  the code uses `browser.*` uniformly;
- using only cross-browser API names (notably `browser.contextMenus`,
  not Firefox's `browser.menus` alias);
- creating context menus inside `runtime.onInstalled` /
  `runtime.onStartup` rather than at top level — service workers
  re-execute the script on every wake, which would otherwise throw
  "duplicate id" on `contextMenus.create`.

The native messaging host manifest carries both `allowed_extensions`
(Firefox's id format) and `allowed_origins` (Chrome's
`chrome-extension://<id>/` URIs) so a single file at
`%APPDATA%\AnchorHub\io.github.contextswitcher.anchorhub.json` serves both
browser families. `install.ps1` registers it under
`HKCU\Software\Mozilla\NativeMessagingHosts\…`; `install-chrome.ps1`
registers the same file under each Chromium hive
(`Google\Chrome`, `Microsoft\Edge`, `BraveSoftware\Brave-Browser`,
`Vivaldi`, `Chromium`) and stages a copy of `extension/` at
`%LOCALAPPDATA%\Programs\AnchorHub\extension` for Load Unpacked.

Needs: impl

<!-- markdownlint-disable-file MD022 -->
