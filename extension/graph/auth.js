"use strict";

// Graph adapter authentication state. The actual OAuth 2.0
// authorization-code-with-PKCE flow runs in the daemon (ankd) because
// browser extensions cannot bind a loopback HTTP port, and Azure's
// "Mobile and desktop applications" redirect URI registration expects
// http://localhost. This file owns:
//
//   * token storage in browser.storage.local
//   * silent refresh against the token endpoint (extension can fetch
//     without binding ports), so day-to-day lookups need no daemon
//   * status display for the options page
//   * the signIn() entry point, which forwards to the background
//     script — that's where the native-messaging port to ankd lives
//
// Storage layout:
//   graphSettings = { clientId, tenant }    — user-supplied override
//   graphTokens   = { accessToken, refreshToken, expiresAt, account }
//
// Loaded in Firefox via manifest "background.scripts" and in Chrome via
// importScripts at the top of background.js. The options page loads it
// via a <script> tag.

if (typeof browser === "undefined" && typeof chrome !== "undefined") {
  globalThis.browser = chrome;
}

(function () {
  const AUTH_BASE   = "https://login.microsoftonline.com";
  const SCOPE       = "Notes.Read offline_access";
  const EXPIRY_SKEW_SECONDS = 60;

  // Shared AnchorHub Azure AD app registration. Public client (no
  // secret), multi-tenant + personal accounts, Notes.Read delegated.
  // Per-user override lives in browser.storage.local under
  // graphSettings.clientId / .tenant — used by anyone running their
  // own tenant or wanting tighter scope control.
  const DEFAULT_CLIENT_ID = "de36badc-bd47-4b2c-b277-ecf1d44416a1";
  const DEFAULT_TENANT    = "common";

  // runtime.sendMessage payload type used by the background script.
  const MSG_TYPE_SIGN_IN = "graphSignIn";

  function parseAccount(idToken) {
    if (!idToken) return null;
    try {
      const payload = JSON.parse(atob(
        idToken.split(".")[1].replace(/-/g, "+").replace(/_/g, "/")
      ));
      return payload.preferred_username || payload.upn || payload.email || null;
    } catch { return null; }
  }

  async function loadConfig() {
    const { graphSettings } = await browser.storage.local.get("graphSettings");
    const clientId = (graphSettings?.clientId ?? "").trim() || DEFAULT_CLIENT_ID;
    const tenant   = (graphSettings?.tenant   ?? "").trim() || DEFAULT_TENANT;
    return { clientId, tenant, isDefault: clientId === DEFAULT_CLIENT_ID };
  }

  async function loadTokens()  { return (await browser.storage.local.get("graphTokens")).graphTokens ?? null; }
  async function storeTokens(t) { await browser.storage.local.set({ graphTokens: t }); }
  async function clearTokens()  { await browser.storage.local.remove("graphTokens"); }

  function tokensFromDaemon(reply) {
    return {
      accessToken:  reply.accessToken,
      // MSAL keeps the refresh token in its own DPAPI-encrypted cache —
      // we never see one in the daemon reply. Expiry below triggers a
      // daemon-side silent re-acquire instead of a refresh fetch.
      refreshToken: reply.refreshToken ?? null,
      expiresAt:    Date.now() + (reply.expiresIn - EXPIRY_SKEW_SECONDS) * 1000,
      account:      reply.account || parseAccount(reply.idToken),
    };
  }

  /**
   * Public entry point. From any extension context, kick off sign-in
   * by sending a runtime message to the background script, which owns
   * the native-messaging port and dispatches the work to ankd. The
   * background side stores the resulting tokens itself, so the caller
   * just receives an ok/error reply.
   */
  async function signIn() {
    const reply = await browser.runtime.sendMessage({ type: MSG_TYPE_SIGN_IN });
    if (!reply?.ok) {
      throw new Error(reply?.error || "Sign-in failed.");
    }
    return await loadTokens();
  }

  /**
   * Called by background.js after the daemon returns tokens. Centralised
   * here so storage shape stays in one file.
   */
  async function persistDaemonTokens(reply) {
    const tokens = tokensFromDaemon(reply);
    await storeTokens(tokens);
    return tokens;
  }

  /**
   * Re-acquire an access token. The refresh token lives in MSAL's
   * DPAPI cache inside the daemon, so we delegate to signIn(); MSAL's
   * AcquireTokenSilent path makes that call fast and non-interactive
   * whenever the cache is still warm.
   */
  async function refresh() {
    return await signIn();
  }

  async function getAccessToken() {
    const tokens = await loadTokens();
    if (!tokens) return null;
    if (Date.now() < tokens.expiresAt) return tokens.accessToken;
    const refreshed = await refresh();
    return refreshed.accessToken;
  }

  async function signOut() { await clearTokens(); }

  async function status() {
    const cfg    = await loadConfig();
    const tokens = await loadTokens();
    return {
      signedIn:  tokens != null,
      account:   tokens?.account ?? null,
      clientId:  cfg.clientId,
      tenant:    cfg.tenant,
      isDefault: cfg.isDefault,
    };
  }

  globalThis.AnchorGraphAuth = {
    DEFAULT_CLIENT_ID, DEFAULT_TENANT,
    MSG_TYPE_SIGN_IN,
    signIn, signOut, refresh, getAccessToken, status,
    // Used only by background.js after a daemon sign-in reply lands.
    _persistDaemonTokens: persistDaemonTokens,
    _loadConfig: loadConfig,
  };
})();
