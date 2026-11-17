---
status: proposed
date: 2026-05-07
decision-makers: [AnchorHub maintainers]
generated-by:
  platform: claude-code
  agent: (none — author-driven)
  skills: []
  model: claude-opus-4-7
---

# Linux port via TCP relay to a Windows-VM-resident COM bridge

## Context and Problem Statement

AnchorHub's OneNote adapter depends on the OneNote desktop application's
COM API, reached via a persistent PowerShell child process
(`OneNoteClient.cs`). PowerShell is required because .NET's COM interop
cannot access Click-to-Run Office type libraries directly (they live in
a registry hive only visible to Office processes).

OneNote desktop has no native Linux build. Linux users who want
AnchorHub need a way for the Firefox-side native messaging host on
Linux to talk to OneNote running somewhere else — without breaking the
frictionless Windows-direct install or fragmenting the codebase.

The user has chosen to run OneNote inside a Windows VM via QEMU and
[WinApps](https://github.com/winapps-org/winapps), which exposes
individual Windows applications as if they were native Linux windows.
This decision is about how AnchorHub crosses the host/guest boundary,
not whether to use a VM.

## Considered Options

* **Option A — TCP relay between Linux `ankd` and a VM-resident
  `ankd.exe`** that runs the existing PowerShell COM bridge unchanged.
  One codebase, three startup modes (direct / forward / listen), same
  wire format on TCP as on stdio.
* **Option B — Microsoft Graph API for OneNote** from a native Linux
  daemon. Replaces the COM bridge entirely with HTTPS calls, requires
  OAuth/MSAL, and only works for notebooks stored in OneDrive /
  SharePoint.
* **Option C — Run the existing Windows daemon under Wine** on Linux.
  No VM; one process; reuses the .NET 8 binary. Depends on Wine's
  ability to host the OneNote COM server.
* **Option D — Drive OneNote-for-Web via a headless browser** (e.g.
  Playwright on Linux). DOM scraping replaces COM.

## Decision Outcome

Chosen option: **Option A — TCP relay**, because:

- It reuses 100% of the OneNote adapter, the URL helpers, and the
  matching logic that already work on Windows. Zero behavioural drift
  between platforms.
- The Windows-direct install path is unchanged: `--forward` and
  `--listen` are alternative argv-selected modes; the default on
  Windows stays direct (in-process COM, no TCP).
- The on-the-wire protocol between Linux `ankd` and VM `ankd.exe` is
  the same length-prefixed JSON the extension already speaks over
  stdio, so the Linux-side process is a pure byte-forwarder with no
  business logic.
- The VM is a known-good environment for the COM bridge — the same one
  the user develops on. No new failure modes from running OneNote
  somewhere it has never run.

Trade-off accepted: every browser hover round-trips through the VM. In
practice this is a small (~5–30 ms) overhead invisible to the user, and
the extension's 24 h result cache means most hovers don't reach the
daemon at all.

## Pros and Cons of the Options

### Option A — TCP relay to VM-resident daemon

* Good, because no logic moves: same `MessageHandler`, same
  `OneNoteClient`, same per-host helpers run inside the VM.
* Good, because Windows-direct install stays bit-for-bit identical.
* Good, because the wire format is already specified and tested
  (length-prefixed JSON), so the relay is trivial.
* Good, because the security boundary is localhost via QEMU
  `hostfwd` — no LAN-exposed port.
* Bad, because the user must run a Windows VM. Cost: disk space, RAM,
  occasional Windows updates.
* Bad, because lookups fail when the VM is off or suspended. Mitigated
  by the disconnected (orange) icon state, which already exists.
* Bad, because the VM lifecycle adds UX surface area (always-on vs.
  on-demand, OneNote autostart, etc.).

### Option B — Microsoft Graph API

* Good, because no VM. Pure Linux daemon, smaller deployment.
* Good, because the same Graph API works on Windows and macOS too;
  long-term it may obsolete the COM bridge entirely.
* Bad, because it only supports notebooks in OneDrive / SharePoint.
  Local notebooks (`.one` files outside OneDrive) are invisible to
  Graph. The user's current notebooks need to be inventoried before
  this is even feasible.
* Bad, because the in-page URL match strategy depends on OneNote's
  local search index. Graph search has different ranking, recall, and
  rate limits, so the matching logic would need to be reimplemented
  and re-tuned. Significant behavioural drift between platforms.
* Bad, because OAuth + MSAL adds installation friction (browser
  redirect, app registration, token refresh) that the COM path avoids.
* Bad, because it doesn't help the "open the page in the local OneNote
  app" UX — Graph can deep-link into OneNote-for-Web, not into the
  desktop app.

### Option C — Wine

* Good, because no VM. One binary, one process, runs natively on the
  Linux desktop.
* Good, because in principle Click-to-Run Office on Wine + AnchorHub
  on Wine would replicate the Windows-direct path exactly.
* Bad, because OneNote-on-Wine support is unmaintained and historically
  fragile. The PowerShell COM trick that already works around an Office
  quirk on Windows is unlikely to survive Wine's COM emulation.
* Bad, because debugging "is this an AnchorHub bug or a Wine bug?"
  burns time disproportionately. Three-way blame between AnchorHub,
  Office, and Wine is a research project, not a port.
* Bad, because OneNote in Wine still has no OneNote process to talk
  to unless OneNote itself is also in Wine — at which point the user
  is approximating a VM with worse fidelity.

### Option D — Drive OneNote-for-Web headlessly

* Good, because no VM and no Office license needed for the daemon
  host.
* Bad, because OneNote-for-Web's DOM is private and changes without
  notice. Every Microsoft UI tweak is a regression risk.
* Bad, because authentication flows in a headless browser are hostile
  (MFA, captchas, cookie expiry).
* Bad, because performance: page loads + script execution dwarf the
  cost of a TCP round-trip, and the bridge would need to maintain a
  hot browser instance to stay competitive. More moving parts than
  the VM.
* Bad, because the matching logic still needs to be reimplemented
  against whatever search OneNote-for-Web exposes — same drift problem
  as Option B.

## More Information

- Plan tracking the implementation: [`plan-linux-port.md`](../plan-linux-port.md)
- Related code: `hub/AnchorHub.Hub/NativeMessaging/OneNoteClient.cs`
  (the PowerShell COM bridge that moves into the VM unchanged),
  `hub/AnchorHub.Hub/NativeMessaging/StdioProtocol.cs` (the wire format
  reused over TCP).
- Spec for the target architecture: [`anchorhub-spec.md`](../anchorhub-spec.md).
  The shim/daemon split there generalises to a forward/listen split
  here; once the spec is folded forward, this ADR becomes part of
  that document's transport section.
- WinApps: <https://github.com/winapps-org/winapps>
