# Plan: Browser → JabRef integration

Goal: the popup shows a **"JabRef" section** (below the OneNote section) that
indicates whether the current page's paper is already in the library, and offers
to add it if not — same UX pattern as the OneNote integration.

---

## JabRef HTTP API (actual, from source)

Port: **23119**. (Port 6050 is native messaging only — unrelated.)

| Method | Path | Notes |
|--------|------|-------|
| `GET`  | `/` | Probe — returns HTML; 200 = JabRef running |
| `GET`  | `/libraries` | Array of open library IDs (string: `filename-uniquePrefix`; plus `"demo"`) |
| `GET`  | `/libraries/{id}` | All entries as `BibEntryDTO[]` JSON, BibTeX, or CSL JSON |
| `GET`  | `/libraries/{id}/entries/{citationKey}` | Single entry by citation key (text or HTML) |
| `POST` | `/libraries/{id}/entries` (`Content-Type: application/x-bibtex`) | Add BibTeX to library; only `id = "current"` accepted today |
| `POST` | `/libraries/{id}/entries/{citationKey}/files` (`application/pdf`) | Attach PDF |

**Missing and needed:** there is no cross-library search by DOI or URL.

### Correct entry types — POST CSL-JSON, let JabRef map

The Zotero translator runner in `JabRef-Browser-Extension-fresh` currently
emits a flat `@article{…}` regardless of the source item's type. Rather than
reimplement a Zotero-itemType→BibTeX map in the extension, JabRef does the
conversion: JabRef's official decision ([ADR 0064](https://github.com/JabRef/jabref/pull/15907),
issue [#15875](https://github.com/JabRef/jabref/issues/15875)) is to use the
citation-js CSL↔Bib(La)TeX mapping (merged in
[#15946](https://github.com/JabRef/jabref/pull/15946) as
`CSLItemTypeDefinitions`).

So the extension emits **CSL-JSON** (Zotero's native CSL-JSON export
translator, or a translator configured to output CSL) and POSTs it:

```
POST /libraries/{id}/entries
Content-Type: application/vnd.citationstyles.csl+json

{ "type": "paper-conference", "title": "…",
  "container-title": "…", "author": [ { "family": "Doe", "given": "Jane" } ],
  "issued": { "date-parts": [[ "2021", 5 ]] } }
```

JabRef maps `type` → the right entry type (`paper-conference` →
`@InProceedings`, `chapter` → `@InBook`, `thesis` → `@Thesis`,
`report` → `@Report`, `webpage` → `@Online`, …) and remaps CSL fields
(`container-title` → `journal`/`booktitle` per type). Accepts a single item
or an array. Entries have no citation key; JabRef generates them on import.

**JabRef-side status:** implemented on branch
`zotero-item-import-endpoint` (worktree `jabref-worktree-1`). New public
`ZoteroCitationMarkParser.parseCslJsonItems(String)` reuses the merged CSL
mapping; new `EntriesResource.addCslJson` consumes the CSL-JSON media type
and appends via the existing `AppendBibTeXToLibrary` command (duplicate
handling + group assignment preserved). Tests in `ZoteroCitationMarkParserTest`
and `EntriesResourceTest`.

### New endpoint to add in JabRef: `POST /libraries:query`

`GET` with query params breaks down at scale (URL length limits, no batch).
`/libraries?doi=...` is wrong semantics (filtering libraries, not searching entries).
`/libraries/{id}/entries?doi=...` requires choosing a library upfront.

**Clean design:** a batch query action on the libraries collection.

```
POST /libraries:query
Content-Type: application/json

{ "dois": ["10.1000/xyz", "10.5555/123"] }
```

```json
{
  "matches": [
    { "doi": "10.1000/xyz", "libraryId": "lib1", "entryId": "Knuth1984" },
    { "doi": "10.5555/123", "libraryId": "lib2", "entryId": "Smith2020" }
  ]
}
```

`entryId` is the citation key. Non-matching DOIs are simply absent from `matches`.

This scales from the single-DOI browser case (one-element array) up to
100-200 DOIs from a Wikipedia page or literature review without extra round trips.

Future extension: add `"urls": [...]` to the same request body for Phase 2
(URL-field lookup) without a new endpoint.

---

## DOI detection (browser side)

Run when tab finishes loading (same trigger as OneNote lookup):

1. URL matches `doi.org/<doi>` or `dx.doi.org/<doi>` → extract from path.
2. `scripting.executeScript` → read first of:
   - `<meta name="citation_doi">` (Springer, ACM, IEEE, Elsevier, most publishers)
   - `<meta name="dc.identifier" content="doi:…">`
   - `<meta name="prism.doi">`

Store in `tabDoi` Map (similar to `tabMatches`).

---

## Phases

### Phase 1 — DOI-identified papers

**On tab load/switch:**
1. Detect DOI → `tabDoi.set(tabId, doi)` (or `null`).
2. Show cached result from `browser.storage.local` immediately (grey = unconfirmed).
3. If cache says **not found** (or no cache entry): query JabRef immediately.
4. If cache says **found**: start a 5 s timer.
   - User switches away before 5 s → cancel timer, skip query.
   - 5 s passes and tab still active → query to confirm; update to green,
     or clear if entry was removed from JabRef.
5. If JabRef unreachable: keep grey ("last known").

`tabJabRef` stores `{ matches, confirmed }`:
- `confirmed: false` — from cache, live query pending or JabRef unreachable → grey
- `confirmed: true`  — live query succeeded → green

A DOI may match in multiple open libraries; all matches shown (one row each),
mirroring how OneNote shows multiple page matches.

**`browser.storage.local` shapes:**
```js
// Cache: one DOI → list of matches across all libraries
jabrefCache: { "<doi>": [{ libraryId, citationKey }, …], … }

// Offline add queue: list of pending adds, each targeting one library
jabrefQueue: [{ doi, targetLibraryId }, …]

// Remembered last-used library for "Add" dropdown
jabrefLastLibrary: "<libraryId>"
```

**Popup "JabRef" section states:**

| Condition | Display |
|---|---|
| No DOI on page | section hidden |
| No cache, JabRef unreachable | section hidden |
| Cache hit, query pending / JabRef unreachable | grey row(s): `citationKey` — `libraryId` |
| Live query: not in any library | DOI badge + **"Add to JabRef"** button |
| Live query: found | green row(s): `citationKey` — `libraryId` |

**On "Add to JabRef" click:**
1. **If JabRef online**: pass DOI to JabRef; JabRef's internal pipeline
   resolves it via CrossRef — mechanism TBD, see
   [ADR 0004](../decisions/0004-jabref-online-add-mechanism.md).
   Current placeholder: `POST /libraries/current/entries` (needs a
   DOI-accepting variant so the fetch pipeline stays server-side).
2. **If JabRef offline**: enqueue `{ doi, targetLibraryId }` in
   `browser.storage.local`; push when JabRef reconnects.
3. User selects target library from a dropdown (populated from
   `GET /libraries`); last choice persisted as `jabrefLastLibrary`.
4. On success: append `{ libraryId, citationKey }` to `jabrefCache[doi]`
   list, update `tabJabRef` to `confirmed: true`, show green — no re-query.

### Phase 2 — `@misc` entries with URL (no DOI)

When no DOI detected and JabRef is reachable:

Section shows a mini form:
- `title` pre-filled from tab title (editable)
- `url` (read-only)
- `year` (editable, pre-filled with current year)
- **"Add to JabRef"** button

Build BibTeX with an empty cite key (`@misc{,\n  …}`) — JabRef generates the
key from its configured pattern. POST same as Phase 1.

### Phase 3 — citation-key lookup (jabref-onenote direction)

Out of scope here. Tracked in `plan-jabref-onenote.md`.

---

## Step-by-step implementation order

### JabRef side
1. Add request DTO `LibraryQueryRequest`: `List<String> dois` (+ future `List<String> urls`).
2. Add response DTO `LibraryQueryMatch`: `String doi`, `String libraryId`, `String entryId`.
3. Add `POST query` method to `LibrariesResource`: deserialise request, iterate **all**
   open library contexts, for each entry check
   `entry.getField(StandardField.DOI).map(d -> doiNormalise(d).equals(doiNormalise(input)))`,
   collect **all** matches across all libraries (a DOI may appear in more than one),
   return `{ "matches": [...] }`.
   DOI normalisation: lowercase, strip `https://doi.org/` prefix.
4. Add `"urls"` support to the same method (filter on `StandardField.URL`).

### Extension side

#### Dependency setup
5. ~~**Submodule**~~ ✓ done — `extension/vendor/jabref-browser-extension/`.

6. ~~**`justfile`**~~ ✓ done — `just setup / install / test`.

#### Manifest changes
7. Add `"offscreen"` permission (MV3 requirement for translator runner).
   Add `http://localhost:23119/*` to `host_permissions`.
   Add `web_accessible_resources` for:
   - `vendor/jabref-browser-extension/sources/*.js`
   - `vendor/jabref-browser-extension/sources/vendor/*.js`
   - `vendor/jabref-browser-extension/translators/zotero/*.js`
   - `vendor/jabref-browser-extension/translators/*.json`

#### Offscreen document
8. Add `offscreen.html` + `offscreen.js` to AnchorHub.
   `offscreen.js` imports `translatorRunner.js` from the submodule and
   listens for `runTranslator` messages — same pattern as in
   `JabRef-Browser-Extension-fresh/offscreen.js`, but with paths prefixed
   `vendor/jabref-browser-extension/`.

#### Background + popup
9. **DOI extraction** — `detectDoi(tabId, url)` in background: send
   `runTranslator` to the offscreen doc; extract `DOI` from the returned
   BibTeX. Runs on the same tab-load trigger as `lookupTab`.
10. **JabRef probe + lookup** — `queryJabRef([doi])` hitting
    `POST /libraries:query`; result stored in `tabJabRef` Map.
11. **Add handler** — `addToJabRef(doi)`: if online, pass DOI to JabRef
    (mechanism per ADR 0004); if offline, enqueue `{ doi, targetLibraryId }`
    in `browser.storage.local`. Persist `targetLibraryId` between sessions.
12. **Popup UI (Phase 1)** — "JabRef" `<section>` below OneNote section.
13. **Popup UI (Phase 2)** — @misc mini-form when no DOI; entry built
    from translator-produced metadata. **Do not build BibTeX in the
    extension** — have the Zotero translator emit **CSL-JSON** and POST it
    to JabRef's CSL-JSON endpoint (see "Correct entry types" below), so the
    entry type (conference paper, book chapter, thesis, …) is preserved.
14. **Commit after each numbered step.**
