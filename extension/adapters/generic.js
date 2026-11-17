// Generic PDF locator — catch-all fallback when no publisher-specific
// adapter matches. Scans the page for the first <a href> that looks like
// it points at a PDF:
//   - href ends in .pdf (case-insensitive)
//   - path segment contains /pdf/, /fulltext/, or /download/
//   - the element advertises type="application/pdf"
//
// Returns the absolute URL of the first match, or null when no candidate
// is found.
(function () {
  const PDF_PATH_HINT = /(\/pdf\/|\/fulltext\/|\/download\/)/i;
  for (const a of document.querySelectorAll("a[href]")) {
    const href = a.getAttribute("href");
    if (!href) continue;
    const lower = href.toLowerCase();
    const looksLikePdf =
      lower.endsWith(".pdf") ||
      PDF_PATH_HINT.test(lower) ||
      a.getAttribute("type") === "application/pdf";
    if (!looksLikePdf) continue;
    try {
      return new URL(href, location.href).href;
    } catch {
      // skip unparseable href
    }
  }
  return null;
})();
