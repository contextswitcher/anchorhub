# Plan: PDF citation hover — reference preview + JabRef/OneNote status

Goal: when hovering over an internal citation link in a PDF opened in
Firefox's built-in PDF.js viewer, show:

a) A rendered preview of the reference list entry (the destination the link
   points to), and
b) Whether the cited paper is in JabRef and/or OneNote, with an excerpt —
   same lookup pipeline as the browser extension's main flow.

Integrated into the AnchorHub extension (same extension, new content
script activated on PDF pages).

---

## Prior art: PDFRefPreview

<https://github.com/belinghy/PDFRefPreview>

A ~400-line bookmarklet / Chrome extension that already implements (a) using
the `PDFViewerApplication` API exposed by PDF.js. Key approach:

- Listens for `mouseover` on `.internalLink` elements in the PDF.js viewer
- Resolves the link destination via `pdfDocument.getDestinations()`
- Renders a canvas snippet of the target page at the destination coordinates
- Positions the canvas near the cursor

**Known issue and fix** (from
[issue #2](https://github.com/belinghy/PDFRefPreview/issues/2)):
the original code ignores the x-coordinate, causing the preview to show the
wrong horizontal slice for two-column papers. The fixed bookmarklet (posted
in issue #2 by the author) handles `XYZ` destination x-offsets correctly
and uses `zIndex: 99` to avoid being obscured by PDF.js UI elements.
**Use the fixed version as the basis.**

---

## Architecture

Content script (`pdf-hover.js`) injected into all URLs:

```js
// Guard: only activate inside PDF.js
if (!window.PDFViewerApplication) return;
```

This makes it activate only when the page has the `PDFViewerApplication`
global — i.e., a PDF opened in Firefox's built-in viewer. No separate
manifest `matches` pattern needed beyond the existing `<all_urls>`.

```
PDF.js page
  └── pdf-hover.js (content script)
        ├── canvas preview (PDFRefPreview logic, fixed version)
        └── DOI extraction from reference text
              └── background.js message
                    ├── POST /libraries/query  → JabRef status
                    └── hub lookup             → OneNote status
```

---

## DOI extraction from reference text

After rendering the canvas preview, also extract the **text** of the
reference entry from the PDF.js text layer:

1. Find the `.textLayer` span(s) that spatially overlap the destination
   coordinates on the target page.
2. Run the extracted text through a DOI regex:
   `/(10\.\d{4,}\/\S+)/` — covers the vast majority of academic references.
3. If found: query JabRef + OneNote as usual.
4. If not: show the canvas preview only (graceful degradation).

Alternatively (more robust): pass the reference text to the offscreen
translator runner — some Zotero translators can extract DOIs from plain
reference strings. This reuses infrastructure already planned for the
browser flow.

---

## Popup augmentation

The canvas preview (from PDFRefPreview) stays as-is. Below it, append a
slim status bar (plain HTML overlay, not canvas):

```
┌─────────────────────────────────────────┐
│  [rendered reference canvas]            │
├─────────────────────────────────────────┤
│ JabRef: Smith2020 (lib1)  ·  OneNote: My notes  │
└─────────────────────────────────────────┘
```

States mirror the main popup:
- Querying: spinner
- Found in JabRef: green citation key + library name
- Found in OneNote: page title (click → focus page via hub)
- Not found in either: "Not in JabRef" / "Not in OneNote" + "Add" links

---

## Limitations

- Works only for PDFs with **internal citation links** embedded by the
  publisher/LaTeX. Most arXiv and publisher PDFs have them; scanned or
  poorly-structured PDFs do not.
- DOI extraction from reference text fails for references without a DOI
  (books, conference proceedings without DOI, web pages). Falls back to
  canvas-only preview.
- Firefox only (PDF.js). SumatraPDF and other native viewers are out of
  scope — no plugin API exists.
- Two-column PDFs with `FitH` destinations may still show slightly
  imprecise x-positioning; this is a known PDF.js limitation.

---

## Step-by-step implementation order

1. **`pdf-hover.js`** — port the fixed PDFRefPreview bookmarklet (issue #2
   version) as a content script. Guard with `PDFViewerApplication` check.
   Verify canvas preview works on a two-column arXiv paper.

2. **DOI extraction** — after canvas render, walk the text layer at the
   destination coordinates, apply DOI regex, return DOI or `null`.

3. **Background message handler** — `getPdfCitationStatus(doi)`:
   runs `queryJabRef` + OneNote hub lookup in parallel, returns combined
   result. Reuses existing `tabJabRef` / hub infrastructure.

4. **Status bar overlay** — append HTML status element below the canvas,
   same grey/green colour scheme as main popup. Click on OneNote result
   focuses the page via hub.

5. **Manifest** — no new permissions needed; `<all_urls>` and `"offscreen"`
   already planned. Add `pdf-hover.js` to `content_scripts`.

6. **Commit after each step.**
