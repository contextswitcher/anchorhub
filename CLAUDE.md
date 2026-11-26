# AnchorHub — Claude Code context

## What this project is

A WebExtension (Firefox 121+ and Chromium-family browsers) + Windows
daemon (`ankd.exe`) that creates stable bidirectional links between
browser pages and items in connected applications. Adapters provide
application-specific behaviour; the OneNote adapter is the one that's
currently working (URL-in-page matching, page creation, free-text name
search).

The extension uses a single `manifest.json` that works in both
browsers — `background.scripts` (Firefox prefers it) and
`background.service_worker` (Chrome's only option) coexist; the host
manifest carries both `allowed_extensions` (Firefox) and
`allowed_origins` (Chromium). The Chrome extension ID is fixed via a
`key` field that `install-chrome.ps1` injects into the staged copy of
the manifest at install time — the source `extension/manifest.json`
omits it so Firefox doesn't emit an "unexpected property" warning.
All `browser.*` calls are usable in both via a tiny
`if (typeof browser === "undefined") globalThis.browser = chrome;`
shim at the top of every script.

## Repo layout

```
extension/          Firefox extension (manifest v3, background.js, popup.*)
hub/
  AnchorHub.Core/          URL normaliser + per-host helpers, IOneNoteBackend (shared)
  AnchorHub.Core.Tests/    xUnit tests for Core
  AnchorHub.Hub/           Daemon (.NET 10 / multi-target net10.0;net10.0-windows) → ankd
    Cli/Commands.cs            CLI entry points (lookup, lookup-name, search-wsi, outlook-*) — Windows-only
    Outlook/OutlookClient.cs   Classic Outlook COM via one-shot PowerShell — Windows-only
    NativeMessaging/
      NativeMessagingHost.cs   Stdin/stdout host (direct mode, Windows-only)
      MessageHandler.cs        Routes actions to IOneNoteBackend
      OneNoteClient.cs         PowerShell COM bridge — IOneNoteBackend impl, Windows-only
      MessageProtocol.cs       4-byte LE length-prefix framing (stdio + TCP)
      TcpListenerHost.cs       Listen mode — serves MessageHandler over TCP
    Forwarding/
      ForwardingHost.cs        Forward mode — relays stdio <-> TCP
  AnchorHub.Hub.Tests/     Loopback integration tests (in-process, no OneNote)
install/
  install.ps1                  Windows-direct install for Firefox (publish + manifest + Mozilla HKCU reg)
  install-chrome.ps1           Windows-direct install for Chromium (Chrome/Edge/Brave/Vivaldi);
                                 same exe + manifest, registers under each Chromium HKCU hive,
                                 stages extension/ for Load Unpacked
  install-linux.sh             Linux forwarder install (publish + manifest + token)
  install-vm.ps1               Windows-VM listener install (publish + Run-key)
  *.json.template              Native messaging manifest template — single file with
                                 both allowed_extensions (Firefox) and allowed_origins (Chromium)
docs/requirements/             OpenFastTrace requirements
docs/anchorhub-spec.md         Target architecture (multi-adapter,
                                 shim+daemon split, named-pipe IPC)
docs/plan-linux-port.md        Linux port via VM-relay (shipped, see status header)
```

## Build and install

```powershell
# Full install (publish → copy exe → write manifest → register HKCU):
powershell -ExecutionPolicy Bypass -File install\install.ps1

# Build only (for iterating):
cd hub && dotnet build

# Tests:
cd hub && dotnet test
```

The hub binary `ankd` runs in three argv-selected modes; only direct
mode is used by the Windows-native install:

- *direct* (no flag) — Firefox stdio ↔ MessageHandler ↔ OneNoteClient.
  Windows-only.
- `--listen=host:port` — TCP server ↔ MessageHandler ↔ OneNoteClient.
  Used inside a Windows VM; pairs with a remote forwarder.
- `--forward=host:port` — Firefox stdio ↔ TCP relay to a listener.
  Cross-platform; primary mode for the Linux install.

`--token-file=<path>` enables a shared-token hello handshake on
`--listen` / `--forward`. Plan: `docs/plan-linux-port.md`. Rationale:
[ADR 0006](docs/decisions/0006-linux-port-via-vm-relay.md).

## Key architectural decisions

### PowerShell COM bridge (`OneNoteClient.cs`)

.NET's COM interop cannot access Click-to-Run Office type libraries — they
live in a registry hive that is only visible to Office processes. Calling
`InvokeMember` or casting to a `[ComImport]` interface both fail with
`TYPE_E_LIBNOTREGISTERED`.

**Fix:** spawn a single persistent `powershell.exe` child process at startup.
PowerShell's COM layer bypasses the restriction. The C# host writes one-line
JSON requests to PowerShell's stdin and reads one Base64-encoded line per
response from stdout. The PowerShell script holds the `OneNote.Application`
COM object for the lifetime of the process.

### Two-pass URL lookup

1. Search OneNote for the normalised URL (`scheme://host/path`, no query/fragment)
2. If no results, fall back to host-only search (suppressed for hosts where
   the bare domain matches every saved page — see `HostFallbackPolicy`)

Per-host helpers (`*UrlHelper.cs`) extract canonical search terms from URLs
that don't match exactly (e.g. forge repos, LinkedIn profiles, Reddit
subreddits, ResearchGate lab members, academia.edu CamelCase usernames).

Each match carries a `searchTerm` field set to whichever query produced it.
Subsequent Ctrl+F calls use this field — **not** the current browser URL.

### URL normalisation

Handled in `AnchorHub.Core/UrlNormalizer.cs`:

- lowercase scheme and host
- strip default ports
- strip trailing `/` (except root path `/`)
- discard query string and fragment

### Icon colours and shapes

From Paul Tol's bright + vibrant qualitative schemes (all colour-blind
safe). **Shape** encodes the lookup phase (so colour-blind users get a
second cue), **colour** encodes the kind of result.

- **Solid anchor** (full size) — a result is in. Colour says which:
  green = exact match, teal = related, orange = daemon disconnected.
- **Thin-stroke gray anchor** (full size) — lookup completed, no OneNote
  match. Same gray hue as the searching anchor; weight/size alone
  distinguishes the lookup phase (thin vs small solid
  vs full solid anchor). Keeps link-dense pages quiet — no "red sea"
  of negatives when most links aren't anchored.
- **Small solid gray anchor** (~60% of canvas, centred) — lookup in
  progress. Smaller-than-result footprint so the "round thing
  enlarges" from the same centre when the result lands.

Traffic-light intuition: green > teal > orange. The two "no positive
result" states (searching, no match) share the gray hue and differ by
shape only.

| State | Icon |
|---|---|
| Exact match | `#44BB99` mint anchor (`green-*.png`) |
| Host-only / related match | `#AAAA00` olive anchor (`host-*.png`) |
| Not connected | `#EE7733` orange anchor (`yellow-*.png`) |
| No match | `#BBBBBB` thin gray anchor (`notfound-*.png`) |
| Searching | small `#BBBBBB` gray anchor (`default-*.png`) |

A literal digit `1`-`9` (or `10+`) is baked into the bottom-right via
OffscreenCanvas — Firefox places the badge top-right and won't move it.

### Result cache

`background.js` caches lookup results per URL for 24h (`urlCache` Map).
On tab switch the cached result is shown immediately; stale/missing
entries trigger a fresh lookup and show grey + "searching…" in the
meantime. Free-text name lookups use a parallel `nameCache` with the
same TTL.

## Committing

After completing each logical step (feature, fix, refactor, or docs
update), create a git commit. Do not batch multiple unrelated changes
into one commit.

Push after each commit to `experimental`, never to `main`. If the
remote has advanced, **merge, never rebase** (`git pull --no-rebase`),
then push. No pull requests, except those Dependabot opens against
`experimental`.

## Releases

Same cycle as koppter and ContextSwitcher
([ADR 0009](docs/decisions/0009-calver-changelog-daily-release.md)):
the daily workflow (`.github/workflows/release.yml`) merges
`experimental` into `main`, turns `## [Unreleased]` of `CHANGELOG.md`
into the previous day's CalVer release `v<YYYY-MM-DD>`, tags it and
publishes the GitHub release with the `ankd` binaries. Sessions do not
merge into `main` or tag by hand.

`CHANGELOG.md` follows Keep a Changelog. Every user-visible change adds
a bullet under `## [Unreleased]` in the same commit; released sections
are never edited. Check: `jbang heylogs@nbbrd check CHANGELOG.md`.

## Starting / installing the app

The daemon is published as a **self-contained** single-file exe so end
users do not need .NET installed. Do not change `--self-contained true`
in `install.ps1`.

`dotnet publish` with `-r win-x64 --self-contained true` downloads
large runtime packs (~200 MB) from NuGet on first use. If the publish
fails with NuGet timeout errors, the network is the cause — retry or
restore manually:

```powershell
dotnet restore hub/AnchorHub.Hub/AnchorHub.Hub.csproj -r win-x64 --source https://api.nuget.org/v3/index.json
```

On a fresh machine with no NuGet sources configured, add nuget.org first:

```powershell
dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org
```

Claude can build and install the daemon by running:

```powershell
powershell -ExecutionPolicy Bypass -File install\install.ps1
```

After that, one manual step is always required — neither Firefox nor
Chromium browsers have a CLI for extension management:

> In Firefox: `about:debugging` → **This Firefox** → **Reload** next to
> AnchorHub (or **Load Temporary Add-on…** → `extension\manifest.json`
> if the extension is not yet loaded).
>
> In Chrome / Edge / Brave / Vivaldi: `chrome://extensions` (or
> `edge://`, `brave://`, `vivaldi://`) → enable Developer mode → click
> the reload icon on AnchorHub (or **Load unpacked** →
> `%LOCALAPPDATA%\Programs\AnchorHub\extension` if not yet loaded).
> The Chromium-direct install script (`install-chrome.ps1`) stages the
> directory there.

## Keeping documentation current

When implementing any feature or fix, also update:

1. **`docs/requirements/anchorhub.md`** — add or revise the relevant
   `req~ank.*~N` requirement. Increment the revision number when the
   meaning of an existing requirement changes. New behaviour always
   needs a new or updated requirement.
2. **`README.md`** — update the Usage section and icon-state table if
   the user-visible behaviour changes.

Do this in the same commit as the code change.

## Common tasks

| Task | Command / file |
|---|---|
| Test a URL lookup manually | `dotnet run --project hub/AnchorHub.Hub -- lookup <url>` |
| Test a name lookup manually | `dotnet run --project hub/AnchorHub.Hub -- lookup-name "Sven Apel"` |
| Regenerate icons (new colour) | `powershell -ExecutionPolicy Bypass -File extension\icons\generate-icons.ps1` |
| Add a new native action | PS script in `OneNoteClient.cs` → `OneNoteClient` method → `MessageHandler` case |
| Capture / open an Outlook mail | `dotnet run --project hub/AnchorHub.Hub -- outlook-capture`, then `-- outlook-open "<anchor>"` |
| List open Outlook To-Do items (JSON, all stores) | `dotnet run --project hub/AnchorHub.Hub -- outlook-todos` |
| Mark an Outlook To-Do item done | `dotnet run --project hub/AnchorHub.Hub -- outlook-complete "<anchor>"` (changes the item) |
| Mirror koppter time blocks into Outlook | `dotnet run --project hub/AnchorHub.Hub -- outlook-blocks-sync < request.json` (changes the calendar; only items with category `koppter` and property `KoppterBlockId`), `-- outlook-blocks-clear --dry-run`, `-- outlook-busy --from 2026-11-16 --to 2026-11-23` |
| Mark an appointment as sideline (planned as free) | `dotnet run --project hub/AnchorHub.Hub -- outlook-sideline "<anchor>" on` (changes only the categories of the appointment; for a series the master) |
| Keep the installed daemon current | `just run-loop` (or `just loop`; `just run-loop install-chrome` for Chromium) — pull, install, wait for new commits |
| Change cache TTL | `CACHE_TTL_MS` constant in `extension/background.js` |
| View daemon logs | `%APPDATA%\AnchorHub\logs\host-YYYYMMDD.log` |
