---
status: proposed
date: 2026-06-01
decision-makers: [AnchorHub maintainers]
generated-by:
  platform: claude-code
  agent: (none — author-driven)
  skills: []
  model: claude-opus-4-7
---

# JabRef fulltext via a vendor-neutral browser-extension protocol

## Context and Problem Statement

JabRef ships several `FulltextFetcher` implementations that download PDFs
directly from publishers (`ApsFetcher`, `IEEE`, `ACS`, etc.). They fail
when the publisher requires institutional SSO, an authenticated session,
or anti-bot defences that block plain HTTP clients. Concrete trigger
case: `ApsFetcher` returns HTTP 418 for
`https://ieeexplore.ieee.org/document/10589877`.

A browser extension already has the necessary session cookies because
the user logged in interactively. AnchorHub already runs a daemon
(`ankd`) with a native-messaging channel to a browser extension; that
infrastructure can be reused to fetch PDFs on JabRef's behalf.

The question: what is the right shape for JabRef → AnchorHub → browser
communication so that

1. JabRef's existing fetcher pipeline absorbs the new path without an
   intrusive refactor;
2. JabRef does not become coupled to AnchorHub specifically — other
   browser-extension companions (notably JabRef-Browser-Extension,
   AGPL-3.0) should be able to serve the same role;
3. AnchorHub stays closed-source/commercial without licence bleed across
   the boundary;
4. the user experience does not regress (no UI freezes, no spurious
   retries, no surprising tab focus stealing).

## Considered Options

* **Option A — `BrowserExtensionFulltextFetcher` + vendor-neutral
  protocol (chosen).** JabRef ships one new fetcher that speaks an
  HTTP-loopback protocol drafted in
  [`docs/requirements/browser-extension-fulltext.md`](../requirements/browser-extension-fulltext.md).
  Providers (AnchorHub, JabRef-Browser-Extension, …) implement the
  protocol independently. Discovery via a well-known directory; per-
  provider bearer-token auth.
* **Option B — AnchorHub-specific fetcher (`AnchorHubFulltextFetcher`)**.
  JabRef adds a fetcher hard-wired to AnchorHub's port and token
  conventions. Simpler but couples JabRef to one commercial vendor.
* **Option C — JabRef's existing native-messaging host
  (`jabrefHost.py`) extended with a fetch-fulltext action.** Reuses the
  Linux native-messaging infrastructure. Forces all communication
  through the OS native-messaging registration mechanism (different per
  OS, different per browser) and assumes JabRef itself runs a native-
  messaging host, which is not its current architecture for fetching.

## Decision Outcome

**Chosen: Option A.** Vendor-neutral protocol with a single JabRef-side
fetcher class `BrowserExtensionFulltextFetcher` that enumerates
providers from a discovery directory and races them in parallel.

Concrete sub-decisions baked into this ADR (in addition to the general
shape):

### 1. Result hand-off: `file://` URL, not raw bytes

`FulltextFetcher#findFullText` returns `Optional<URL>`. The provider
writes the PDF to local disk and returns the path; the fetcher wraps it
as a `file://` URL. JabRef's existing pipeline copies the file into the
library directory under the configured naming pattern. No changes to
JabRef's attach logic.

Trade-off accepted: cross-host topologies (Linux JabRef talking to a
provider on a Windows VM) cannot use `file://`. Deferred to Phase 4 of
the plan, which would add an `Accept: application/pdf` body-streaming
variant. Not needed for the Windows-direct install that is phase 1's
scope.

### 2. DOI resolution lives in the provider (ankd), not the extension

Resolving DOI → publisher URL is plain HTTP and unit-testable without a
browser. Keeping it in `ankd` lets the per-publisher adapter picker run
in C# tests and isolates the extension as a "given this URL, return a
PDF" service. The trade-off — needing the daemon to be running for
fetcher requests — already holds for OneNote lookups.

### 3. Single HTTP server, per-realm tokens (single `--http-listen` flag)

`ankd` exposes one loopback HTTP server (`--http-listen=host:port`).
Route families ("realms") are enabled by passing per-realm token-file
flags; omitting the flag refuses all routes in that realm with `401`.
Phase 1 ships one realm: `--fulltext-token-file`.

Trade-off versus separate servers per concern: shared lifecycle, one
bind, less duplicated HTTP code. Per-realm tokens preserve isolation:
JabRef's fulltext token cannot call a future `/v1/anchor` write
endpoint. Future realms (`anchor`, `status`, …) slot in by adding new
token flags without changing the server.

### 4. `TrustLevel.PUBLISHER`, no new `SOURCE` tier

The browser-extension fetcher sits at the same trust tier as direct
publisher fetchers. Both produce real publisher PDFs; whichever returns
first wins. No new enum value added to JabRef.

Rejected alternative — `TrustLevel.SOURCE` above `PUBLISHER` —
overstates the new tier's authority (a misconfigured browser session
can return a "subscription preview" page just as misleadingly as a
publisher API returning a teaser PDF).

### 5. Extension owns the queue; ankd is a dumb pipe; no JabRef retries

`ankd` does not internally queue fetch requests. It forwards each
request to the extension over native messaging and holds the JabRef HTTP
connection open until the extension returns a result. The browser
extension maintains a FIFO queue with a global concurrency cap of 3 and
a per-publisher cap of 1.

JabRef issues a single synchronous `POST /v1/fulltext` with a long
client-side socket timeout (suggest 5 minutes). No retry, no polling,
no callbacks. `503 busy` exists as a safety valve for a misbehaving
provider whose queue overflows; JabRef treats `503` as a soft miss
identical to `404`. Cancellation propagates via TCP connection close →
`ankd` → `abortFulltext` native-messaging action → tab/download
abort.

Rejected alternatives:

- **Server-side queue with `503 busy` + JabRef retry loop**: introduces
  polling, surprises the user with mysterious 5-second waits, complicates
  client error handling.
- **Callback URL from JabRef back to provider**: forces JabRef to run a
  server endpoint for fulltext, conflates the offline/online add path,
  and breaks the simple sync-fetcher contract.
- **Long polling / SSE in v1**: streaming progress events are a UX win
  but couple v1 to a JabRef-wide refactor of `FulltextFetchers`. Parked
  as Phase 5; spec leaves room for an `Accept: text/event-stream`
  response mode added later without breaking v1 clients.

### 6. Capabilities opaque, generic fallback in the provider

The spec deliberately does not include a capabilities endpoint. Providers
decide per-request whether they can serve a given DOI/URL. AnchorHub
runs a per-publisher adapter when one matches and otherwise a generic
adapter that scans the loaded page for any plausible PDF link. Only when
both fail does the provider return `404 no-adapter` / `404 no-pdf-found`.

Rejected alternative — `GET /v1/capabilities` listing supported
publishers — couples JabRef to a shared publisher taxonomy that must be
versioned across implementations.

### 7. Discovery under JabRef's config dir

Provider discovery files live under
`%APPDATA%\JabRef\fulltext-providers\` (and platform equivalents).
JabRef enumerates them at fetcher init.

Rejected alternative — vendor-neutral path
`%APPDATA%\BrowserExtensionFulltextProviders\` — decouples from JabRef's
install but pays the cost of an unfamiliar location and breaks if any
other tool ever consumes the protocol (unlikely). The JabRef-config
location follows the principle of least surprise for users.

## Pros and Cons of the Options

### Option A — Vendor-neutral protocol

* Good, because JabRef ships one fetcher class regardless of which
  provider serves the request.
* Good, because AGPL-3.0 (JBE) and commercial-closed (AnchorHub) can
  coexist as independent implementations of the same wire spec — no
  source crosses the licence boundary.
* Good, because future companions can implement the protocol without
  patching JabRef.
* Bad, because the discovery mechanism is one more thing to specify and
  test, and provider-side install scripts must write/remove the
  discovery file correctly.
* Bad, because the spec doc must live somewhere licensable. Drafted in
  the AnchorHub repo for design reference; intended to migrate to JabRef
  under JabRef's MIT licence once stabilised.

### Option B — AnchorHub-specific fetcher

* Good, because faster to implement (no discovery, no spec).
* Bad, because it presupposes that no other browser companion will ever
  serve fulltext, foreclosing on JabRef-Browser-Extension's potential
  implementation.
* Bad, because the AnchorHub name appears in JabRef's MIT codebase as a
  product reference; awkward when AnchorHub is closed-source.

### Option C — Native-messaging host extension

* Good, because no network port is opened on JabRef's side.
* Bad, because JabRef does not currently maintain a native-messaging
  host for fetching, only for the entry-add direction (see ADR 0004),
  and native-messaging registration differs per OS/browser.
* Bad, because the extension already has a native-messaging connection
  to `ankd`; routing fulltext through JabRef's native-messaging host
  would introduce a separate IPC topology.

## More Information

- Plan: [`docs/plan-jabref-fulltext-fetch.md`](../plan-jabref-fulltext-fetch.md)
- Wire spec: [`docs/requirements/browser-extension-fulltext.md`](../requirements/browser-extension-fulltext.md)
- Related entry-add ADRs: [0003-jabref-offline-handling.md](0003-jabref-offline-handling.md),
  [0004-jabref-online-add-mechanism.md](0004-jabref-online-add-mechanism.md)
- JabRef fetcher interface: `jablib/.../importer/FulltextFetcher.java`
- JabRef fetcher registry: `jablib/.../importer/WebFetchers.java`
