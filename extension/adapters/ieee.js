// IEEE Xplore PDF locator.
//
// IEEE renders the PDF icon as an Angular-driven
// <a href="javascript:void()" title="...">. The title attribute is the
// signal:
//   * "You do not have access to this PDF"  -> auth required.
//   * "Download PDF" / anything else        -> signed-in, click triggers
//                                               the actual download or
//                                               a navigation to the PDF.
//
// Strategy:
//   1. Find the PDF button by stable data-analytics_identifier.
//   2. Real href -> return it.
//   3. javascript:void + "no access" title -> click PDF, poll for the
//      SeamlessAccess "Access Through" button and click it too (silent
//      re-auth when an IdP session is alive). Return { authRequired: true }.
//   4. javascript:void + other title (signed in) -> click PDF and return
//      { clickedDownload: true } so the extension watches for the
//      download / navigation triggered by the page itself.
//   5. Fall through to legacy selectors and finally a synthesised URL.

(async () => {
  const wait = ms => new Promise(r => setTimeout(r, ms));

  const match = location.pathname.match(/^\/document\/(\d+)/);
  if (!match) {
    console.log("[FT/ieee] not a /document/<n> path:", location.pathname);
    return null;
  }
  const arnumber = match[1];

  const pdfBtn = document.querySelector(
    'a[data-analytics_identifier="document-lh-action-downloadpdf"]');

  if (pdfBtn) {
    const href = pdfBtn.getAttribute("href") || "";
    const title = pdfBtn.getAttribute("title") || "";
    console.log("[FT/ieee] PDF button found; href=", href, "title=", title);

    if (href && !href.startsWith("javascript:")) {
      const abs = new URL(href, location.href).href;
      console.log("[FT/ieee] real href on PDF button ->", abs);
      return abs;
    }

    const noAccess = /do not have access/i.test(title);
    if (noAccess) {
      console.log("[FT/ieee] no-access title; clicking PDF to open sign-in dialog");
      pdfBtn.click();

      const accessSelector = [
        'button[data-analytics_identifier^="doc_details_sign_in_seamlessaccess_access_through"]',
        'button.stats-Doc_Details_sign_in_seamlessaccess_access_through_institution_name_btn',
      ].join(",");

      let accessBtn = null;
      for (let i = 0; i < 20; i++) {
        accessBtn = document.querySelector(accessSelector);
        if (accessBtn) break;
        await wait(100);
      }

      if (accessBtn) {
        console.log("[FT/ieee] auto-clicking Access Through SSO button");
        accessBtn.click();
        return { authRequired: true, autoSso: true };
      }

      console.log("[FT/ieee] SeamlessAccess button did not appear within timeout");
      return { authRequired: true };
    }

    // Signed-in state: title is "Download PDF" or similar. The PDF
    // viewer rendered by IEEE's Angular handler is an iframe whose
    // source is /stampPDF/getPDF.jsp?tp=&arnumber=<id>. When the user
    // has a live session, that URL returns the PDF bytes directly —
    // browser.downloads.download will reuse the tab's cookies for the
    // same host. No need to click the button or chase a page-triggered
    // download.
    const signedInPdf = `${location.origin}/stampPDF/getPDF.jsp?tp=&arnumber=${arnumber}`;
    console.log("[FT/ieee] signed-in title; returning synth URL", signedInPdf);
    return signedInPdf;
  }

  const fallbackSelectors = [
    'a[aria-label="Download PDF" i]',
    'a[aria-label*="PDF" i][href*="/stampPDF/"]',
    'a[href*="/stampPDF/getPDF.jsp"]',
    'a[href$=".pdf"]',
  ];
  for (const sel of fallbackSelectors) {
    const a = document.querySelector(sel);
    if (a && a.href) {
      console.log("[FT/ieee] PDF link via fallback selector", sel, "->", a.href);
      return a.href;
    }
  }

  const bodyText = document.body ? document.body.textContent : "";
  const authMarkers = [
    "Full text access may be available",
    "To access full text, please use your institutional",
    "SeamlessAccess",
  ];
  const matched = authMarkers.find(m => bodyText.includes(m));
  if (matched) {
    console.log("[FT/ieee] auth wall detected via text marker:", matched);
    return { authRequired: true };
  }

  const synth = `${location.origin}/stampPDF/getPDF.jsp?tp=&arnumber=${arnumber}`;
  console.log("[FT/ieee] no DOM PDF link and no auth wall; falling back to synthesised:", synth);
  return synth;
})();
