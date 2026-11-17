"use strict";

// Cross-browser shim — see background.js for rationale.
if (typeof browser === "undefined" && typeof chrome !== "undefined") {
  globalThis.browser = chrome;
}

// ── Context-menu lookup: forward the current text selection to the
// background so it can populate AnchorHub's right-click submenu with
// OneNote + Graph hits *before* the user actually opens the menu.
// Debounced to one round-trip per ~300 ms of typing/dragging.
(function setupContextMenuSelectionFeed() {
  let selectionTimer = null;
  const send = () => {
    let text = "";
    try { text = (window.getSelection()?.toString() ?? "").trim(); } catch { return; }
    browser.runtime.sendMessage({ type: "selectionChange", text }).catch(() => { /* SW idle / unreachable */ });
  };
  document.addEventListener("selectionchange", () => {
    clearTimeout(selectionTimer);
    selectionTimer = setTimeout(send, 300);
  });
  // Right-click can land before the debounce fires. Force a fresh send on
  // contextmenu (capture phase) so the background populates the menu before
  // Firefox actually opens it. Cheap — no fetch unless text changed.
  document.addEventListener("contextmenu", () => {
    clearTimeout(selectionTimer);
    send();
  }, true);
})();

// Right-click menu copy action for JabRef hits. The service worker cannot
// reach navigator.clipboard, so the background asks us to do it.
browser.runtime.onMessage.addListener((msg) => {
  if (msg?.type === "copyToClipboard" && typeof msg.text === "string") {
    navigator.clipboard.writeText(msg.text).catch(() => {});
  }
});

// Debug logging — opt-in. Enable in the page console with
//   localStorage.setItem("onl-debug", "1"); location.reload();
// then watch Ctrl+Shift+K. Disable with localStorage.removeItem("onl-debug").
// Used heavily inside the person-name scanner where a missing marker is
// otherwise invisible (no marker = no breadcrumb).
const ONL_DEBUG = (() => {
  try { return localStorage.getItem("onl-debug") === "1"; } catch { return false; }
})();
function dbg(...args) { if (ONL_DEBUG) console.log("[ONL]", ...args); }

// In-page status markers for sites where the user browses lists of items
// that may already be referenced in OneNote. Per-site scanners discover
// candidate URLs and inject a 16px marker next to each item; the marker
// lookups are batched via bulkLookup. Click opens the toolbar popup
// inside an iframe-backed shadow-DOM overlay so all popup logic is reused.
//
// Currently supported:
//   github.com/topics/<topic>, github.com/search?…   — anchor on /star form
//   github.com/<user> profile sidebar               — vcard-details links
//   alternativeto.net/{software,browse,list,…}      — anchor on /software/<slug>/about/ links
//   www.researchgate.net/lab/<slug>                 — anchor on /profile/<slug> links
//   www.linkedin.com (any post-bearing page)        — author bubble (/in/<slug>) +
//                                                     post bubble (built from
//                                                     data-urn permalink).
//   www.google.* /search                            — bubble next to each result title (h3)
//   www.mdpi.com /<journal>/<vol>/<issue>/<id>     — bubble next to each author name,
//                                                     aggregating the four hover-card
//                                                     profile-platform lookups (most
//                                                     fitting wins)
//   any host (content containers)                   — every external link inside
//                                                     <article>, .markdown-body,
//                                                     .comment-body, .s-prose,
//                                                     .md, [itemprop=articleBody],
//                                                     .author__urls (Jekyll
//                                                     academicpages sidebar);
//                                                     plus <main> / [role=main]
//                                                     on hosts without a
//                                                     dedicated scanner.
//   any host (heading-anchored)                     — plain-text person names
//                                                     under "Organizers" /
//                                                     "Committee" / etc.;
//                                                     marker only when OneNote
//                                                     has a hit, lookup is by
//                                                     name (no URL).

const SEEN_ATTR = "data-onl-injected";

const ICONS = {
  exact:       "icons/green-16.png",
  "host-only": "icons/host-16.png",
  jabref:      "icons/green-16.png",
  none:        "icons/notfound-16.png",
};

// ── Marker ───────────────────────────────────────────────────────────────────

function makeMarker(url, titleHint) {
  const span = document.createElement("span");
  span.className = "onl-marker";
  span.dataset.onlUrl = url;
  span.title = "AnchorHub — searching…";
  span.style.cssText = [
    "display: inline-block",
    "width: 16px",
    "height: 16px",
    "margin-right: 6px",
    "background-image: url(" + browser.runtime.getURL("icons/default-16.png") + ")",
    "background-size: contain",
    "background-repeat: no-repeat",
    "background-position: center",
    "cursor: pointer",
    "vertical-align: middle",
  ].join("; ");
  // Capture phase + stopImmediatePropagation: SPA frameworks (Angular on
  // ieeexplore.ieee.org, etc.) attach a capture-phase click listener on
  // document for routing that calls stopPropagation — that runs before a
  // bubble-phase listener, so a bubble handler here never fires (the
  // marker's hover title still shows, so it looks like "nothing happens"
  // on click). Listening in capture on the marker itself wins the race.
  span.addEventListener("click", e => {
    e.preventDefault();
    e.stopImmediatePropagation();
    // Read the current URL from the dataset so callers can re-target the
    // marker after creation (e.g. mdpi.com aggregated lookups want to point
    // the click at whichever profile URL produced the winning match).
    showOverlay(span.dataset.onlUrl || url, titleHint);
  }, true);
  return span;
}

// Multi-URL aggregated lookup. Used by scanners that attach a single marker
// to a parent element while looking up several related URLs (e.g. mdpi.com
// author hover-cards with 4 external profile-platform links). Picks the
// most-fitting result across all URLs (exact > host-only > none) and points
// the marker's click target at whichever URL produced that winning match,
// so the overlay shows the relevant OneNote pages.
const MATCH_RANK = { exact: 4, jabref: 3, "host-only": 2, none: 1 };

async function dispatchAggregatedLookup(tasks) {
  if (tasks.length === 0) return;
  const concurrency = 4;
  let i = 0;
  async function worker() {
    while (i < tasks.length) {
      const t = tasks[i++];
      try {
        const { ok, results } = await browser.runtime.sendMessage({
          type: "bulkLookup",
          urls: t.urls,
        });
        if (!ok || !results) continue;
        let bestRank = 0, bestUrl = t.urls[0], bestType = "none";
        let bestTotal = 0, bestJabref = 0;
        for (const url of t.urls) {
          const r = results[url];
          if (!r) continue;
          const rank = MATCH_RANK[r.matchType] ?? 0;
          if (rank > bestRank) {
            bestRank   = rank;
            bestUrl    = url;
            bestType   = r.matchType;
            bestTotal  = r.total ?? 0;
            bestJabref = r.jabrefCount ?? 0;
          } else if (rank === bestRank && rank > 1) {
            // Tie within a non-"none" tier — sum totals so the badge count
            // reflects every contributing URL.
            bestTotal  += r.total ?? 0;
            bestJabref += r.jabrefCount ?? 0;
          }
        }
        t.marker.dataset.onlUrl = bestUrl;
        applyStatus(t.marker, { matchType: bestType, total: bestTotal, jabrefCount: bestJabref });
      } catch { /* hub offline — leave default icon */ }
    }
  }
  await Promise.all(Array.from(
    { length: Math.min(concurrency, tasks.length) }, worker
  ));
}

// Google Scholar aggregates papers from many publisher / repository hosts
// (mit.edu, academia.edu, elsevier.com, …). A host-only ("same
// site / related") fallback there matches every unrelated paper the user
// ever saved from that same host, so on Scholar result pages it is pure
// noise. Downgrade host-only to "no match" — exact matches (the paper
// itself is in OneNote) still show. Matches scholar.google.<tld>.
function isScholarPage() {
  return /^scholar\.google\.[a-z.]{2,}$/.test(location.hostname);
}

function applyStatus(marker, status) {
  let key = status?.matchType ?? "none";
  if (key === "host-only" && isScholarPage()) key = "none";
  const icon = ICONS[key] ?? ICONS.none;
  marker.style.backgroundImage = "url(" + browser.runtime.getURL(icon) + ")";
  const jab     = status?.jabrefCount ?? 0;
  const jabPart = jab > 0 ? `, also in JabRef (${jab})` : "";
  if (key === "exact") {
    marker.title = `In OneNote (${status.total})${jabPart}`;
  } else if (key === "host-only") {
    marker.title = `Same site / related in OneNote (${status.total})${jabPart}`;
  } else if (key === "jabref") {
    marker.title = `In JabRef (${jab})`;
  } else {
    marker.title = "Not found";
  }
}

// Per-URL lookups are funnelled through a single shared queue with a
// small concurrency cap so each marker flips as soon as its own lookup
// comes back, rather than waiting for the whole page's worth of
// lookups to finish.
//
// `dedup`: collapse non-exact markers that share a (matchType, searchTerm)
// dedup key. Only enabled by scanContentLinks where many sibling links
// often resolve to the same host-only fallback (e.g. several sub-page
// links on a personal site). Host-specific scanners (Google search
// results, GitHub repo cards, LinkedIn posts, …) place one marker per
// list item and want per-item feedback regardless of whether two items
// happen to share a host-only context, so they leave dedup off.
//
// `lazy`: defer the lookup until the marker scrolls into (or near) the
// viewport. An IntersectionObserver with `rootMargin` extending one
// viewport-height below pre-loads the next viewport so slow scrolling
// is silky. Used by scanContentLinks on link-dense pages
// (github.com/jnv/lists has ~3000 links inside .markdown-body) where
// eager lookup of every marker leaves the queue minutes deep.

const LOOKUP_CONCURRENCY = 4;
const lookupQueue = [];
let lookupWorkersActive = 0;

async function runOneLookup(t) {
  // SPA re-render detached the marker — a lookup would only pin dead DOM.
  if (!t.marker.isConnected) return;
  try {
    const { ok, results } = await browser.runtime.sendMessage({
      type: "bulkLookup",
      urls: [t.url],
    });
    if (ok && results) {
      const r = results[t.url];
      if (r) {
        applyStatus(t.marker, r);
        if (t.dedup) applyDedup(t.marker, r.dedupKey);
      }
    }
  } catch { /* hub offline — leave default icon */ }
}

async function runLookupWorker() {
  // LIFO: pop the most recently enqueued task first. When the user
  // scrolls to a new section while old offscreen tasks are still
  // pending, the newly-intersecting tasks get processed before the
  // old backlog. Old offscreen tasks still complete eventually as the
  // queue drains, but the user-visible region settles first.
  while (lookupQueue.length > 0) {
    const t = lookupQueue.pop();
    await runOneLookup(t);
  }
  lookupWorkersActive--;
}

function enqueueLookup(task) {
  lookupQueue.push(task);
  while (lookupWorkersActive < LOOKUP_CONCURRENCY && lookupQueue.length > 0) {
    lookupWorkersActive++;
    runLookupWorker();
  }
}

// Lazy-lookup observer: fires when a marker enters the viewport (or the
// one-viewport-tall pre-load band below it). On intersect, the observer
// stops watching that marker and pushes its task onto the shared lookup
// queue. The pre-load margin means a marker's icon is usually settled
// before slow scrolling brings it into actual view.
//
// In addition, the FIRST intersecting marker in each IO batch is also
// looked up directly in parallel, bypassing the worker queue. This
// gives the user immediate visible activity ("something is happening")
// when scrolling to a new section even if the worker pool is still
// chewing through earlier batches.
const lazyLookupTasks = new Map();
const lazyLookupObserver = new IntersectionObserver((entries) => {
  let directHead = null;
  for (const entry of entries) {
    if (!entry.isIntersecting) continue;
    const t = lazyLookupTasks.get(entry.target);
    if (!t) continue;
    lazyLookupObserver.unobserve(entry.target);
    lazyLookupTasks.delete(entry.target);
    if (directHead === null) {
      directHead = t;       // process this one immediately, in parallel
    } else {
      enqueueLookup(t);     // rest go to the queue
    }
  }
  if (directHead) runOneLookup(directHead);
}, { rootMargin: "0px 0px 100% 0px" });

// [impl->req~ank.cache.bounded~1]
// SPA re-renders detach observed markers; they never intersect, so the
// observer + task map would retain them (and their closures) forever.
function pruneDetachedLazyTasks() {
  if (lazyLookupTasks.size < 500) return;
  for (const el of [...lazyLookupTasks.keys()]) {
    if (!el.isConnected) {
      lazyLookupObserver.unobserve(el);
      lazyLookupTasks.delete(el);
    }
  }
}

async function dispatchLookup(tasks, { dedup = false, lazy = false } = {}) {
  if (tasks.length === 0) return;
  if (lazy) {
    pruneDetachedLazyTasks();
    for (const t of tasks) {
      const queueTask = { url: t.url, marker: t.marker, dedup };
      lazyLookupTasks.set(t.marker, queueTask);
      lazyLookupObserver.observe(t.marker);
    }
    return;
  }
  for (const t of tasks) {
    enqueueLookup({ url: t.url, marker: t.marker, dedup });
  }
}

// Dedup state: collapse sibling markers that resolve to the same OneNote
// search-context. The hub annotates non-exact results with a dedupKey
// derived from (matchType, searchTerm); for exact matches and "none" the
// key is null (each URL is its own thing). Stale entries (where the
// original marker was torn down by SPA navigation) are replaced rather
// than starving fresh markers.
const seenDedupMarkers = new Map();

function applyDedup(marker, dedupKey) {
  if (!dedupKey) return;
  const existing = seenDedupMarkers.get(dedupKey);
  if (existing && existing !== marker && existing.isConnected) {
    marker.remove();
    return;
  }
  seenDedupMarkers.set(dedupKey, marker);
}

// ── Site: github.com ─────────────────────────────────────────────────────────

function scanGithub() {
  const forms = document.querySelectorAll(
    `form[action$="/star"]:not([${SEEN_ATTR}]), form[action$="/unstar"]:not([${SEEN_ATTR}])`
  );
  if (forms.length === 0) return;

  // On a repo's own project page the star/unstar form belongs to the repo
  // header — placing the marker beside the form dumps it far to the right,
  // next to the Star/Fork/Watch buttons. Anchor it to the repo-name heading
  // instead so the row reads "<owner> / <repo> <Public> ●".
  //
  // GitHub moved the Watch/Fork/Star controls *out* of
  // #repository-container-header into a separate sticky actions bar, so the
  // old "form is inside the header" test now always fails. Key off the
  // repo-name link's href instead: the header still carries
  // strong[itemprop="name"] > a[href="/<owner>/<repo>"], and a star form
  // whose action is /<owner>/<repo>/star is that repo's header star. On
  // topic/search pages there is no such repo-name heading (or it points at a
  // different repo), so those cards keep the marker beside their own form.
  const nameEl   = document.querySelector('strong[itemprop="name"]');
  const nameHref = (nameEl?.querySelector("a")?.getAttribute("href") || "")
    .replace(/[?#].*$/, "").replace(/\/$/, "");

  const tasks = [];
  for (const form of forms) {
    const action = (form.getAttribute("action") || "").replace(/[?#].*$/, "");
    const m = action.match(/^\/([^/]+)\/([^/]+)\/(?:un)?star$/);
    if (!m) continue;
    form.setAttribute(SEEN_ATTR, "1");
    try {
      const url    = `https://github.com/${m[1]}/${m[2]}`;
      const isRepoHeaderStar = nameHref === `/${m[1]}/${m[2]}`;
      const titleRow = isRepoHeaderStar ? nameEl.parentElement : null;
      let marker;
      if (titleRow) {
        // A repo page renders both a star and an unstar form (CSS-toggled);
        // only the first one through here gets the marker.
        if (titleRow.querySelector(".onl-marker")) continue;
        marker = makeMarker(url, `${m[1]}/${m[2]}`);
        marker.style.marginLeft  = "6px";
        marker.style.marginRight = "0";
        titleRow.appendChild(marker);
      } else {
        const parent = form.parentElement;
        if (!parent || !parent.contains(form)) throw new Error("form detached");
        marker = makeMarker(url, `${m[1]}/${m[2]}`);
        parent.insertBefore(marker, form);
      }
      tasks.push({ url, marker });
    } catch {
      form.removeAttribute(SEEN_ATTR);
    }
  }
  dispatchLookup(tasks);
}

// User / org "Repositories" listing (github.com/<owner>, github.com/orgs/…).
// Those cards have no inline star form — the repo name is a plain
// `a[itemprop="name codeRepository"]` link — so scanGithub() skips them.
// Anchor a marker after each repo-name link so the row reads
// "<repo> <Public> ●". Lazy so a profile with hundreds of repos only
// looks up the ones scrolled into view.
function scanGithubRepoList() {
  const links = document.querySelectorAll(
    `a[itemprop="name codeRepository"]:not([${SEEN_ATTR}])`
  );
  const tasks = [];
  for (const a of links) {
    a.setAttribute(SEEN_ATTR, "1");
    const m = (a.getAttribute("href") || "").match(/^\/([^/?#]+)\/([^/?#]+)/);
    if (!m) continue;
    const url = `https://github.com/${m[1]}/${m[2]}`;
    const marker = makeMarker(url, `${m[1]}/${m[2]}`);
    marker.style.marginLeft  = "6px";
    marker.style.marginRight = "0";
    a.insertAdjacentElement("afterend", marker);
    tasks.push({ url, marker });
  }
  dispatchLookup(tasks, { lazy: true });
}

// GitHub profile sidebar (vcard-details): mark the homepage / LinkedIn /
// BlueSky / Twitter / Mastodon / etc. links the user has added to their
// profile. Marker placed *after* the link text, so each row reads
// "linkedin.com/in/foo  <bubble>".
function scanGithubProfile() {
  const list = document.querySelector("ul.vcard-details");
  if (!list) return;
  const links = list.querySelectorAll(`a[href]:not([${SEEN_ATTR}])`);
  if (links.length === 0) return;

  const selfUrl = location.origin + location.pathname;
  const seenUrls = new Set();
  const tasks = [];
  for (const a of links) {
    const url = a.href;
    if (!url || !/^https?:/.test(url)) continue;
    // Mark before dedup checks so a re-scan doesn't reconsider skipped anchors.
    a.setAttribute(SEEN_ATTR, "1");
    if (url === selfUrl) continue;
    if (seenUrls.has(url)) continue;
    seenUrls.add(url);
    try {
      const parent = a.parentElement;
      if (!parent || !parent.contains(a)) throw new Error("anchor detached");
      const titleHint = (a.textContent ?? "").trim() || url;
      const marker = makeMarker(url, titleHint);
      marker.style.marginLeft  = "6px";
      marker.style.marginRight = "0";
      parent.insertBefore(marker, a.nextSibling);
      tasks.push({ url, marker });
    } catch {
      a.removeAttribute(SEEN_ATTR);
    }
  }
  dispatchLookup(tasks);
}

// ── Site: researchgate.net ───────────────────────────────────────────────────

// Lab pages list members with /profile/<slug>?_sg=…&_tp=… tracking URLs.
// We send the literal href to the hub; UrlNormalizer (Core) strips the
// query string so OneNote pages recorded with the canonical profile URL
// still match. Slug regex is only used here for dedup and marker label.
function scanResearchGate() {
  if (!/^\/lab\//.test(location.pathname)) return;
  const links = document.querySelectorAll(`a[href*="/profile/"]:not([${SEEN_ATTR}])`);
  if (links.length === 0) return;

  const bySlug = new Map();
  for (const a of links) {
    const href = a.getAttribute("href") || "";
    const m = href.match(/^\/profile\/([^/?#]+)/);
    if (!m) continue;
    a.setAttribute(SEEN_ATTR, "1");
    const slug = m[1];
    if (!bySlug.has(slug)) bySlug.set(slug, []);
    bySlug.get(slug).push(a);
  }

  const tasks = [];
  for (const [slug, anchors] of bySlug) {
    try {
      const a = pickTitleAnchor(anchors);
      const parent = a.parentElement;
      if (!parent || !parent.contains(a)) throw new Error("anchor detached");
      const url = a.href;
      const marker = makeMarker(url, slug);
      marker.style.marginLeft  = "6px";
      marker.style.marginRight = "0";
      parent.insertBefore(marker, a.nextSibling);
      tasks.push({ url, marker });
    } catch {
      for (const x of anchors) x.removeAttribute(SEEN_ATTR);
    }
  }
  dispatchLookup(tasks);
}

// ── Site: alternativeto.net ──────────────────────────────────────────────────

function scanAlternativeTo() {
  const currentSlug = (location.pathname.match(/^\/software\/([^/]+)\//) || [])[1];
  const links = document.querySelectorAll(`a[href*="/software/"]:not([${SEEN_ATTR}])`);
  if (links.length === 0) return;

  // Group all candidate anchors by slug; mark every considered anchor as
  // SEEN_ATTR up-front so a subsequent MutationObserver tick doesn't
  // re-evaluate the skipped duplicates with a fresh, empty seenSlugs and
  // inject a second marker for the same app.
  const bySlug = new Map();
  for (const a of links) {
    const href = a.getAttribute("href") || "";
    const m = href.match(/^\/software\/([^/?#]+)\/about\/?$/);
    if (!m) continue;
    a.setAttribute(SEEN_ATTR, "1");
    const slug = m[1];
    if (slug === currentSlug) continue;
    if (!bySlug.has(slug)) bySlug.set(slug, []);
    bySlug.get(slug).push(a);
  }

  const tasks = [];
  for (const [slug, anchors] of bySlug) {
    try {
      const a = pickTitleAnchor(anchors);
      const parent = a.parentElement;
      if (!parent || !parent.contains(a)) throw new Error("anchor detached");
      const url = `https://alternativeto.net/software/${slug}/`;
      const marker = makeMarker(url, slug);
      parent.insertBefore(marker, a);
      tasks.push({ url, marker });
    } catch (e) {
      // React may have re-rendered the anchor between scan and inject.
      // Clear SEEN_ATTR so the next scan retries this slug.
      for (const x of anchors) x.removeAttribute(SEEN_ATTR);
    }
  }
  dispatchLookup(tasks);
}

// ── Site: google search ──────────────────────────────────────────────────────

// On a Google /search results page each organic result is a link wrapping
// (or sibling to) an <h3> that holds the visible title. Strategy:
//   1. Find every h3 whose closest ancestor anchor has an http(s) href.
//   2. Pull the destination URL — modern Google has direct hrefs; older /url?
//      redirects are unwrapped.
//   3. Inject the marker at the end of the h3 so the bubble sits next to the
//      title text. The marker's click handler preventDefaults the link, so
//      clicking the bubble opens the popup overlay instead of navigating.
//
// Ads and Knowledge-Graph cards also contain h3s; we accept the false
// positives rather than chase brittle ad-detection heuristics. The marker is
// always informative — grey if nothing is anchored.

function unwrapGoogleRedirect(href) {
  if (!href) return null;
  try {
    if (href.startsWith("/url?") || href.startsWith("https://www.google.")
        && new URL(href).pathname === "/url") {
      const u = new URL(href, location.origin);
      const q = u.searchParams.get("q") || u.searchParams.get("url");
      if (q && /^https?:/i.test(q)) return q;
      return null;
    }
  } catch { return null; }
  return /^https?:/i.test(href) ? href : null;
}

function scanGoogle() {
  if (!location.pathname.startsWith("/search")) return;
  const headings = document.querySelectorAll(`h3:not([${SEEN_ATTR}])`);
  if (headings.length === 0) return;

  const tasks = [];
  for (const h3 of headings) {
    const a = h3.closest("a[href]");
    if (!a) continue;
    const url = unwrapGoogleRedirect(a.getAttribute("href") || a.href);
    if (!url) continue;
    h3.setAttribute(SEEN_ATTR, "1");
    try {
      const titleHint = (h3.textContent ?? "").trim() || url;
      const marker = makeMarker(url, titleHint);
      marker.style.marginLeft  = "6px";
      marker.style.marginRight = "0";
      // Place marker as a sibling of the wrapping anchor — h3 is itself
      // inside the result anchor in Google's markup, so appending into h3
      // would put the marker inside <a>. That leaks the result URL into
      // the status bar on hover and can let the anchor's click handler
      // swallow the marker click before our preventDefault runs.
      const insertAfter = escapeAnchorAncestor(h3);
      const parent = insertAfter.parentElement;
      if (!parent || !parent.contains(insertAfter)) throw new Error("h3 detached");
      parent.insertBefore(marker, insertAfter.nextSibling);
      tasks.push({ url, marker });
    } catch {
      h3.removeAttribute(SEEN_ATTR);
    }
  }
  dispatchLookup(tasks);
}

// ── Site: zeit.de ────────────────────────────────────────────────────────────

// Front-page / section cards use Svelte-rendered teasers:
//   <article class="zon-teaser zon-teaser--standard">
//     <div class="zon-teaser__container">
//       <a class="zon-teaser__link" href="https://www.zeit.de/.../">
//         <h3 class="zon-teaser__heading">
//           <span class="zon-teaser__kicker">[Z+ icon] Kategorie</span>
//           <span class="zon-teaser__title">Headline</span>
//         </h3>
//       </a>
//       <div class="duv-two-cols">
//         <a class="zon-teaser__summary" href="…same URL…"><p>Summary</p></a>
//         <p class="zon-teaser__byline">…</p>
//       </div>
//     </div>
//   </article>
//
// Two anchors per card both point to the same article URL — without a
// dedicated scanner the generic content scanner would mark each one, and
// the layout would put bubbles in confusing places (next to the
// summary, on top of the byline, …). scanZeit suppresses both, finds
// the .zon-teaser__title span inside the headline link, and appends a
// single marker there so the bubble sits inline at the very end of the
// headline text.
//
// The marker ends up inside the wrapping <a> — that breaks the usual
// "don't sit inside the link" rule, but inline-at-end-of-headline is
// only reachable from inside the title span; the marker's click handler
// still preventDefaults the link's navigation, so clicking the bubble
// opens the popup rather than navigating to the article.

function scanZeit() {
  const cards = document.querySelectorAll(
    `article.zon-teaser:not([${SEEN_ATTR}])`
  );
  if (cards.length === 0) return;

  const tasks = [];
  for (const card of cards) {
    // Skip ads — their kicker carries the .zon-teaser__kicker--ad modifier.
    if (card.querySelector(".zon-teaser__kicker--ad")) {
      card.setAttribute(SEEN_ATTR, "1");
      continue;
    }

    const headlineLink = card.querySelector("a.zon-teaser__link");
    if (!headlineLink) continue;
    const url = headlineLink.href;
    if (!url || !/^https?:/i.test(url)) continue;

    // Suppress per-anchor markers from scanContentLinks. Both the headline
    // and summary anchors point at the same article URL; we want exactly
    // one bubble per card and we control its position ourselves.
    for (const a of card.querySelectorAll("a[href]")) {
      a.setAttribute(SEEN_ATTR, "1");
    }
    card.setAttribute(SEEN_ATTR, "1");

    // Prefer the title span (inline placement at end of headline). Fall
    // back to the <h3> wrapper if a variant lacks the span.
    const attachPoint =
      headlineLink.querySelector(".zon-teaser__title")
      ?? headlineLink.querySelector(".zon-teaser__heading")
      ?? headlineLink;

    try {
      const titleHint = (attachPoint.textContent ?? "").trim() || url;
      const marker = makeMarker(url, titleHint);
      marker.style.marginLeft  = "6px";
      marker.style.marginRight = "0";
      attachPoint.appendChild(marker);
      tasks.push({ url, marker });
    } catch {
      card.removeAttribute(SEEN_ATTR);
    }
  }
  dispatchLookup(tasks);
}

// ── Site: linkedin.com ───────────────────────────────────────────────────────

// Feed posts and individual post pages each render a post in a container
// with `data-urn="urn:li:activity:N"`. Two markers per post:
//   1. Author — bubble after the first /in/<slug> link with text. Lookup is
//      by canonical profile URL; the hub's LinkedInUrlHelper falls back to
//      a person-name search so OneNote pages that mention the person in
//      plain text still match.
//   2. Post — bubble before the "..." control-menu button, using the
//      synthesised permalink https://www.linkedin.com/feed/update/<urn>/.
// Markers are tagged on the link / button (not the post container) so a
// re-scan picks up whichever half hadn't rendered yet.

const LI_POST_URN_RE = /^urn:li:activity:\d+$/;

function buildLinkedInPostUrl(urn) {
  return `https://www.linkedin.com/feed/update/${encodeURIComponent(urn)}/`;
}

function canonicaliseLinkedInProfileUrl(href) {
  if (!href) return null;
  let u;
  try { u = new URL(href, location.origin); } catch { return null; }
  const m = u.pathname.match(/^\/in\/([^/?#]+)/);
  if (!m) return null;
  return `https://www.linkedin.com/in/${m[1]}`;
}

// LinkedIn wraps the post header in <a href="?highlightedUpdateUrn=…"> for
// permalink navigation. A marker inserted as a sibling of an /in/<slug>
// anchor inherits that wrapper as its hover ancestor, so the browser shows
// the post-feed URL in the status bar instead of nothing — confusing, since
// hovering the marker should feel like inspecting it, not the surrounding
// card. Walk up through any <a> ancestors and insert the marker as a
// sibling of the outermost <a> so the status bar stays clean.
function escapeAnchorAncestor(el) {
  let current = el;
  while (current.parentElement && current.parentElement.tagName === "A") {
    current = current.parentElement;
  }
  return current;
}

function scanLinkedIn() {
  const posts = document.querySelectorAll('[data-urn^="urn:li:activity:"]');
  if (posts.length === 0) return;

  const tasks = [];
  for (const post of posts) {
    const urn = post.getAttribute("data-urn") || "";
    if (!LI_POST_URN_RE.test(urn)) continue;

    // 1. Author bubble — first /in/<slug> link with visible text. The avatar
    //    link points at the same slug but is icon-only; the name link
    //    carries the human-readable text we want as the tooltip.
    const candidates = post.querySelectorAll(`a[href*="/in/"]:not([${SEEN_ATTR}])`);
    let chosen = null;
    for (const a of candidates) {
      if ((a.textContent ?? "").trim()) { chosen = a; break; }
    }
    if (chosen) {
      const url = canonicaliseLinkedInProfileUrl(chosen.href);
      if (url) {
        chosen.setAttribute(SEEN_ATTR, "1");
        try {
          const insertAfter = escapeAnchorAncestor(chosen);
          const parent = insertAfter.parentElement;
          if (!parent || !parent.contains(insertAfter)) throw new Error("anchor detached");
          const titleHint = (chosen.textContent ?? "").trim() || url;
          const marker = makeMarker(url, titleHint);
          marker.style.marginLeft  = "6px";
          marker.style.marginRight = "0";
          parent.insertBefore(marker, insertAfter.nextSibling);
          tasks.push({ url, marker });
        } catch {
          chosen.removeAttribute(SEEN_ATTR);
        }
      }
    }

    // 2. Post bubble — placed before the "..." control-menu button. LinkedIn
    //    labels the trigger via aria-label; the exact phrasing varies by
    //    locale and post type, so match a few known prefixes.
    const menuBtn = post.querySelector(
      `button[aria-label*="actions" i]:not([${SEEN_ATTR}]),`
      + ` button[aria-label*="control menu" i]:not([${SEEN_ATTR}]),`
      + ` button[aria-label*="more options" i]:not([${SEEN_ATTR}])`
    );
    if (menuBtn) {
      menuBtn.setAttribute(SEEN_ATTR, "1");
      try {
        const insertBefore = escapeAnchorAncestor(menuBtn);
        const parent = insertBefore.parentElement;
        if (!parent || !parent.contains(insertBefore)) throw new Error("button detached");
        const url = buildLinkedInPostUrl(urn);
        const marker = makeMarker(url, urn);
        marker.style.marginLeft  = "0";
        marker.style.marginRight = "6px";
        parent.insertBefore(marker, insertBefore);
        tasks.push({ url, marker });
      } catch {
        menuBtn.removeAttribute(SEEN_ATTR);
      }
    }
  }

  dispatchLookup(tasks);
}

// Pick the anchor most likely to be a "title" link (the one rendering the
// app name) rather than an icon-only link. Icon links often wrap an <img>
// plus visually-hidden text, so plain text-content checks aren't enough.
function pickTitleAnchor(anchors) {
  // 1. Inside a heading element — strongest signal for a title link.
  const heading = anchors.find(x => x.closest("h1,h2,h3,h4,h5,h6"));
  if (heading) return heading;
  // 2. Has text and no descendant <img> — text-only link.
  const textOnly = anchors.find(x =>
    (x.textContent ?? "").trim() && !x.querySelector("img")
  );
  if (textOnly) return textOnly;
  // 3. Any anchor with visible text.
  const withText = anchors.find(x => (x.textContent ?? "").trim());
  return withText ?? anchors[0];
}

// ── Site: www.mdpi.com (author hover-card aggregation) ──────────────────────

// MDPI article pages list each author as <span class="inlineblock"> wrapping
// a trigger <div class="profile-card-drop"> (the visible name) and a hidden
// <div class="f-dropdown profile-card-content"> popup containing four
// platform buttons (SciProfiles / Scilit / Preprints.org / Google Scholar).
// All popups are present in the DOM at page load.
//
// Without aggregation, scanContentLinks would mark each of the four buttons
// inside every popup — invisible until hover, and fragmented across panels.
// Instead we hide those individual markers (SEEN_ATTR on each button) and
// place ONE marker right after the author name, pointing the click at
// whichever profile URL produced the most-fitting OneNote match.

function scanMdpi() {
  const wrappers = document.querySelectorAll(
    `span.inlineblock:not([${SEEN_ATTR}])`
  );
  if (wrappers.length === 0) return;

  const tasks = [];
  for (const wrapper of wrappers) {
    const trigger = wrapper.querySelector(":scope > div.profile-card-drop");
    const popup   = wrapper.querySelector(":scope > div.f-dropdown.profile-card-content");
    if (!trigger || !popup) continue;

    const buttons = popup.querySelectorAll(".profile-card__buttons a.button--color-inversed");
    if (buttons.length === 0) continue;

    const urls = [];
    for (const a of buttons) {
      // Suppress the per-link marker scanContentLinks would inject — popup
      // markers are invisible until hover and add no signal we don't already
      // surface on the aggregated trigger marker. Setting SEEN_ATTR blocks
      // future scanContentLinks runs from re-marking; removing any existing
      // sibling marker handles the case where scanContentLinks already ran
      // (e.g. on the previous scan tick before the popup was claimed by
      // scanMdpi, or because the popup hadn't rendered yet at that point).
      a.setAttribute(SEEN_ATTR, "1");
      const existingMarker = a.nextElementSibling;
      if (existingMarker && existingMarker.classList?.contains("onl-marker")) {
        existingMarker.remove();
      }
      const href = a.getAttribute("href") || "";
      if (!href) continue;
      let target;
      try { target = new URL(href, location.origin); } catch { continue; }
      if (!/^https?:$/i.test(target.protocol)) continue;
      urls.push(target.href);
    }
    if (urls.length === 0) continue;

    wrapper.setAttribute(SEEN_ATTR, "1");
    try {
      const titleHint = (trigger.textContent ?? "").trim() || urls[0];
      const marker = makeMarker(urls[0], titleHint);
      marker.style.marginLeft  = "4px";
      marker.style.marginRight = "0";
      // Insert right after the trigger so it renders inline next to the
      // visible author name; the hidden popup follows but doesn't affect flow.
      wrapper.insertBefore(marker, trigger.nextSibling);
      tasks.push({ urls, marker });
    } catch {
      wrapper.removeAttribute(SEEN_ATTR);
    }
  }
  dispatchAggregatedLookup(tasks);
}

// ── Any host: content-container link markers ─────────────────────────────────

// Mark every http(s) link inside a curated set of "content" containers —
// places where users embed pasted URLs (issue comments, blog posts,
// Stack Overflow answers, Reddit posts, etc.). The allowlist is much
// narrower than `<a>` everywhere; it deliberately skips nav/footer/sidebar
// to keep the bubble density tolerable on link-heavy pages.
//
// Site-specific scanners run first and own the SEEN_ATTR for the elements
// they touch (star forms, vcard anchors, …). Those don't overlap with what
// this scanner walks, but the flag is checked anyway so any future overlap
// silently defers to the more precise scanner.

// Pagination, breadcrumb, and other in-page navigation links — never
// pasted reference URLs, so a marker next to them is always noise.
// Detected structurally:
//   - <nav> / [role="navigation"] — semantic case (most modern sites)
//   - class names containing "pagination" / "pagenav", or a whole-class
//     "pager" — covers older non-semantic markup (Bootstrap 3 pagers,
//     WordPress wp-pagenavi, etc.)
//   - rel="next" / "prev" / "first" / "last" — set by many CMSs on
//     pagination anchors even when they sit outside <nav>
function isInPageNavigation(a) {
  const rel = (a.getAttribute("rel") || "").toLowerCase();
  if (/\b(next|prev|previous|first|last)\b/.test(rel)) return true;
  // Anchors explicitly acting as buttons (heise.de "Kommentare lesen",
  // most modern marketing CTAs, Stack Overflow vote arrows, …). The text
  // is an action verb, not a reference URL.
  const role = (a.getAttribute("role") || "").toLowerCase();
  if (role === "button") return true;
  return a.closest(
    'nav, [role="navigation"], footer, [role="contentinfo"],'
    + ' [class*="pagination" i], [class~="pager"], [class*="pagenav" i]'
  ) != null;
}

// Action-row button labels (en + de). Used to recognise an OK/Cancel-style
// dialog footer so links sitting in it (e.g. a "Forgot password?" link below
// the buttons) are treated as UI chrome, not pasted reference URLs.
const DIALOG_BUTTON_LABELS =
  /^(ok|cancel|abbrechen|abbruch|close|schlie(ß|ss)en|yes|no|ja|nein|save|speichern|submit|senden|absenden|sign in|log ?in|anmelden|register|registrieren|continue|weiter|back|zur(ü|ue)ck|apply|(ü|ue)bernehmen|confirm|best(ä|ae)tigen|delete|l(ö|oe)schen|discard|verwerfen)$/i;

// Login screens and modal action rows. A marker here is always noise:
//   - Login form: the anchor shares a form/dialog ancestor with a password
//     input ("Passwort vergessen?" / "Forgot password?" links).
//   - Dialog footer: the anchor's container also holds an OK/Cancel-style
//     button (the link sits next to the dialog's action buttons).
function isLoginOrDialogControl(a) {
  const scope = a.closest('form, dialog, [role="dialog"], [aria-modal="true"]');
  if (scope && scope.querySelector('input[type="password" i]')) return true;
  const parent = a.parentElement;
  if (parent) {
    const buttons = parent.querySelectorAll(
      'button, [role="button"], input[type="button"], input[type="submit"]'
    );
    for (const b of buttons) {
      const label = (b.value || b.textContent || "").trim();
      if (DIALOG_BUTTON_LABELS.test(label)) return true;
    }
  }
  return false;
}

// Hosts where the generic content-link scanner is suppressed entirely.
// These are workspace-style apps (Overleaf project list, …) where every
// row is a link to a workspace item; even if a few items match OneNote
// pages by exact URL, the visual density of markers across a personal
// project listing is noise rather than signal. Host-specific scanners
// can still inject targeted markers when they make sense.
const SKIP_CONTENT_SCAN_HOSTS = new Set([
  "www.overleaf.com",
  "overleaf.com",
]);

// Article/prose containers — these are tight enough to use anywhere.
const CONTENT_CONTAINER_SELECTOR_NARROW = [
  "article",
  "[role='article']",
  ".markdown-body",          // GitHub issues, PRs, READMEs, gists, comments
  ".comment-body",           // GitHub & similar
  ".s-prose",                // Stack Exchange
  ".md",                     // Reddit
  "[itemprop='articleBody']", // schema.org-marked article bodies
  ".author__urls",           // Jekyll academicpages / minimal-mistakes sidebar
                             // profile links (Email, Scholar, ORCID, ResearchGate,
                             // LinkedIn, …) — these are the highest-signal
                             // "where to find me" links on academic personal pages.
  ".ce-bodytext",            // TYPO3 content-element body text (German
                             // university sites: HTWK, KIT-old, etc.) —
                             // always wraps prose+links, no nav/buttons,
                             // safe to scan in both main content and
                             // right-side "Links" sidebars.
].join(",");

// HTML5 main-landmark selectors. Only used on hosts that don't have a
// dedicated scanner — on rich apps (github.com PR/issue lists,
// linkedin.com feed, google search, …) <main> wraps every UI control
// (filters, toolbar buttons, author chips, label tags, …) and produces
// massive marker spam. On unknown hosts (academic CMS pages like KIT,
// personal sites, blogs that don't use <article>), <main> is the right
// fallback. The list is extended with common CMS content-wrapper IDs
// for sites that predate HTML5 landmarks — e.g. TYPO3 BootstrapPackage
// (used by many German university and Gesellschaft-für-Informatik
// sub-sites) wraps its content in <div id="page-content"
// class="bp-page-content"> rather than <main>.
const MAIN_LANDMARK_SELECTOR = [
  "main",
  "[role='main']",
  "#page-content",      // TYPO3 BootstrapPackage main content wrapper
  ".bp-page-content",   // same, as a class
].join(",");

// ── Scanner: BibTeX blocks ───────────────────────────────────────────────────
// Pages that render a BibTeX entry inside a <pre> / <pre><code> block
// (typical case: a "Citation" section in a GitHub README) get a marker
// floated to the top-right of the block. Click opens the popup against
// the entry's `url` field (or an arxiv URL synthesised from `eprint`),
// so the user sees whether the cited paper is anchored in OneNote.
//
// The parser mirrors the one in background.js's detectBibtex(); the
// duplication keeps the content script self-contained and avoids the
// timing fragility of asking the background process whether its
// per-tab BibTeX cache has populated yet.

function parseBibtexLocal(text) {
  const entries = [];
  if (!/^\s*@\w+\s*\{/.test(text)) return entries;
  const entryRe = /@(\w+)\s*\{\s*([^\s,]+)\s*,([\s\S]*)/g;
  let m;
  while ((m = entryRe.exec(text))) {
    const [, type, citationKey, rest] = m;
    let depth = 1, end = -1;
    for (let i = 0; i < rest.length; i++) {
      const c = rest[i];
      if (c === "{") depth++;
      else if (c === "}") { depth--; if (depth === 0) { end = i; break; } }
    }
    if (end < 0) continue;
    const body  = rest.slice(0, end);
    const entry = { entryType: type, citationKey };
    const fieldRe = /(\w+)\s*=\s*(\{([^{}]*)\}|"([^"]*)")/g;
    let fm;
    while ((fm = fieldRe.exec(body))) {
      const key = fm[1].toLowerCase();
      const val = (fm[3] ?? fm[4] ?? "").trim();
      entry[key] = val;
    }
    entries.push(entry);
    entryRe.lastIndex = m.index + (m[0].length - rest.length) + end + 1;
  }
  return entries;
}

// ── Site: arxiv.org (HTML paper view) ───────────────────────────────────────
//
// arxiv.org/html/<id> renders a paper via LaTeXML. Two things get a marker:
//   1. the paper itself — after the <h1 class="ltx_title_document"> title,
//      looked up by its arXiv abs URL (id from the page path) and arXiv DOI;
//   2. each bibliography entry (<li class="ltx_bibitem">) — a marker after
//      the reference title, looked up by the entry's arXiv id (parsed from
//      the "arXiv preprint arXiv:NNNN.NNNNN" text) and/or its DOI (the
//      "External Links" doi.org anchors).
//
// The DOI anchors are flagged SEEN so the generic content scanner doesn't
// also mark them — the aggregated reference marker already covers them.

// Bare arXiv id (no version suffix) from an /abs//html//pdf path.
function arxivIdFromPath(pathname) {
  const m = /\/(?:abs|html|pdf)\/(\d{4}\.\d{4,5})/i.exec(pathname);
  return m ? m[1] : null;
}

// Bare arXiv id from an "arXiv:NNNN.NNNNN" mention in free text.
function arxivIdInText(text) {
  const m = /arXiv:\s*(\d{4}\.\d{4,5})/i.exec(text);
  return m ? m[1] : null;
}

function scanArxiv() {
  const tasks = [];

  // 1. Paper title.
  const h1 = document.querySelector(`h1.ltx_title_document:not([${SEEN_ATTR}])`);
  if (h1) {
    const paperId = arxivIdFromPath(location.pathname);
    if (paperId) {
      h1.setAttribute(SEEN_ATTR, "1");
      const urls = [
        `https://arxiv.org/abs/${paperId}`,
        `https://doi.org/10.48550/arXiv.${paperId}`,
      ];
      const marker = makeMarker(urls[0], (h1.textContent ?? "").trim());
      marker.style.marginLeft  = "8px";
      marker.style.marginRight = "0";
      h1.appendChild(marker);
      tasks.push({ urls, marker });
    }
  }

  // 2. Bibliography entries.
  for (const li of document.querySelectorAll(`li.ltx_bibitem:not([${SEEN_ATTR}])`)) {
    const titleSpan = li.querySelector("span.ltx_bib_title");
    if (!titleSpan) continue;

    const urls = [];
    const arxivId = arxivIdInText(li.textContent ?? "");
    if (arxivId) urls.push(`https://arxiv.org/abs/${arxivId}`);

    // External-link anchors — keep only doi.org / dx.doi.org ("Link" and
    // "Document"), normalised to a canonical https://doi.org/<doi> URL.
    for (const a of li.querySelectorAll("span.ltx_bib_links a[href]")) {
      let target;
      try { target = new URL(a.getAttribute("href"), location.origin); }
      catch { continue; }
      if (!/(^|\.)doi\.org$/i.test(target.hostname)) continue;
      a.setAttribute(SEEN_ATTR, "1");
      const doiUrl = `https://doi.org${target.pathname}`;
      if (!urls.includes(doiUrl)) urls.push(doiUrl);
    }

    if (urls.length === 0) continue;
    li.setAttribute(SEEN_ATTR, "1");

    const marker = makeMarker(urls[0], (titleSpan.textContent ?? "").trim());
    marker.style.marginLeft  = "6px";
    marker.style.marginRight = "0";
    // Append to the title's bibblock so the dot lands after the trailing
    // period that follows the title span.
    const titleBlock = titleSpan.closest("span.ltx_bibblock") ?? titleSpan;
    titleBlock.appendChild(marker);
    tasks.push({ urls, marker });
  }

  dispatchAggregatedLookup(tasks);
}

function scanBibtex() {
  const sources = document.querySelectorAll("pre");
  const tasks = [];
  for (const pre of sources) {
    if (pre.hasAttribute(SEEN_ATTR)) continue;
    const text = pre.textContent ?? "";
    if (!/^\s*@\w+\s*\{/.test(text)) continue;
    const entries = parseBibtexLocal(text);
    if (!entries.length) continue;
    pre.setAttribute(SEEN_ATTR, "1");

    // One marker per <pre>, attached to the first entry's identifier.
    // Multiple entries in the same block are rare; the popup will list
    // them all when the marker is clicked.
    const entry = entries[0];
    const url = entry.url
              || (entry.eprint ? `https://arxiv.org/abs/${entry.eprint}` : null);
    if (!url) continue;

    const marker = makeMarker(url, `BibTeX: ${entry.citationKey}`);
    // Float to the top-right of the <pre> so it visually lands beside
    // the @<type>{<key>, line without splitting the code's text nodes.
    marker.style.float       = "right";
    marker.style.margin      = "0 0 4px 6px";
    pre.style.position       = pre.style.position || "relative";
    pre.insertBefore(marker, pre.firstChild);
    tasks.push({ url, marker });
  }
  if (tasks.length) dispatchLookup(tasks);
}

function scanContentLinks() {
  if (SKIP_CONTENT_SCAN_HOSTS.has(location.hostname)) return;
  // Use the broader allowlist (which includes <main> / [role='main']) only
  // on hosts without a dedicated scanner. Rich apps with their own scanners
  // tend to wrap entire UIs in <main>, which would produce marker spam over
  // filter chips, label tags, toolbar links, etc.
  const hasHostScanner =
    HOST_HANDLERS[location.hostname] != null
    || HOST_PATTERN_HANDLERS.some(({ match }) => match.test(location.hostname));
  const selector = hasHostScanner
    ? CONTENT_CONTAINER_SELECTOR_NARROW
    : `${CONTENT_CONTAINER_SELECTOR_NARROW},${MAIN_LANDMARK_SELECTOR}`;

  const containers = document.querySelectorAll(selector);
  if (containers.length === 0) return;

  const tasks = [];
  const seenInThisScan = new Set();  // dedup nested containers (article inside article)
  for (const container of containers) {
    if (seenInThisScan.has(container)) continue;
    // If this container is wholly inside another container we'll already visit,
    // skip it — the outer pass picks up its links.
    if (container.parentElement?.closest(selector)) continue;
    seenInThisScan.add(container);

    const links = container.querySelectorAll(`a[href]:not([${SEEN_ATTR}])`);
    for (const a of links) {
      const href = a.getAttribute("href") || "";
      if (!href || href.startsWith("#")) continue;

      let target;
      try { target = new URL(href, location.origin); } catch { continue; }
      if (!/^https?:$/i.test(target.protocol)) continue;
      // Skip self-links to the current page (toolbar icon already shows that state).
      if (target.origin === location.origin
          && target.pathname === location.pathname
          && target.search === location.search) continue;
      // Skip auth/login hosts (login.tailscale.com, login.microsoftonline.com,
      // accounts.google.com, …) — sign-in pages, never pasted references.
      if (/^(login|signin|accounts|auth|sso|oauth)\./i.test(target.host)) continue;
      // Skip "back to homepage" style anchors — host root matches every
      // saved link to that site (or none); the bubble is noise either
      // way. heise.de's "Zur Startseite" CTA is the canonical case.
      if (target.host === location.host
          && (target.pathname === "/" || target.pathname === "")) continue;
      // Skip icon-only / image-only anchors (envelopes, ORCID badges, social-share
      // strips, …). Without visible text, the marker has nothing to be "about" —
      // and these anchors cluster heavily in article-wrapped metadata headers
      // (e.g. mdpi.com bibliographic blocks where author rows pack 4+ icons each).
      const anchorText = (a.textContent ?? "").trim();
      if (!anchorText) continue;
      // Skip pagination / breadcrumb / in-page nav — these are <main>-internal
      // navigation, not pasted reference URLs.
      if (isInPageNavigation(a)) continue;
      // Skip login forms ("Passwort vergessen?") and dialog action rows
      // (links sitting next to OK/Cancel buttons) — UI chrome, not references.
      if (isLoginOrDialogControl(a)) continue;

      a.setAttribute(SEEN_ATTR, "1");
      try {
        // Hoist out of any wrapping <a> ancestor so the marker is placed
        // outside it. Some pages (notably ones with click-handler outer
        // anchors, or markdown-rendered pages where a link wraps a span+
        // text combination) end up putting the marker inside the link,
        // which underlines the bubble visually, leaks the link URL into
        // the status bar on hover, and can let the anchor's click handler
        // swallow the marker click before preventDefault runs.
        const insertAfter = escapeAnchorAncestor(a);
        const parent = insertAfter.parentElement;
        if (!parent || !parent.contains(insertAfter)) throw new Error("anchor detached");
        const titleHint = anchorText;
        const marker = makeMarker(target.href, titleHint);
        marker.style.marginLeft  = "4px";
        marker.style.marginRight = "0";
        parent.insertBefore(marker, insertAfter.nextSibling);
        tasks.push({ url: target.href, marker });
      } catch {
        a.removeAttribute(SEEN_ATTR);
      }
    }
  }
  // Generic content-link scanning is the one place where dedup pays off:
  // a personal page often has many sub-page links that all fall to the
  // same host-only fallback and would otherwise paint as identical teal
  // dots. Host-specific scanners place one marker per list-item and skip
  // dedup so every result gets its own per-item feedback.
  //
  // Lazy: link-dense pages (e.g. github.com/jnv/lists with ~3000 anchors
  // inside .markdown-body) would queue thousands of lookups against
  // OneNote's serial COM bridge — minutes deep. With lazy, lookups only
  // fire as markers enter the viewport (with a one-viewport-tall
  // pre-load band below for smooth scrolling).
  dispatchLookup(tasks, { dedup: true, lazy: true });
}

// ── Site: any (heading-anchored person-name scan) ────────────────────────────

// Pages like conference / lab / committee / org sites list people as plain
// text (no link), so neither host scanners nor URL fallbacks can pick them
// up. The strategy is structural: find headings whose text matches a small
// set of person-y keywords, walk into the following list/table, and extract
// names. Lookup happens by free-text search (hub action `searchText`); a
// marker is only injected when OneNote reports a hit, so misses stay
// invisible — the false-positive risk on plain-text name detection is high
// enough that surfacing nothing for a non-match is the right default.

const NAME_SEEN_ATTR = "data-onl-name-injected";

const PERSON_HEADING_RE =
  /\b(organi[sz]ers?|committees?|chairs?|speakers?|authors?|members?|team|faculty|staff|reviewers?|editors?|presenters?|panel|panell?ists?|keynotes?|invited\s+talks?|advisory|board|people)\b/i;

// Multi-word phrases that match the person-name regex but aren't people.
// Compared lower-cased.
const PERSON_STOP_PHRASES = new Set([
  // Geography
  "united states","united kingdom","czech republic","new zealand","south africa",
  "south america","north america","new york","los angeles","san francisco",
  "san diego","las vegas","hong kong","cape town","saudi arabia","sri lanka",
  "el salvador","puerto rico","sao paulo","buenos aires","rio de janeiro",
  // Web boilerplate
  "open source","working group","best paper","important dates","call for",
  "read more","sign in","sign up","log in","log out","contact us","about us",
  "site map","privacy policy","terms of","follow us",
  // Academic phrases (the heading text itself, when it leaks into a list item)
  "program committee","steering committee","organizing committee","program chair",
  "general chair","workshop chair","keynote speaker","invited speaker",
  "coffee break","lunch break","welcome reception","closing remarks",
  "poster session","computer science","software engineering",
  "artificial intelligence","machine learning","data science",
]);

// Unicode-aware: 2-4 capitalized tokens, optional connecting particle
// (de/van/von/…) before the second token. Anchored to word boundaries so
// "DavidJohn" doesn't match.
const NAME_RE =
  /\b\p{Lu}[\p{L}'’-]{1,}(?:\s+(?:de|del|della|van|von|der|den|ten|la|le|du|da|di|of|y)\s+\p{Lu}[\p{L}'’-]{1,})?(?:\s+\p{Lu}[\p{L}'’-]{1,}){1,3}\b/gu;

// Title prefixes that commonly appear before names. Multiple can stack
// ("Jun.-Prof. Dr."). Trailing whitespace is required so the prefix
// doesn't bleed into the name capture.
const PERSON_TITLE_RE_SRC =
  "(?:Jun\\.[\\-‐‑‒–—]?\\s*)?(?:Prof|Dr|Mr|Ms|Mrs|Mx)\\.\\s+";

// Shared name-shape pattern: a single name token (no capture, no anchors).
// 2-4 capitalized words, optional connecting particle ("de", "von", "van",
// …) before the second word.
const NAME_SHAPE_SRC =
  `\\p{Lu}[\\p{L}'’\\-]{1,}` +
  `(?:\\s+(?:de|del|della|van|von|der|den|ten|la|le|du|da|di|of|y)\\s+\\p{Lu}[\\p{L}'’\\-]{1,})?` +
  `(?:\\s+\\p{Lu}[\\p{L}'’\\-]{1,}){1,3}`;

// Global regex: find every `[title-prefix]* Name (affiliation)` chunk
// inside an italicised speaker line. Multi-name lines like
// "Jun.-Prof. Dr. Mohamed Soliman (Universität Paderborn), Dr. Jan Keim
// (KIT – …)" yield one match per person, each with a clean name in the
// capture group (titles consumed and discarded).
const ITALIC_NAME_RE = new RegExp(
  `(?:${PERSON_TITLE_RE_SRC})*(${NAME_SHAPE_SRC})\\s*\\([^)]+\\)`,
  "gu"
);

// Bare-name regex (no trailing (affiliation) parens). Used to extract
// names from comma-separated co-author lists like "Ada Lovelace,
// Alan Turing" or "Grace Hopper, Edsger Dijkstra, Barbara Liskov"
// — conference programmes often render these as bold lists without
// per-person affiliations.
const BARE_NAME_RE = new RegExp(
  `(?:${PERSON_TITLE_RE_SRC})*(${NAME_SHAPE_SRC})`,
  "gu"
);

// Whole-element gate for the bare-name scan. The element's text must
// be a comma-or-semicolon-separated list of at least two bare names
// with nothing else in it. This keeps the bare-name scan from firing
// on arbitrary bold text ("Wichtig:", "Achtung:", "Best Paper Award",
// …) — without the parens anchor we need *some* structural signal.
const COAUTHOR_LIST_RE = new RegExp(
  `^\\s*(?:${PERSON_TITLE_RE_SRC})*${NAME_SHAPE_SRC}` +
  `(?:\\s*[,;]\\s*(?:${PERSON_TITLE_RE_SRC})*${NAME_SHAPE_SRC})+\\s*$`,
  "u"
);

function looksLikePersonName(s) {
  const lc = s.toLowerCase();
  if (PERSON_STOP_PHRASES.has(lc)) return false;
  // Reject all-caps tokens like "USA" / "MIT" — require at least one lowercase.
  if (!/[\p{Ll}]/u.test(s)) return false;
  return true;
}

function extractNameCandidate(text) {
  if (!text) return null;
  // Strip trailing affiliation: "Sven Apel, Saarland University" → "Sven Apel".
  // Same for parenthesised affiliation: "Sven Apel (Saarland)" → "Sven Apel".
  const stripped = text
    .replace(/\s*[(\[].*$/, "")
    .replace(/\s*[,—–-]\s+.*$/, "")
    .trim();
  const cleaned = stripped.replace(/^(?:Prof\.|Dr\.|Mr\.|Ms\.|Mrs\.)\s+/i, "");
  if (!cleaned) return null;
  const matches = cleaned.match(NAME_RE);
  if (!matches || matches.length === 0) return null;
  const candidate = matches[0];
  // Guard: if the matched name is much shorter than the cleaned line, we
  // probably picked a fragment out of a long descriptive paragraph (e.g. a
  // talk abstract starting "John Smith showed how…") rather than a list entry.
  if (candidate.length * 2 < cleaned.length) return null;
  if (!looksLikePersonName(candidate)) return null;
  return candidate;
}

function findPersonContainers() {
  const headings = document.querySelectorAll("h1,h2,h3,h4,h5,h6");
  const out = [];
  for (const h of headings) {
    const txt = (h.textContent ?? "").trim();
    if (!PERSON_HEADING_RE.test(txt)) continue;
    dbg("findPersonContainers: matched heading", h.tagName, JSON.stringify(txt));
    const level = parseInt(h.tagName.substring(1), 10);
    let n = h.nextElementSibling;
    let added = 0;
    while (n) {
      if (/^H[1-6]$/.test(n.tagName)) {
        const lvl = parseInt(n.tagName.substring(1), 10);
        if (lvl <= level) break;
      }
      if (n.tagName === "UL" || n.tagName === "OL" || n.tagName === "TABLE") {
        out.push(n); added++;
      }
      const inner = n.querySelectorAll?.("ul,ol,table");
      if (inner) for (const inn of inner) { out.push(inn); added++; }
      n = n.nextElementSibling;
    }
    if (added === 0) dbg("  heading matched but no list/table followed it");
  }
  return out;
}

function collectItemsFromContainer(container) {
  let items = [];
  if (container.tagName === "UL" || container.tagName === "OL")
    items = Array.from(container.querySelectorAll(":scope > li"));
  else if (container.tagName === "TABLE")
    items = Array.from(container.querySelectorAll("tr"));
  // Skip items that already have a URL marker — the host scanner has the
  // canonical-URL lookup, which is more precise than a name search.
  return items.filter(it => !it.querySelector(".onl-marker"));
}

function scanPersonHeadings() {
  const containers = findPersonContainers();
  dbg("scanPersonHeadings: containers found =", containers.length);
  const found = [];
  for (const c of containers) {
    const items = collectItemsFromContainer(c);
    const local = [];
    const skipped = [];
    for (const it of items) {
      if (it.hasAttribute(NAME_SEEN_ATTR)) continue;
      // Skip items where an inner <em>/<i> has already been name-marked by
      // scanItalicNames; the italic scan injects after the <em> with finer
      // visual placement than appending to the end of the <li>.
      if (it.querySelector(`[${NAME_SEEN_ATTR}]`)) continue;
      const candidate = extractNameCandidate(it.textContent ?? "");
      if (candidate) local.push({ item: it, name: candidate });
      else skipped.push((it.textContent ?? "").trim().slice(0, 60));
    }
    if (ONL_DEBUG) {
      dbg("  container", c.tagName, "items=", items.length,
          "candidates=", local.map(x => x.name),
          "skipped(no-name)=", skipped);
    }
    // Container heuristic: require ≥2 candidates so a single "Czech Republic"
    // sitting in a venue list doesn't get treated as a person.
    if (local.length < 2) {
      if (local.length === 1)
        dbg("    container dropped: only 1 candidate (threshold ≥2)", local[0].name);
      continue;
    }
    for (const e of local) {
      e.item.setAttribute(NAME_SEEN_ATTR, "1");
      found.push({ name: e.name, anchor: e.item, position: "append" });
    }
  }
  return found;
}

// Speaker lines on keynote / programme pages are emphasised — most
// commonly italic, sometimes only bold. The emphasis + parenthesised
// affiliation pattern is strong enough on its own to skip the
// heading-anchor heuristic.
//   <em>Fabrizio Montesi (University of Southern Denmark)</em>
//   <em>Dr. Vladimir Yussupov (ABB Research, Mannheim; R&D Scientist)</em>
//   <em>Jun.-Prof. Dr. Mohamed Soliman (Universität Paderborn),
//        Dr. Jan Keim (KIT – Karlsruher Institut für Technologie)</em>
//   <strong>Thorsten Weßling (ista SE, Essen)</strong>     ← German programme list
// Two passes: italic first (typically innermost when nested with bold),
// then bold for elements that don't contain an already-processed italic
// descendant. One marker is injected per person found; multi-name
// elements yield multiple markers placed inline at each closing paren.
function scanItalicNames() {
  const found = [];

  // Pass 1: italic — <em>, <i>.
  for (const el of document.querySelectorAll(
    `em:not([${NAME_SEEN_ATTR}]), i:not([${NAME_SEEN_ATTR}])`
  )) {
    extractEmphasisedNames(el, found);
  }

  // Pass 2: bold — <strong>, <b>. Skip elements that wrap an already-
  // processed italic descendant (the inner <em> already accounts for
  // the same names; double-marking would inject duplicates).
  for (const el of document.querySelectorAll(
    `strong:not([${NAME_SEEN_ATTR}]), b:not([${NAME_SEEN_ATTR}])`
  )) {
    if (el.querySelector(`[${NAME_SEEN_ATTR}]`)) continue;
    extractEmphasisedNames(el, found);
  }

  if (found.length > 0) dbg("scanItalicNames: candidates =", found.map(x => x.name));
  return found;
}

function extractEmphasisedNames(el, found) {
  const text = (el.textContent ?? "").trim();
  if (!text) return;
  let matched = false;
  for (const m of text.matchAll(ITALIC_NAME_RE)) {
    const name = m[1];
    if (!looksLikePersonName(name)) {
      dbg("scanItalicNames: rejected by stop-list", name);
      continue;
    }
    found.push({
      name,
      anchor: el,
      position: "inline",
      matchEnd: m.index + m[0].length,  // offset within el.textContent
    });
    matched = true;
  }
  if (matched) {
    el.setAttribute(NAME_SEEN_ATTR, "1");
    return;
  }

  // Fallback: bare-name co-author list (no (affiliation) parens). Only
  // applied when the whole element text matches the co-author-list
  // shape — that's the structural signal that compensates for the
  // missing parens anchor, keeping the bare-name scan from firing on
  // arbitrary bold text.
  if (!COAUTHOR_LIST_RE.test(text)) return;
  for (const m of text.matchAll(BARE_NAME_RE)) {
    const name = m[1];
    if (!looksLikePersonName(name)) {
      dbg("scanItalicNames (bare): rejected by stop-list", name);
      continue;
    }
    found.push({
      name,
      anchor: el,
      position: "inline",
      matchEnd: m.index + m[0].length,
    });
    matched = true;
  }
  if (matched) el.setAttribute(NAME_SEEN_ATTR, "1");
}

function scanPersonNames() {
  // Italic-with-affiliation runs first so its NAME_SEEN_ATTR markers are in
  // place when scanPersonHeadings checks for already-marked descendants.
  const candidates = [...scanItalicNames(), ...scanPersonHeadings()];
  if (candidates.length === 0) return;
  // Group by name so duplicates trigger one lookup but mark every position.
  const byName = new Map();
  for (const c of candidates) {
    if (!byName.has(c.name)) byName.set(c.name, []);
    byName.get(c.name).push(c);
  }
  dispatchNameLookup(byName);
}

// One in-flight request per name (concurrency cap of 4) so each marker
// appears as soon as its own lookup returns rather than waiting for the
// whole page's worth of names.
async function dispatchNameLookup(byName) {
  const names = [...byName.keys()];
  dbg("dispatchNameLookup: names =", names);
  const concurrency = 4;
  let i = 0;
  async function worker() {
    while (i < names.length) {
      const n = names[i++];
      try {
        const { ok, results } = await browser.runtime.sendMessage({
          type: "bulkLookupNames",
          names: [n],
        });
        const status = results?.[n];
        dbg("  lookup", n, "→", status);
        if (ok && status?.matchType === "exact") {
          for (const c of byName.get(n) ?? []) injectNameMarker(c, status);
        }
      } catch (e) { dbg("  lookup failed", n, e?.message); }
    }
  }
  await Promise.all(Array.from(
    { length: Math.min(concurrency, names.length) }, worker
  ));
}

function injectNameMarker({ anchor, position, name, matchEnd }, status) {
  try {
    const marker = makeNameMarker(name, status);
    // Inline placement: walk the anchor's text-node children summing
    // textContent length until we reach the recorded matchEnd offset
    // (computed at scan time from em.textContent), then splitText there
    // and insert the marker between the halves. Order-independent — works
    // even when an earlier name's marker has already split the text node.
    if (position === "inline" && matchEnd != null) {
      let offset = 0;
      for (const node of [...anchor.childNodes]) {
        if (node.nodeType !== Node.TEXT_NODE) continue;
        const len = node.length;
        if (offset + len >= matchEnd) {
          const splitOffset = matchEnd - offset;
          if (splitOffset <= 0) {
            anchor.insertBefore(marker, node);
          } else if (splitOffset >= len) {
            anchor.insertBefore(marker, node.nextSibling);
          } else {
            const tail = node.splitText(splitOffset);
            anchor.insertBefore(marker, tail);
          }
          return;
        }
        offset += len;
      }
      // Walk didn't reach matchEnd (em mutated since scan?) — fall through
      // to "after element" placement so the marker still appears somewhere.
    }
    if (position === "after" || position === "inline") {
      const parent = anchor.parentElement;
      if (!parent || !parent.contains(anchor)) return;
      parent.insertBefore(marker, anchor.nextSibling);
    } else {
      anchor.appendChild(marker);
    }
  } catch { /* DOM detached during async lookup */ }
}

function makeNameMarker(name, status) {
  const span = document.createElement("span");
  span.className = "onl-marker onl-name-marker";
  span.dataset.onlName = name;
  span.title = `In OneNote (${status.total}) — ${name}`;
  span.style.cssText = [
    "display: inline-block",
    "width: 16px",
    "height: 16px",
    "margin-left: 6px",
    "background-image: url(" + browser.runtime.getURL(ICONS.exact) + ")",
    "background-size: contain",
    "background-repeat: no-repeat",
    "background-position: center",
    "cursor: pointer",
    "vertical-align: middle",
  ].join("; ");
  span.addEventListener("click", e => {
    e.preventDefault();
    e.stopPropagation();
    showOverlayForName(name);
  });
  return span;
}

function showOverlayForName(name) {
  ensureOverlayHost();
  const qs = new URLSearchParams({ name, title: name, embedded: "1" });
  overlayIframe.src = browser.runtime.getURL(`popup.html?${qs.toString()}`);
  overlayHost.style.pointerEvents = "auto";
  overlayBackdrop.style.display   = "block";
  overlayIframe.style.display     = "block";
  document.addEventListener("keydown", escListener);
}

// ── Dispatcher ───────────────────────────────────────────────────────────────

const HOST_HANDLERS = {
  "github.com":            () => { scanGithub(); scanGithubProfile(); scanGithubRepoList(); },
  "alternativeto.net":     scanAlternativeTo,
  "www.researchgate.net":  scanResearchGate,
  "www.linkedin.com":      scanLinkedIn,
  "linkedin.com":          scanLinkedIn,
  "www.mdpi.com":          scanMdpi,
  "mdpi.com":              scanMdpi,
  "www.zeit.de":           scanZeit,
  "zeit.de":               scanZeit,
  "arxiv.org":             scanArxiv,
  "www.arxiv.org":         scanArxiv,
};

// Pattern-matched hosts. Used for sites with many country-code TLDs (e.g.
// google.com / google.de / google.co.uk / …) where enumerating every
// hostname in HOST_HANDLERS isn't practical.
const HOST_PATTERN_HANDLERS = [
  { match: /^(www\.)?google\.[a-z.]{2,}$/, handler: scanGoogle },
];

// ── Site: link.springer.com ──────────────────────────────────────────────────

// The journal masthead ("Software and Systems Modeling" over its cover
// image) links to the journal home (`/journal/<id>`) via
// `a[data-test="journal-heading-href"]`, but it sits *outside* <main>, so
// the generic content-link scanner never marks it — the user only saw
// bubbles down in the submission-guidelines body. Anchor a marker on the
// journal-heading title so the journal itself gets a bubble at the top of
// every journal sub-page (submission-guidelines, articles, updates, …).
//
// Deliberately NOT registered in HOST_HANDLERS: that would flip
// scanContentLinks to the narrow container allowlist (dropping <main>),
// killing the body-prose bubbles that already work here. Called directly
// from scan() instead, like scanBibtex.
function scanSpringer() {
  const heading = document.querySelector(
    `a[data-test="journal-heading-href"]:not([${SEEN_ATTR}])`
  );
  if (!heading) return;
  const href = heading.getAttribute("href") || "";
  let url;
  try { url = new URL(href, location.origin).href; } catch { return; }
  heading.setAttribute(SEEN_ATTR, "1");
  try {
    const anchor = escapeAnchorAncestor(heading);
    const parent = anchor.parentElement;
    if (!parent || !parent.contains(anchor)) throw new Error("heading detached");
    const marker = makeMarker(url, (heading.textContent ?? "").trim() || url);
    marker.style.marginLeft  = "6px";
    marker.style.marginRight = "0";
    parent.insertBefore(marker, anchor.nextSibling);
    dispatchLookup([{ url, marker }]);   // eager — one marker, wanted immediately
  } catch {
    heading.removeAttribute(SEEN_ATTR);
  }
}

function scan() {
  const handler = HOST_HANDLERS[location.hostname];
  if (handler) handler();
  if (location.hostname === "link.springer.com") scanSpringer();
  for (const { match, handler: h } of HOST_PATTERN_HANDLERS) {
    if (match.test(location.hostname)) { h(); break; }
  }
  // BibTeX block scanner runs before the generic content scanner so the
  // <pre> blocks get their floated marker before scanContentLinks claims
  // the parent container.
  scanBibtex();
  // Generic content-area scan runs after host scanners so any element they
  // already marked (SEEN_ATTR) is silently skipped — host scanners are more
  // precise (canonical URL synthesis, slug dedup) and own the placement.
  scanContentLinks();
  // Run last so items already covered by URL markers can be skipped by the
  // name scan (collectItemsFromContainer filters them out).
  scanPersonNames();
}

// Throttle (not debounce) — React-heavy pages like AlternativeTo fire
// mutations continuously while lazy-loading more cards, which would keep
// resetting a debounce timer indefinitely. With a throttle, scan runs at
// least every 200 ms while activity is ongoing.
let scanTimer = null;
function scheduleScan() {
  if (scanTimer) return;
  scanTimer = setTimeout(() => {
    scanTimer = null;
    scan();
  }, 200);
}

// In-page markers depend on OneNote lookups; suppress the entire scanner
// (and existing markers, if any) while the adapter is disabled.
let onenoteEnabled = true;
let scanObserver   = null;

function startScanning() {
  if (scanObserver) return;
  scanObserver = new MutationObserver(scheduleScan);
  if (!document.hidden) {
    scanObserver.observe(document.documentElement, { childList: true, subtree: true });
    scheduleScan();
  }
}

// [impl->req~ank.cache.bounded~1]
// Hidden tabs keep mutating (feeds, tickers, ads) — rescanning each of
// them at up to 5 Hz multiplies allocation churn across every background
// tab. Observe only while visible; one rescan on return catches up.
document.addEventListener("visibilitychange", () => {
  if (!scanObserver) return;
  if (document.hidden) {
    scanObserver.disconnect();
  } else {
    scanObserver.observe(document.documentElement, { childList: true, subtree: true });
    scheduleScan();
  }
});

function stopScanning() {
  if (scanObserver) { scanObserver.disconnect(); scanObserver = null; }
  for (const el of document.querySelectorAll(".onl-marker")) el.remove();
  for (const el of document.querySelectorAll(`[${SEEN_ATTR}]`)) el.removeAttribute(SEEN_ATTR);
}

let anchorhubPaused = false;

function applyScanState() {
  const shouldScan = onenoteEnabled && !anchorhubPaused;
  if (shouldScan) startScanning(); else stopScanning();
}

browser.storage.local.get(["adapterSettings", "anchorhubPaused"]).then(s => {
  onenoteEnabled  = (s.adapterSettings?.onenote ?? true);
  anchorhubPaused = !!s.anchorhubPaused;
  applyScanState();
});

browser.storage.onChanged.addListener((changes, area) => {
  if (area !== "local") return;
  let touched = false;
  if (changes.adapterSettings) {
    const next = (changes.adapterSettings.newValue?.onenote ?? true);
    if (next !== onenoteEnabled) { onenoteEnabled = next; touched = true; }
  }
  if (changes.anchorhubPaused) {
    const next = !!changes.anchorhubPaused.newValue;
    if (next !== anchorhubPaused) { anchorhubPaused = next; touched = true; }
  }
  if (touched) applyScanState();
});

// ── Overlay ──────────────────────────────────────────────────────────────────

let overlayHost     = null;
let overlayShadow   = null;
let overlayBackdrop = null;
let overlayIframe   = null;
let escListener     = null;

function ensureOverlayHost() {
  if (overlayHost) return;

  overlayHost = document.createElement("div");
  overlayHost.id = "onl-overlay-host";
  overlayHost.style.cssText = [
    "all: initial",
    "position: fixed",
    "inset: 0",
    "z-index: 2147483647",
    "pointer-events: none",
  ].join("; ");
  overlayShadow = overlayHost.attachShadow({ mode: "closed" });

  overlayBackdrop = document.createElement("div");
  overlayBackdrop.style.cssText = [
    "position: absolute",
    "inset: 0",
    "background: rgba(0,0,0,0.15)",
    "pointer-events: auto",
    "display: none",
  ].join("; ");
  overlayBackdrop.addEventListener("click", hideOverlay);
  overlayShadow.appendChild(overlayBackdrop);

  overlayIframe = document.createElement("iframe");
  overlayIframe.style.cssText = [
    "position: absolute",
    "top: 50%",
    "left: 50%",
    "transform: translate(-50%, -50%)",
    "width: 360px",
    "height: 480px",
    "max-height: 80vh",
    "border: 1px solid rgba(0,0,0,0.3)",
    "border-radius: 6px",
    "box-shadow: 0 8px 32px rgba(0,0,0,0.3)",
    "background: white",
    "pointer-events: auto",
    "display: none",
  ].join("; ");
  overlayIframe.addEventListener("click", e => e.stopPropagation());
  overlayShadow.appendChild(overlayIframe);

  document.documentElement.appendChild(overlayHost);

  escListener = e => { if (e.key === "Escape") hideOverlay(); };
}

function showOverlay(url, titleHint) {
  ensureOverlayHost();
  const qs = new URLSearchParams({ url, title: titleHint ?? url, embedded: "1" });
  overlayIframe.src = browser.runtime.getURL(`popup.html?${qs.toString()}`);
  overlayHost.style.pointerEvents = "auto";
  overlayBackdrop.style.display   = "block";
  overlayIframe.style.display     = "block";
  document.addEventListener("keydown", escListener);
}

function hideOverlay() {
  if (!overlayHost) return;
  overlayHost.style.pointerEvents = "none";
  overlayBackdrop.style.display   = "none";
  overlayIframe.style.display     = "none";
  overlayIframe.src = "about:blank";
  document.removeEventListener("keydown", escListener);
}
