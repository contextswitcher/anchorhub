import { runTranslatorOnHtml } from './vendor/jabref-browser-extension/sources/translatorRunner.js';

console.debug('[offscreen] started');

if (typeof browser === 'undefined' && typeof chrome !== 'undefined') {
  globalThis.browser = chrome;
}

browser.runtime.onMessage.addListener(async (msg, _sender, sendResponse) => {
  if (!msg || msg.type !== 'runTranslator') return;
  const { url, translators } = msg;
  try {
    const resp = await fetch(url, { credentials: 'omit' });
    const html = await resp.text();

    if (!Array.isArray(translators) || !translators.length) {
      throw new Error('No translator paths provided');
    }

    let lastError = null;
    for (const t of translators) {
      try {
        const mod = await import(browser.runtime.getURL(t));
        const result = await runTranslatorOnHtml(mod, html, url);
        if (result !== null && typeof result !== 'undefined') {
          await browser.runtime.sendMessage({ type: 'offscreenResult', url, result });
          sendResponse({ ok: true });
          return true;
        }
      } catch (e) {
        lastError = e;
        console.warn('[offscreen] translator failed, trying next:', t, e);
      }
    }

    const error = lastError ? String(lastError) : null;
    await browser.runtime.sendMessage({ type: 'offscreenResult', url, result: null, error });
    sendResponse({ ok: !error, result: null, error });
  } catch (e) {
    const error = String(e);
    await browser.runtime.sendMessage({ type: 'offscreenResult', url, error });
    sendResponse({ ok: false, error });
  }
  return true;
});
