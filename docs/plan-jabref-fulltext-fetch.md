# Plan: JabRef → AnchorHub fulltext PDF fetch

Goal: when JabRef cannot fetch a paper's PDF directly (paywall, anti-bot, 418,
institutional auth), JabRef asks AnchorHub to fetch it. AnchorHub uses the
user's logged-in browser session to obtain the PDF and hands the file path
back to JabRef, which attaches it to the entry with the same semantics as a
drag-drop onto the main table (move into the configured file directory and
rename per the configured pattern).

AnchorHub implements the provider side of the **Browser-Extension Fulltext
Provider Protocol v1**, drafted in
[`requirements/browser-extension-fulltext.md`](requirements/browser-extension-fulltext.md).
The protocol is provider-agnostic; JabRef-Browser-Extension may eventually
implement it too. JabRef ships a single, vendor-neutral consumer:
`BrowserExtensionFulltextFetcher`.

First publisher adapter: **IEEE Xplore**. Other publishers follow the same
shape.

---

## Licensing constraint

This plan is written from scratch in this repo. No code, diagrams, file
structure, or text are taken from the `researcher-cockpit-private` student
project. Any overlap is incidental. The student project's bridge is a known
prior art reference only; its implementation is not a source.

AnchorHub stays closed-source/commercial. The protocol (separate spec doc)
is intended to land in the JabRef repository under JabRef's MIT license once
stabilised. JabRef-Browser-Extension (AGPL-3.0) may implement the same
protocol independently — no code crosses the licence boundary, only the
wire format. Work on that implementation is not done as part of AnchorHub.

---

## Trigger surface (JabRef side)

JabRef already runs every registered `FulltextFetcher` for an entry via
`FulltextFetchers#findFullTextPDF` (see `WebFetchers#getFullTextFetchers`).
Each fetcher returns `Optional<URL>`; the highest-trust hit wins; JabRef
downloads the URL and attaches it.

A single, vendor-neutral fetcher is added to JabRef:

```
fetchers.add(new BrowserExtensionFulltextFetcher(prefs));
```

`BrowserExtensionFulltextFetcher` enumerates all providers from the
discovery directory (see protocol spec), races them in parallel, and
returns the first successful `file://` URL. AnchorHub is one such
provider; JabRef-Browser-Extension may be another in future.

Per-request behaviour:

- `findFullText(BibEntry)`:
  1. Read DOI from `StandardField.DOI` (and URL from `StandardField.URL`).
     If both absent → `Optional.empty()`.
  2. Load discovery files from `%APPDATA%\JabRef\fulltext-providers\`
     (or platform equivalent). Filter out user-disabled providers.
  3. For each enabled provider, `POST http://127.0.0.1:<port>/v1/fulltext`
     body `{ "doi": "...", "url": "..." }` with provider's bearer token,
     in parallel.
  4. First `200 { path: "..." }` wins. Return
     `Optional.of(new File(path).toURI().toURL())` — a `file://` URL.
     JabRef's existing pipeline copies it and attaches it; standard
     move-and-rename behaviour applies.
  5. All providers miss → `Optional.empty()`.
- `getTrustLevel()`: `TrustLevel.SOURCE` (new tier above `PUBLISHER`).
  Rationale: a provider returns the actual PDF the user can already see in
  the browser; publisher-level guesses are subordinate.
- `getDownloadHeaders()`: empty — `file://` needs none.

### Why `file://` not raw bytes

`FulltextFetcher` returns `URL`. Stuffing raw bytes through the interface
would mean changing the interface or writing a temp file inside the fetcher.
A `file://` URL pointing at a temp path the provider already wrote is the
smallest delta to JabRef and lets the rename-on-attach logic stay
untouched.

The temp file is deleted by the provider after a TTL (suggest 1 h) or
after JabRef calls a follow-up `DELETE /v1/fulltext/<id>` once it has
moved/renamed the file. TTL is sufficient; the DELETE is an optional
cleanup.

Phase 4 (cross-host topology) may add a raw-bytes mode where JabRef
receives the PDF in the HTTP response body instead of a path. Not needed
for the IEEE Windows-direct slice.

---

## AnchorHub side

### Transport: HTTP loopback `127.0.0.1:<port>`

A new mode for `ankd`:

```
ankd --http-listen=127.0.0.1:<port> --fulltext-token-file=<path>
```

`--http-listen` starts a single loopback HTTP server. Route families
("realms") are enabled by passing per-realm token-file flags; omitting a
flag refuses all routes in that realm with `401`. Phase 1 ships one realm:

| Realm | Path prefix | Enabling flag |
|---|---|---|
| fulltext | `/v1/fulltext`, `/v1/health` | `--fulltext-token-file=<path>` |

Future realms (out of scope here, listed so the server design accommodates
them):

| Realm | Path prefix | Enabling flag (future) |
|---|---|---|
| anchor | `/v1/anchor` (URL-lookup for non-browser callers) | `--anchor-token-file=...` |
| status | `/v1/status` (tray app, observability) | `--status-token-file=...` |

The fulltext realm implements the provider side of the protocol described
in [`requirements/browser-extension-fulltext.md`](requirements/browser-extension-fulltext.md).
Endpoints: `GET /v1/health`, `POST /v1/fulltext`,
`DELETE /v1/fulltext/{id}`. Token + Origin check per spec.

`GET /v1/health` is special-cased: it accepts the fulltext token (today
the only realm). When more realms ship, health becomes either un-auth
(trivial liveness only) or accepts any valid realm token; that decision
happens when the second realm lands.

### Discovery file

`install.ps1` (and the Linux installer) writes
`%APPDATA%\JabRef\fulltext-providers\anchorhub.json`:

```json
{
  "name": "anchorhub",
  "displayName": "AnchorHub",
  "port": 17893,
  "tokenFile": "C:\\Users\\<user>\\AppData\\Roaming\\AnchorHub\\fulltext.token",
  "protocolVersion": 1
}
```

If the chosen port is in use at startup, ankd picks another and rewrites
the discovery file. Uninstaller removes the file.

### Fulltext flow inside ankd

1. Resolve DOI to publisher URL.
   - HEAD `https://doi.org/<doi>`, follow redirects, take final URL.
   - For IEEE: assert host is `ieeexplore.ieee.org` and path is
     `/document/<arnumber>`. Reject otherwise (phase 1 IEEE-only).
2. Dispatch to browser via native messaging: `fetchFulltext` action with
   `{ id, url, publisherHint: "ieee" }`.
3. Wait for browser response (timeout, e.g. 60 s).
4. Browser response is `{ id, path }` (path is where the browser saved the
   file). Return that path verbatim in the HTTP response.

The DOI → URL resolution can also live in the extension; placing it in
`ankd` keeps the browser adapter purely a "fetch this URL → return file"
service. Decision: keep in `ankd` for testability without a browser.

### Per-publisher adapter (IEEE first)

`AnchorHub.Core` already hosts URL helpers (`*UrlHelper.cs`). Add a parallel
notion: **`*FulltextAdapter`**, hosted in `AnchorHub.Core` so it is
unit-testable without OneNote or a browser.

Each adapter exposes:

- `Matches(NormalizedUrl) → bool`
- `PublisherKey(NormalizedUrl) → string` — stable key for per-publisher
  concurrency throttling (e.g. `"ieeexplore.ieee.org"`).
- `DocumentUrl(NormalizedUrl) → string` — the URL the browser tab should
  open.
- `ContentScriptName → string` — file name under `adapters/` in the
  extension (e.g. `"ieee"`).

`IeeeFulltextAdapter`:

- `Matches`: host is `ieeexplore.ieee.org` and path matches `/document/<digits>`.
- `PublisherKey`: `"ieeexplore.ieee.org"`.
- `DocumentUrl`: returns the input URL unchanged.
- `ContentScriptName`: `"ieee"`.
- The actual PDF locator (iframe / `stamp.jsp?tp=&arnumber=<id>` /
  `stampPDF/getPDF.jsp?...`) lives in the browser content script, because
  IEEE renders the PDF link client-side.

### Generic fallback adapter

When no `*FulltextAdapter.Matches(url)` returns true, the orchestrator
falls back to a **generic adapter**:

- `PublisherKey`: the URL's host (per-host throttle still applies).
- `DocumentUrl`: the resolved publisher URL unchanged.
- `ContentScriptName`: `"generic"`.

The generic content script scans the loaded page for `<a href>` links
whose href matches:

- ends with `.pdf` (case-insensitive)
- contains `/pdf/`, `/fulltext/`, or `/download/` path segments
- has `type="application/pdf"`

Picks the first match. Returns null if nothing matches → ankd returns
`404 no-pdf-found`. If both per-publisher and generic adapters were
skipped (no `Matches`, no generic-scan candidate), return
`404 no-adapter`.

### Concurrency throttle (extension-side)

The browser extension owns the queue, not ankd. ankd just dispatches
`fetchFulltext` to the extension over native messaging and awaits a
result. Per-request correlation id matches request to response on the
native-messaging channel.

Extension queue policy:

- Global cap: 3 concurrent fetches in flight.
- Per-publisher cap: 1 (keyed by URL host).
- FIFO admission queue. New requests wait until a global and a
  per-publisher slot are both free.
- Queue depth limit: 50. Beyond that, the extension returns
  `{ error: "busy" }` and ankd surfaces `503 busy` to the HTTP client.
  This is a safety valve for misbehaving callers, not regular flow
  control.

ankd safety valve: if the extension has not acknowledged a queued request
within a generous timeout (e.g. 90 s before *any* progress signal), ankd
also returns `503 busy` and closes the native-messaging request.

### Cancellation

JabRef cancels via TCP connection close (`FulltextFetchers` aborts
losing fetchers once a winner is chosen). ankd is meant to detect the
closed HTTP connection and send an `abortFulltext` message to the
extension with the same correlation id; the extension drops the request
from its queue or, if already in flight, closes the tab and aborts the
download.

**Implementation note (phase 1):** `HttpListener` (used by ankd in
.NET 8) does not surface mid-request client disconnect as a
cancellation token — there is no built-in equivalent of Kestrel's
`HttpContext.RequestAborted`. As a result, phase 1 relies on the
safety-valve timer (90 s) to fire abort when the client has gone away
but the request is still pending. The dispatcher's cancellation
registration sends `abortFulltext` automatically whenever the linked
CTS triggers, so connection-close cleanup happens within 90 s of the
client closing the connection.

A future improvement could swap `HttpListener` for ASP.NET Core Kestrel
(`Microsoft.AspNetCore.Server.Kestrel`) to get proper `RequestAborted`
propagation, at the cost of pulling Kestrel into the self-contained
single-file publish. Tracked as a Phase 4/5 polish item; not blocking
phase 1.

### Browser side

Native-messaging action `fetchFulltext`. Two-pass strategy: background tab
first, retry visible if nothing found.

```js
// In background.js
case "fetchFulltext": {
  const pdfUrl = await tryFetch(action.url, action.contentScriptName, false)
              || await tryFetch(action.url, action.contentScriptName, true);
  if (!pdfUrl) { sendResponse({ error: "no-pdf-found" }); return; }
  const dlId = await browser.downloads.download({
    url: pdfUrl, filename: `anchorhub/${action.id}.pdf`, saveAs: false,
  });
  const path = await waitForDownloadComplete(dlId);   // listener on downloads.onChanged
  sendResponse({ id: action.id, path });
}

async function tryFetch(url, scriptName, active) {
  const tab = await browser.tabs.create({ url, active });
  try {
    await waitForTabComplete(tab.id);                 // listener on tabs.onUpdated
    const result = await browser.scripting.executeScript({
      target: { tabId: tab.id },
      files:  [`adapters/${scriptName}.js`],          // e.g. adapters/ieee.js or adapters/generic.js
    });
    return result[0].result;                          // string PDF URL or null
  } finally {
    await browser.tabs.remove(tab.id);
  }
}
```

`adapters/ieee.js` (content script, runs in page context):

- Locate the PDF link rendered on `https://ieeexplore.ieee.org/document/<id>`.
  Selector candidates (verify against live page): the `<iframe>` whose
  `src` contains `/stamp/stamp.jsp` exposes the arnumber; the actual binary
  PDF is at `https://ieeexplore.ieee.org/stampPDF/getPDF.jsp?tp=&arnumber=<id>`.
- Return that URL as the script result.

The adapter file is a thin DOM scraper. Tests live as fixture HTML files
plus a small DOM-based test harness; no need to mock the network.

### Auth and paywall

AnchorHub relies on the browser already being logged in (institutional SSO,
publisher account, etc.). It does not store credentials and does not perform
auth itself. If the browser tab cannot see a PDF link, the adapter returns
"no PDF found" and JabRef falls through to its other fetchers.

---

## Decisions

1. **`FulltextFetcher` trust level**: reuse `TrustLevel.PUBLISHER`. No
   enum change. Provider sits alongside `ApsFetcher`, `IEEE`, etc.; in
   case of ties `FulltextFetchers` picks non-deterministically — acceptable
   because all "publisher-level" results are equally good PDFs.
2. **Cookie-isolation model**: same-browser assumption. AnchorHub uses
   cookies of whichever browser hosts the extension; mismatch (login in
   browser A, extension in browser B) silently returns a
   subscription-preview HTML page that the IEEE adapter then fails to
   extract a PDF link from, returning `404 no-pdf-found`. Documented in
   README; no special detection code in phase 1.
3. **Linux/VM topology**: Windows-direct install only for phase 1. The
   Linux forwarder install does not enable the fulltext realm
   (`install-linux.sh` omits `--fulltext-token-file`; no discovery file
   written). Phase 4 may add raw-bytes streaming for cross-host topologies.
4. **JabRef preference granularity**: master toggle only. Single "Use
   browser-extension fulltext" switch in `BrowserExtensionFulltext-
   Preferences`. All discovered providers queried when enabled. Per-provider
   toggles can be added later if multi-provider setups become common.
5. **Concurrency cap**: enforced in the **extension**, not in ankd. ankd
   is a dumb pipe: it forwards the request to the extension over native
   messaging and holds the JabRef HTTP connection open until the
   extension returns a result. The extension maintains a FIFO queue with
   a global cap of 3 concurrent fetches and a per-publisher cap of 1 (no
   two tabs on `ieeexplore.ieee.org` at once — most publishers throttle
   aggressively). Requests beyond a sane queue depth (e.g. 50) cause ankd
   to return `503 busy` as a **safety valve**, not normal flow control.
   JabRef does **not** retry `503` — it is treated as a soft miss, same
   as `404`. No JabRef-side retries, no polling, no callbacks: a single
   long-blocking synchronous `POST /v1/fulltext` per fetch attempt.
6. **Tab visibility**: `tabs.create({ active: false })` by default. If the
   adapter returns "no PDF found" on first try, retry once with
   `{ active: true }` — covers lazy-loaded content that requires a
   viewport. Tab closed on download complete in both cases.
7. **No adapter for the resolved publisher**: ankd runs a **generic
   fallback**. Open the resolved publisher URL in a tab; generic content
   script scans for `<a href>` ending in `.pdf` or matching common PDF
   path patterns (`/pdf/`, `/fulltext/`, etc.); if one is found, download
   and return. Only if both the publisher-specific adapter (when present)
   *and* the generic fallback fail does ankd return
   `404 { error: "no-adapter" }` (no adapter found anything) or
   `404 { error: "no-pdf-found" }` (adapter ran but page had no PDF
   link). The distinction lets JabRef log the actual failure mode.

---

## Phases

### Phase 1 — IEEE only, Windows-direct install

- ankd: `--http-listen` + `--fulltext-token-file` mode, `/v1/health`,
  `/v1/fulltext` (DOI in, `IeeeFulltextAdapter`, native-messaging
  dispatch). Writes discovery JSON under
  `%APPDATA%\JabRef\fulltext-providers\anchorhub.json`.
- ext: `fetchFulltext` action, IEEE content adapter, downloads-API wiring,
  tab lifecycle.
- JabRef: `BrowserExtensionFulltextFetcher` reads discovery dir, races
  enabled providers, returns `file://` URL.

### Phase 2 — more publishers

Add adapters for ACS, Springer, Wiley, ScienceDirect, ResearchGate. Each
adapter is one small content script + one entry in the adapter registry.
No change to the wire protocol.

### Phase 3 — URL-only fetches (no DOI)

JabRef passes `{ url: "..." }` instead of `{ doi: "..." }`. AnchorHub
adapters dispatch on URL host.

### Phase 4 — Linux forwarder support

If the Linux/VM topology actually needs cross-host fulltext, change the
HTTP response from `{ path }` to a streaming PDF body. JabRef writes it to
a temp file before returning the `file://` URL. Skipped for now.

### Phase 5 (deferred) — streaming progress events

Phase 1–4 keep the existing sync `FulltextFetcher` interface
(`Optional<URL>`). `FulltextFetchers` already runs each fetcher as a
`Callable` on an `ExecutorService`, so the UI does not freeze and per-
fetcher parallelism exists. What is missing is **streaming intermediate
state** for live UX feedback ("AnchorHub: opening tab on
ieeexplore.ieee.org…", "queued behind 2 other fetches…",
"ResearchGate returned first; AnchorHub cancelled").

Proposed shape — JDK-native, no extra reactive dep:

- New interface `StreamingFulltextFetcher` returning
  `CompletableFuture<Optional<FetcherResult>>` plus a side-channel
  `Consumer<FetchProgress>` (or a `Flow.Publisher<FetchProgress>`).
- `FulltextFetchers` upgraded to merge progress events from all
  fetchers and forward the first successful completion as before.
- All other 15-odd fetchers get a sync→stream adapter so they continue
  to work without changes.

Notes:

- **ReactFX is deprecated in JabRef.** Codebase trend is EasyBind +
  plain JavaFX bindings. Do not introduce ReactFX here.
- This is a **JabRef-wide refactor**, not an AnchorHub change. Not
  blocked on AnchorHub; AnchorHub's provider would emit progress events
  via the same native-messaging channel it already uses for
  `fetchFulltext` / `abortFulltext`, with a new `progressFulltext`
  message.
- Spec-level addition: a new optional `text/event-stream` response mode
  on `POST /v1/fulltext` (Accept-header-negotiated) carrying SSE
  progress events terminating with the file path. Backwards-compatible
  with v1 — clients that do not request the event stream still get the
  single JSON response.

Out of scope for this repository's milestones; track separately when
JabRef's fetcher infrastructure evolves.

---

## Step-by-step implementation order

### AnchorHub.Core

1. New file `IFulltextAdapter.cs` interface (`Matches`, `PublisherKey`,
   `DocumentUrl`, `ContentScriptName`).
2. New file `IeeeFulltextAdapter.cs` implementing the interface.
3. New file `GenericFulltextAdapter.cs` — host-based fallback (always
   matches; `ContentScriptName = "generic"`).
4. New file `FulltextAdapters.cs` registry: returns the first matching
   publisher-specific adapter, or `GenericFulltextAdapter` as fallback.
5. Unit tests in `AnchorHub.Core.Tests/IeeeFulltextAdapterTests.cs` and
   `FulltextAdaptersTests.cs`.

### AnchorHub.Hub

6. New file `HttpListenerHost.cs`: minimal `HttpListener` server bound to
   `127.0.0.1:<port>`. Router dispatches by path prefix to per-realm
   handlers. Per-realm bearer-token check (token loaded from the realm's
   `--<realm>-token-file`); requests to a realm whose flag was not passed
   return `401`. Origin check applies globally. Long-running responses:
   no server-side socket timeout; connection held until extension returns.
7. Wire `--http-listen=host:port` and `--fulltext-token-file=<path>` argv
   into `Program.cs`. Server starts only if `--http-listen` is present;
   fulltext realm enables only if `--fulltext-token-file` is present.
8. `FulltextRealmHandler` implements the DOI-resolve → adapter-pick →
   native-messaging-dispatch flow. No internal queue; the extension owns
   queueing. Surfaces extension-reported `{error:"busy"}` as HTTP `503`.
9. New action enum value / case for `fetchFulltext` going **toward** the
   extension (today native messaging is browser→hub; this is hub→browser).
   Verify the current message direction is bidirectional; if it is not,
   add a request/response correlation id on top of the existing framing.
10. `abortFulltext` action: when the HTTP client disconnects (token
    cancellation triggered by `HttpListenerContext.Response.OutputStream`
    write failure or registered `CancellationToken`), send `abortFulltext`
    with the same correlation id to the extension.
11. Safety-valve timer in `FulltextRealmHandler`: if the extension has not
    sent any progress / completion message within e.g. 90 s, return
    `503 busy` and abandon the request.
12. Discovery-file writer: at startup, after binding the port, write
    `%APPDATA%\JabRef\fulltext-providers\anchorhub.json` (and Linux/macOS
    equivalents) with current `port` and `tokenFile`. Remove on graceful
    shutdown. **Linux installer skips the discovery write** — fulltext
    realm not enabled in forwarder topology.
13. Integration test in `AnchorHub.Hub.Tests` using a stub adapter and a
    stub browser (loopback) that simulates the file-path response, plus
    a cancellation test (client closes connection → stub browser sees
    `abortFulltext`).

### Extension

14. `adapters/ieee.js` content script: locate PDF URL, return string.
15. `adapters/generic.js` content script: scan for any `<a href>` matching
    PDF-link heuristics, return first.
16. `background.js`: handle `fetchFulltext` action — two-pass tab strategy
    (background then visible), `downloads.download`,
    `downloads.onChanged`, return path.
17. Per-publisher serial / global concurrency queue in `background.js`:
    Map keyed by host (per-publisher slot) + counter (global slot).
    `abortFulltext` handler removes queued entries or aborts in-flight
    tabs. Returns `{ error: "busy" }` if queue depth exceeds 50.
18. Manifest: add `"downloads"` permission. Add `"scripting"` if not
    already present. Add `https://ieeexplore.ieee.org/*` to
    `host_permissions`. Add `<all_urls>` for the generic fallback (or
    accept that generic fallback only works on hosts in
    `host_permissions`; phase 1 ships IEEE only so `<all_urls>` deferred).
19. Manual test: trigger `/v1/fulltext` with the example DOI
    `10.1109/SANER60148.2024.00014` via `curl` against ankd, observe a
    file on disk.

### JabRef (in `jabref-worktree-2`)

20. New file `jablib/.../importer/fetcher/BrowserExtensionFulltextFetcher.java`.
    Vendor-neutral — reads provider list from the discovery directory.
    `TrustLevel.PUBLISHER`. Per-fetcher HTTP client configured with a long
    socket timeout (5 min); no retry logic.
21. Discovery-directory enumerator: parse all `*.json` under
    `%APPDATA%\JabRef\fulltext-providers\` (or platform equivalent), skip
    unparseable entries. Log warnings for malformed files.
22. Per-provider client: `Authorization: Bearer <token>` read from
    `tokenFile`, `POST /v1/fulltext`, race all providers in parallel,
    take first 200. `503` and `404` both treated as soft miss (no retry).
23. JabRef preference group `BrowserExtensionFulltextPreferences`: single
    master enabled flag. Discovered provider list shown in the pref panel
    for visibility only (no per-provider toggles in phase 1). Pref panel
    under "Web search".
24. Register in `WebFetchers#getFullTextFetchers`.
25. Tests under
    `jablib/src/test/java/.../BrowserExtensionFulltextFetcherTests.java`
    against a wiremock HTTP server simulating one or two providers
    (race-to-first-200, 503-is-miss, 404-is-miss, slow-response within
    timeout).

### End-to-end

26. Manual: JabRef → "Look up document identifier / Fetch full text" on
    an IEEE entry → PDF appears as a linked file moved+renamed per pattern.
27. Update `README.md` Usage section with the IEEE fulltext capability and
    the same-browser cookie-isolation note (decision 2).
28. Add `req~ank.jabref.fulltext-fetch~1` to `docs/requirements/anchorhub.md`
    describing the AnchorHub provider role, the `--http-listen` /
    `--fulltext-token-file` startup flags, the discovery file location,
    the IEEE adapter + generic fallback, the extension-side throttle, the
    no-retry sync HTTP model, and the Windows-direct-only scope.
29. ADR: new `0007-jabref-fulltext-via-browser-extension-protocol.md`
    covering the choice of `file://` URL over raw-bytes streaming, the
    placement of DOI resolution in `ankd` rather than the extension, the
    single-server / per-realm-token HTTP design, the choice of
    `TrustLevel.PUBLISHER` over a new `SOURCE` tier, and the
    extension-as-queue-owner / sync-HTTP / no-retry model.

Commit after each numbered step. Single-publisher scope keeps each commit
small and reviewable.

---

## Related

- [`requirements/browser-extension-fulltext.md`](requirements/browser-extension-fulltext.md)
  — wire protocol this plan implements.
- [`plan-browser-jabref.md`](plan-browser-jabref.md) — popup-side JabRef
  integration. Independent of this plan but shares the JabRef HTTP surface
  (port 23119) for entry queries, not for fulltext.
- [ADR 0003 / 0004](decisions/) — JabRef ↔ extension communication
  decisions for the entry-add path; fulltext is a separate direction
  (JabRef → ankd, not ext → JabRef) and not constrained by those ADRs.
- `docs/plan-pdf-citation-hover.md` — unrelated PDF feature.
