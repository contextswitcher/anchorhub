"use strict";

// Cross-browser shim. Firefox exposes `browser.*` as promise-based;
// Chrome only exposes `chrome.*` (also promise-based since MV3).
// Aliasing lets the rest of this file use `browser.*` everywhere.
if (typeof browser === "undefined" && typeof chrome !== "undefined") {
  globalThis.browser = chrome;
}

// Chrome's MV3 service worker has only one entry point, so it can't list
// graph/auth.js next to this file the way Firefox does in
// "background.scripts". importScripts is the supported shim.
if (typeof importScripts === "function" && typeof globalThis.AnchorGraphAuth === "undefined") {
  importScripts("graph/auth.js");
}
if (typeof importScripts === "function" && typeof globalThis.AnchorFulltext === "undefined") {
  importScripts("fulltext.js");
}

const HOST_NAME        = "io.github.contextswitcher.anchorhub";
const DEBOUNCE_MS      = 300;
const CACHE_TTL_MS     = 24 * 60 * 60 * 1000;
const JABREF_BASE      = "http://localhost:23119";
const JABREF_CONFIRM_DELAY_MS = 5000;

// Badge colours. The "in OneNote" mint must stay in sync with the
// exact-match icon colour defined in extension/icons/generate-icons.ps1
// (the 'green' slot — Tol light mint #44BB99). The other two are
// badge-only and not part of the Tol icon palette.
const BADGE_COLOR_EXACT_MATCH   = "#44BB99";  // mint — JabRef "J" indicator background
const BADGE_COLOR_JABREF_OFFLINE = "#9B59B6"; // purple — JabRef "!" indicator background
const BADGE_COLOR_PAUSED         = "#CC3311"; // Tol vibrant red — "OFF" badge while paused

let port = null;

// ── Adapter settings ──────────────────────────────────────────────────────────
// Both adapters default to enabled. The options page (options.html) writes
// `adapterSettings` to local storage; the listener below reacts live so the
// user doesn't need to reload the extension after toggling.
// graph defaults off because it requires Azure AD configuration + sign-in
// before it can be used; flipping it on without setup would surface auth
// errors on every lookup.
const SETTINGS_DEFAULTS = { onenote: true, jabref: true, graph: false, contextswitcher: true };
let settings = { ...SETTINGS_DEFAULTS };
// Resolved once initial settings load completes — work that depends on
// adapter state (the initial connect(), active-tab refresh) awaits this.
let settingsReady;

async function loadSettings() {
  const stored = (await browser.storage.local.get("adapterSettings")).adapterSettings ?? {};
  settings = { ...SETTINGS_DEFAULTS, ...stored };
  paused = !!(await browser.storage.local.get("anchorhubPaused")).anchorhubPaused;
}

// Quick-disable toggle. When true, all per-tab lookup work is short-
// circuited and every tab shows the neutral "no match" icon — used
// for screenshots and shoulder-surfing situations where the user
// wants the toolbar to look like an unconfigured browser.
let paused = false;

// The native-messaging port to ankd is shared by the OneNote COM adapter
// and the Graph OAuth listener, so the port must stay up whenever either
// of them is enabled.
function needsDaemon() {
  return settings.onenote || settings.graph || settings.contextswitcher;
}

function disconnectNativePort() {
  if (!port) return;
  try { port.disconnect(); } catch {}
  port = null;
}

async function refreshActiveTabAfterSettingsChange() {
  const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id || !tab.url) return;
  // Clear per-tab OneNote-derived state so the icon / tooltip reflects the
  // new adapter configuration.
  tabMatches.delete(tab.id);
  tabTotal.delete(tab.id);
  tabOneNoteTitle.delete(tab.id);
  tabDoi.delete(tab.id);
  tabJabRef.delete(tab.id);
  tabJabRefOnline.delete(tab.id);
  tabBibtex.delete(tab.id);
  tabBibtexMatches.delete(tab.id);
  tabBibtexJabRef.delete(tab.id);
  tabContextSwitcher.delete(tab.id);
  tabCsPromises.delete(tab.id);
  clearTimeout(jabrefConfirmTimers.get(tab.id));
  jabrefConfirmTimers.delete(tab.id);

  // Re-run the ContextSwitcher lookup so toggling the adapter reflects live.
  if (settings.contextswitcher) runContextSwitcherLookup(tab.id, tab.url);

  if (settings.onenote) {
    setSearchingIcon(tab.id);
    browser.action.setTitle({ tabId: tab.id, title: "AnchorHub — searching…" });
    await lookupTab(tab.id, tab.url, { force: true });
  } else {
    setDisabledIcon(tab.id);
  }
  if (settings.jabref) {
    const doi = await detectDoi(tab.id, tab.url);
    if (doi) await runJabRefLookup(tab.id, doi, { fromCache: false });
    else     await runJabRefUrlLookup(tab.id, tab.url);
  }
  const bibtex = await detectBibtex(tab.id);
  if (bibtex?.length) {
    await Promise.all([
      runBibtexOneNoteLookup(tab.id, bibtex),
      runBibtexJabRefLookup(tab.id, bibtex),
    ]);
  }
  updateBadge(tab.id);
}

function setDisabledIcon(tabId) {
  browser.action.setIcon({ tabId, path: {
    16: "icons/notfound-16.png",
    32: "icons/notfound-32.png",
    48: "icons/notfound-48.png",
  }}).catch(() => {});
  browser.action.setBadgeText({ tabId, text: "" });
  browser.action.setTitle({ tabId, title: "AnchorHub — OneNote disabled" });
}

// [impl->req~ank.icon.paused~1]
// Paused: neutral icon plus a red "OFF" badge so the toolbar itself says
// why nothing is being looked up (mirrors ContextSwitcher's disabled look).
function setPausedIcon(tabId) {
  browser.action.setIcon({ tabId, path: {
    16: "icons/notfound-16.png",
    32: "icons/notfound-32.png",
    48: "icons/notfound-48.png",
  }}).catch(() => {});
  browser.action.setBadgeText({ tabId, text: "OFF" });
  browser.action.setBadgeBackgroundColor({ tabId, color: BADGE_COLOR_PAUSED });
  browser.action.setBadgeTextColor({ tabId, color: "#ffffff" });
  browser.action.setTitle({ tabId, title: "AnchorHub — paused" });
}

browser.storage.onChanged.addListener((changes, area) => {
  if (area !== "local" || !changes.adapterSettings) return;
  const prev = settings;
  settings = { ...SETTINGS_DEFAULTS, ...(changes.adapterSettings.newValue ?? {}) };

  const prevNeeds = prev.onenote    || prev.graph;
  const nowNeeds  = settings.onenote || settings.graph;
  if (prevNeeds && !nowNeeds) {
    disconnectNativePort();
    // Per-tab icon state is reset by refreshActiveTabAfterSettingsChange
    // below; clear the global "all tabs orange" suppression so a later
    // re-enable doesn't think the hub is still down.
    hubDisconnected = false;
  } else if (!prevNeeds && nowNeeds) {
    connect();
  }
  refreshActiveTabAfterSettingsChange();
});

// Hub-software-level disconnect (forwarder up, listener unreachable).
// Distinct from port-level disconnect (forwarder process gone) because
// the forwarder still answers requests with a synthetic
// {ok:false, error:{code:"DISCONNECTED"}} per req~ank.transport
// .disconnected-state~1. While set, suppress per-tab icon updates so
// "no-match grey" doesn't paint over the orange "not connected" icon.
let hubDisconnected = false;

const pending            = new Map();  // requestId → { resolve, reject }
const tabMatches         = new Map();  // tabId     → matches array
const tabTotal           = new Map();  // tabId     → total match count from hub
const urlCache           = new Map();  // url       → { matches, total, ts }
const nameCache          = new Map();  // name      → { matches, total, ts }
const debounceTimers     = new Map();  // tabId     → timer id
const tabLookupPromises  = new Map();  // tabId     → Promise (resolves when lookup done)

// ContextSwitcher adapter state. tabContextSwitcher holds the last result per
// tab ({ found, task } | { found:false } | null); tabCsPromises lets getMatches
// wait for an in-flight lookup so the popup shows the section on first open.
const tabContextSwitcher = new Map();  // tabId → result | null
const tabCsPromises      = new Map();  // tabId → Promise
const csCache            = new Map();  // url   → { result, ts }

// JabRef state per tab
const tabDoi              = new Map();  // tabId → doi string | null
const tabJabRef           = new Map();  // tabId → { matches: [{libraryId, citationKey}], confirmed: bool }
const tabJabRefOnline     = new Map();  // tabId → bool (last known online state)
const jabrefConfirmTimers = new Map();  // tabId → timer id (5 s deferred confirm query)

// BibTeX-on-page state per tab
// tabBibtex         : tabId → array of parsed entries (or null)
//                     entry shape: { entryType, citationKey, url?, doi?, eprint?, archiveprefix?, ... }
// tabBibtexMatches  : tabId → OneNote matches array for BibTeX URLs (matchType=exact, searchTerm=bibtex url)
// tabBibtexJabRef   : tabId → { matches: [{libraryId, citationKey}] }
const tabBibtex        = new Map();
const tabBibtexMatches = new Map();
const tabBibtexJabRef  = new Map();

// ── Connection ────────────────────────────────────────────────────────────────

function setAllTabsIcon(name) {
  browser.tabs.query({}).then(tabs => {
    for (const t of tabs) {
      browser.action.setIcon({ tabId: t.id, path: {
        16: `icons/${name}-16.png`,
        32: `icons/${name}-32.png`,
        48: `icons/${name}-48.png`,
      }}).catch(() => {});
    }
  });
}

function connect() {
  if (!needsDaemon()) {
    console.log("[ONL] No adapter needs the daemon — skipping native host connect.");
    return;
  }
  console.log("[ONL] Connecting to native host:", HOST_NAME);
  let myPort;
  try {
    myPort = browser.runtime.connectNative(HOST_NAME);
  } catch (e) {
    console.error("[ONL] connectNative threw:", e);
    setAllTabsIcon("yellow");
    return;
  }
  port = myPort;
  myPort.onMessage.addListener(onMessage);
  myPort.onDisconnect.addListener(() => {
    const err = browser.runtime.lastError;
    console.warn("[ONL] Disconnected from native host.", err ? err.message : "(no error)");
    // Only clear the shared `port` if the *current* port is the one
    // that just died. Otherwise a stale onDisconnect from an abandoned
    // port can clobber a freshly-opened replacement.
    if (port === myPort) {
      port = null;
      rejectAllPending("Hub disconnected");
      if (!needsDaemon()) return;
      setAllTabsIcon("yellow");
      const delay = Math.min(30000, (connect._backoff || 1000));
      connect._backoff = delay * 2;
      console.log(`[ONL] Reconnecting in ${delay}ms…`);
      setTimeout(connect, delay);
    } else {
      console.log("[ONL] Disconnect from stale port — current port still alive.");
    }
  });
  connect._backoff = 1000;
  console.log("[ONL] Native hub connected.");
}

// [impl->req~ank.cache.bounded~1]
// Without a timeout, a request the hub never answers pins its pending
// entry and the whole awaiting promise chain (markers, closures) forever,
// and the content-script lookup workers stall permanently.
const REQUEST_TIMEOUT_MS = 30_000;

function sendRequest(msg) {
  return new Promise((resolve, reject) => {
    if (!port) {
      console.warn("[ONL] sendRequest: no port, action=", msg.action);
      return reject(new Error("Hub not connected"));
    }
    const timer = setTimeout(() => {
      if (pending.delete(msg.requestId)) {
        reject(new Error(`Hub request timed out (${msg.action})`));
      }
    }, REQUEST_TIMEOUT_MS);
    pending.set(msg.requestId, { resolve, reject, timer });
    port.postMessage(msg);
  });
}

function rejectAllPending(reason) {
  for (const cb of pending.values()) {
    clearTimeout(cb.timer);
    cb.reject(new Error(reason));
  }
  pending.clear();
}

function onMessage(msg) {
  // Hub-initiated requests carry an `action` field (e.g. fetchFulltext,
  // abortFulltext). Route them through the relevant feature module
  // before falling through to the response-handling path below, which
  // only knows about ext-initiated requests.
  if (msg?.action) {
    if (globalThis.AnchorFulltext?.handleHubMessage(msg, port)) return;
    console.warn("[ONL] Unhandled hub-initiated action:", msg.action);
    return;
  }

  console.debug("[ONL] ← response action implied, requestId=", msg.requestId, "ok=", msg.ok);

  // [impl->req~ank.transport.disconnected-state~1]
  // Track hub-software-level disconnect transitions. Only the edge
  // matters for icon work — the popup-side status pings would
  // otherwise re-trigger setAllTabsIcon on every poll.
  if (!msg.ok && msg.error?.code === "DISCONNECTED") {
    if (!hubDisconnected) {
      hubDisconnected = true;
      console.warn("[ONL] Hub upstream unreachable; flipping to disconnected icon.");
      setAllTabsIcon("yellow");
    }
  } else if (msg.ok && hubDisconnected) {
    hubDisconnected = false;
    console.log("[ONL] Hub upstream recovered; per-tab icons will refresh on next lookup.");
  }

  const cb = pending.get(msg.requestId);
  if (!cb) { console.warn("[ONL] No pending callback for requestId=", msg.requestId); return; }
  pending.delete(msg.requestId);
  clearTimeout(cb.timer);
  msg.ok ? cb.resolve(msg) : cb.reject(new Error(msg.error?.message));
}

// [impl->req~ank.transport.disconnected-state~1]
// While the hub upstream is unreachable, navigation to a new URL
// would otherwise paint the "searching" gray square over the orange
// disconnect icon for the brief window before the lookup returns.
// Pick the right base icon directly so that flash never happens.
function setSearchingIcon(tabId) {
  const name = hubDisconnected ? "yellow" : "default";
  browser.action.setIcon({ tabId, path: {
    16: `icons/${name}-16.png`,
    32: `icons/${name}-32.png`,
    48: `icons/${name}-48.png`,
  }});
}

function uuid() {
  return crypto.randomUUID();
}

// ── Cache helpers ─────────────────────────────────────────────────────────────

// [impl->req~ank.cache.session-persistence~1]
// `urlCache` and `nameCache` live in Maps, but Chrome tears the MV3 service
// worker down after ~30 s idle and every Map goes with it. Both are mirrored
// into browser.storage.session so a cold restart re-reads warm results
// instead of reporting "not found" until the user hits Refresh. Session
// storage is memory-backed and cleared when the browser closes, so nothing
// lands on disk; the 24 h TTL is still enforced on read.
const SESSION_URL_CACHE_KEY  = "sessionUrlCache";
const SESSION_NAME_CACHE_KEY = "sessionNameCache";
const SESSION_CACHE_MAX      = 500;  // newest N entries per cache get persisted
const PERSIST_DEBOUNCE_MS    = 500;

// storage.session needs Firefox 115+ / Chrome 102+; older builds simply lose
// the caches on restart as before.
const sessionStore = browser.storage.session ?? null;

// Resolves once the persisted caches have been merged back in. Read paths
// that must not race hydration await this.
const cachesReady = hydrateCaches();

async function hydrateCaches() {
  if (!sessionStore) return;
  try {
    const stored = await sessionStore.get([SESSION_URL_CACHE_KEY, SESSION_NAME_CACHE_KEY]);
    mergeCache(urlCache,  stored[SESSION_URL_CACHE_KEY]);
    mergeCache(nameCache, stored[SESSION_NAME_CACHE_KEY]);
  } catch (e) {
    console.warn("[ONL] Session cache hydrate failed:", e);
  }
}

// Entries written since the worker started win: a live lookup that finished
// while hydration was in flight must not be overwritten by the older copy.
function mergeCache(map, entries) {
  if (!Array.isArray(entries)) return;
  const now = Date.now();
  for (const [key, entry] of entries) {
    if (!entry || now - entry.ts > CACHE_TTL_MS) continue;
    if (!map.has(key)) map.set(key, entry);
  }
}

let persistTimer = null;
function persistCaches() {
  if (!sessionStore) return;
  clearTimeout(persistTimer);
  persistTimer = setTimeout(() => {
    const newest = map => Array.from(map.entries())
      .sort((a, b) => b[1].ts - a[1].ts)
      .slice(0, SESSION_CACHE_MAX);
    sessionStore.set({
      [SESSION_URL_CACHE_KEY]:  newest(urlCache),
      [SESSION_NAME_CACHE_KEY]: newest(nameCache),
    }).catch(e => console.warn("[ONL] Session cache persist failed:", e));
  }, PERSIST_DEBOUNCE_MS);
}

function getCached(url) {
  const entry = urlCache.get(url);
  if (!entry) return null;
  if (Date.now() - entry.ts > CACHE_TTL_MS) { dropCached(url); return null; }
  return entry;  // { matches, total, ts }
}

// [impl->req~ank.cache.bounded~1]
// Entries are otherwise only evicted on read, so a browsing session with
// in-page markers grows these Maps without bound.
// ponytail: insertion-order eviction, LRU if hit-rate ever matters
const CACHE_MAX_ENTRIES = 5000;
function capCache(map) {
  while (map.size > CACHE_MAX_ENTRIES) map.delete(map.keys().next().value);
}

function setCached(url, entry)      { urlCache.set(url, entry);   capCache(urlCache);  persistCaches(); }
function dropCached(url)            { if (urlCache.delete(url))   persistCaches(); }
function setCachedName(name, entry) { nameCache.set(name, entry); capCache(nameCache); persistCaches(); }
function dropCachedName(name)       { if (nameCache.delete(name)) persistCaches(); }

// Stores a promise for tabId and auto-removes it when resolved.
function setLookupPromise(tabId, promise) {
  tabLookupPromises.set(tabId, promise);
  promise.finally(() => {
    if (tabLookupPromises.get(tabId) === promise) tabLookupPromises.delete(tabId);
  });
}

// ── dblp person page ──────────────────────────────────────────────────────────

// Person URLs look like https://dblp.org/pid/k/BarbaraKonig1.html
// Also matches the officially-maintained mirrors:
//   dblp.uni-trier.de  (Trier)
//   dblp.dagstuhl.de   (Dagstuhl)
const DBLP_PERSON_RE = /^https?:\/\/dblp\.(?:org|uni-trier\.de|dagstuhl\.de)\/pid\//i;

// Returns { orcid, name } scraped from a dblp person page, or nulls on failure.
async function scrapeDblpPerson(tabId) {
  try {
    const [{ result }] = await browser.scripting.executeScript({
      target: { tabId },
      func: () => {
        // ORCID is exposed as a link to orcid.org in the author header.
        const orcidLink = document.querySelector('a[href*="orcid.org/"]');
        let orcid = null;
        if (orcidLink) {
          const m = orcidLink.href.match(/orcid\.org\/(\d{4}-\d{4}-\d{4}-\d{3}[\dX])/i);
          if (m) orcid = m[1].toUpperCase();
        }
        // Name lives in <span itemprop="name"> on the person header.
        const nameEl = document.querySelector('span[itemprop="name"]')
                    ?? document.querySelector('h1 span');
        const name   = nameEl?.textContent?.trim() || null;
        return { orcid, name };
      },
    });
    return result ?? { orcid: null, name: null };
  } catch {
    return { orcid: null, name: null };
  }
}

// ── DOI detection ─────────────────────────────────────────────────────────────

// CrossRef DOIs always start with "10.<registrant>/" where registrant is 4+
// digits. The suffix may contain dots, slashes, hyphens, and letters. The
// leading `/` anchor avoids false matches inside version-like substrings
// (e.g. `v10.1234/foo`). Stops at whitespace, query string, or fragment.
// Covers doi.org, dx.doi.org, dl.acm.org/doi/<doi>,
// link.springer.com/article/<doi>, onlinelibrary.wiley.com/doi/<doi>, etc.
const DOI_PATH_RE = /\/(10\.\d{4,}\/[^\s?#]+)/i;
const DOI_META_NAMES = ["citation_doi", "dc.identifier", "prism.doi"];

async function detectDoi(tabId, url) {
  if (!settings.jabref) { tabDoi.set(tabId, null); return null; }
  // Fast path: any URL that contains a DOI in its path
  const urlMatch = DOI_PATH_RE.exec(url);
  if (urlMatch) {
    const doi = urlMatch[1].replace(/^doi:/i, "").toLowerCase();
    tabDoi.set(tabId, doi);
    return doi;
  }

  // Inject content script to read citation meta tags
  try {
    const [{ result }] = await browser.scripting.executeScript({
      target: { tabId },
      func: (names) => {
        for (const name of names) {
          const el = document.querySelector(
            `meta[name="${name}"], meta[name="${name.toLowerCase()}"]`
          );
          if (el?.content) return el.content.replace(/^doi:/i, "").toLowerCase();
        }
        return null;
      },
      args: [DOI_META_NAMES],
    });
    tabDoi.set(tabId, result ?? null);
    return result ?? null;
  } catch {
    tabDoi.set(tabId, null);
    return null;
  }
}

// ── BibTeX-on-page detection ──────────────────────────────────────────────────
// Pages such as GitHub READMEs render BibTeX citation blocks (a
// ```bibtex code fence renders into <pre><code class="language-bibtex">…</code></pre>
// inside .markdown-body). When detected, AnchorHub treats each entry's
// url / doi / eprint as if the user were on the cited resource itself —
// the URL is looked up in OneNote and the doi/eprint/url in JabRef, so
// repos that publicise a paper surface their canonical OneNote page and
// JabRef library entry from the repo page.
async function detectBibtex(tabId) {
  if (!settings.onenote && !settings.jabref) { tabBibtex.set(tabId, null); return null; }
  try {
    const [{ result }] = await browser.scripting.executeScript({
      target: { tabId },
      func: () => {
        // BibTeX blocks live in <pre> or <pre><code>. We accept anything
        // whose text looks like an @entry{key, …} block; some pages omit
        // the language class.
        const seen = new Set();  // dedupe identical blocks
        const sources = [
          ...document.querySelectorAll("pre code"),
          ...document.querySelectorAll("pre"),
        ];
        const entries = [];
        // Top-level @<type>{<key>, … } — body is captured up to the
        // matching brace. The brace balancer below trims it cleanly.
        const entryRe = /@(\w+)\s*\{\s*([^\s,]+)\s*,([\s\S]*)/g;
        for (const el of sources) {
          const text = (el.textContent ?? "").trim();
          if (!text || seen.has(text)) continue;
          if (!/^\s*@\w+\s*\{/.test(text)) continue;
          seen.add(text);
          entryRe.lastIndex = 0;
          let m;
          while ((m = entryRe.exec(text))) {
            const [, type, citationKey, rest] = m;
            // Brace-balance to find the entry close.
            let depth = 1, end = -1;
            for (let i = 0; i < rest.length; i++) {
              const c = rest[i];
              if (c === "{") depth++;
              else if (c === "}") { depth--; if (depth === 0) { end = i; break; } }
            }
            if (end < 0) continue;
            const body    = rest.slice(0, end);
            // Full source of this entry as it appears on the page,
            // captured before any field parsing so the popup can show
            // the verbatim BibTeX (whitespace and all).
            const entryEnd = m.index + (m[0].length - rest.length) + end + 1;
            const raw      = text.slice(m.index, entryEnd);
            const entry    = { entryType: type, citationKey, _raw: raw };
            // Simple field parser: key = {value} or key = "value".
            // Values can't contain unbalanced braces in well-formed BibTeX.
            const fieldRe = /(\w+)\s*=\s*(\{([^{}]*)\}|"([^"]*)")/g;
            let fm;
            while ((fm = fieldRe.exec(body))) {
              const key = fm[1].toLowerCase();
              const val = (fm[3] ?? fm[4] ?? "").trim();
              entry[key] = val;
            }
            entries.push(entry);
            // Skip past the consumed body so the outer regex resumes after it.
            entryRe.lastIndex = entryEnd;
          }
        }
        return entries.length > 0 ? entries : null;
      },
    });
    tabBibtex.set(tabId, result);
    return result;
  } catch {
    tabBibtex.set(tabId, null);
    return null;
  }
}

// Run OneNote lookups for each BibTeX entry's url field. Each match keeps
// `searchTerm = bibtex url` so the popup can show what produced the match
// and a click into OneNote opens find-on-page pre-filled with that URL.
async function runBibtexOneNoteLookup(tabId, entries) {
  if (!settings.onenote || !entries?.length) { tabBibtexMatches.set(tabId, []); return []; }
  const out = [];
  for (const entry of entries) {
    if (!entry.url) continue;
    try {
      const resp    = await sendRequest({ requestId: uuid(), action: "lookup", url: entry.url });
      const matches = resp.matches ?? [];
      for (const m of matches) {
        out.push({ ...m, bibtexCitationKey: entry.citationKey, bibtexUrl: entry.url });
      }
    } catch { /* hub offline — leave entry without matches */ }
  }
  tabBibtexMatches.set(tabId, out);
  return out;
}

// Run a single JabRef query carrying every doi / eprint / url we extracted
// from the page's BibTeX entries. JabRef returns one match list which we
// store verbatim; the popup shows them under the BibTeX section.
async function runBibtexJabRefLookup(tabId, entries) {
  if (!settings.jabref || !entries?.length) { tabBibtexJabRef.set(tabId, { matches: [] }); return; }
  const dois = [], eprints = [], urls = [];
  for (const entry of entries) {
    if (entry.doi)    dois.push(entry.doi);
    if (entry.eprint) eprints.push(entry.eprint);
    if (entry.url)    urls.push(entry.url);
  }
  if (!dois.length && !eprints.length && !urls.length) {
    tabBibtexJabRef.set(tabId, { matches: [] });
    return;
  }
  const matches = await queryJabRef({ dois, urls, eprints });
  tabBibtexJabRef.set(tabId, { matches });
}

// ── JabRef integration ────────────────────────────────────────────────────────

async function jabrefIsOnline() {
  if (!settings.jabref) return false;
  try {
    const r = await fetch(`${JABREF_BASE}/`, { method: "GET", signal: AbortSignal.timeout(1000) });
    return r.ok || r.status < 500;
  } catch {
    return false;
  }
}

// Escape a value for a Search.g4 STRING_LITERAL.
function escapeJabRefLiteral(s) {
  return s.replace(/\\/g, "\\\\").replace(/"/g, '\\"');
}

// POST a list of Search.g4 expressions to /libraries:query. Returns one
// match list per input expression, in the same order. Normalizes the API
// field `entryId` -> `citationKey` to match the jabrefCache shape.
async function postJabRefQueries(expressions) {
  if (!expressions.length) return expressions.map(() => []);
  try {
    const r = await fetch(`${JABREF_BASE}/libraries:query`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ queries: expressions }),
      signal: AbortSignal.timeout(5000),
    });
    if (!r.ok) return expressions.map(() => []);
    const data = await r.json();
    const results = data.results ?? [];
    return expressions.map((_, i) => (results[i]?.matches ?? []).map(m => ({
      libraryId:   m.libraryId,
      citationKey: m.entryId ?? m.citationKey ?? "",
    })));
  } catch {
    return expressions.map(() => []);
  }
}

// Look up doi / url / eprint values from page BibTeX. Each value becomes its
// own `=~i` (contains) query — contains matches a DOI whether stored bare or
// `https://doi.org/`-prefixed. Returns a flat, de-duplicated match list.
async function queryJabRef({ dois = [], urls = [], eprints = [] } = {}) {
  const expressions = [
    ...dois.map(d    => `doi =~i "${escapeJabRefLiteral(d)}"`),
    ...urls.map(u    => `url =~i "${escapeJabRefLiteral(u)}"`),
    ...eprints.map(e => `eprint =~i "${escapeJabRefLiteral(e)}"`),
  ];
  if (!expressions.length) return [];
  const lists = await postJabRefQueries(expressions);
  const seen = new Set();
  const merged = [];
  for (const m of lists.flat()) {
    const key = `${m.libraryId}|${m.citationKey}`;
    if (seen.has(key)) continue;
    seen.add(key);
    merged.push(m);
  }
  return merged;
}

async function runJabRefLookup(tabId, doi, { fromCache = false } = {}) {
  if (!settings.jabref) return;
  const cached = await browser.storage.local.get("jabrefCache").then(s => (s.jabrefCache ?? {})[doi]);

  if (fromCache && cached?.length) {
    tabJabRef.set(tabId, { matches: cached, confirmed: false });
    updateBadge(tabId);
    // Deferred confirm: if tab is still active after 5 s, run live query
    clearTimeout(jabrefConfirmTimers.get(tabId));
    jabrefConfirmTimers.set(tabId, setTimeout(async () => {
      jabrefConfirmTimers.delete(tabId);
      const activeTabs = await browser.tabs.query({ active: true, currentWindow: true });
      if (!activeTabs.some(t => t.id === tabId)) return;
      await runJabRefLookup(tabId, doi, { fromCache: false });
    }, JABREF_CONFIRM_DELAY_MS));
    return;
  }

  const online = await jabrefIsOnline();
  tabJabRefOnline.set(tabId, online);
  if (!online) {
    if (cached?.length) tabJabRef.set(tabId, { matches: cached, confirmed: false });
    updateBadge(tabId);
    return;
  }

  const matches = await queryJabRef({ dois: [doi] });
  tabJabRef.set(tabId, { matches, confirmed: true });
  updateBadge(tabId);

  // Persist to jabrefCache
  const store = await browser.storage.local.get("jabrefCache").then(s => s.jabrefCache ?? {});
  store[doi] = matches;
  await browser.storage.local.set({ jabrefCache: store });
}

async function runJabRefUrlLookup(tabId, url) {
  if (!settings.jabref) return;
  const store = await browser.storage.local.get("jabrefUrlCache").then(s => s.jabrefUrlCache ?? {});
  const cached = store[url];

  if (cached !== undefined) {
    // Show cached (may be empty array = confirmed not found last time)
    tabJabRef.set(tabId, { matches: cached, confirmed: false });
    updateBadge(tabId);
    if (cached.length) {
      // Deferred confirm only when there are matches worth re-checking
      clearTimeout(jabrefConfirmTimers.get(tabId));
      jabrefConfirmTimers.set(tabId, setTimeout(async () => {
        jabrefConfirmTimers.delete(tabId);
        const activeTabs = await browser.tabs.query({ active: true, currentWindow: true });
        if (!activeTabs.some(t => t.id === tabId)) return;
        await runJabRefUrlLookup(tabId, url);
      }, JABREF_CONFIRM_DELAY_MS));
    } else {
      // Not found in cache: query live immediately so the add form appears
      const online = await jabrefIsOnline();
      tabJabRefOnline.set(tabId, online);
      if (online) {
        const matches = await queryJabRef({ urls: [url] });
        tabJabRef.set(tabId, { matches, confirmed: true });
        store[url] = matches;
        await browser.storage.local.set({ jabrefUrlCache: store });
      }
      updateBadge(tabId);
    }
    return;
  }

  const online = await jabrefIsOnline();
  tabJabRefOnline.set(tabId, online);
  if (!online) { updateBadge(tabId); return; }

  const matches = await queryJabRef({ urls: [url] });
  tabJabRef.set(tabId, { matches, confirmed: true });
  store[url] = matches;
  await browser.storage.local.set({ jabrefUrlCache: store });
  updateBadge(tabId);
}

// ── Tab monitoring ────────────────────────────────────────────────────────────

browser.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (changeInfo.status !== "complete" || !tab.url) return;
  clearTimeout(debounceTimers.get(tabId));
  clearTimeout(jabrefConfirmTimers.get(tabId));
  jabrefConfirmTimers.delete(tabId);
  // Clear stale result and show "searching…" immediately.
  tabMatches.delete(tabId);
  tabDoi.delete(tabId);
  tabJabRef.delete(tabId);
  tabJabRefOnline.delete(tabId);
  tabOneNoteTitle.delete(tabId);
  tabBibtex.delete(tabId);
  tabBibtexMatches.delete(tabId);
  tabBibtexJabRef.delete(tabId);
  tabContextSwitcher.delete(tabId);
  tabCsPromises.delete(tabId);
  if (paused) {
    setPausedIcon(tabId);
    return;
  }
  if (settings.onenote) {
    setSearchingIcon(tabId);
    browser.action.setTitle({ tabId, title: "AnchorHub — searching…" });
  } else {
    setDisabledIcon(tabId);
  }
  browser.action.setBadgeText({ tabId, text: "" });
  // Track searching state so getMatches can wait.
  setLookupPromise(tabId, new Promise(resolve => {
    debounceTimers.set(tabId, setTimeout(async () => {
      const onenotePromise = settings.onenote
        ? lookupTab(tabId, tab.url).catch(() => [])
        : Promise.resolve([]);
      const doiPromise = settings.jabref
        ? detectDoi(tabId, tab.url)
        : Promise.resolve(null);
      const bibtexPromise = detectBibtex(tabId);
      const csPromise = settings.contextswitcher
        ? runContextSwitcherLookup(tabId, tab.url).catch(() => null)
        : Promise.resolve(null);
      const [matches, doi, bibtex] = await Promise.all([
        onenotePromise, doiPromise, bibtexPromise,
      ]);
      const jabrefPromise = !settings.jabref
        ? Promise.resolve()
        : doi
          ? browser.storage.local.get("jabrefCache").then(s => {
              const hasCached = !!(s.jabrefCache ?? {})[doi]?.length;
              return runJabRefLookup(tabId, doi, { fromCache: hasCached });
            })
          : runJabRefUrlLookup(tabId, tab.url);
      const bibtexLookups = bibtex?.length
        ? Promise.all([
            runBibtexOneNoteLookup(tabId, bibtex),
            runBibtexJabRefLookup(tabId, bibtex),
          ])
        : Promise.resolve();
      // Wait for JabRef + BibTeX (max 2 s) so the popup shows both
      // sections on first open without an extra refresh.
      await Promise.race([
        Promise.all([jabrefPromise, bibtexLookups, csPromise]),
        new Promise(r => setTimeout(r, 2000)),
      ]);
      resolve(matches);
    }, DEBOUNCE_MS));
  }));
});

browser.tabs.onActivated.addListener(({ tabId }) => {
  browser.tabs.get(tabId).then(async tab => {
    if (!tab.url) return;
    // Hydration may still be in flight right after a worker restart; without
    // this the cached result is missed and the icon flashes "searching".
    await cachesReady;
    if (paused) {
      setPausedIcon(tabId);
      return;
    }
    // ContextSwitcher is independent of the OneNote toolbar icon, so refresh
    // it even when OneNote is disabled — it only surfaces in the popup.
    if (settings.contextswitcher) runContextSwitcherLookup(tabId, tab.url);
    if (!settings.onenote) {
      setDisabledIcon(tabId);
      updateBadge(tabId);
      return;
    }
    const cached = getCached(tab.url);
    if (cached) {
      tabMatches.set(tabId, cached.matches);
      tabTotal.set(tabId, cached.total);
      updateIcon(tabId, cached.matches);
    } else {
      setSearchingIcon(tabId);
      browser.action.setTitle({ tabId, title: "AnchorHub — searching…" });
      setLookupPromise(tabId, lookupTab(tabId, tab.url));
    }
    updateBadge(tabId);
  });
});

browser.tabs.onRemoved.addListener(tabId => {
  tabMatches.delete(tabId);
  tabTotal.delete(tabId);
  tabLookupPromises.delete(tabId);
  debounceTimers.delete(tabId);
  tabDoi.delete(tabId);
  tabJabRef.delete(tabId);
  tabJabRefOnline.delete(tabId);
  tabOneNoteTitle.delete(tabId);
  tabBibtex.delete(tabId);
  tabBibtexMatches.delete(tabId);
  tabBibtexJabRef.delete(tabId);
  tabContextSwitcher.delete(tabId);
  tabCsPromises.delete(tabId);
  tabSelection.delete(tabId);
  clearTimeout(jabrefConfirmTimers.get(tabId));
  jabrefConfirmTimers.delete(tabId);
});

// Tab-less URL lookup. Returns { matches, total } from the cache, otherwise
// runs a fresh hub lookup and caches the result. Used by the in-page overlay
// (content script) where there is no associated browser tab.
async function lookupUrl(url, { force = false } = {}) {
  if (!settings.onenote && !settings.graph) return { matches: [], total: 0 };
  if (!force) {
    await cachesReady;
    const cached = getCached(url);
    if (cached) return { matches: cached.matches, total: cached.total };
  }

  // Run the local COM lookup and the Microsoft Graph lookup in parallel
  // so cloud-only matches surface alongside local-cached ones without
  // waiting for the slower of the two.
  const [com, graph] = await Promise.all([
    settings.onenote ? lookupUrlCom(url)   : { matches: [], total: 0 },
    settings.graph   ? lookupUrlGraph(url) : { matches: [], total: 0 },
  ]);
  const matches = [...com.matches, ...graph.matches];
  const total   = com.total + graph.total;
  setCached(url, { matches, total, ts: Date.now() });
  return { matches, total };
}

async function lookupUrlCom(url) {
  try {
    const resp    = await sendRequest({ requestId: uuid(), action: "lookup", url });
    const matches = (resp.matches ?? []).map(m => ({ ...m, source: m.source || "com" }));
    return { matches, total: resp.total ?? matches.length };
  } catch {
    return { matches: [], total: 0 };
  }
}

async function lookupUrlGraph(url) {
  try {
    const status = await AnchorGraphAuth.status();
    if (!status.signedIn) return { matches: [], total: 0 };
    const cfg = await AnchorGraphAuth._loadConfig();
    const resp = await sendRequest({
      requestId: uuid(),
      action:    "graphLookup",
      clientId:  cfg.clientId,
      tenant:    cfg.tenant,
      query:     url,
    });
    const matches = (resp.matches ?? []).map(m => ({ ...m, source: "graph" }));
    return { matches, total: matches.length };
  } catch (err) {
    console.warn("[ONL] Graph lookup failed:", err?.message || err);
    return { matches: [], total: 0 };
  }
}

// Classifies a result list to a single status for the in-page marker.
//
// dedupKey collapses sibling markers on the same page that resolve to the
// same OneNote search-context. Multiple links on a page often fall to the
// same host-only or helper fallback (e.g. all subpages of a personal site
// fall to host-only on the same host) and produce identical teal markers
// — visually noisy. Exact matches keep distinct keys (each URL is its own
// item) and "none" results have no key (each grey marker stays put).
function summariseStatus({ matches, total }, jabrefCount = 0) {
  const exact    = matches.some(m => m.matchType === "exact");
  const hostLike = matches.some(m => m.matchType === "host-only" || m.matchType === "related");
  let matchType = exact ? "exact" : hostLike ? "host-only" : "none";
  // OneNote found nothing but JabRef has the entry — still a positive hit.
  if (matchType === "none" && jabrefCount > 0) matchType = "jabref";
  let dedupKey = null;
  if (matchType === "host-only" && matches.length > 0) {
    dedupKey = `${matches[0].matchType}|${matches[0].searchTerm}`;
  }
  return { matchType, total, dedupKey, jabrefCount };
}

// Map a marker URL to the Search.g4 expression that finds it in JabRef.
// A doi.org link matches the `doi` field; an arxiv link the `eprint`
// field; a publisher link that embeds a DOI in its path (Springer's
// /book/10.1007/…, /article/…, /chapter/… etc.) matches `doi` OR the
// raw `url`; anything else just the `url` field.
function jabrefExprForUrl(url) {
  const urlClause = `url =~i "${escapeJabRefLiteral(url)}"`;
  try {
    const u = new URL(url);
    if (/(^|\.)doi\.org$/i.test(u.hostname)) {
      const doi = decodeURIComponent(u.pathname.replace(/^\/+/, ""));
      return `doi =~i "${escapeJabRefLiteral(doi)}"`;
    }
    if (/(^|\.)arxiv\.org$/i.test(u.hostname)) {
      const m = /\/(?:abs|pdf|html)\/(\d{4}\.\d{4,5})/i.exec(u.pathname);
      if (m) return `eprint =~i "${escapeJabRefLiteral(m[1])}" OR ${urlClause}`;
    }
    const doiInPath = /\b(10\.\d{4,}\/[^\s?#]+)/i.exec(decodeURIComponent(u.pathname));
    if (doiInPath) {
      return `doi =~i "${escapeJabRefLiteral(doiInPath[1])}" OR ${urlClause}`;
    }
  } catch { /* unparseable URL — fall through to a url-field match */ }
  return urlClause;
}

// One batched JabRef query for a list of marker URLs; returns a match
// count per URL, in the same order.
async function jabrefCountsForUrls(urls) {
  if (!settings.jabref || !urls.length) return urls.map(() => 0);
  const lists = await postJabRefQueries(urls.map(jabrefExprForUrl));
  return lists.map(l => l.length);
}

async function bulkLookup(urls, { concurrency = 4 } = {}) {
  const results = {};
  const jabrefCounts = await jabrefCountsForUrls(urls);
  let i = 0;
  async function worker() {
    while (i < urls.length) {
      const idx = i++;
      const u   = urls[idx];
      results[u] = summariseStatus(await lookupUrl(u), jabrefCounts[idx]);
    }
  }
  await Promise.all(Array.from(
    { length: Math.min(concurrency, urls.length) }, worker
  ));
  return results;
}

// Free-text OneNote search by name (used by the in-page person-name scanner).
// ── ContextSwitcher lookup ──────────────────────────────────────────────────
// Asks the daemon whether this URL belongs to a ContextSwitcher task (a URL in
// some task's browser.urls). Returns { found:true, task:{taskId,title,status,
// matchedUrl} } or { found:false }, or null when the adapter is off / the
// daemon is unreachable. Cached per URL for CACHE_TTL_MS.
async function contextSwitcherLookup(url, { force = false } = {}) {
  if (!settings.contextswitcher || !url) return null;
  if (!force) {
    const e = csCache.get(url);
    if (e && Date.now() - e.ts <= CACHE_TTL_MS) return e.result;
  }
  try {
    const resp = await sendRequest({ requestId: uuid(), action: "contextSwitcherLookup", url });
    const result = resp?.ok
      ? (resp.found ? { found: true, task: resp.task } : { found: false })
      : null;
    if (result) { csCache.set(url, { result, ts: Date.now() }); capCache(csCache); }
    return result;
  } catch {
    return null;
  }
}

// Runs a ContextSwitcher lookup for a tab and records the result + the in-flight
// promise so getMatches can await it.
function runContextSwitcherLookup(tabId, url, { force = false } = {}) {
  const p = contextSwitcherLookup(url, { force })
    .then(result => { tabContextSwitcher.set(tabId, result); return result; })
    .catch(() => { tabContextSwitcher.set(tabId, null); return null; });
  tabCsPromises.set(tabId, p);
  return p;
}

// Result shape mirrors lookupUrl so the marker dispatcher reuses the same path.
// `nameCache` itself is declared next to `urlCache` because the session-cache
// helpers up there mirror both.

function getCachedName(name) {
  const e = nameCache.get(name);
  if (!e) return null;
  if (Date.now() - e.ts > CACHE_TTL_MS) { dropCachedName(name); return null; }
  return e;
}

async function lookupName(name, { force = false } = {}) {
  if (!settings.onenote) return { matches: [], total: 0 };
  if (!force) {
    await cachesReady;
    const cached = getCachedName(name);
    if (cached) return { matches: cached.matches, total: cached.total };
  }
  try {
    const resp    = await sendRequest({ requestId: uuid(), action: "searchText", term: name });
    const matches = resp.matches ?? [];
    const total   = resp.total   ?? matches.length;
    setCachedName(name, { matches, total, ts: Date.now() });
    return { matches, total };
  } catch {
    return { matches: [], total: 0 };
  }
}

async function bulkLookupNames(names, { concurrency = 4 } = {}) {
  const results = {};
  let i = 0;
  async function worker() {
    while (i < names.length) {
      const n = names[i++];
      const r = await lookupName(n);
      // For names: only "exact match found in some page" (matchType=related from
      // searchText, since URL-identity doesn't apply). Anything with total>0 is a
      // hit; we treat it as a green marker so the user sees "yes I have notes".
      results[n] = { matchType: r.total > 0 ? "exact" : "none", total: r.total };
    }
  }
  await Promise.all(Array.from(
    { length: Math.min(concurrency, names.length) }, worker
  ));
  return results;
}

async function lookupTab(tabId, url, { force = false } = {}) {
  // Skip non-http(s) URLs (about:..., chrome://..., moz-extension://...,
  // file://, etc.). These are extension/browser pages with no possible
  // OneNote match; sending them to ankd just produces "Unparseable URL"
  // noise in the console.
  if (!url || !/^https?:\/\//i.test(url)) {
    tabMatches.set(tabId, []);
    tabTotal.set(tabId, 0);
    updateIcon(tabId, []);
    return [];
  }
  if (!settings.onenote) {
    tabMatches.set(tabId, []);
    tabTotal.set(tabId, 0);
    setDisabledIcon(tabId);
    return [];
  }
  if (!force) {
    await cachesReady;
    const cached = getCached(url);
    if (cached) {
      tabMatches.set(tabId, cached.matches);
      tabTotal.set(tabId, cached.total);
      updateIcon(tabId, cached.matches);
      return cached.matches;
    }
  }

  console.log("[ONL] lookup tab=", tabId, "url=", url, force ? "(forced)" : "");
  try {
    const resp    = await sendRequest({ requestId: uuid(), action: "lookup", url });
    let matches   = resp.matches ?? [];
    let total     = resp.total   ?? matches.length;

    // dblp person page: if URL not found, walk the ORCID and then the name.
    if (matches.length === 0 && DBLP_PERSON_RE.test(url)) {
      const augmented = await dblpFallback(tabId, url);
      if (augmented.length > 0) { matches = augmented; total = augmented.length; }
    }

    setCached(url, { matches, total, ts: Date.now() });
    tabMatches.set(tabId, matches);
    tabTotal.set(tabId, total);
    updateIcon(tabId, matches);
    return matches;
  } catch (e) {
    console.error("[ONL] lookup failed:", e.message);
    tabMatches.set(tabId, []);
    tabTotal.set(tabId, 0);
    updateIcon(tabId, []);
    return [];
  }
}

// Runs the complete per-tab lookup set — OneNote, DOI/JabRef, BibTeX and
// ContextSwitcher — and resolves with the OneNote matches. Shared by the
// explicit Refresh path and by getMatches when a restarted service worker
// holds no state for the tab. `force` bypasses every cache (Refresh); without
// it warm cache entries are reused, so a cold worker repopulates itself
// without re-querying the hub for URLs it already knows.
async function runTabLookups(tab, { force = false } = {}) {
  const doi    = tabDoi.get(tab.id) ?? await detectDoi(tab.id, tab.url);
  const bibtex = await detectBibtex(tab.id);
  const [matches, , , , contextswitcher] = await Promise.all([
    lookupTab(tab.id, tab.url, { force }),
    doi
      ? runJabRefLookup(tab.id, doi, { fromCache: !force })
      : runJabRefUrlLookup(tab.id, tab.url),
    bibtex?.length ? runBibtexOneNoteLookup(tab.id, bibtex) : Promise.resolve(),
    bibtex?.length ? runBibtexJabRefLookup(tab.id, bibtex)  : Promise.resolve(),
    runContextSwitcherLookup(tab.id, tab.url, { force }),
  ]);
  return { matches, doi, contextswitcher };
}

// Two-step dblp fallback: ORCID URL lookup first, then person-name text search.
async function dblpFallback(tabId, url) {
  const { orcid, name } = await scrapeDblpPerson(tabId);

  if (orcid) {
    try {
      const orcidUrl = `https://orcid.org/${orcid}`;
      const resp     = await sendRequest({ requestId: uuid(), action: "lookup", url: orcidUrl });
      const ms       = resp.matches ?? [];
      if (ms.length > 0) {
        console.log("[ONL] dblp fallback hit via ORCID", orcid, "for", url);
        return ms.map(m => ({ ...m, matchType: "related", searchTerm: orcidUrl }));
      }
    } catch (e) {
      console.warn("[ONL] dblp ORCID lookup failed:", e.message);
    }
  }

  if (name) {
    try {
      const resp = await sendRequest({ requestId: uuid(), action: "searchText", term: name });
      const ms   = resp.matches ?? [];
      if (ms.length > 0) {
        console.log("[ONL] dblp fallback hit via name", name, "for", url);
        return ms;
      }
    } catch (e) {
      console.warn("[ONL] dblp name search failed:", e.message);
    }
  }

  return [];
}

const tabOneNoteTitle = new Map(); // tabId → OneNote-derived tooltip text

// ── Icon overlay (digit baked into bottom-right) ──────────────────────────────
// WebExtensions has no API to position the action badge — Firefox renders it
// top-right. To put the count on the bottom we paint it onto the icon image
// itself with OffscreenCanvas and feed the result to setIcon.imageData.
const baseBitmapCache   = new Map();  // `${name}-${size}` → ImageBitmap
const renderedIconCache = new Map();  // `${name}:${label}:${size}` → ImageData

async function getBaseBitmap(name, size) {
  const key = `${name}-${size}`;
  let bmp = baseBitmapCache.get(key);
  if (bmp) return bmp;
  const resp = await fetch(browser.runtime.getURL(`icons/${name}-${size}.png`));
  bmp = await createImageBitmap(await resp.blob());
  baseBitmapCache.set(key, bmp);
  return bmp;
}

function drawCountAtBottom(ctx, label, size) {
  const fontPx = Math.max(7, Math.floor(size * (label.length === 1 ? 0.7 : 0.5)));
  ctx.font         = `bold ${fontPx}px sans-serif`;
  ctx.textBaseline = "bottom";
  ctx.textAlign    = "right";
  ctx.lineWidth    = Math.max(2, Math.floor(size * 0.18));
  ctx.strokeStyle  = "#ffffff";
  ctx.fillStyle    = "#000000";
  ctx.strokeText(label, size, size);
  ctx.fillText(label, size, size);
}

async function renderBadgedIcon(name, label, size) {
  const key = `${name}:${label}:${size}`;
  let img = renderedIconCache.get(key);
  if (img) return img;
  const bmp = await getBaseBitmap(name, size);
  const c   = new OffscreenCanvas(size, size);
  const ctx = c.getContext("2d");
  ctx.drawImage(bmp, 0, 0, size, size);
  if (label) drawCountAtBottom(ctx, label, size);
  img = ctx.getImageData(0, 0, size, size);
  renderedIconCache.set(key, img);
  return img;
}

async function setIconWithCount(tabId, name, label) {
  try {
    const [i16, i32, i48] = await Promise.all([
      renderBadgedIcon(name, label, 16),
      renderBadgedIcon(name, label, 32),
      renderBadgedIcon(name, label, 48),
    ]);
    browser.action.setIcon({ tabId, imageData: { 16: i16, 32: i32, 48: i48 } }).catch(() => {});
  } catch {
    browser.action.setIcon({ tabId, path: {
      16: `icons/${name}-16.png`,
      32: `icons/${name}-32.png`,
      48: `icons/${name}-48.png`,
    }}).catch(() => {});
  }
}

// 0 → no overlay, 1-9 → literal digit, ≥10 → "10+".
function formatCount(n) {
  if (n >= 10) return "10+";
  if (n >= 1)  return String(n);
  return "";
}

function updateIcon(tabId, matches) {
  // [impl->req~ank.transport.disconnected-state~1]
  // While hub upstream is unreachable, leave the orange icon in
  // place. Per-tab updates resume automatically on the first ok:true
  // response (which clears hubDisconnected via onMessage).
  if (hubDisconnected) return;

  const exact    = matches.filter(m => m.matchType === "exact");
  const hostOnly = matches.filter(m => m.matchType === "host-only" || m.matchType === "related");
  const total    = tabTotal.get(tabId) ?? matches.length;
  const label    = formatCount(total);

  let name, title;
  if (exact.length > 0) {
    name  = "green";
    title = exact.length === 1
      ? `In OneNote: ${exact[0].title}`
      : `In OneNote (${exact.length} pages)`;
  } else if (hostOnly.length > 0) {
    name  = "host";
    title = hostOnly.length === 1
      ? `Same site in OneNote: ${hostOnly[0].title}`
      : `Same site in OneNote (${hostOnly.length} pages)`;
  } else {
    name  = "notfound";
    title = "AnchorHub — not found";
  }

  setIconWithCount(tabId, name, label);
  tabOneNoteTitle.set(tabId, title);
  browser.action.setTitle({ tabId, title });
  updateBadge(tabId);
}

function updateBadge(tabId) {
  if (paused) { setPausedIcon(tabId); return; }
  const jabref    = tabJabRef.get(tabId);
  const online    = tabJabRefOnline.get(tabId);
  const baseTitle = tabOneNoteTitle.get(tabId) ?? "AnchorHub";
  const total     = tabTotal.get(tabId) ?? (tabMatches.get(tabId)?.length ?? 0);

  // OneNote count is baked into the icon — badge slot is reserved for JabRef
  // indicators only. When OneNote has matches, suppress the badge so it does
  // not collide with the digit overlay.
  if (total > 0) {
    browser.action.setBadgeText({ tabId, text: "" });
    const jabrefSuffix = jabref?.matches?.length > 0 ? "\nIn JabRef"
                       : online === false           ? "\nJabRef offline"
                       : "";
    browser.action.setTitle({ tabId, title: baseTitle + jabrefSuffix });
  } else if (jabref?.matches?.length > 0) {
    browser.action.setBadgeText({ tabId, text: "J" });
    browser.action.setBadgeBackgroundColor({ tabId, color: BADGE_COLOR_EXACT_MATCH });
    browser.action.setBadgeTextColor({ tabId, color: "#000000" });
    browser.action.setTitle({ tabId, title: baseTitle + "\nIn JabRef" });
  } else if (online === false) {
    browser.action.setBadgeText({ tabId, text: "!" });
    browser.action.setBadgeBackgroundColor({ tabId, color: BADGE_COLOR_JABREF_OFFLINE });
    browser.action.setBadgeTextColor({ tabId, color: "#ffffff" });
    browser.action.setTitle({ tabId, title: baseTitle + "\nJabRef offline" });
  } else {
    browser.action.setBadgeText({ tabId, text: "" });
    browser.action.setTitle({ tabId, title: baseTitle });
  }
}

// Bundles per-tab BibTeX state for the popup. Returns null when nothing
// was detected so the popup can skip rendering the section entirely.
function buildBibtexResponse(tabId) {
  const entries = tabBibtex.get(tabId);
  if (!entries?.length) return null;
  return {
    entries,
    onenoteMatches: tabBibtexMatches.get(tabId) ?? [],
    jabrefMatches:  tabBibtexJabRef.get(tabId)?.matches ?? [],
  };
}

function buildMiscBibtex({ title, url, year }) {
  const escape = s => s.replace(/[{}\\]/g, c => `\\${c}`);
  const today  = new Date().toISOString().slice(0, 10);
  return `@misc{,\n  title  = {${escape(title)}},\n  url    = {${escape(url)}},\n  year   = {${year}},\n  note   = {Accessed: ${today}}\n}\n`;
}

async function getJabRefLibraries() {
  if (!settings.jabref) return [];
  try {
    const r = await fetch(`${JABREF_BASE}/libraries`, { signal: AbortSignal.timeout(3000) });
    if (!r.ok) return [];
    return await r.json();  // string[] of library IDs
  } catch {
    return [];
  }
}

// ── Message bridge for popup ──────────────────────────────────────────────────

browser.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg.type === "getSettings") {
    sendResponse({ ok: true, settings: { ...settings }, paused });
    return false;
  }
  if (msg.type === "selectionChange") {
    // Fire-and-forget — the context menu may open before this finishes,
    // in which case the parent label says "looking up…" and the children
    // stay hidden. The next update lands as soon as the daemon replies.
    handleSelectionChange(_sender.tab?.id, msg.text).catch(err =>
      console.warn("[ONL] selectionChange handler failed:", err?.message || err));
    return false;
  }
  if (msg.type === "graphSignIn") {
    // The OAuth listener lives in the daemon (loopback HttpListener →
    // browser → token endpoint). Forward the config to ankd, persist
    // the returned tokens through the auth module, then ack.
    console.log("[ONL] graphSignIn handler entered. port=", !!port, "settings=", settings);
    (async () => {
      try {
        // The service worker may have just woken to deliver this very
        // message — finish initial settings load + connect before
        // touching the port. Otherwise the port may still be null.
        if (settingsReady) await settingsReady;
        console.log("[ONL] graphSignIn: settingsReady resolved. port=", !!port, "needsDaemon=", needsDaemon());
        if (!port && needsDaemon()) {
          connect();
          for (let i = 0; i < 25 && !port; i++) {
            await new Promise(r => setTimeout(r, 100));
          }
          console.log("[ONL] graphSignIn: post-retry port=", !!port);
        }
        if (!port) {
          throw new Error("Daemon (ankd) is not reachable. Confirm it is installed and the AnchorHub native-messaging host is registered.");
        }

        const cfg   = await AnchorGraphAuth._loadConfig();
        const reply = await sendRequest({
          requestId: uuid(),
          action:    "graphSignIn",
          clientId:  cfg.clientId,
          tenant:    cfg.tenant,
        });
        if (!reply?.ok || !reply.tokens) {
          throw new Error(reply?.error?.message || "Daemon did not return tokens.");
        }
        await AnchorGraphAuth._persistDaemonTokens(reply.tokens);
        sendResponse({ ok: true });
      } catch (err) {
        console.warn("[ONL] graphSignIn failed:", err);
        sendResponse({ ok: false, error: err.message || String(err) });
      }
    })();
    return true;
  }
  if (msg.type === "getMatches") {
    // Override mode (in-page overlay): caller supplies explicit url/title or
    // name; skip tab lookup, skip DOI/JabRef, just return OneNote matches.
    if (msg.name) {
      lookupName(msg.name).then(({ matches, total }) => {
        sendResponse({
          matches,
          total,
          doi:      null,
          jabref:   null,
          tabTitle: msg.title ?? msg.name,
          tabUrl:   "",
          tabName:  msg.name,
        });
      });
      return true;
    }
    if (msg.url) {
      Promise.all([lookupUrl(msg.url), contextSwitcherLookup(msg.url)]).then(([{ matches, total }, contextswitcher]) => {
        sendResponse({
          matches,
          total,
          doi:      null,
          jabref:   null,
          contextswitcher,
          tabTitle: msg.title ?? "",
          tabUrl:   msg.url,
        });
      });
      return true;
    }
    browser.tabs.query({ active: true, currentWindow: true }).then(async ([tab]) => {
      // A popup click can be what wakes the worker, so adapter settings and
      // the native port may not be up yet when this arrives.
      await settingsReady;
      await cachesReady;
      const tabId  = tab?.id;
      const pending = [tabLookupPromises.get(tabId), tabCsPromises.get(tabId)].filter(Boolean);
      const respond = () => {
        const matches = tabMatches.get(tabId) ?? [];
        sendResponse({
          matches,
          total:    tabTotal.get(tabId) ?? matches.length,
          doi:      tabDoi.get(tabId) ?? null,
          jabref:   tabJabRef.get(tabId) ?? null,
          bibtex:   buildBibtexResponse(tabId),
          contextswitcher: tabContextSwitcher.get(tabId) ?? null,
          tabTitle: tab?.title ?? "",
          tabUrl:   tab?.url   ?? "",
        });
      };
      if (pending.length) { Promise.all(pending).then(respond); return; }
      // [impl->req~ank.cache.popup-cold-start~1]
      // `tabMatches.has(tabId)` distinguishes "the lookup ran and found
      // nothing" (empty array) from "no lookup ever ran under this service
      // worker" (no entry at all). The latter happens on every popup open
      // after Chrome has suspended the MV3 worker — all per-tab Maps are
      // gone. Rebuilding here keeps the first click from reporting a false
      // "Not found in OneNote" / "JabRef offline" until the user hits
      // Refresh.
      if (tabMatches.has(tabId) || !tab?.url || paused) { respond(); return; }
      const lookup = runTabLookups(tab).then(({ matches }) => matches, () => []);
      setLookupPromise(tabId, lookup);
      lookup.then(respond);
    });
    return true;
  }
  if (msg.type === "refresh") {
    if (msg.name) {
      dropCachedName(msg.name);
      lookupName(msg.name, { force: true }).then(({ matches, total }) => {
        sendResponse({
          matches,
          total,
          doi:      null,
          jabref:   null,
          tabTitle: msg.title ?? msg.name,
          tabUrl:   "",
          tabName:  msg.name,
        });
      });
      return true;
    }
    if (msg.url) {
      // Force a fresh hub lookup for the supplied URL (no tab state to mutate).
      dropCached(msg.url);
      csCache.delete(msg.url);
      Promise.all([
        lookupUrl(msg.url, { force: true }),
        contextSwitcherLookup(msg.url, { force: true }),
      ]).then(([{ matches, total }, contextswitcher]) => {
        sendResponse({
          matches,
          total,
          doi:      null,
          jabref:   null,
          contextswitcher,
          tabTitle: msg.title ?? "",
          tabUrl:   msg.url,
        });
      });
      return true;
    }
    browser.tabs.query({ active: true, currentWindow: true }).then(async ([tab]) => {
      await settingsReady;
      if (!tab?.url) { sendResponse({ matches: [], doi: null, jabref: null, bibtex: null, tabTitle: "", tabUrl: "" }); return; }
      // Clear all caches for this URL so lookups go live
      dropCached(tab.url);
      csCache.delete(tab.url);
      tabJabRef.delete(tab.id);
      tabBibtex.delete(tab.id);
      tabBibtexMatches.delete(tab.id);
      tabBibtexJabRef.delete(tab.id);
      const { matches, doi, contextswitcher } = await runTabLookups(tab, { force: true });
      sendResponse({
        matches,
        total:    tabTotal.get(tab.id) ?? matches.length,
        doi,
        jabref:   tabJabRef.get(tab.id) ?? null,
        bibtex:   buildBibtexResponse(tab.id),
        contextswitcher,
        tabTitle: tab.title ?? "",
        tabUrl:   tab.url,
      });
    });
    return true;
  }
  if (msg.type === "bulkLookup") {
    bulkLookup(msg.urls ?? [])
      .then(results => sendResponse({ ok: true, results }))
      .catch(e => sendResponse({ ok: false, error: e.message }));
    return true;
  }
  if (msg.type === "bulkLookupNames") {
    bulkLookupNames(msg.names ?? [])
      .then(results => sendResponse({ ok: true, results }))
      .catch(e => sendResponse({ ok: false, error: e.message }));
    return true;
  }
  if (msg.type === "focus") {
    sendRequest({ requestId: uuid(), action: "focus", pageId: msg.pageId, query: msg.searchTerm ?? "" })
      .then(() => sendResponse({ ok: true }))
      .catch(e => sendResponse({ ok: false, error: e.message }));
    return true;
  }
  if (msg.type === "getJabRefLibraries") {
    getJabRefLibraries()
      .then(libraries => sendResponse({ ok: true, libraries }))
      .catch(e => sendResponse({ ok: false, error: e.message }));
    return true;
  }
  if (msg.type === "addToJabRef") {
    // Step 11: Add handler. Online mechanism is TBD (ADR 0004).
    // For now, always queue offline and flush when JabRef reconnects.
    const { doi, targetLibraryId } = msg;
    browser.storage.local.get("jabrefQueue").then(async s => {
      const queue = s.jabrefQueue ?? [];
      queue.push({ doi, targetLibraryId, ts: Date.now() });
      await browser.storage.local.set({ jabrefQueue: queue, jabrefLastLibrary: targetLibraryId });
      sendResponse({ ok: true, queued: true });
    });
    return true;
  }
  if (msg.type === "addMiscToJabRef") {
    const { title, url, year, targetLibraryId } = msg;
    const bibtex = buildMiscBibtex({ title, url, year });
    fetch(`${JABREF_BASE}/libraries/${encodeURIComponent(targetLibraryId)}/entries`, {
      method: "POST",
      headers: { "Content-Type": "application/x-bibtex" },
      body: bibtex,
      signal: AbortSignal.timeout(5000),
    })
      .then(async r => {
        if (r.ok) {
          browser.storage.local.set({ jabrefLastLibrary: targetLibraryId });
          sendResponse({ ok: true });
        } else {
          let details = "";
          try { details = await r.text(); } catch { /* ignore */ }
          sendResponse({ ok: false, error: `HTTP ${r.status}`, details });
        }
      })
      .catch(e => sendResponse({ ok: false, error: e.message }));
    return true;
  }
  if (msg.type === "openSearch") {
    if (msg.name) {
      // Hub's openSearch falls back to the literal query when TryNormalize
      // fails (which it does for plain text), so this works for names too.
      sendRequest({ requestId: uuid(), action: "openSearch", query: msg.name })
        .then(() => sendResponse({ ok: true }))
        .catch(e => sendResponse({ ok: false, error: e.message }));
      return true;
    }
    if (msg.url) {
      sendRequest({ requestId: uuid(), action: "openSearch", query: msg.url })
        .then(() => sendResponse({ ok: true }))
        .catch(e => sendResponse({ ok: false, error: e.message }));
      return true;
    }
    browser.tabs.query({ active: true, currentWindow: true }).then(([tab]) => {
      sendRequest({ requestId: uuid(), action: "openSearch", query: tab?.url ?? "" })
        .then(() => sendResponse({ ok: true }))
        .catch(e => sendResponse({ ok: false, error: e.message }));
    });
    return true;
  }
  if (msg.type === "listNotebooks") {
    sendRequest({ requestId: uuid(), action: "listNotebooks" })
      .then(resp => sendResponse({ ok: true, notebooks: resp.notebooks }))
      .catch(e => sendResponse({ ok: false, error: e.message }));
    return true;
  }
  if (msg.type === "addPage") {
    // Override mode: caller supplies url/title (in-page overlay). No active
    // tab to update; only the URL cache benefits from the optimistic insert.
    if (msg.url) {
      sendRequest({
        requestId: uuid(),
        action:    "addPage",
        sectionId: msg.sectionId,
        title:     msg.title ?? msg.url,
        url:       msg.url,
      })
      .then(resp => {
        const optimistic = [{
          pageId:       resp.pageId,
          title:        msg.title ?? msg.url,
          notebookName: msg.notebookName ?? "",
          sectionName:  msg.sectionName  ?? "",
          matchType:    "exact",
          searchTerm:   msg.url,
        }];
        setCached(msg.url, { matches: optimistic, total: 1, ts: Date.now() });
        sendResponse({ ok: true, pageId: resp.pageId });
      })
      .catch(e => sendResponse({ ok: false, error: e.message }));
      return true;
    }
    browser.tabs.query({ active: true, currentWindow: true }).then(([tab]) => {
      sendRequest({
        requestId: uuid(),
        action:    "addPage",
        sectionId: msg.sectionId,
        title:     tab?.title ?? tab?.url ?? "",
        url:       tab?.url   ?? "",
      })
      .then(resp => {
        if (tab?.url) {
          // Optimistic match: FindPages index can lag minutes behind a new page.
          // Store the result immediately so the icon turns green without waiting.
          const optimistic = [{
            pageId:       resp.pageId,
            title:        tab.title ?? tab.url ?? "",
            notebookName: msg.notebookName ?? "",
            sectionName:  msg.sectionName  ?? "",
            matchType:    "exact",
            searchTerm:   tab.url,
          }];
          tabMatches.set(tab.id, optimistic);
          tabTotal.set(tab.id, 1);
          setCached(tab.url, { matches: optimistic, total: 1, ts: Date.now() });
          updateIcon(tab.id, optimistic);
        }
        sendResponse({ ok: true, pageId: resp.pageId });
      })
      .catch(e => sendResponse({ ok: false, error: e.message }));
    });
    return true;
  }
});

// ── Append-bullet feature (Ctrl+Alt+O / context menu) ────────────────────────

// Firefox auto-wraps every extension's top-level context-menu items
// inside an extension-named parent when there are >1 of them, which
// double-nests AnchorHub's submenu ("AnchorHub" > "AnchorHub — N hits"
// > result). Use a single root item with everything as children so
// the visible label is the dynamic "AnchorHub — N hits for ‘…’".
const MENU_ID_ROOT            = "anchorhub-root";
const MENU_ID_APPEND          = "onl-append-bullet";
const MENU_RESULT_COUNT       = 8;
const MENU_RESULT_IDS         = Array.from({ length: MENU_RESULT_COUNT }, (_, i) => `anchorhub-lookup-result-${i}`);

// Selection state: latest text per tab + result data per menu-item id.
// Selection lookups are versioned by selectionLookupSeq so late-returning
// results from an outdated selection don't overwrite menu items.
const tabSelection      = new Map();   // tabId → { text }
const menuMatchData     = new Map();   // menuItemId → match payload
let   selectionLookupSeq = 0;

// `contextMenus` is the cross-browser name (Firefox aliases `menus`).
// Chrome service workers re-run this file on every wake — calling
// create() at top level would throw "duplicate id". Wrap in
// runtime.onInstalled (which fires once per install/update) and also
// re-create on browser startup; either firing is enough, and the
// duplicate-id error from the second is harmless.
const MENU_ID_PAUSE_TOGGLE = "anchorhub-pause-toggle";

function ensureContextMenu() {
  try {
    browser.contextMenus.create({
      id:       MENU_ID_PAUSE_TOGGLE,
      title:    paused ? "Resume AnchorHub" : "Pause AnchorHub",
      contexts: ["action"],
    });
  } catch {}

  try {
    browser.contextMenus.create({
      id:       MENU_ID_ROOT,
      title:    "AnchorHub — select text to look up",
      contexts: ["selection"],
    });
  } catch { /* already created on a prior wake */ }

  try {
    browser.contextMenus.create({
      id:       MENU_ID_APPEND,
      parentId: MENU_ID_ROOT,
      title:    "Send to OneNote with URL",
      contexts: ["selection"],
    });
  } catch {}

  // Separator between the append action and the dynamic result rows.
  try {
    browser.contextMenus.create({
      id:       "anchorhub-results-separator",
      parentId: MENU_ID_ROOT,
      type:     "separator",
      contexts: ["selection"],
    });
  } catch {}

  for (const id of MENU_RESULT_IDS) {
    try {
      browser.contextMenus.create({
        id,
        parentId: MENU_ID_ROOT,
        title:    "(no results yet)",
        contexts: ["selection"],
        visible:  false,
      });
    } catch {}
  }
}
browser.runtime.onInstalled.addListener(ensureContextMenu);
browser.runtime.onStartup?.addListener(ensureContextMenu);

browser.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === MENU_ID_PAUSE_TOGGLE) {
    paused = !paused;
    browser.storage.local.set({ anchorhubPaused: paused });
    browser.contextMenus.update(MENU_ID_PAUSE_TOGGLE, {
      title: paused ? "Resume AnchorHub" : "Pause AnchorHub",
    }).catch(() => {});
    if (paused) {
      browser.tabs.query({}).then(tabs => {
        for (const t of tabs) if (t.id != null) setPausedIcon(t.id);
      });
    } else {
      refreshActiveTabAfterSettingsChange();
    }
    return;
  }
  if (info.menuItemId === MENU_ID_APPEND) {
    const text = (info.selectionText ?? "").trim();
    const url  = tab?.url ?? "";
    if (text && url) appendBullet(text, url);
    return;
  }
  if (typeof info.menuItemId === "string" && info.menuItemId.startsWith("anchorhub-lookup-result-")) {
    const m = menuMatchData.get(info.menuItemId);
    if (!m) return;
    if (m.source === "graph") {
      const target = m.clientUrl || m.webUrl;
      if (target) browser.tabs.create({ url: target });
    } else if (m.source === "jabref") {
      // Service workers cannot reach navigator.clipboard reliably; ask the
      // content script that originated the selection to copy the key.
      if (tab?.id && m.citationKey) {
        browser.tabs.sendMessage(tab.id, {
          type: "copyToClipboard",
          text: m.citationKey,
        }).catch(() => {});
      }
    } else {
      sendRequest({
        requestId: uuid(),
        action:    "focus",
        pageId:    m.pageId,
        query:     m.searchTerm ?? "",
      }).catch(() => {});
    }
  }
});

function truncateForMenu(s, max = 40) {
  return s.length > max ? s.slice(0, max - 1) + "…" : s;
}

function matchToMenuLabel(m) {
  if (m.source === "jabref") {
    const sub = m.libraryId ? ` — ${m.libraryId}` : "";
    return truncateForMenu(`📚 ${m.citationKey || "(no key)"}${sub}`, 80);
  }
  const icon = m.source === "graph" ? "☁ " : "";
  const sub  = m.notebookName
    ? ` — ${m.notebookName}${m.sectionName ? "/" + m.sectionName : ""}`
    : "";
  return truncateForMenu(`${icon}${m.title ?? "(untitled)"}${sub}`, 80);
}

async function handleSelectionChange(tabId, text) {
  if (!tabId) return;
  text = (text || "").trim();
  // Quick guards: too short or huge selections are noise.
  if (text.length < 2 || text.length > 80) {
    await resetLookupMenu("AnchorHub — select text to look up");
    return;
  }
  const prev = tabSelection.get(tabId);
  if (prev?.text === text) return;
  tabSelection.set(tabId, { text });

  const seq = ++selectionLookupSeq;
  await browser.contextMenus.update(MENU_ID_ROOT, {
    title: `AnchorHub — looking up "${truncateForMenu(text)}"…`,
  }).catch(() => {});
  await Promise.all(MENU_RESULT_IDS.map(id =>
    browser.contextMenus.update(id, { visible: false }).catch(() => {})
  ));

  const [com, graph, jabref] = await Promise.all([
    settings.onenote ? lookupSelectionCom(text)    : [],
    settings.graph   ? lookupSelectionGraph(text)  : [],
    settings.jabref  ? lookupSelectionJabref(text) : [],
  ]);
  // Drop stale results if a newer selection has been processed in the meantime.
  if (seq !== selectionLookupSeq) return;

  const merged = [...com, ...graph, ...jabref].slice(0, MENU_RESULT_COUNT);
  if (merged.length === 0) {
    await browser.contextMenus.update(MENU_ID_ROOT, {
      title: `AnchorHub — no matches for "${truncateForMenu(text)}"`,
    }).catch(() => {});
    return;
  }
  await browser.contextMenus.update(MENU_ID_ROOT, {
    title: `AnchorHub — ${merged.length} hit${merged.length === 1 ? "" : "s"} for "${truncateForMenu(text)}"`,
  }).catch(() => {});

  menuMatchData.clear();
  for (let i = 0; i < MENU_RESULT_COUNT; i++) {
    const id = MENU_RESULT_IDS[i];
    if (i < merged.length) {
      const m = merged[i];
      menuMatchData.set(id, m);
      await browser.contextMenus.update(id, {
        title:   matchToMenuLabel(m),
        visible: true,
      }).catch(() => {});
    } else {
      await browser.contextMenus.update(id, { visible: false }).catch(() => {});
    }
  }
}

async function resetLookupMenu(title) {
  await browser.contextMenus.update(MENU_ID_ROOT, { title }).catch(() => {});
  await Promise.all(MENU_RESULT_IDS.map(id =>
    browser.contextMenus.update(id, { visible: false }).catch(() => {})
  ));
  menuMatchData.clear();
}

async function lookupSelectionCom(text) {
  try {
    const resp = await sendRequest({ requestId: uuid(), action: "searchText", term: text });
    return (resp.matches ?? []).map(m => ({ ...m, source: "com" }));
  } catch { return []; }
}

async function lookupSelectionGraph(text) {
  try {
    const status = await AnchorGraphAuth.status();
    if (!status.signedIn) return [];
    const cfg = await AnchorGraphAuth._loadConfig();
    const resp = await sendRequest({
      requestId: uuid(),
      action:    "graphLookup",
      clientId:  cfg.clientId,
      tenant:    cfg.tenant,
      query:     text,
    });
    return (resp.matches ?? []).map(m => ({ ...m, source: "graph" }));
  } catch { return []; }
}

// Build a Search.g4 expression for a free-text selection. `=~i`
// (case-insensitive contains) across the fields the user most often
// recognises in a citation: author, title, keywords, abstract.
function buildJabRefFreeTextQuery(text) {
  const literal = escapeJabRefLiteral(text);
  const fields  = ["author", "title", "keywords", "abstract"];
  return fields.map(f => `${f} =~i "${literal}"`).join(" OR ");
}

// Build the Search.g4 expression for a context-menu selection. A DOI- or
// URL-shaped selection matches that one field; anything else is free text.
function buildJabRefSelectionQuery(trimmed) {
  if (/^10\.\d{4,}\/\S+$/.test(trimmed)) {
    return `doi =~i "${escapeJabRefLiteral(trimmed)}"`;
  }
  if (/^https?:\/\//i.test(trimmed)) {
    return `url =~i "${escapeJabRefLiteral(trimmed)}"`;
  }
  return buildJabRefFreeTextQuery(trimmed);
}

async function lookupSelectionJabref(text) {
  if (!settings.jabref) return [];
  const trimmed = text.trim();
  if (!trimmed) return [];

  const [matches] = await postJabRefQueries([buildJabRefSelectionQuery(trimmed)]);
  return (matches ?? []).map(m => ({
    source:      "jabref",
    citationKey: m.citationKey,
    libraryId:   m.libraryId ?? "",
  }));
}

browser.commands.onCommand.addListener(async (cmd) => {
  if (cmd !== "send-selection-to-onenote") return;
  const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id || !tab.url) return;
  let text = "";
  try {
    const [{ result }] = await browser.scripting.executeScript({
      target: { tabId: tab.id },
      func:   () => (window.getSelection()?.toString() ?? "").trim(),
    });
    text = result ?? "";
  } catch { /* page may forbid scripting (about:, addons, …) */ }
  if (text) appendBullet(text, tab.url);
});

function appendBullet(text, url) {
  if (!settings.onenote) {
    console.warn("[ONL] appendBullet ignored — OneNote adapter is disabled.");
    return;
  }
  sendRequest({ requestId: uuid(), action: "appendBullet", text, url })
    .catch(e => console.warn("[ONL] appendBullet failed:", e.message));
}

settingsReady = loadSettings().then(connect).then(() => {
  if (!paused) return;
  browser.tabs.query({}).then(tabs => {
    for (const t of tabs) if (t.id != null) setPausedIcon(t.id);
  });
});
