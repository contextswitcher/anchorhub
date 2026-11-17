// JabRef fulltext-fetch handler (extension side).
//
// The hub (ankd) sends `fetchFulltext` requests over the native-messaging
// channel when JabRef asks for a PDF. This module owns the FIFO queue,
// per-publisher serialisation, tab/download lifecycle, and abort
// propagation. It returns responses by `port.postMessage({ requestId, ... })`
// — the read loop on the hub side demuxes by `requestId`.
//
// Loaded by background.js (Firefox: background.scripts entry; Chrome:
// importScripts at the top of the service worker). The public surface is
// `globalThis.AnchorFulltext.handleHubMessage(msg, port)` — background.js
// calls it for every incoming message that carries an `action` field.
//
// Protocol contract: docs/requirements/browser-extension-fulltext.md
//
// [impl->req~bxf.concurrency-cap~1]
// [impl->req~bxf.cancellation~1]

"use strict";

if (typeof browser === "undefined" && typeof chrome !== "undefined") {
  globalThis.browser = chrome;
}

const FT_GLOBAL_CAP    = 3;     // max concurrent in-flight fetches across all hosts
const FT_PER_HOST_CAP  = 1;     // max concurrent per publisher host
const FT_QUEUE_DEPTH   = 50;    // queued + in-flight admission limit; over -> busy
// SSO chain wait must fit inside ankd's 90 s safety valve. Silent IdP
// re-auth typically settles within a few seconds; if SSO needs
// interactive password entry, the wait will time out and we surface
// auth-required so the user can retry after signing in manually.
const FT_SSO_TIMEOUT_MS  = 30_000;
// Window for a page-triggered download to start after the adapter
// signals clickedDownload.
const FT_PAGE_DL_TIMEOUT_MS = 15_000;

const ftPending = new Map(); // requestId -> job (queued or running)
const ftQueue   = [];        // jobs waiting for a slot (FIFO subset of ftPending)
const ftPerHost = new Map(); // publisherKey -> running count
let   ftGlobal  = 0;         // total running count

function ftHostHasSlot(host) {
  return (ftPerHost.get(host) || 0) < FT_PER_HOST_CAP;
}

function ftScheduleNext(port) {
  if (ftGlobal >= FT_GLOBAL_CAP) return;
  for (let i = 0; i < ftQueue.length; i++) {
    const job = ftQueue[i];
    if (!ftHostHasSlot(job.publisherKey)) continue;
    ftQueue.splice(i, 1);
    ftGlobal++;
    ftPerHost.set(job.publisherKey, (ftPerHost.get(job.publisherKey) || 0) + 1);
    ftRun(job, port).finally(() => {
      ftGlobal = Math.max(0, ftGlobal - 1);
      ftPerHost.set(job.publisherKey, Math.max(0, (ftPerHost.get(job.publisherKey) || 0) - 1));
      ftPending.delete(job.requestId);
      ftScheduleNext(port);
    });
    if (ftGlobal >= FT_GLOBAL_CAP) return;
    i--;
  }
}

function ftReply(port, requestId, payload) {
  console.log(`[FT] -> reply requestId=${requestId}`, payload);
  if (payload && typeof payload.path === "string") {
    // Devtools renders file:// URLs as clickable links; native Windows
    // paths (C:\...) do not get auto-linked.
    const fileUrl = "file:///" + payload.path.replace(/\\/g, "/");
    console.log(`[FT]    file:`, fileUrl);
  }
  try { port.postMessage({ requestId, ...payload }); }
  catch (e) { console.warn("[FT] postMessage failed:", e); }
}

function ftReplyError(port, requestId, code, message) {
  ftReply(port, requestId, { error: code, message });
}

function ftWaitTabComplete(tabId, signal) {
  return new Promise((resolve, reject) => {
    if (signal.aborted) return reject(new Error("aborted"));
    const onUpdated = (id, info) => {
      if (id !== tabId) return;
      if (info.status === "complete") {
        cleanup();
        resolve();
      }
    };
    const onRemoved = (id) => {
      if (id !== tabId) return;
      cleanup();
      reject(new Error("tab closed"));
    };
    const onAbort = () => { cleanup(); reject(new Error("aborted")); };
    function cleanup() {
      browser.tabs.onUpdated.removeListener(onUpdated);
      browser.tabs.onRemoved.removeListener(onRemoved);
      signal.removeEventListener("abort", onAbort);
    }
    browser.tabs.onUpdated.addListener(onUpdated);
    browser.tabs.onRemoved.addListener(onRemoved);
    signal.addEventListener("abort", onAbort, { once: true });

    // Tab may have finished loading before this listener subscribed —
    // tabs.create resolves quickly and the page can race ahead. Probe
    // the current status; if already complete, resolve immediately.
    browser.tabs.get(tabId).then(tab => {
      if (tab && tab.status === "complete") {
        cleanup();
        resolve();
      }
    }).catch(() => { /* tab gone; onRemoved will fire */ });
  });
}

/// Opens a tab, runs the publisher adapter, returns
/// { pdfUrl, tabId, pageDlPromise }. The pageDlPromise is pre-subscribed
/// to downloads.onCreated filtered by the original URL's host before the
/// adapter runs — so an adapter that clicks the page's own Download
/// button (returning { clickedDownload: true }) does not race the
/// subscription. Caller awaits pageDlPromise iff the adapter signalled
/// clickedDownload; otherwise it is left to time out silently.
async function ftTryFetch(url, scriptName, active, signal) {
  if (signal.aborted) {
    return { pdfUrl: null, tabId: null, pageDlPromise: Promise.reject(new Error("aborted")) };
  }
  const hostFilter = (() => { try { return new URL(url).host; } catch { return ""; } })();
  const pageDlPromise = ftWaitForPageDownload(hostFilter, signal, FT_PAGE_DL_TIMEOUT_MS);
  // Swallow unhandled rejection when the caller chooses not to await it.
  pageDlPromise.catch(() => {});

  let tab;
  try {
    tab = await browser.tabs.create({ url, active });
  } catch (e) {
    console.warn("[FT] tabs.create failed:", e);
    return { pdfUrl: null, tabId: null, pageDlPromise };
  }
  console.log(`[FT] tab ${tab.id} opened (active=${active}) for ${url}`);
  try {
    await ftWaitTabComplete(tab.id, signal);
    if (signal.aborted) return { pdfUrl: null, tabId: tab.id, pageDlPromise };
    const result = await browser.scripting.executeScript({
      target: { tabId: tab.id },
      files:  [`adapters/${scriptName}.js`],
    });
    const pdfUrl = result?.[0]?.result || null;
    console.log(`[FT] adapter ${scriptName} on tab ${tab.id} ->`, pdfUrl);
    return { pdfUrl, tabId: tab.id, pageDlPromise };
  } catch (e) {
    if (!signal.aborted) console.warn(`[FT] tryFetch error on tab ${tab.id}:`, e);
    return { pdfUrl: null, tabId: tab.id, pageDlPromise };
  }
}

async function ftCloseTab(tabId) {
  if (tabId == null) return;
  try { await browser.tabs.remove(tabId); } catch {}
}

/// Waits for `browser.downloads.onCreated` to fire for a download
/// whose URL matches the given host. Used when the adapter clicks a
/// page-level "Download" button and the page itself starts the
/// download — we cannot construct the URL ourselves.
function ftWaitForPageDownload(hostFilter, signal, maxMs) {
  return new Promise((resolve, reject) => {
    if (signal.aborted) return reject(new Error("aborted"));
    const deadline = setTimeout(() => { cleanup(); reject(new Error("page-download-timeout")); }, maxMs);
    const onCreated = (item) => {
      try {
        const u = new URL(item.url);
        if (hostFilter && u.host.indexOf(hostFilter) === -1) return;
      } catch { return; }
      cleanup();
      resolve(item);
    };
    const onAbort = () => { cleanup(); reject(new Error("aborted")); };
    function cleanup() {
      browser.downloads.onCreated.removeListener(onCreated);
      signal.removeEventListener("abort", onAbort);
      clearTimeout(deadline);
    }
    browser.downloads.onCreated.addListener(onCreated);
    signal.addEventListener("abort", onAbort, { once: true });
  });
}

/// Waits for the tab to navigate back to a URL on the original
/// publisher host (matches the document-id path of `originalUrl`).
/// Used after the adapter signals authRequired and auto-clicks an
/// SSO button — the SSO chain typically lands the user back on the
/// original page once authentication succeeds.
function ftWaitForOriginalUrlSettled(tabId, originalUrl, signal, maxMs) {
  return new Promise((resolve, reject) => {
    if (signal.aborted) return reject(new Error("aborted"));
    let originalOrigin;
    let originalPath;
    try {
      const u = new URL(originalUrl);
      originalOrigin = u.origin;
      originalPath   = u.pathname;
    } catch {
      return reject(new Error("originalUrl unparseable"));
    }
    const deadline = setTimeout(() => { cleanup(); reject(new Error("sso-timeout")); }, maxMs);
    const onUpdated = (id, info, tab) => {
      if (id !== tabId) return;
      if (info.status !== "complete") return;
      const url = tab.url || info.url;
      if (!url) return;
      try {
        const u = new URL(url);
        if (u.origin === originalOrigin && u.pathname === originalPath) {
          cleanup();
          resolve();
        }
      } catch { /* ignore unparseable transient URLs */ }
    };
    const onAbort = () => { cleanup(); reject(new Error("aborted")); };
    function cleanup() {
      browser.tabs.onUpdated.removeListener(onUpdated);
      signal.removeEventListener("abort", onAbort);
      clearTimeout(deadline);
    }
    browser.tabs.onUpdated.addListener(onUpdated);
    signal.addEventListener("abort", onAbort, { once: true });
  });
}

function ftWaitDownload(dlId, signal) {
  return new Promise((resolve, reject) => {
    if (signal.aborted) return reject(new Error("aborted"));
    const onChanged = async (delta) => {
      if (delta.id !== dlId) return;
      const state = delta.state?.current;
      if (state === "complete") {
        cleanup();
        try {
          const items = await browser.downloads.search({ id: dlId });
          resolve(items?.[0]);
        } catch (e) { reject(e); }
      } else if (state === "interrupted") {
        cleanup();
        reject(new Error("download interrupted"));
      }
    };
    const onAbort = () => {
      cleanup();
      browser.downloads.cancel(dlId).catch(() => {});
      reject(new Error("aborted"));
    };
    function cleanup() {
      browser.downloads.onChanged.removeListener(onChanged);
      signal.removeEventListener("abort", onAbort);
    }
    browser.downloads.onChanged.addListener(onChanged);
    signal.addEventListener("abort", onAbort, { once: true });
  });
}

async function ftRun(job, port) {
  const { requestId, url, contentScriptName, abortController } = job;
  const signal = abortController.signal;

  console.log(`[FT] start job ${requestId} ${contentScriptName} ${url}`);

  // Tabs opened during this job. Only closed on full success; left open
  // otherwise so the user can inspect what the adapter saw.
  const openedTabs = [];
  let succeeded = false;

  try {
    // Two-pass strategy: background tab first (does not steal focus); if
    // the adapter found nothing, retry once with the tab visible — covers
    // lazy-rendered content that needs a viewport.
    let { pdfUrl, tabId, pageDlPromise } = await ftTryFetch(url, contentScriptName, false, signal);
    if (tabId != null) openedTabs.push(tabId);

    // Adapter clicked the page's own download button and the page is
    // (or will be) starting the download. Capture it via the
    // pre-subscribed downloads.onCreated promise.
    if (pdfUrl && typeof pdfUrl === "object" && pdfUrl.clickedDownload) {
      try {
        const created = await pageDlPromise;
        console.log(`[FT] page download started:`, created);
        const item = await ftWaitDownload(created.id, signal);
        if (signal.aborted) return;
        console.log("[FT] download item:", item);
        const mime = (item?.mime ?? "").toLowerCase();
        if (!mime.includes("pdf")) {
          ftReplyError(port, requestId, "no-pdf-found",
            `download did not yield a PDF (mime=${item?.mime || "none"})`);
          return;
        }
        succeeded = true;
        ftReply(port, requestId, {
          id:        requestId,
          path:      item?.filename || null,
          sourceUrl: created.url,
        });
        return;
      } catch (e) {
        console.warn(`[FT] page download not observed for job ${requestId}:`, e?.message || e);
        ftReplyError(port, requestId, "no-pdf-found",
          "page-triggered download did not start within timeout");
        return;
      }
    }

    // Adapter signalled the page needs interactive sign-in (e.g.
    // institutional SSO). Focus the tab so the user can complete the
    // flow, then wait for the SSO chain to land back on the original
    // publisher URL. Once it does, re-run the adapter — by then the
    // session is established and the adapter should find a real PDF
    // link without the user having to click anything in JabRef.
    if (pdfUrl && typeof pdfUrl === "object" && pdfUrl.authRequired) {
      console.log(`[FT] auth required for job ${requestId}; focusing tab ${tabId} and waiting for SSO chain`);
      if (tabId != null) {
        try { await browser.tabs.update(tabId, { active: true }); } catch {}
      }
      try {
        await ftWaitForOriginalUrlSettled(tabId, url, signal, FT_SSO_TIMEOUT_MS);
        console.log(`[FT] SSO chain returned to original URL; re-running adapter on tab ${tabId}`);

        // Pre-subscribe so we don't race the page-triggered download.
        const hostFilter = (() => { try { return new URL(url).host; } catch { return ""; } })();
        const dlPromise = ftWaitForPageDownload(hostFilter, signal, FT_PAGE_DL_TIMEOUT_MS);

        const second = await browser.scripting.executeScript({
          target: { tabId },
          files:  [`adapters/${contentScriptName}.js`],
        });
        pdfUrl = second?.[0]?.result || null;
        console.log(`[FT] post-SSO adapter result on tab ${tabId} ->`, pdfUrl);

        if (pdfUrl && typeof pdfUrl === "object" && pdfUrl.clickedDownload) {
          console.log(`[FT] adapter clicked page download; awaiting downloads.onCreated`);
          const created = await dlPromise;
          console.log(`[FT] page download started:`, created);
          const item = await ftWaitDownload(created.id, signal);
          if (signal.aborted) return;
          console.log("[FT] download item:", item);
          const mime = (item?.mime ?? "").toLowerCase();
          if (!mime.includes("pdf")) {
            ftReplyError(port, requestId, "no-pdf-found",
              `download did not yield a PDF (mime=${item?.mime || "none"})`);
            return;
          }
          succeeded = true;
          ftReply(port, requestId, {
            id:        requestId,
            path:      item?.filename || null,
            sourceUrl: created.url,
          });
          return;
        }

        if (pdfUrl && typeof pdfUrl === "object" && pdfUrl.authRequired) {
          console.warn(`[FT] still auth-required after SSO settle (job ${requestId})`);
          ftReplyError(port, requestId, "auth-required",
            "sign-in did not yield access; complete it in the focused tab, then retry");
          return;
        }
        if (!pdfUrl) {
          ftReplyError(port, requestId, "no-pdf-found",
            "adapter found no PDF link after sign-in");
          return;
        }
        // Fall through to download with the freshly-resolved pdfUrl.
      } catch (e) {
        console.warn(`[FT] SSO wait failed for job ${requestId}:`, e?.message || e);
        ftReplyError(port, requestId, "auth-required",
          "sign-in did not complete within timeout; retry after signing in manually");
        return;
      }
    }

    if (!pdfUrl && !signal.aborted) {
      const retry = await ftTryFetch(url, contentScriptName, true, signal);
      pdfUrl = retry.pdfUrl;
      if (retry.tabId != null) openedTabs.push(retry.tabId);

      // clickedDownload on the visible retry — same capture logic as
      // the background-tab branch.
      if (pdfUrl && typeof pdfUrl === "object" && pdfUrl.clickedDownload) {
        try {
          const created = await retry.pageDlPromise;
          console.log(`[FT] page download started (visible retry):`, created);
          const item = await ftWaitDownload(created.id, signal);
          if (signal.aborted) return;
          const mime = (item?.mime ?? "").toLowerCase();
          if (!mime.includes("pdf")) {
            ftReplyError(port, requestId, "no-pdf-found",
              `download did not yield a PDF (mime=${item?.mime || "none"})`);
            return;
          }
          succeeded = true;
          ftReply(port, requestId, {
            id:        requestId,
            path:      item?.filename || null,
            sourceUrl: created.url,
          });
          return;
        } catch (e) {
          console.warn(`[FT] page download not observed (visible retry):`, e?.message || e);
          ftReplyError(port, requestId, "no-pdf-found",
            "page-triggered download did not start within timeout");
          return;
        }
      }

      // Same SSO wait-and-rerun handling on the visible retry.
      if (pdfUrl && typeof pdfUrl === "object" && pdfUrl.authRequired) {
        console.log(`[FT] auth required for job ${requestId} (visible retry); waiting for SSO chain on tab ${retry.tabId}`);
        try {
          await ftWaitForOriginalUrlSettled(retry.tabId, url, signal, FT_SSO_TIMEOUT_MS);
          const second = await browser.scripting.executeScript({
            target: { tabId: retry.tabId },
            files:  [`adapters/${contentScriptName}.js`],
          });
          pdfUrl = second?.[0]?.result || null;
          console.log(`[FT] post-SSO adapter result (visible) on tab ${retry.tabId} ->`, pdfUrl);
          if (pdfUrl && typeof pdfUrl === "object" && pdfUrl.authRequired) {
            ftReplyError(port, requestId, "auth-required",
              "sign-in did not yield access; complete it in the focused tab, then retry");
            return;
          }
          if (!pdfUrl) {
            ftReplyError(port, requestId, "no-pdf-found",
              "adapter found no PDF link after sign-in");
            return;
          }
        } catch (e) {
          console.warn(`[FT] SSO wait failed for job ${requestId} (visible):`, e?.message || e);
          ftReplyError(port, requestId, "auth-required",
            "sign-in did not complete within timeout; retry after signing in manually");
          return;
        }
      }
    }
    if (signal.aborted) return;
    if (!pdfUrl) {
      console.warn(`[FT] no PDF link for job ${requestId} — leaving ${openedTabs.length} tab(s) open for inspection`);
      ftReplyError(port, requestId, "no-pdf-found", "adapter found no PDF link");
      return;
    }

    console.log(`[FT] downloading ${pdfUrl}`);
    const filename = `anchorhub/${requestId}.pdf`;
    const dlId = await browser.downloads.download({ url: pdfUrl, filename, saveAs: false });
    const item = await ftWaitDownload(dlId, signal);
    if (signal.aborted) return;

    // A "complete" download is not necessarily a PDF — publishers often
    // 200 OK an HTML error / paywall page. The local filename is always
    // .pdf (we chose it), so it cannot be used as a check. Rely on the
    // mime detected by the browser; absent or non-PDF mime = treat as
    // failure and leave the tab open so the user can inspect what came
    // back.
    console.log("[FT] download item:", item);
    const mime = (item?.mime ?? "").toLowerCase();
    if (!mime.includes("pdf")) {
      console.warn(`[FT] download for ${requestId} is not a PDF (mime=${item?.mime || "(none)"}); treating as failure`);
      ftReplyError(port, requestId, "no-pdf-found",
        `download did not yield a PDF (mime=${item?.mime || "none"})`);
      return;
    }

    succeeded = true;
    console.log(`[FT] success job ${requestId} -> ${item?.filename}`);
    ftReply(port, requestId, {
      id:        requestId,
      path:      item?.filename || null,
      sourceUrl: pdfUrl,
    });
  } catch (e) {
    if (signal.aborted) return;
    console.warn(`[FT] ftRun error for job ${requestId}:`, e);
    ftReplyError(port, requestId, "internal-error", e?.message || String(e));
  } finally {
    if (succeeded) {
      for (const tabId of openedTabs) await ftCloseTab(tabId);
    } else if (openedTabs.length) {
      console.log(`[FT] leaving ${openedTabs.length} tab(s) open for debugging (job ${requestId})`);
    }
  }
}

function ftHandleFetch(msg, port) {
  const requestId = msg.requestId;
  if (!requestId) {
    console.warn("[FT] fetchFulltext without requestId; dropping.");
    return;
  }
  if (ftPending.size >= FT_QUEUE_DEPTH) {
    ftReplyError(port, requestId, "busy", "queue full");
    return;
  }
  const url = msg.url;
  const contentScriptName = msg.contentScriptName;
  if (!url || !contentScriptName) {
    ftReplyError(port, requestId, "internal-error", "missing url or contentScriptName");
    return;
  }
  let publisherKey = msg.publisherKey;
  if (!publisherKey) {
    try { publisherKey = new URL(url).host; }
    catch { publisherKey = "_unknown_"; }
  }
  const job = {
    requestId,
    url,
    publisherKey,
    contentScriptName,
    abortController: new AbortController(),
  };
  ftPending.set(requestId, job);
  ftQueue.push(job);
  ftScheduleNext(port);
}

function ftHandleAbort(msg) {
  const requestId = msg.requestId;
  if (!requestId) return;
  const job = ftPending.get(requestId);
  if (!job) return;
  job.abortController.abort();
  // If still queued (never ran), remove proactively — ftRun's finally
  // does the bookkeeping for running jobs.
  const idx = ftQueue.findIndex((j) => j.requestId === requestId);
  if (idx >= 0) {
    ftQueue.splice(idx, 1);
    ftPending.delete(requestId);
  }
}

globalThis.AnchorFulltext = {
  // Returns true when the message was a hub-initiated fulltext action
  // and has been handled; background.js should then stop further
  // processing for this message.
  handleHubMessage(msg, port) {
    if (msg?.action === "fetchFulltext") { ftHandleFetch(msg, port); return true; }
    if (msg?.action === "abortFulltext") { ftHandleAbort(msg);       return true; }
    return false;
  },
};
