# Plan: Linux port via Windows-VM relay

> **Status (2026-05-08).** All ten steps shipped; commits noted per step
> below. Linux side has not yet been exercised end-to-end — the author
> is on Windows. Loopback tests cover the relay logic without OneNote
> (`hub/AnchorHub.Hub.Tests`).

Goal: run AnchorHub on Linux against OneNote in a Windows VM (QEMU +
[WinApps](https://github.com/winapps-org/winapps)), without disturbing
the existing Windows-direct path.

**Non-goal:** any change to today's Windows UX. The Windows-direct
install (`install.ps1` → `ankd.exe` as native messaging host with the
PowerShell COM bridge) must keep working bit-for-bit.

**Scope (v1):** OneNote adapter only — what works on Windows today.
Future adapters (JabRef, Outlook, …) inherit the same transport when
they land.

---

## Key insight: pure byte-forwarder

The relay between Linux and the VM does not need to know anything about
OneNote, URL helpers, or matching logic. The on-the-wire format used
between the extension and `ankd.exe` over stdio (4-byte LE
length-prefix JSON, see `StdioProtocol.cs`) can be reused verbatim over
TCP. The Linux-side `ankd` becomes a pure pass-through; the
VM-resident `ankd` runs the same `MessageHandler` it does today.

One codebase, three startup modes selected by argv:

| Mode | Selected by | Stdio side | Backend |
|---|---|---|---|
| **direct** (Windows native, default) | no flag | Firefox ↔ MessageHandler | `LocalOneNoteBackend` (today's `OneNoteClient`) |
| **forward** (Linux, or Windows-using-VM) | `--forward=host:port` | Firefox ↔ TCP relay | (none — pure pass-through) |
| **listen** (inside the Windows VM) | `--listen=host:port` | TCP server ↔ MessageHandler | `LocalOneNoteBackend` |

Wire format on TCP = same length-prefixed JSON as stdio. Auth: a shared
random token, written into both ends at install time, sent in a
`{action:"hello", token:"…"}` framed message after connect.

---

## Architecture sketch

```
Linux host                                   Windows guest (QEMU + WinApps)
+----------+   stdio   +----------+   TCP    +----------+   PS+COM   +---------+
| Firefox  | <-------> | ankd     | <------> | ankd     | <--------> | OneNote |
|          |  (NM)     | --forward|          | --listen |            |         |
+----------+           +----------+          +----------+            +---------+
                                  ^                    ^
                                  |   QEMU hostfwd     |
                                  | tcp:127.0.0.1:9876 |
                                  +--------------------+
```

On Windows native (no VM), the picture is unchanged from today:

```
+----------+   stdio   +----------+   PS+COM   +---------+
| Firefox  | <-------> | ankd.exe | <--------> | OneNote |
+----------+           | direct   |            |         |
                       +----------+            +---------+
```

---

## Steps

Each step lists what changes and how to know it's done.

### 1. Extract transport seam — ✅ `99fc5fd`

- Generalise `NativeMessaging/StdioProtocol.cs` to take any
  `Stream` pair (rename to `MessageProtocol` if useful). Existing stdio
  callers pass `Console.OpenStandardInput()` / `OpenStandardOutput()`.
- Move `OneNoteClient` behind a new interface `IOneNoteBackend` (in
  `AnchorHub.Core`, platform-neutral). `MessageHandler` takes
  `IOneNoteBackend` via constructor instead of `new OneNoteClient()`.
- Keep `[SupportedOSPlatform("windows")]` on the implementation, drop
  it from `MessageHandler`.

**Done when:** Windows tests still green; `MessageHandler` no longer
references `OneNoteClient` directly.

### 2. Multi-target the hub csproj — ✅ `f670015`

- `<TargetFrameworks>net8.0;net8.0-windows</TargetFrameworks>` in
  `AnchorHub.Hub.csproj`.
- Conditionally include `OneNoteClient.cs` and the
  `LocalOneNoteBackend` adapter under `'$(TargetFramework)' ==
  'net8.0-windows'` only.
- Verify `dotnet publish -r linux-x64 -f net8.0` produces a binary that
  excludes the COM code.

**Done when:** Linux build succeeds without bringing in
`Microsoft.PowerShell` or COM types.

### 3. Add `--listen` mode — ✅ `f0ffafb` (refactor in `fadcb23`)

- New file `NativeMessaging/TcpListenerHost.cs`. `TcpListener` on the
  configured endpoint, accept one connection at a time (single
  consumer, same as stdio today), wrap streams in `MessageProtocol`,
  hand off to the same loop the stdio host uses with
  `LocalOneNoteBackend`.
- argv parsing in `Program.cs`: `--listen=<host:port>
  --token-file=<path>`.

**Done when:** running `ankd.exe --listen=127.0.0.1:9876
--token-file=tok` on Windows accepts a TCP client and serves
`status` / `lookup` correctly.

### 4. Add `--forward` mode — ✅ `ab97616` (refactor in `fadcb23`)

- New file `Forwarding/ForwardingHost.cs`. Reads framed messages from
  stdin, writes them to a `TcpClient` connected to the listener;
  reads framed responses back, writes them to stdout. No JSON parsing
  on this path — pure framed-byte forwarding.
- Auto-reconnect with backoff. While disconnected, intercept and
  answer `status` and `ping` actions locally with `{ok:false,
  error:{code:"DISCONNECTED"}}` so the popup can flip to orange
  promptly without waiting for a network timeout.

**Done when:** the same end-to-end flow as direct mode works through
two `ankd` processes connected by TCP.

### 5. Auth — ✅ `2761262`

- `--token-file=<path>` accepted by both `--listen` and `--forward`.
- Forwarder sends `{action:"hello", token:"…"}` as the first framed
  message after connecting; listener rejects the connection if it
  doesn't match.
- Token is a random 32-byte hex string, generated by the installer.

**Done when:** mismatched tokens are refused, matching tokens succeed.

### 6. Connectivity ping — ✅ `9e253c9` (lazy detection only; periodic ping deferred)

- `MessageHandler` already gains nothing from a separate `ping` —
  reuse the existing `status` action (no COM call).
- The popup polls `status` every N seconds while in forward mode and
  flips the icon to "not connected" (orange) within ~1 s of the VM
  going down. (Existing extension state machine already handles
  disconnected; only the trigger source changes.)

**Done when:** stopping the VM-side `ankd` flips the icon to orange
within ~1 s; restarting it flips back.

### 7. Linux install script — ✅ `b944864`

- `install/install-linux.sh`:
  - `dotnet publish -c Release -r linux-x64 --self-contained true
    -p:PublishSingleFile=true -f net8.0` → `~/.local/share/anchorhub/ankd`
  - Generate token, write to `~/.config/anchorhub/token`
  - Write Firefox native messaging manifest to
    `~/.mozilla/native-messaging-hosts/io.github.contextswitcher.anchorhub.json`,
    pointing at the binary with
    `--forward=127.0.0.1:9876 --token-file=~/.config/anchorhub/token`.
  - Print the token (or its file path) so the user can copy it into
    the VM.

**Done when:** running the script on a fresh Linux box leaves
everything ready for Firefox to find the host.

### 8. VM install variant — ✅ `fc60694`

- `install/install-vm.ps1` (runs *inside* the Windows guest):
  - Same `dotnet publish`, same `ankd.exe`.
  - Run-key launches `ankd.exe --listen=127.0.0.1:9876
    --token-file=%APPDATA%\AnchorHub\token`.
  - Read the token from a path the user pastes during install (or
    accept it as a script argument).
  - Ensure OneNote autostarts so the COM bridge has a target to
    attach to. (`OneNoteClient` already falls back to `New-Object`
    when no instance is running, so this is a UX nicety, not a
    correctness requirement.)
- QEMU side: `hostfwd=tcp:127.0.0.1:9876-:9876` in the VM launch.
- Document the WinApps interplay: the daemon should run in the VM's
  user session at all times, *not* be tied to a RemoteApp window's
  lifetime.

**Done when:** booting the VM brings up `ankd.exe --listen` and
makes the Linux popup go from orange to grey/cyan/teal as expected.

### 9. Tests — ✅ `fadcb23`

- Existing Windows tests untouched.
- New loopback integration test: spawn `ankd --listen=127.0.0.1:0`
  with a stub `IOneNoteBackend`, connect a `MessageProtocol` client,
  exercise `status` and `lookup`. Verifies the listen path without
  requiring OneNote.
- Forward-mode test: spawn listener + forwarder in-process, drive
  through the forwarder.

### 10. Documentation — ✅ this commit

- `README.md` — Linux installation section.
- `docs/requirements/anchorhub.md` — `req~ank.transport.*` reqs.
- ADR `docs/decisions/0006-linux-port-via-vm-relay.md` — why VM-relay
  over Microsoft Graph, Wine, or RDP automation.

---

## Open questions

- **VM lifecycle**: always-on QEMU guest (autostart at login,
  daemon + OneNote running 24/7) vs. WinApps on-demand boot? The
  on-demand path adds 10–30 s before the first lookup of each
  session resolves; the always-on path costs background RAM. Pick
  one for the README's "recommended setup" section.
- **OneNote-in-VM autostart strategy**: does WinApps keep OneNote
  alive in the background, or only while a RemoteApp window is open?
  If the latter, `ankd --listen` will sometimes need to `New-Object
  OneNote.Application` itself; verify this surfaces a window in the
  guest's user session that WinApps will then forward.
- **Token distribution UX**: copy-paste during setup, or a tiny
  helper that writes both files via SSH / virsh / shared-folder?
  Probably copy-paste in v1 — installs are one-time.
- **Bind address for `--listen`**: ~~localhost-only inside the guest
  vs. all interfaces~~ **Resolved (2026-07-07): must bind all interfaces
  (`0.0.0.0`).** QEMU user-mode (slirp) `hostfwd` delivers forwarded
  connections to the guest's NAT interface (e.g. `10.0.2.15`), *not*
  loopback, so a `127.0.0.1` binding is unreachable from the host and the
  popup stays orange. `install-vm.ps1` now defaults to `0.0.0.0:9876` and
  adds a Windows Firewall inbound rule for the port (the NAT-facing SYN is
  otherwise silently dropped — a hang, not a refusal). The guest stays
  NAT-isolated: reachable only via the host's hostfwd. Confirmed by a
  framed-`hello` probe timing out against a loopback-bound listener while
  the guest's `0.0.0.0`-bound RDP forward answered normally.

---

## Out of scope for v1

- Adapters other than OneNote.
- Cross-machine relay (Linux laptop → Windows desktop on the LAN).
  Same code would work, but the security model (LAN-exposed TCP)
  needs more thought than localhost-via-hostfwd.
- macOS host (different VM tooling; revisit once Linux works).
- Replacing the relay with Microsoft Graph or a Wine port — see
  ADR 0006 for why those are not the v1 choice.
