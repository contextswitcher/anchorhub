"use strict";

if (typeof browser === "undefined") globalThis.browser = chrome;

// Defaults mirror background.js SETTINGS_DEFAULTS so the options page
// reflects the same baseline that adapter-gated code sees before the user
// has visited this page.
const DEFAULTS = { onenote: true, jabref: true, graph: false, contextswitcher: true };

const $onenote = document.getElementById("adapter-onenote");
const $jabref  = document.getElementById("adapter-jabref");
const $graph   = document.getElementById("adapter-graph");
const $cs      = document.getElementById("adapter-contextswitcher");

const $clientId = document.getElementById("graph-client-id");
const $tenant   = document.getElementById("graph-tenant");
const $signIn   = document.getElementById("btn-graph-signin");
const $signOut  = document.getElementById("btn-graph-signout");
const $account  = document.getElementById("graph-account");
const $error    = document.getElementById("graph-error");

const $status   = document.getElementById("status");

let statusTimer = null;
function flashSaved() {
  $status.hidden = false;
  clearTimeout(statusTimer);
  statusTimer = setTimeout(() => { $status.hidden = true; }, 1200);
}

function showError(msg) {
  $error.hidden  = false;
  $error.textContent = msg;
}

function clearError() {
  $error.hidden = true;
  $error.textContent = "";
}

async function loadAdapters() {
  const stored = (await browser.storage.local.get("adapterSettings")).adapterSettings ?? {};
  $onenote.checked = stored.onenote ?? DEFAULTS.onenote;
  $jabref.checked  = stored.jabref  ?? DEFAULTS.jabref;
  $graph.checked   = stored.graph   ?? DEFAULTS.graph;
  $cs.checked      = stored.contextswitcher ?? DEFAULTS.contextswitcher;
}

async function saveAdapters() {
  await browser.storage.local.set({
    adapterSettings: {
      onenote: $onenote.checked,
      jabref:  $jabref.checked,
      graph:   $graph.checked,
      contextswitcher: $cs.checked,
    },
  });
  flashSaved();
}

async function loadGraphConfig() {
  const { graphSettings } = await browser.storage.local.get("graphSettings");
  $clientId.value = graphSettings?.clientId ?? "";
  $tenant.value   = graphSettings?.tenant   ?? "";
  $clientId.placeholder = AnchorGraphAuth.DEFAULT_CLIENT_ID;
  $tenant.placeholder   = AnchorGraphAuth.DEFAULT_TENANT;
}

async function saveGraphConfig() {
  await browser.storage.local.set({
    graphSettings: {
      clientId: $clientId.value.trim(),
      tenant:   $tenant.value.trim() || "common",
    },
  });
  flashSaved();
}

async function refreshAuthState() {
  const st = await AnchorGraphAuth.status();
  if (st.signedIn) {
    $signIn.hidden  = true;
    $signOut.hidden = false;
    $account.textContent = st.account ? `Signed in as ${st.account}` : "Signed in";
  } else {
    $signIn.hidden  = false;
    $signOut.hidden = true;
    // Block sign-in until the user enables the adapter — signing in
    // while the toggle is off would store a token that nothing consumes.
    $signIn.disabled = !$graph.checked;
    $signIn.title    = $graph.checked
      ? ""
      : "Enable the OneNote (Microsoft Graph) adapter first.";
    $account.textContent = $graph.checked ? "Not signed in" : "Adapter disabled";
  }
}

$onenote.addEventListener("change", saveAdapters);
$jabref.addEventListener("change", saveAdapters);
$cs.addEventListener("change", saveAdapters);
$graph.addEventListener("change", async () => { await saveAdapters(); await refreshAuthState(); });

$clientId.addEventListener("change", async () => { await saveGraphConfig(); await refreshAuthState(); });
$tenant  .addEventListener("change", async () => { await saveGraphConfig(); await refreshAuthState(); });

$signIn.addEventListener("click", async () => {
  clearError();
  $signIn.disabled = true;
  try {
    await AnchorGraphAuth.signIn();
  } catch (err) {
    showError(err.message || String(err));
  } finally {
    await refreshAuthState();
  }
});

$signOut.addEventListener("click", async () => {
  clearError();
  await AnchorGraphAuth.signOut();
  await refreshAuthState();
});

(async () => {
  await loadAdapters();
  await loadGraphConfig();
  await refreshAuthState();
})();
