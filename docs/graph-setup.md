# Microsoft Graph (OneNote cloud) adapter

The Graph adapter queries OneNote pages stored in OneDrive or SharePoint
via the Microsoft Graph REST API. It runs in parallel with the native
daemon and surfaces matches from notebooks that aren't synced to the
local OneNote install — useful on Linux (no Windows daemon) or when the
desktop OneNote app isn't running.

Authentication uses the OAuth 2.0 authorization-code flow with PKCE.
The initial sign-in runs in the daemon (`ankd`): it binds a short-lived
HTTP listener on a free loopback port, opens the system browser to the
Microsoft consent page, catches the redirect, and exchanges the code
for tokens. From then on the browser extension refreshes the token
silently against `login.microsoftonline.com` — no daemon needed for
day-to-day lookups. The refresh token is stored in the extension via
`browser.storage.local` and never leaves the browser profile. There is
no shared client secret.

Because the OAuth listener runs in the daemon, **first-time sign-in
needs `ankd` to be running**. Subsequent silent refreshes do not.

## Default usage

AnchorHub ships with a built-in Azure AD application registration. To
enable the adapter:

1. Open AnchorHub's options page.
2. Tick **OneNote (Microsoft Graph)** in the *Adapters* section.
3. Under *Microsoft Graph sign-in*, click **Sign in with Microsoft**.
4. Approve the consent prompt for `Notes.Read` and `offline_access`.

That's all — the adapter is now active and lookups run in parallel with
the native daemon.

## Built-in app registration details

| Property              | Value |
|-----------------------|-------|
| Application (client) ID | `de36badc-bd47-4b2c-b277-ecf1d44416a1` |
| Tenant                  | `common` (multi-tenant + personal Microsoft accounts) |
| Public client flows     | enabled |
| Delegated permissions   | `Notes.Read`, `offline_access` |
| Redirect URI            | `http://localhost` (any port + any path under that host) |

The app does not request write permissions, so it cannot create, modify,
or delete OneNote content.

## Bring your own Azure AD app (optional)

You can override the built-in client ID — for example, to keep all auth
inside your organisation's tenant or to scope sign-in narrowly.

### 1 — Register an application

1. Sign in to <https://entra.microsoft.com>.
2. **Identity → Applications → App registrations → New registration**.
3. **Name**: `AnchorHub` (free-form).
4. **Supported account types**:
   - Use *Accounts in any organizational directory and personal Microsoft
     accounts* for the most flexibility.
   - Use *Accounts in this organizational directory only* for a
     single-tenant deployment (paste the tenant GUID into AnchorHub's
     *Tenant* field).
5. Leave **Redirect URI** blank for now.
6. Click **Register**. Copy the **Application (client) ID** from the
   overview blade.

### 2 — Allow public client flows

1. Open **Authentication** in the app's left navigation.
2. **Advanced settings → Allow public client flows → Yes**.
3. Save.

### 3 — Register the loopback redirect URI

1. Open **Authentication → Add a platform → Mobile and desktop
   applications**.
2. Under **Custom redirect URIs**, add **`http://localhost`** (literal,
   no port). RFC 8252 §7.3 lets Microsoft accept any port and any path
   under that host, so the daemon can pick a fresh ephemeral port on
   each sign-in.
3. Save.

### 4 — Grant the Notes.Read scope

1. Open **API permissions** in the app's left navigation.
2. **Add a permission → Microsoft Graph → Delegated permissions**.
3. Tick `Notes.Read` and `offline_access`. Click **Add permissions**.

Admin consent is not required for personal accounts; for organisational
tenants an admin may need to grant consent at first sign-in.

### 5 — Configure AnchorHub

1. AnchorHub options page → *Microsoft Graph sign-in* → *Advanced*.
2. Paste the **Application (client) ID** from step 1.
3. **Tenant**: leave blank to use `common`, or paste the tenant GUID for
   single-tenant apps.
4. Click **Sign in with Microsoft**.

## Troubleshooting

**`AADSTS50011: redirect URI mismatch`** — register `http://localhost`
(literal, no port, no path) as a *Mobile and desktop applications*
redirect URI in your Azure app. Any port + path under loopback is
accepted automatically.

**Sign-in does not open a browser tab** — the daemon (`ankd`) is not
running. Reload it via the install step, then click *Sign in* again.

**`AADSTS7000218: client_secret_or_assertion required`** — the app
isn't flagged as a public client. Revisit step 2.

**Sign-in succeeds but lookups return nothing** — the Microsoft Graph
search index does not always include URL substrings reliably. Lookups
fall back to title/section/notebook matching when the search index
misses a URL. This is a known Graph limitation.
