# AnchorHub — Daemon Core Spec (v0.3, C# / .NET 10, JIT daemon)

> **Revision note.** v0.2 proposed daemon-AOT; v0.3 reverts the daemon to JIT after finding that Windows NativeAOT does not support built-in COM, which would cripple the Outlook and OneNote adapters. Shim stays NativeAOT — that's where cold-start latency actually matters. All other open questions resolved. Deployment strategy updated to self-contained single-file publish so the "no runtime install" benefit is preserved.

## Decisions settled in v0.3

| Question                      | Decision                                                   |
|-------------------------------|-----------------------------------------------------------|
| NativeAOT scope               | Shim AOT, daemon JIT + R2R + SelfContained                |
| Adapters                      | Project references, no runtime plugin loading             |
| Installer (v0.1)              | Portable ZIP + `install.ps1` / `uninstall.ps1`           |
| Installer (v0.2 public)       | WiX v5 MSI                                                |
| Default hotkey (capture)      | `Ctrl+Shift+H`                                            |
| Default hotkey (popup)        | `Ctrl+Shift+Alt+H`                                        |
| Future UI framework           | Avalonia                                                  |
| Logging                       | Serilog                                                   |
| Config format                 | TOML (Tomlyn)                                             |
| .NET minimum                  | .NET 10 Desktop Runtime, bundled via self-contained       |
| ORM / DB access               | Dapper (regular, since daemon is JIT)                     |

## Overview

AnchorHub is a Windows daemon that makes every addressable artifact across your apps reachable through a single stable URI scheme: `anchor://`. Clicking an `anchor://` URL anywhere hands it to AnchorHub, which routes to the right adapter and brings the target to the foreground. A global hotkey performs the inverse capture.

- **`anchor_shim.exe`** — NativeAOT console executable (~5 MB). Registered as the `anchor://` URL handler. Windows spawns it on every URL click; it connects to the running daemon over a named pipe and exits.
- **`anchorhubd.exe`** — .NET 10 JIT daemon, long-lived, published self-contained with ReadyToRun (~60–80 MB single file). Holds adapters, DB connection, IPC server, hotkey listener. Runs at user login.

## Architecture at a glance

```
 +----------------+         +-----------------------+
 | URL click      |  %1     | anchor_shim.exe       |
 | (any app)      +-------->| NativeAOT, <5MB       |
 +----------------+         | HKCU URL handler      |
                            +----------+------------+
                                       | named pipe
                                       v
 +-------------+   hotkey    +---------+---------------+
 | Global      +------------>| anchorhubd.exe          |
 | hotkey      |             | .NET 10 JIT + R2R       |
 +-------------+             | SelfContained, single   |
                             | file, ~60 MB            |
                             |                         |
                             |  Generic Host +         |
                             |  adapters (project refs)|
                             |  Dapper + SQLite        |
                             |  NamedPipeServer        |
                             |  WM_HOTKEY pump         |
                             +-------------------------+
```

## Solution layout

```
AnchorHub/
├── AnchorHub.sln
├── src/
│   ├── AnchorHub.Core/              # IAdapter, Anchor, DTOs (net10.0, no Win deps)
│   ├── AnchorHub.Shim/              # NativeAOT (net10.0-windows)
│   ├── AnchorHub.Daemon/            # Generic-Host app (net10.0-windows)
│   ├── AnchorHub.Storage/           # Microsoft.Data.Sqlite + Dapper
│   ├── AnchorHub.Ipc/               # Named pipe protocol DTOs, source-gen JSON
│   ├── AnchorHub.Platform/          # P/Invoke: RegisterHotKey, message-only window,
│   │                                #   WindowsIdentity SID lookup, UIA helpers
│   └── Adapters/
│       ├── AnchorHub.Adapter.Explorer/
│       ├── AnchorHub.Adapter.Outlook/
│       ├── AnchorHub.Adapter.OneNote/
│       └── AnchorHub.Adapter.Jabref/
├── tests/
│   └── AnchorHub.Tests/             # xUnit + FluentAssertions
├── scripts/
│   ├── install.ps1                  # v0.1 installer
│   └── uninstall.ps1                # v0.1 uninstaller
└── installers/                      # v0.2+
    └── AnchorHub.Installer/         # WiX v5 MSI
```

Key simplification vs v0.2: `AnchorHub.Daemon.csproj` references each adapter project directly (`<ProjectReference Include="..\Adapters\AnchorHub.Adapter.Outlook\..." />`). No `AssemblyLoadContext`, no plugin discovery, no manifest files. Adding an adapter = adding a project reference and recompiling.

## 1. URL-scheme registration

### Registry keys (HKCU, per-user)

```reg
Windows Registry Editor Version 5.00

[HKEY_CURRENT_USER\Software\Classes\anchor]
@="URL:AnchorHub Protocol"
"URL Protocol"=""

[HKEY_CURRENT_USER\Software\Classes\anchor\DefaultIcon]
@="\"%LOCALAPPDATA%\\AnchorHub\\anchor_shim.exe\",0"

[HKEY_CURRENT_USER\Software\Classes\anchor\shell\open\command]
@="\"%LOCALAPPDATA%\\AnchorHub\\anchor_shim.exe\" \"%1\""
```

Written by `install.ps1` via `New-Item` / `Set-ItemProperty`; removed by `uninstall.ps1`.

### Shim binary — NativeAOT

```xml
<!-- AnchorHub.Shim.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <IlcOptimizationPreference>Size</IlcOptimizationPreference>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
</Project>
```

Output: ~5 MB, no runtime dependency, <50ms cold start. All JSON serialization via `System.Text.Json` with source-generated `JsonSerializerContext` (NativeAOT-compatible).

Shim flow:

```csharp
static async Task<int> Main(string[] args)
{
    if (args.Length != 1 || !args[0].StartsWith("anchor://", StringComparison.Ordinal))
        return 2;

    var sid = WindowsIdentity.GetCurrent().User!.Value;
    var pipeName = $"anchorhubd-{sid}";

    await using var client = new NamedPipeClientStream(
        ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    try { await client.ConnectAsync(TimeSpan.FromSeconds(2)); }
    catch (TimeoutException)
    {
        LaunchDaemonDetached();
        await client.ConnectAsync(TimeSpan.FromSeconds(5));
    }

    var request = JsonSerializer.Serialize(
        new ResolveRequest(args[0]), IpcJsonContext.Default.ResolveRequest);
    await WriteLineAsync(client, request);
    var response = JsonSerializer.Deserialize(
        await ReadLineAsync(client), IpcJsonContext.Default.Response);

    return response?.Ok == true ? 0 : 1;
}
```

## 2. URI grammar

Unchanged from v0.1/v0.2. Recapping for self-containment:

```
anchor://<adapter>/<resource-type>/<id>[?query][#fragment]
```

Reserved query keys: `title`, `hint`, `captured`, `v`. Adapter-specific extras must be prefixed with `x-`. Canonicalization strips `title` / `hint` from identity; they live in `anchors.title` / `anchors.meta`. If resolution fails, the daemon shows a toast with the `title` so the user knows what the link was.

Examples:

```
anchor://outlook/message/AAMkAD%2Flong...?title=Q4+plan+review&v=1
anchor://onenote/page/%7Babc-def-123%7D?title=Kickoff+notes&v=1
anchor://explorer/file/0x0123456789ABCDEF?path=C%3A%5C...&title=doc.pdf&v=1
anchor://vscode/file/C%3A%2Fsrc%2Fmain.py?line=42&title=main.py%3A42&v=1
anchor://chrome/tab/?url=https%3A%2F%2Fexample.com&text=selected+quote&title=Example&v=1
anchor://jabref/entry/Smith2023quantum?title=Smith+2023+%E2%80%94+Quantum+foo&v=1
```

## 3. Storage

### Library stack

- `Microsoft.Data.Sqlite` (raw connection + native SQLite library)
- **Dapper (regular, not Dapper.AOT)** — daemon is JIT, so runtime IL-emit is fine
- No EF Core (overkill for this schema)

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.*" />
  <PackageReference Include="Dapper"                Version="2.*" />
</ItemGroup>
```

### Schema (unchanged from v0.1/v0.2)

```sql
CREATE TABLE anchors (
    uri         TEXT PRIMARY KEY,
    adapter     TEXT NOT NULL,
    resource    TEXT NOT NULL,
    id          TEXT NOT NULL,
    title       TEXT,
    first_seen  TEXT NOT NULL,
    last_seen   TEXT NOT NULL,
    meta        TEXT
);
CREATE INDEX idx_anchors_adapter ON anchors(adapter);

CREATE TABLE links (
    source      TEXT NOT NULL REFERENCES anchors(uri),
    relation    TEXT NOT NULL,
    target      TEXT NOT NULL REFERENCES anchors(uri),
    created     TEXT NOT NULL,
    note        TEXT,
    PRIMARY KEY (source, relation, target)
);
CREATE INDEX idx_links_target ON links(target);

CREATE TABLE bookmarks (
    uri         TEXT PRIMARY KEY REFERENCES anchors(uri),
    pinned      INTEGER NOT NULL DEFAULT 0,
    tags        TEXT,
    note        TEXT,
    created     TEXT NOT NULL
);

CREATE TABLE resolves (
    uri         TEXT NOT NULL REFERENCES anchors(uri),
    resolved_at TEXT NOT NULL,
    success     INTEGER NOT NULL,
    branch      TEXT,
    duration_ms INTEGER
);

CREATE TABLE _meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
```

### Canonicalization

1. Lowercase `scheme`, `adapter`, `resource-type`.
2. Sort query keys alphabetically.
3. Normalize percent-encoding.
4. Strip `title` and `hint` from the key — they're metadata, not identity. Store in `anchors.title` / `anchors.meta`.

### Location

```
%APPDATA%\AnchorHub\anchorhub.db            # user data: links, bookmarks
%APPDATA%\AnchorHub\config.toml             # user config
%APPDATA%\AnchorHub\logs\                   # rotated logs (14-day retention)
%LOCALAPPDATA%\AnchorHub\                   # binaries
  ├── anchorhubd.exe
  ├── anchor_shim.exe
  └── scripts\
      ├── install.ps1
      └── uninstall.ps1
```

Split rationale: `%APPDATA%` roams (if domain-joined); `%LOCALAPPDATA%` doesn't. Binaries never roam.

### Migrations

Embedded SQL resources `Migrations\0001_initial.sql`, `0002_*.sql`, etc. On startup:
1. Read `_meta.db_schema_version` (default 0 if missing).
2. Execute all migrations with ordinal > current, in ascending order, each in its own transaction.
3. Update `_meta.db_schema_version`.

### RDF/Turtle export

`anchorhubd.exe export --format=turtle > graph.ttl` produces:

```turtle
@prefix anchor: <anchor://> .
@prefix hub:    <https://anchorhub.local/vocab#> .

<anchor://outlook/message/AAMkAD...>
    hub:title    "Q4 plan review" ;
    hub:adapter  "outlook" ;
    hub:firstSeen "2026-04-22T14:30:00Z" .

<anchor://outlook/message/AAMkAD...>
    hub:related <anchor://onenote/page/{abc-def-123}> .
```

Honors the linked-data discipline without RDF runtime machinery.

## 4. Adapter loading (simplified — project references)

### `IAdapter` contract (unchanged shape, simpler lifecycle)

Defined in `AnchorHub.Core`:

```csharp
public interface IAdapter
{
    string Name { get; }
    string Version { get; }
    IReadOnlyList<string> ResourceTypes { get; }
    IReadOnlyList<string> NativeUrlSchemes { get; }
    int Priority { get; }

    Task<bool> ProbeAsync(CancellationToken ct);
    Task<Anchor?> CaptureAsync(FocusContext context, CancellationToken ct);
    Task<ResolutionResult> ResolveAsync(Anchor anchor, CancellationToken ct);
    Task<AnchorDescription?> DescribeAsync(Anchor anchor, CancellationToken ct);
}
```

Shared records in `Core`:

```csharp
public sealed record FocusContext(IntPtr Hwnd, string ProcessName, string WindowTitle, string? UiTreeSummary);
public sealed record Anchor(string Uri, string Adapter, string Resource, string Id,
                            string? Title, int Version, IDictionary<string, string> Query);
public sealed record ResolutionResult(bool Success, string Branch, TimeSpan Duration, string? Error);
public sealed record AnchorDescription(string? Title, string? Preview, bool Exists, DateTimeOffset? Modified);
```

### Registration via DI (not plugin discovery)

Each adapter project registers itself by convention. In `AnchorHub.Daemon\Program.cs`:

```csharp
builder.Services
    .AddAnchorAdapter<ExplorerAdapter>()
    .AddAnchorAdapter<OutlookAdapter>()
    .AddAnchorAdapter<OneNoteAdapter>()
    .AddAnchorAdapter<JabrefAdapter>();
```

Where:

```csharp
public static IServiceCollection AddAnchorAdapter<TAdapter>(this IServiceCollection services)
    where TAdapter : class, IAdapter
{
    services.AddSingleton<IAdapter, TAdapter>();
    return services;
}
```

`AdapterRegistry` (a singleton) is built from `IEnumerable<IAdapter>` resolved from DI at startup. Each adapter's `ProbeAsync` is called once; only those returning `true` become active. Adding an adapter to AnchorHub is three steps:
1. Create a new class-library project in `src/Adapters/`
2. Add `ProjectReference` to it in `AnchorHub.Daemon.csproj`
3. Add one line to `Program.cs` calling `AddAnchorAdapter<YourAdapter>()`

That's the entirety of the "adapter loading" story. No `AssemblyLoadContext`, no manifest files, no plugin discovery.

### Resource-type routing

When a URI `anchor://outlook/message/...` arrives, `AdapterRegistry` finds adapters advertising `Name == "outlook"` AND `ResourceTypes.Contains("message")`. If multiple match, highest `Priority` wins. On capture, all active adapters get `CaptureAsync` called with the current `FocusContext`; the one returning a non-null `Anchor` with highest priority wins.

## 5. Daemon process model

### Hosting

`Microsoft.Extensions.Hosting` Generic Host with DI, config, and Serilog:

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddTomlFile(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                 "AnchorHub", "config.toml"),
    optional: true);

builder.Services.AddSingleton<IAnchorRepository, SqliteAnchorRepository>();
builder.Services.AddSingleton<AdapterRegistry>();
builder.Services.AddHostedService<PipeServerService>();
builder.Services.AddHostedService<HotkeyService>();

builder.Services
    .AddAnchorAdapter<ExplorerAdapter>()
    .AddAnchorAdapter<OutlookAdapter>()
    .AddAnchorAdapter<OneNoteAdapter>()
    .AddAnchorAdapter<JabrefAdapter>();

builder.Logging.ClearProviders();
builder.Services.AddSerilog((_, lc) => lc
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.File(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "AnchorHub", "logs", "anchorhub-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14));

var host = builder.Build();
await host.RunAsync();
```

### Publish configuration (self-contained single-file)

```xml
<!-- AnchorHub.Daemon.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishReadyToRun>true</PublishReadyToRun>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
    <DebuggerSupport>false</DebuggerSupport>
    <!-- Trim conservatively — daemon is JIT, aggressive trim not needed -->
    <PublishTrimmed>false</PublishTrimmed>
  </PropertyGroup>
</Project>
```

Expected output: single `anchorhubd.exe`, ~60–80 MB, no .NET runtime install required on target machines. ReadyToRun precompilation gives the daemon ~150ms cold start (the first-run single-file extraction adds ~300ms one time; subsequent starts are fast).

### Single-instance enforcement

```csharp
var sid = WindowsIdentity.GetCurrent().User!.Value;
using var mutex = new Mutex(initiallyOwned: true, $"Global\\AnchorHubDaemon-{sid}", out var created);
if (!created)
{
    // Another instance running — optionally ping it via pipe to bring UI forward, then exit.
    return 0;
}
```

### IPC: named pipe

- Pipe name: `anchorhubd-{user-SID}`.
- Server: `NamedPipeServerStreamAcl.Create` with `PipeSecurity` granting `PipeAccessRights.ReadWrite` to the current user SID only.
- Framing: UTF-8 NDJSON (one request per line, one response per line).
- Serialization: `System.Text.Json` with source-generated contexts (shared between shim and daemon).

### Message types

```jsonc
{ "method": "resolve",  "uri": "anchor://..." }
  → { "ok": true, "branch": "api" }

{ "method": "capture" }
  → { "ok": true, "anchor": "anchor://outlook/message/..." }

{ "method": "links",    "uri": "anchor://...", "direction": "both" }
  → { "ok": true, "links": [...] }

{ "method": "bookmark", "uri": "anchor://...", "tags": ["project-x"], "note": "..." }
{ "method": "unbookmark","uri": "anchor://..." }
{ "method": "bookmarks","filter": { "pinned": true, "tag": "project-x" } }
  → { "ok": true, "items": [...] }

{ "method": "adapters.list" }
  → { "ok": true, "adapters": [{ "name": "outlook", "active": true, "branches": [...] }, ...] }

{ "method": "ping" }
  → { "ok": true, "pong": "..." }
```

Error shape: `{ "ok": false, "error": { "code": "...", "message": "..." } }`.

### Global hotkey (no WinForms/WPF)

`RegisterHotKey` via P/Invoke, on a dedicated STA thread running a message-only window (`HWND_MESSAGE`):

```csharp
internal sealed class HotkeyService : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct)
    {
        var thread = new Thread(Pump) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(ct);
        return Task.CompletedTask;
    }

    private static void Pump(object? state)
    {
        var hwnd = CreateMessageOnlyWindow();
        RegisterHotKey(hwnd, HOTKEY_CAPTURE, MOD_CONTROL | MOD_SHIFT, VK_H);
        RegisterHotKey(hwnd, HOTKEY_POPUP,   MOD_CONTROL | MOD_SHIFT | MOD_ALT, VK_H);

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == WM_HOTKEY)
            {
                switch ((int)msg.wParam)
                {
                    case HOTKEY_CAPTURE: _ = CaptureAsync(); break;
                    case HOTKEY_POPUP:   _ = ShowPopupAsync(); break;
                }
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }
}
```

Default bindings: `Ctrl+Shift+H` (capture → anchor on clipboard + toast), `Ctrl+Shift+Alt+H` (open popup). Both configurable via `config.toml`.

### Autostart

```csharp
using var run = Registry.CurrentUser.OpenSubKey(
    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)!;
run.SetValue("AnchorHub",
    $"\"{Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\AnchorHub\anchorhubd.exe")}\" --autostart");
```

`--autostart` suppresses first-run welcome and starts the daemon hidden.

## 6. Security and privacy

- **Local-only.** Named pipe only, no TCP.
- **Pipe ACL** scoped to current user SID via `PipeSecurity`.
- **No telemetry** in v0.1.
- **Plaintext SQLite** — email subjects, window titles, filenames appear in `anchors.title` and `anchors.meta`. Document prominently. SQLCipher optional in v0.2+.
- **UIA / OCR fallback** opt-in per adapter via `config.toml`. Some adapters (Outlook classic, OneNote) may fall back to reading on-screen content when APIs are unavailable; those adapters log a line for every fallback so the user can see what's happening.
- **Adapters share the daemon's process and identity.** They run as the user, with whatever COM/Graph/API permissions the user has. No sandboxing between adapters (acceptable since all adapter code is first-party, statically linked).

## 7. Versioning and migration

### Anchor URI version (`?v=<int>`)

Current: `v=1`. When an adapter needs a breaking ID-format change:

```csharp
public interface IMigratingAdapter : IAdapter
{
    Task<Anchor?> MigrateAsync(Anchor oldAnchor, CancellationToken ct);
}
```

On startup, the adapter registry asks each migrating adapter to scan `anchors` for older-`v` rows and rewrite them.

### DB schema version

Managed by the embedded-SQL migrations mechanism in §3.

## 8. Out of scope for v0.1

- **Sync** (multi-machine) — all data local.
- **Mobile** — Windows only.
- **Multi-user** — per-user install, no cross-Windows-account sharing.
- **UI** — bookmarks window, capture popup, settings window. Specified separately; will use Avalonia.
- **Adapter implementations** — each specified in its own doc.
- **Cloud auth flows** — Graph/MSAL for Outlook-new and OneNote-cloud. Handled by those adapters in their own specs.

## 9. Installer for v0.1 — portable ZIP

### Layout inside the ZIP

```
AnchorHub-v0.1.0-win-x64.zip
├── anchorhubd.exe                   # self-contained single-file
├── anchor_shim.exe                  # NativeAOT
├── install.ps1
├── uninstall.ps1
├── LICENSE.txt
└── README.txt
```

### `install.ps1` (outline)

```powershell
#Requires -Version 5.1

$ErrorActionPreference = 'Stop'

$installRoot = Join-Path $env:LOCALAPPDATA 'AnchorHub'
$dataRoot    = Join-Path $env:APPDATA      'AnchorHub'
$here        = Split-Path -Parent $MyInvocation.MyCommand.Path

# 1. Copy binaries
New-Item -Force -ItemType Directory -Path $installRoot, $dataRoot, "$dataRoot\logs" | Out-Null
Copy-Item "$here\anchorhubd.exe",   "$installRoot\anchorhubd.exe"   -Force
Copy-Item "$here\anchor_shim.exe",  "$installRoot\anchor_shim.exe"  -Force
Copy-Item "$here\uninstall.ps1",    "$installRoot\uninstall.ps1"    -Force

# 2. Register anchor:// URL scheme (HKCU, no admin)
$anchorKey = 'HKCU:\Software\Classes\anchor'
New-Item -Force -Path $anchorKey | Out-Null
Set-ItemProperty -Path $anchorKey -Name '(default)' -Value 'URL:AnchorHub Protocol'
Set-ItemProperty -Path $anchorKey -Name 'URL Protocol' -Value ''

$cmdKey = "$anchorKey\shell\open\command"
New-Item -Force -Path $cmdKey | Out-Null
Set-ItemProperty -Path $cmdKey -Name '(default)' -Value "`"$installRoot\anchor_shim.exe`" `"%1`""

# 3. Register autostart
Set-ItemProperty `
    -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'AnchorHub' `
    -Value "`"$installRoot\anchorhubd.exe`" --autostart"

# 4. Launch daemon
Start-Process -FilePath "$installRoot\anchorhubd.exe" -ArgumentList '--autostart' -WindowStyle Hidden

Write-Host "AnchorHub installed to $installRoot. Data in $dataRoot."
Write-Host "Press Ctrl+Shift+H in any app to capture. Paste the anchor:// URL anywhere."
```

### `uninstall.ps1` (outline)

```powershell
#Requires -Version 5.1

$ErrorActionPreference = 'Continue'
$installRoot = Join-Path $env:LOCALAPPDATA 'AnchorHub'
$dataRoot    = Join-Path $env:APPDATA      'AnchorHub'

# 1. Ask daemon to shut down (if running)
try {
    $sid      = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $pipeName = "anchorhubd-$sid"
    # Connect and send { "method": "shutdown" } — best-effort
    # ...
} catch { }

# 2. Kill any remaining process
Get-Process -Name 'anchorhubd' -ErrorAction SilentlyContinue | Stop-Process -Force

# 3. Remove autostart
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AnchorHub' -ErrorAction SilentlyContinue

# 4. Remove URL handler
Remove-Item -Recurse -Force 'HKCU:\Software\Classes\anchor' -ErrorAction SilentlyContinue

# 5. Remove binaries
Remove-Item -Recurse -Force $installRoot -ErrorAction SilentlyContinue

# 6. Ask before deleting data
$keep = Read-Host "Delete user data at $dataRoot ? (y/N)"
if ($keep -eq 'y') { Remove-Item -Recurse -Force $dataRoot -ErrorAction SilentlyContinue }

Write-Host "AnchorHub uninstalled."
```

Notes:
- Both scripts are unsigned in v0.1; users will need to run PowerShell with `-ExecutionPolicy Bypass` on first invocation, or unblock the scripts (`Unblock-File install.ps1`). Document this in README.
- MSI (WiX v5) replaces these scripts in v0.2.

## 10. Build order (revised)

1. **Solution scaffold.** Create projects per §Solution layout. Verify `AnchorHub.Shim` publishes as NativeAOT EXE; `AnchorHub.Daemon` publishes as self-contained R2R single-file EXE.
2. **URL-scheme plumbing spike.** Write `install.ps1`, register the scheme. Daemon logs every received URI via a trivial `PipeServerService`. Click `anchor://test` in any app → daemon log shows it.
3. **IPC protocol.** DTOs in `AnchorHub.Ipc` with source-generated JSON contexts (shared between shim and daemon). Full pipe server/client with error handling. `resolve` round-trips end-to-end.
4. **Storage.** Dapper + SQLite + migrations from embedded SQL. xUnit tests against a temp SQLite file. Verify canonicalization rules.
5. **Adapter contract + DI registration.** `IAdapter`, records, `AdapterRegistry`. Ship a dummy `EchoAdapter` that "resolves" `anchor://echo/noop/<x>` by popping a message box with the title.
6. **Explorer adapter.** Full path + NTFS ObjectID. Resolve via `SHParseDisplayName` + `SHOpenFolderAndSelectItems`. Proves the contract end-to-end without COM or Graph.
7. **Hotkey capture.** `HotkeyService` with P/Invoke'd message-only window. Capture from Explorer puts `anchor://explorer/...` on the clipboard.
8. **Outlook adapter (three branches).** Classic COM, New Outlook + Graph + MSAL, UIA fallback. The architecture's proof point.
9. **OneNote adapter.** Port OneNoteHub's lookup logic to the new contract.
10. **CLI + minimal popup.** `anchorhubd.exe bookmarks list/add/search`, and a simple Avalonia popup (separate exe) triggered by `Ctrl+Shift+Alt+H`, talking to the daemon over the existing pipe.

Everything beyond step 10 is v0.2+: WiX MSI installer, JabRef adapter, Chrome/CDP adapter, mobile companion, sync.

## Appendix: NuGet packages (v0.1 target list)

| Package                         | Where used          | Why                                          |
|---------------------------------|---------------------|----------------------------------------------|
| `Microsoft.Extensions.Hosting`  | Daemon              | Generic Host, DI, config, lifecycle          |
| `Microsoft.Data.Sqlite`         | Storage             | SQLite provider                              |
| `Dapper`                        | Storage             | Lightweight mapping (regular, not AOT)       |
| `Serilog.Extensions.Hosting`    | Daemon              | Structured logging integration               |
| `Serilog.Sinks.File`            | Daemon              | File output with rolling                     |
| `Tomlyn`                        | Daemon              | TOML config parsing                          |
| `System.IO.Pipes.AccessControl` | Daemon              | `PipeSecurity` for ACL'd named pipes         |
| `Microsoft.Identity.Client`     | Outlook adapter     | MSAL for Graph auth (step 8)                 |
| `Microsoft.Graph`               | Outlook adapter     | Graph SDK (step 8)                           |
| `xunit`, `FluentAssertions`     | Tests               | Test stack                                   |

No ASP.NET Core, no WinForms, no WPF. No Dapper.AOT needed (daemon is JIT). Avalonia enters at step 10 (popup UI), not before.

## What changed from v0.2

- **Daemon runtime**: AOT → JIT (+ R2R + SelfContained single-file). COM compatibility restored.
- **Adapter loading**: `AssemblyLoadContext` plugin model → project references + DI registration. Significantly simpler.
- **Dapper**: Dapper.AOT → regular Dapper (JIT supports runtime IL-emit).
- **Installer**: WiX MSI → portable ZIP + PowerShell scripts for v0.1; MSI deferred to v0.2.
- **Hotkey**: `Ctrl+Shift+,` → `Ctrl+Shift+H` (mnemonic = Hub).
- **Open questions section**: removed; all decisions consolidated into the table at the top.

All other sections (URI grammar, storage schema, security posture, versioning) unchanged from v0.2.
