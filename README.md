# AnchorHub

Stable bidirectional links between items across applications. A
WebExtension (Firefox + Chromium) and Windows daemon, with pluggable
adapters for individual applications.

| Component | Status |
|---|---|
| Firefox extension | working |
| Chromium extension (Chrome / Edge / Brave / Vivaldi) | working — single manifest, single codebase |
| Windows daemon (`ankd.exe`) | working |
| OneNote adapter | working (URL-in-page matching, page creation, name search) |
| JabRef adapter | in progress |
| ContextSwitcher adapter | working (task-URL matching + `contextswitcher://` deep links) |
| Outlook adapter | in progress (classic Outlook: `ankd outlook-capture` / `outlook-open <anchor>` / `outlook-todos` / `outlook-complete <anchor>` / `outlook-blocks-sync` / `outlook-blocks-clear` / `outlook-busy` / `outlook-sideline <anchor> on|off` CLI; `ankd version` lists the commands of the build) |
| Linux host (relay to OneNote in a Windows VM) | hub-side plumbing complete, install scripts shipped — see [`docs/plan-linux-port.md`](docs/plan-linux-port.md) |
| macOS host (Apple Silicon, Chrome — JabRef fulltext only) | native build, no OneNote/VM; installer shipped, unexercised on hardware |

The current functionality (everything in this README) is what the
OneNote adapter provides. The architecture splits adapters out properly
— see `docs/anchorhub-spec.md` for the target design.

## Components

- `extension/` — browser extension (Manifest V3, runs in Firefox 121+
  and any Chromium browser)
- `hub/` — Windows daemon (`ankd.exe`) and adapters (.NET 8)
- `install/` — portable PowerShell installer (`install.ps1` for Firefox,
  `install-chrome.ps1` for Chromium-family browsers)

## Icon colours

Colours from Paul Tol's colour-blind-safe light + vibrant qualitative
schemes. **Hue cue + shape cue:** mint = the page is anchored, olive a
half-step down for partial / related matches (the two are visibly
different hues so they can't be confused), orange a "caution" warning
when the daemon is not reachable. The two "no positive result" states
(searching, no match) share the grey hue and differ by shape only — a
small anchor while the lookup is in flight, a thin outline-weight anchor
once it returns empty.

| Icon | Meaning |
|---|---|
| Mint anchor (`#44BB99`) | Current URL is anchored in a connected adapter — click to open |
| Olive anchor (`#AAAA00`) | A related anchor (e.g. same site) found |
| Thin grey anchor (`#BBBBBB`) | Connected to daemon, no match |
| Orange anchor (`#EE7733`) | Daemon not connected |
| Small grey anchor (`#BBBBBB`) | Lookup in progress |
| Thin grey anchor + red `OFF` badge | AnchorHub paused (right-click the icon → Resume) |

A small digit (1-9, or `10+`) is baked into the bottom-right of the
icon when there are multiple matches.

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| Windows | 10 x64 or later | |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8) | 8.0+ | builds the daemon; the Windows installers auto-install it via winget if missing |
| Firefox | 121+ | needs MV3 `background.service_worker` support so the same manifest works in Chromium |
| Chrome / Edge / Brave / Vivaldi | recent | any modern Chromium |
| OneNote | Desktop app (Microsoft 365 or perpetual) | required for the OneNote adapter; launched automatically if not running |

No Microsoft account setup or app registration required — the daemon
talks directly to the OneNote desktop app via its COM API.

Optional: with the [OneMore](https://onemoreaddin.com/) add-in's
[search indexing](https://onemoreaddin.com/commands/%E2%86%92%20Search%20Indexing.htm)
turned on (AnchorHub follows that setting), the daemon searches OneMore's local full-text index
(`%LOCALAPPDATA%\OneMore\OneMore.db`) first and uses OneNote's own search
only when that index has no hit.

The Windows installers (`install.ps1`, `install-chrome.ps1`,
`install-vm.ps1`) bootstrap their own build prerequisites through
`install/ensure-prereqs.ps1`: if the .NET SDK is missing they install it
with `winget` (`Microsoft.DotNet.SDK.8`), and they register the
`nuget.org` package source if it is absent — a freshly installed SDK has
no source configured, so `dotnet restore` would otherwise fail with
`NU1100`.

## Quick start

```powershell
# 1. Clone
git clone https://github.com/contextswitcher/anchorhub.git
cd AnchorHub

# 2. Build and install the native daemon (no admin required)
#    For Firefox:
powershell -ExecutionPolicy Bypass -File install\install.ps1
#    For Chrome / Edge / Brave / Vivaldi (also stages a load-unpacked dir):
powershell -ExecutionPolicy Bypass -File install\install-chrome.ps1

# 3a. Load the extension in Firefox
#     about:debugging → This Firefox → Load Temporary Add-on…
#     → select extension\manifest.json
#
# 3b. Load the extension in a Chromium browser
#     chrome://extensions (or edge://, brave://, vivaldi://)
#     → enable Developer mode → Load unpacked
#     → select %LOCALAPPDATA%\Programs\AnchorHub\extension
```

> **Firefox restart:** the extension is loaded as a temporary add-on
> and is removed when Firefox exits. Re-run step 3a after each restart,
> or package it as a signed add-on.

> **Chromium browsers** keep the loaded unpacked extension across
> restarts. The extension ID is fixed
> (`bhnaeoglhfolnnkciokfdinoghhjlfeh`, derived from the `key` in
> `manifest.json`) so the host manifest's `allowed_origins` matches on
> every machine.

## What happens on install

`install.ps1` (Firefox; no admin rights required):

0. Ensures build prerequisites via `ensure-prereqs.ps1` (winget-installs
   the .NET SDK if missing; registers the `nuget.org` source if absent)
1. Runs `dotnet publish` to produce a self-contained `ankd.exe`
2. Copies the exe to `%LOCALAPPDATA%\Programs\AnchorHub\`
3. Writes the native messaging manifest to `%APPDATA%\AnchorHub\`
4. Registers `HKCU\SOFTWARE\Mozilla\NativeMessagingHosts\io.github.contextswitcher.anchorhub`

`install-chrome.ps1` (Chromium-family) does the same first three
steps, then registers under each Chromium hive
(`HKCU\SOFTWARE\Google\Chrome\NativeMessagingHosts\…`,
`…\Microsoft\Edge\…`, `…\BraveSoftware\Brave-Browser\…`,
`…\Vivaldi\…`, `…\Chromium\…`) and stages a copy of `extension\` at
`%LOCALAPPDATA%\Programs\AnchorHub\extension` for Load Unpacked. The
host manifest carries both `allowed_extensions` (Firefox) and
`allowed_origins` (Chromium) so a single file serves both.

## Usage

Open a browser tab for any URL referenced in your OneNote notes — the
icon turns teal. Click the icon to open the popup; click a match row to
jump to that page in OneNote and open find-on-page (Ctrl+F) pre-filled
with the URL so you see exactly where it appears.

Each adapter section (OneNote, JabRef, ContextSwitcher) carries a green
"found" or gray "not found" status chip and a matching coloured edge, so
you can see at a glance which application holds the page.

If the page belongs to a [ContextSwitcher](https://github.com/contextswitcher/contextswitcher)
task (its URL is in the task's `browser.urls`), a **ContextSwitcher**
section shows the task title and two buttons: **Switch to task** jumps
your whole working context to it, and **Show in app** focuses and
selects it — both via the `contextswitcher://` deep-link protocol.

If the URL is **not found**, the popup shows a "Copy link" button.
Lookup results are cached for 24 hours and survive the browser
suspending the extension's background worker; use the **↺ Refresh**
button to force a fresh search. If the popup is opened for a tab the
background has no result for — typically the first click after the
worker was suspended — it runs the lookup then and shows "Loading…"
until it returns, rather than reporting a premature "not found".

Right-click the toolbar icon and choose **Pause AnchorHub** to stop all
lookups (e.g. for screenshots). Every tab then shows a red `OFF` badge
and the popup only says "✗ Disabled"; **Resume AnchorHub** switches
back.

When a section has more than 9 matches, an **"and N more… (open in
OneNote)"** row appears. Clicking it brings OneNote to the foreground
with its global search (Ctrl+E) pre-filled.

### Settings

Open the AnchorHub options page from the popup footer (⚙) or from
`about:addons` → AnchorHub → **Preferences**. Each adapter can be
enabled or disabled independently:

- **OneNote** — when enabled, the extension talks to the native
  daemon (`ankd`) for URL and name lookups.
- **JabRef** — when enabled, the extension queries
  `http://localhost:23119` for DOI and URL matches in your open
  libraries.
- **OneNote (Microsoft Graph)** — when enabled and signed in, the
  extension queries the Graph API for OneNote pages stored in
  OneDrive. The sign-in panel appears below the toggle.
- **ContextSwitcher** — when enabled, the daemon (`ankd`) matches the
  current URL against the `browser.urls` of the task files in
  `~/.contextswitcher/tasks`, and the popup offers deep links to
  switch to the matching task or reveal it in the app.

OneNote, JabRef, and ContextSwitcher default to enabled; Graph
defaults to off and must be signed in once. Toggles take effect
immediately — no extension reload required.

### Right-click lookup

Select text on any page and right-click. AnchorHub's submenu
populates with hits from every enabled adapter:

- OneNote pages whose title or body contains the selection,
- Microsoft Graph (cloud OneNote) hits, prefixed with `☁`,
- JabRef library entries, prefixed with `📚`.

Clicking a OneNote row jumps to the page and pre-fills find-on-page
with the matching term; a Graph row opens the page in OneNote
Desktop or the web client; a JabRef row copies the citation key to
the clipboard. The submenu also carries the existing "Send to
OneNote with URL" append action.

JabRef matching uses the Search.g4 syntax exposed by `jabsrv`. A
free-text selection is searched across `author`, `title`,
`keywords`, and `abstract` with the case-insensitive `=~i` operator;
DOI-shaped and URL-shaped selections match the `doi` / `url` field
instead. Every lookup is a single `/libraries/query` POST.

### BibTeX-on-page detection

When the active page renders a BibTeX block (typical case: a "Citation"
section in a GitHub README), AnchorHub extracts each entry's `url`,
`doi`, and `eprint` fields and looks them up too. The popup gains a
"Reference" section showing the BibTeX source verbatim plus any OneNote
and JabRef hits — so opening the repo of a paper surfaces the paper's
OneNote note and JabRef entry without leaving the repo. A status
bubble is floated at the top-right of every BibTeX `<pre>` block on
the page, mirroring the OneNote lookup state for the entry's `url`.

### In-page status markers

On supported pages (GitHub topics/search/profile/repository lists, AlternativeTo,
ResearchGate lab pages, arXiv HTML papers, plus a generic person-name
scan on any page), small markers appear next to items that are
referenced in your OneNote or JabRef libraries. Click any marker to
open the popup against that item.

Each marker is resolved against both adapters: green when the item is
in OneNote (the tooltip notes a JabRef hit too, if any), green with an
`In JabRef` tooltip when it is only in JabRef, and grey `Not found`
when it is in neither.

On an arXiv HTML paper (`arxiv.org/html/<id>`) a marker is added after
the paper title and after each reference's title — the reference
markers look the paper up by its arXiv id and/or DOI.

On Google Scholar (`scholar.google.*`) result pages, "same site /
related" (olive) markers are suppressed — Scholar links point at many
publisher/repository hosts and a bare-host match there is noise. Only
exact matches (the paper itself is in OneNote) are shown; everything
else stays grey.

Markers are kept off UI chrome: pagination / breadcrumb / in-page
navigation, login screens (a "Forgot password?" link in a form with a
password field), dialog action rows (links sitting next to
OK/Cancel-style buttons), and links to auth hosts (`login.*`,
`signin.*`, `accounts.*`, `auth.*`, `sso.*`, `oauth.*`, e.g.
`https://login.tailscale.com/`) are skipped — these are never pasted
reference URLs, so a marker beside them would be noise.

### Backlinks on OneNote pages

ContextSwitcher and koppter send `ankd onenote-backlinks-sync` the
complete list of their tasks (and categories) that link to a OneNote page.
`ankd` keeps one line at the top of each such page, `↩ ContextSwitcher:
Fix NPE · koppter: Call the dentist`, each entry a link back into the
application. A done task is struck through; a deleted task or category
loses its entry, and the line goes when no entry is left. The line carries
a marker, so AnchorHub only ever rewrites its own line. `--all` rewrites
every line, e.g. after editing one by hand.

### Diagnostic CLI

```powershell
# Run from anywhere after install:
& "$env:LOCALAPPDATA\Programs\AnchorHub\ankd.exe" lookup https://example.com/

# Free-text search by name (useful for the person-name scanner):
& "$env:LOCALAPPDATA\Programs\AnchorHub\ankd.exe" lookup-name "Sven Apel"
```

Or from the repo during development:

```powershell
cd hub
dotnet run --project AnchorHub.Hub -- lookup https://example.com/
dotnet run --project AnchorHub.Hub -- lookup-name "Sven Apel"
dotnet run --project AnchorHub.Hub -- search-wsi https://example.com/
```

## Linux (with OneNote in a Windows VM)

The same `ankd` binary runs in two cooperating modes:

- **Forwarder** on the Linux host — registered as Firefox's native
  messaging host (`--forward=host:port`).
- **Listener** inside a Windows VM that hosts OneNote
  (`--listen=host:port`).

Both speak the same length-prefixed JSON protocol the daemon already
speaks over stdio, carried over a TCP socket forwarded by QEMU
`hostfwd`. Design rationale (vs. Microsoft Graph, Wine, headless
OneNote-for-Web) is in
[ADR 0006](docs/decisions/0006-linux-port-via-vm-relay.md);
implementation plan in
[`docs/plan-linux-port.md`](docs/plan-linux-port.md).

The Windows-native install above is **not affected**: `install.ps1`
continues to install `ankd.exe` in direct mode with no TCP listener,
no token, and no VM.

### Three-step install

1. **On the Linux host.** Builds, generates a token, registers
   Firefox's native messaging manifest, prints the token:

   ```bash
   ./install/install-linux.sh
   ```

2. **In your QEMU launcher.** Forward the listener port:

   ```
   -netdev user,id=net0,hostfwd=tcp:127.0.0.1:9876-:9876
   ```

3. **Inside the Windows VM** (an **elevated** PowerShell — the firewall
   rule needs admin). Paste the token from step 1:

   ```powershell
   powershell -ExecutionPolicy Bypass -File install\install-vm.ps1 -Token <hex>
   ```

   The script publishes `ankd.exe`, registers an autostart Run-key
   entry for `--listen` mode, launches the listener immediately, and adds
   a Windows Firewall inbound rule for the port. It binds `0.0.0.0` by
   default: QEMU user-mode (slirp) `hostfwd` delivers to the guest's NAT
   interface, not loopback, so a `127.0.0.1` binding would leave the popup
   stuck orange. The guest stays NAT-isolated (reachable only via the
   host's hostfwd).

Then reload the AnchorHub extension in Firefox
(`about:debugging` → Reload).

### Failure mode

When the VM is suspended, paused, or has OneNote closed, the
forwarder synthesises `{ ok: false, error: { code: "DISCONNECTED" } }`
responses and the toolbar icon flips to orange (the same state the
extension shows when the daemon process itself is gone). On the next
successful response after the VM comes back, per-tab icons resume
updating automatically.

### WinApps interop note

When OneNote is launched as a WinApps RemoteApp, focusing a OneNote
page from `--listen` brings that window to the foreground on the
Linux side too — RemoteApp surfaces each Windows window individually.
But keep `ankd.exe` and OneNote alive in the VM's persistent user
session, *not* tied to a RemoteApp window's lifetime, otherwise
closing the OneNote window from Linux can kill the COM server and
stall lookups.

## macOS (Apple Silicon, Chrome — JabRef fulltext only)

macOS runs a **native** daemon — no OneNote, no Windows VM. The
`net8.0` build omits the OneNote COM bridge entirely and serves only the
JabRef fulltext provider (and Microsoft Graph sign-in). OneNote
anchor-lookup is Windows-only, so its actions return `DISCONNECTED`
here.

```bash
# Publishes ankd (osx-arm64), writes the fulltext token, registers the
# Chrome native-messaging manifest, and stages the extension.
./install/install-mac.sh
```

Then load the extension:

> chrome://extensions → enable **Developer mode** → **Load unpacked** →
> `~/Library/Application Support/AnchorHub/extension`

> **Currently disabled.** The fulltext provider no longer auto-enables
> (JabRef fulltext fetching moves to the JabRef browser-extension
> plugin). Add `JABREF_FULLTEXT_AUTOENABLE` to `<DefineConstants>` in
> `AnchorHub.Hub.csproj` to restore the behaviour below.

The daemon is spawned by Chrome on demand and auto-enables the fulltext
realm on `127.0.0.1:17893` from the token at
`~/.config/AnchorHub/fulltext.token` (the .NET `ApplicationData` path on
macOS — note it is `~/.config`, *not* `~/Library`). It advertises itself
to JabRef by writing
`~/Library/Application Support/JabRef/fulltext-providers/anchorhub.json`.

Verify end-to-end once Chrome has started it:

```bash
curl -H "Authorization: Bearer $(cat ~/.config/AnchorHub/fulltext.token)" \
     http://127.0.0.1:17893/v1/health
```

Scope: Apple Silicon and Chrome only. Firefox/Safari and Intel macs are
out of scope for now, and — as with the Linux path — the macOS build
ships without end-to-end validation on real hardware yet.

## Updating

After any code change:

```powershell
powershell -ExecutionPolicy Bypass -File install\install.ps1
# Then in Firefox: about:debugging → Reload (or re-load the extension)
```

## Development

```powershell
cd hub
dotnet build          # build all projects
dotnet test           # run unit tests
```

See `CLAUDE.md` for architecture notes useful when working with AI
coding assistants.

## Uninstall

```powershell
# Windows
powershell -ExecutionPolicy Bypass -File install\uninstall.ps1
```

```bash
# macOS (Apple Silicon)
./install/uninstall-mac.sh
```

Both remove the daemon, native-messaging manifest, token, and JabRef
discovery file. The browser extension stays loaded — remove it manually
(`chrome://extensions` → Remove).
