"use strict";

// Cross-browser shim — see background.js for rationale.
if (typeof browser === "undefined" && typeof chrome !== "undefined") {
  globalThis.browser = chrome;
}

const SECTION_LIMIT = 9;
const content       = document.getElementById("content");
const metaSection   = document.getElementById("meta-section");
const csSection     = document.getElementById("contextswitcher-section");
const bibtexSection = document.getElementById("bibtex-section");
const jabrefSection = document.getElementById("jabref-section");
const refreshBtn    = document.getElementById("btn-refresh");
const settingsBtn   = document.getElementById("btn-settings");
const copyBtn       = document.getElementById("btn-copy");

// Filled by load(); both default true so the very first render before
// the settings response arrives mirrors the production-default state.
let adapterSettings = { onenote: true, jabref: true, contextswitcher: true };
let paused = false;

// In-page overlay (content script) loads popup.html?url=…&title=…&embedded=1
// or popup.html?name=…&embedded=1 to render the popup against an arbitrary
// URL or person name instead of the active tab.
const params         = new URLSearchParams(location.search);
const overrideUrl    = params.get("url");
const overrideName   = params.get("name");
const overrideTitle  = params.get("title") ?? "";
const isEmbedded     = params.get("embedded") === "1" || !!overrideUrl || !!overrideName;
const isNameMode     = !!overrideName;
const overrideFields = overrideName
  ? { name: overrideName, title: overrideTitle || overrideName }
  : overrideUrl
    ? { url: overrideUrl, title: overrideTitle }
    : {};

if (isEmbedded) {
  // No DOI detection runs for synthetic URLs; JabRef integration is hidden
  // in v1 of the in-page overlay. BibTeX-on-page detection runs against
  // the live tab, not a synthesised URL, so it is hidden too.
  jabrefSection.remove();
  bibtexSection.remove();
}

settingsBtn.addEventListener("click", () => {
  browser.runtime.openOptionsPage();
  window.close();
});

copyBtn.addEventListener("click", async () => {
  if (!currentUrl) return;
  try {
    await navigator.clipboard.writeText(currentUrl);
    copyBtn.textContent = "Copied";
    copyBtn.disabled    = true;
    setTimeout(() => {
      copyBtn.textContent = "Copy link";
      copyBtn.disabled    = false;
    }, 1500);
  } catch {
    copyBtn.textContent = "Copy failed";
  }
});

// Standard reload shortcuts (Ctrl+R / Cmd+R / F5) trigger the same
// refresh as the button. Use the capture phase so the event is consumed
// before the browser's chrome-level reload handler closes the popup and
// reloads the underlying tab.
addEventListener("keydown", e => {
  const reload = e.key === "F5"
              || ((e.ctrlKey || e.metaKey) && e.code === "KeyR");
  if (!reload || refreshBtn.disabled) return;
  e.preventDefault();
  refreshBtn.click();
}, true);

// URL the "Copy link" button writes to the clipboard. Captured from the
// most recent getMatches/refresh response so it stays in sync with what
// the popup is currently showing.
let currentUrl = "";

browser.runtime.sendMessage({ type: "getSettings" })
  .then(resp => {
    if (resp?.settings) adapterSettings = resp.settings;
    paused = !!resp?.paused;
  })
  .catch(() => {})
  .then(() => {
    if (paused) { renderPaused(); return; }
    return browser.runtime.sendMessage({ type: "getMatches", ...overrideFields })
      .then(renderAll);
  });

function renderAll({ matches, total, doi, jabref, bibtex, contextswitcher, tabTitle, tabUrl }) {
  currentUrl = tabUrl ?? "";
  render(matches, total);
  renderContextSwitcher(contextswitcher);
  if (!isEmbedded) {
    renderBibtex(bibtex);
    renderJabRef(doi, jabref, tabTitle, tabUrl);
  }
}

// [impl->req~ank.popup.paused~1]
// Paused via the toolbar context menu: one line instead of misleading
// "not found" / "offline" sections.
function renderPaused() {
  content.innerHTML = "";
  const p = document.createElement("p");
  p.className = "no-match";
  p.innerHTML = "<b>✗ Disabled</b> — AnchorHub is paused. Right-click the toolbar icon to resume.";
  content.appendChild(p);
  for (const s of [csSection, metaSection, jabrefSection, bibtexSection]) if (s.isConnected) s.hidden = true;
  copyBtn.hidden      = true;
  refreshBtn.disabled = true;
}

refreshBtn.addEventListener("click", () => {
  content.innerHTML = '<p class="loading">Searching…</p>';
  if (!isEmbedded) {
    jabrefSection.hidden = true;
    bibtexSection.hidden = true;
  }
  copyBtn.hidden       = true;
  refreshBtn.disabled  = true;
  browser.runtime.sendMessage({ type: "refresh", ...overrideFields }).then(({ matches, total, doi, jabref, bibtex, contextswitcher, tabTitle, tabUrl }) => {
    currentUrl = tabUrl ?? "";
    render(matches, total);
    renderContextSwitcher(contextswitcher);
    if (!isEmbedded) {
      renderBibtex(bibtex);
      renderJabRef(doi, jabref, tabTitle, tabUrl);
    }
    refreshBtn.disabled = false;
  });
});

function statusChip(found) {
  const c = document.createElement("span");
  c.className = "status-chip " + (found ? "chip-found" : "chip-empty");
  c.textContent = found ? "found" : "not found";
  return c;
}

function render(matches, total) {
  content.innerHTML = "";
  content.classList.remove("section-found", "section-empty");

  if (!adapterSettings.onenote) {
    const p = document.createElement("p");
    p.className = "no-match";
    p.innerHTML = 'OneNote adapter is disabled. <a href="#" id="popup-open-settings">Open settings</a> to enable it.';
    content.appendChild(p);
    p.querySelector("#popup-open-settings").addEventListener("click", e => {
      e.preventDefault();
      browser.runtime.openOptionsPage();
      window.close();
    });
    return;
  }

  const exact    = (matches ?? []).filter(m => m.matchType === "exact");
  const hostOnly = (matches ?? []).filter(m => m.matchType === "host-only");
  const related  = (matches ?? []).filter(m => m.matchType === "related");
  const hasAny   = exact.length > 0 || hostOnly.length > 0 || related.length > 0;

  const header = document.createElement("div");
  header.className = "section-label onenote-header";
  header.textContent = isNameMode ? `Name: "${overrideName}"` : "OneNote";
  header.appendChild(statusChip(hasAny));
  content.appendChild(header);

  content.classList.add(hasAny ? "section-found" : "section-empty");

  if (!hasAny) {
    const p = document.createElement("p");
    p.className = "no-match";
    p.textContent = "Not found in OneNote.";
    content.appendChild(p);
  }

  if (exact.length > 0)    appendSection("Exact matches", exact,    total ?? matches.length);
  if (related.length > 0)  appendSection(isNameMode ? "Pages mentioning this name" : "Related", related, total ?? matches.length);
  if (hostOnly.length > 0) appendSection("Same site",     hostOnly, total ?? matches.length);

  // Footer "Copy link" is offered when this specific URL is not yet
  // anchored in OneNote. Name mode has no canonical URL to copy, so the
  // button stays hidden there.
  copyBtn.hidden = !(exact.length === 0 && !isNameMode && currentUrl);
}

function appendSection(label, matches, total) {
  const h = document.createElement("div");
  h.className = "section-label";
  h.textContent = label;
  content.appendChild(h);

  const visible  = matches.slice(0, SECTION_LIMIT);
  const overflow = (total ?? matches.length) - visible.length;

  for (const m of visible) {
    const fromGraph = m.source === "graph";
    const row = document.createElement("div");
    row.className = "match-row"
      + (m.matchType !== "exact" ? " host-only" : "")
      + (fromGraph                ? " from-graph" : "");
    if (m.searchTerm) {
      row.title = `Matched: ${m.searchTerm}${fromGraph ? "  (Microsoft Graph)" : ""}`;
    }
    const cloudBadge = fromGraph
      ? '<span class="cloud-badge" title="From Microsoft Graph (cloud)">☁</span>'
      : '';
    row.innerHTML = `
      <span class="match-title">${cloudBadge}${esc(m.title)}</span>
      <span class="match-meta">${esc(m.notebookName ?? "")} / ${esc(m.sectionName ?? "")}</span>
    `;
    row.addEventListener("click", () => {
      if (fromGraph) {
        // Graph match — open the OneNote desktop protocol URI (onenote:)
        // when available, fall back to the web client. browser.tabs.create
        // hands the URL to the OS protocol handler.
        const target = m.clientUrl || m.webUrl;
        if (target) browser.tabs.create({ url: target });
      } else {
        browser.runtime.sendMessage({ type: "focus", pageId: m.pageId, searchTerm: m.searchTerm });
      }
    });
    content.appendChild(row);
  }

  if (overflow > 0) {
    const more = document.createElement("div");
    more.className = "more-row";
    more.textContent = `and ${overflow} more… (open in OneNote)`;
    more.addEventListener("click", () => {
      browser.runtime.sendMessage({ type: "openSearch", ...overrideFields });
    });
    content.appendChild(more);
  }
}

// ── ContextSwitcher section ───────────────────────────────────────────────────
// Shows whether the current page belongs to a ContextSwitcher task (a URL in
// some task's browser.urls). When it does, offers two deep links into the
// ContextSwitcher app: "Switch to task" (contextswitcher://switch/<id>, runs
// the switch) and "Show in app" (contextswitcher://task/<id>, focus + select).
// contextswitcher is null when the adapter is off or the daemon is unreachable.

function renderContextSwitcher(cs) {
  csSection.innerHTML = "";
  csSection.hidden = true;
  csSection.classList.remove("section-found", "section-empty");

  if (!adapterSettings.contextswitcher) return;
  // Name mode (in-page overlay for a person name) has no URL to match.
  if (isNameMode) return;
  // null → adapter reported nothing / daemon offline: stay quiet.
  if (!cs) return;

  csSection.hidden = false;

  const found = !!cs.found;
  const header = document.createElement("div");
  header.className = "section-label contextswitcher-header";
  header.textContent = "ContextSwitcher";
  header.appendChild(statusChip(found));
  csSection.appendChild(header);
  csSection.classList.add(found ? "section-found" : "section-empty");

  if (!found) {
    const p = document.createElement("p");
    p.className = "no-match";
    p.textContent = "This page is not part of a ContextSwitcher task.";
    csSection.appendChild(p);
    return;
  }

  const task = cs.task ?? {};
  const taskId = task.taskId ?? "";

  const row = document.createElement("div");
  row.className = "cs-task-row";
  const title = document.createElement("div");
  title.className = "cs-task-title";
  title.textContent = task.title || taskId;
  const meta = document.createElement("div");
  meta.className = "cs-task-meta";
  meta.textContent = task.status && task.status !== "active"
    ? `${taskId} · ${task.status}`
    : taskId;
  row.appendChild(title);
  row.appendChild(meta);
  csSection.appendChild(row);

  const actions = document.createElement("div");
  actions.className = "cs-actions";

  const btnSwitch = document.createElement("button");
  btnSwitch.className = "cs-btn cs-btn-switch";
  btnSwitch.textContent = "Switch to task";
  btnSwitch.title = "Run this task's switch in ContextSwitcher (contextswitcher://switch/…)";
  btnSwitch.addEventListener("click", () => openCs("switch", taskId));

  const btnShow = document.createElement("button");
  btnShow.className = "cs-btn cs-btn-show";
  btnShow.textContent = "Show in app";
  btnShow.title = "Focus and select this task in ContextSwitcher (contextswitcher://task/…)";
  btnShow.addEventListener("click", () => openCs("task", taskId));

  actions.appendChild(btnSwitch);
  actions.appendChild(btnShow);
  csSection.appendChild(actions);
}

// Hands a contextswitcher:// deep link to the OS protocol handler (same route
// the Graph section uses for onenote: links). Task ids may contain "/" for
// group folders — keep the slashes, percent-encode each segment (per the
// planned grammar, contextswitcher#47).
function openCs(kind, taskId) {
  if (!taskId) return;
  const path = taskId.split("/").map(encodeURIComponent).join("/");
  browser.tabs.create({ url: `contextswitcher://${kind}/${path}` });
  window.close();
}

// ── BibTeX section ────────────────────────────────────────────────────────────
// Surfaces BibTeX blocks rendered on the current page (typical case: a
// "Citation" section in a GitHub README). Each entry's url is looked up
// in OneNote and its doi/eprint/url in JabRef; both result sets render
// here so the user can jump straight from the repo page to the cited
// paper's OneNote note or JabRef entry.

function renderBibtex(bibtex) {
  bibtexSection.innerHTML = "";
  bibtexSection.hidden = true;
  if (!bibtex || !bibtex.entries?.length) return;
  bibtexSection.hidden = false;

  const header = document.createElement("div");
  header.className = "section-label bibtex-header";
  header.textContent = "Reference";
  bibtexSection.appendChild(header);

  // Full BibTeX source for each detected entry. The raw text is captured
  // by the background parser so the popup can show exactly what the page
  // rendered. Future enhancement: also surface the BibTeX produced by
  // translators (e.g. the form that gets posted to JabRef).
  for (const entry of bibtex.entries) {
    if (!entry._raw) continue;
    const pre = document.createElement("pre");
    pre.className = "bibtex-source";
    pre.textContent = entry._raw;
    bibtexSection.appendChild(pre);
  }

  // OneNote matches across all BibTeX URLs.
  if (bibtex.onenoteMatches?.length) {
    const lbl = document.createElement("div");
    lbl.className = "section-sublabel";
    lbl.textContent = "In OneNote";
    bibtexSection.appendChild(lbl);
    for (const m of bibtex.onenoteMatches) {
      const row = document.createElement("div");
      row.className = "match-row";
      if (m.bibtexUrl) row.title = `Matched: ${m.bibtexUrl}`;
      row.innerHTML = `
        <span class="match-title">${esc(m.title)}</span>
        <span class="match-meta">${esc(m.notebookName ?? "")} / ${esc(m.sectionName ?? "")}</span>
      `;
      row.addEventListener("click", () => {
        browser.runtime.sendMessage({ type: "focus", pageId: m.pageId, searchTerm: m.bibtexUrl ?? m.searchTerm });
      });
      bibtexSection.appendChild(row);
    }
  }

  // JabRef matches (libraryId — citationKey rows).
  if (bibtex.jabrefMatches?.length) {
    const lbl = document.createElement("div");
    lbl.className = "section-sublabel";
    lbl.textContent = "In JabRef";
    bibtexSection.appendChild(lbl);
    for (const m of bibtex.jabrefMatches) {
      const row = document.createElement("div");
      row.className = "jabref-row jabref-confirmed";
      row.textContent = `${m.citationKey} — ${m.libraryId}`;
      bibtexSection.appendChild(row);
    }
  }
}

// ── JabRef section ────────────────────────────────────────────────────────────

function renderJabRef(doi, jabref, tabTitle, tabUrl) {
  jabrefSection.innerHTML = "";
  jabrefSection.hidden = true;
  jabrefSection.classList.remove("section-found", "section-empty");

  if (!adapterSettings.jabref) return;
  if (!tabUrl) return;

  jabrefSection.hidden = false;

  const found = !!jabref && jabref.matches.length > 0;

  const header = document.createElement("div");
  header.className = "section-label jabref-header";
  header.textContent = "JabRef";
  // Offline gets no chip — the "JabRef offline" line below says it.
  if (jabref) header.appendChild(statusChip(found));
  jabrefSection.appendChild(header);

  if (jabref) jabrefSection.classList.add(found ? "section-found" : "section-empty");

  if (!jabref) {
    const offline = document.createElement("p");
    offline.className = "jabref-offline";
    offline.textContent = "JabRef offline";
    jabrefSection.appendChild(offline);
    return;
  }

  // Phase 1: DOI page — show matches or "Add" button
  if (doi) {
    const doiBadge = document.createElement("div");
    doiBadge.className = "jabref-doi";
    doiBadge.textContent = doi;
    jabrefSection.appendChild(doiBadge);

    if (jabref.matches.length > 0) {
      for (const m of jabref.matches) {
        const row = document.createElement("div");
        row.className = "jabref-row" + (jabref.confirmed ? " jabref-confirmed" : " jabref-unconfirmed");
        row.textContent = `${m.citationKey} — ${m.libraryId}`;
        jabrefSection.appendChild(row);
      }
    } else if (jabref.confirmed) {
      renderAddToJabRef(doi);
    }
    return;
  }

  // Phase 2: No DOI — URL-based lookup result + @misc add form
  if (jabref.matches.length > 0) {
    for (const m of jabref.matches) {
      const row = document.createElement("div");
      row.className = "jabref-row" + (jabref.confirmed ? " jabref-confirmed" : " jabref-unconfirmed");
      row.textContent = `${m.citationKey} — ${m.libraryId}`;
      jabrefSection.appendChild(row);
    }
  } else if (jabref.confirmed) {
    renderMiscForm(tabTitle ?? "", tabUrl);
  }
}

function renderAddToJabRef(doi) {
  const wrap = document.createElement("div");
  wrap.className = "jabref-add-ui";

  const selLib = document.createElement("select");
  selLib.className = "jabref-lib-select";
  selLib.innerHTML = '<option value="">Loading libraries…</option>';
  selLib.disabled = true;

  const btnAdd = document.createElement("button");
  btnAdd.className = "jabref-btn-add";
  btnAdd.textContent = "+ Add to JabRef";
  btnAdd.disabled = true;

  wrap.appendChild(selLib);
  wrap.appendChild(btnAdd);
  jabrefSection.appendChild(wrap);

  browser.storage.local.get("jabrefLastLibrary").then(prefs => {
    browser.runtime.sendMessage({ type: "getJabRefLibraries" }).then(({ ok, libraries }) => {
      if (!ok || !libraries?.length) {
        selLib.innerHTML = '<option value="">No libraries open</option>';
        return;
      }
      selLib.innerHTML = "";
      for (const libId of libraries) {
        const opt = document.createElement("option");
        opt.value = libId;
        opt.textContent = libId;
        selLib.appendChild(opt);
      }
      if (prefs.jabrefLastLibrary && libraries.includes(prefs.jabrefLastLibrary)) {
        selLib.value = prefs.jabrefLastLibrary;
      }
      selLib.disabled = false;
      btnAdd.disabled = false;
    });
  });

  btnAdd.addEventListener("click", () => {
    const targetLibraryId = selLib.value;
    if (!targetLibraryId) return;
    btnAdd.disabled = true;
    btnAdd.textContent = "Queuing…";
    browser.runtime.sendMessage({ type: "addToJabRef", doi, targetLibraryId }).then(({ ok }) => {
      if (ok) {
        wrap.innerHTML = '<span class="jabref-queued">Queued — will add when JabRef syncs.</span>';
      } else {
        btnAdd.disabled = false;
        btnAdd.textContent = "+ Add to JabRef";
      }
    });
  });
}

function renderMiscForm(tabTitle, tabUrl) {
  const wrap = document.createElement("div");
  wrap.className = "jabref-misc-form";

  const mkRow = (labelText, input) => {
    const row = document.createElement("div");
    row.className = "jabref-form-row";
    const lbl = document.createElement("label");
    lbl.textContent = labelText;
    row.appendChild(lbl);
    row.appendChild(input);
    return row;
  };

  const inpTitle = document.createElement("input");
  inpTitle.type = "text";
  inpTitle.value = tabTitle;
  inpTitle.className = "jabref-input";

  const inpUrl = document.createElement("input");
  inpUrl.type = "text";
  inpUrl.value = tabUrl;
  inpUrl.readOnly = true;
  inpUrl.className = "jabref-input jabref-input-readonly";

  const inpYear = document.createElement("input");
  inpYear.type = "text";
  inpYear.value = String(new Date().getFullYear());
  inpYear.className = "jabref-input jabref-input-year";
  inpYear.maxLength = 4;

  const selLib = document.createElement("select");
  selLib.className = "jabref-lib-select";
  selLib.innerHTML = '<option value="">Loading…</option>';
  selLib.disabled = true;

  const btnAdd = document.createElement("button");
  btnAdd.className = "jabref-btn-add";
  btnAdd.textContent = "+ Add to JabRef";
  btnAdd.disabled = true;

  wrap.appendChild(mkRow("Title", inpTitle));
  wrap.appendChild(mkRow("URL", inpUrl));
  wrap.appendChild(mkRow("Year", inpYear));

  const bottomRow = document.createElement("div");
  bottomRow.className = "jabref-add-ui";
  bottomRow.appendChild(selLib);
  bottomRow.appendChild(btnAdd);
  wrap.appendChild(bottomRow);

  jabrefSection.appendChild(wrap);

  // Populate library dropdown
  browser.storage.local.get("jabrefLastLibrary").then(prefs => {
    browser.runtime.sendMessage({ type: "getJabRefLibraries" }).then(({ ok, libraries }) => {
      if (!ok || !libraries?.length) {
        selLib.innerHTML = '<option value="">No libraries open</option>';
        return;
      }
      selLib.innerHTML = "";
      for (const libId of libraries) {
        const opt = document.createElement("option");
        opt.value = libId;
        opt.textContent = libId;
        selLib.appendChild(opt);
      }
      const last = prefs.jabrefLastLibrary;
      if (last) {
        if (libraries.includes(last)) {
          selLib.value = last;
        } else {
          // JabRef appends a per-file hash suffix that changes when the
          // file is rewritten; fall back to matching by basename so the
          // remembered choice survives a hash bump.
          const stripHash = id => id.replace(/-[0-9a-f]{6,}$/i, "");
          const lastBase = stripHash(last);
          const match = libraries.find(id => stripHash(id) === lastBase);
          if (match) selLib.value = match;
        }
      }
      selLib.disabled = false;
      btnAdd.disabled = false;
    });
  });

  selLib.addEventListener("change", () => {
    if (selLib.value) browser.storage.local.set({ jabrefLastLibrary: selLib.value });
  });

  btnAdd.addEventListener("click", () => {
    const targetLibraryId = selLib.value;
    if (!targetLibraryId) return;
    btnAdd.disabled = true;
    btnAdd.textContent = "Adding…";
    browser.runtime.sendMessage({
      type: "addMiscToJabRef",
      title: inpTitle.value,
      url:   tabUrl,
      year:  inpYear.value,
      targetLibraryId,
    }).then(({ ok, error, details }) => {
      if (ok) {
        wrap.innerHTML = '<span class="jabref-queued">Added to JabRef.</span>';
      } else {
        btnAdd.disabled = false;
        btnAdd.textContent = "+ Add to JabRef";
        const err = document.createElement("span");
        err.className = "jabref-error";
        err.textContent = error ?? "Failed.";
        if (details) err.title = details;
        wrap.appendChild(err);
      }
    });
  });
}

function esc(s) {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}
