---
status: proposed
date: 2026-04-20
decision-makers: [AnchorHub maintainers]
generated-by:
  platform: claude-code
  agent: adr-writer-madr
  skills: []
  model: claude-sonnet-4-6
---

# Mechanism for Adding Entries to a Running JabRef GUI

## Context and Problem Statement

When JabRef is running, the browser extension should add BibTeX entries
through JabRef directly (rather than JabKit writing to `.bib` files) so
that changes are immediately visible in the open library and citation-key
generation uses the user's configured pattern. Three communication paths
are possible; each has different security, portability, and complexity
tradeoffs.

This decision is scoped to the **online add** path only. The offline path
is settled in [0003-jabref-offline-handling.md](0003-jabref-offline-handling.md).

## Considered Options

* **Option A — Native messaging host (`jabrefHost.py` / equivalent)**:
  The browser extension sends BibTeX via the OS-registered native messaging
  host. JabRef receives it without opening a network port. Requires adding
  an `--import` mode to the host script.
* **Option B — HTTP endpoint (jabsrv)**:
  Use the existing `POST /libraries/current/entries` at
  `http://localhost:23119`. Already implemented. Any local process can POST
  to it; risk is limited to localhost but is real.
* **Option C — JabRef GUI CLI flag**:
  Add an `--import-bibtex` flag to JabRef's GUI launcher. The hub detects
  a running instance and invokes it as a subprocess. Requires IPC to target
  a running instance; CLI flag semantics for a GUI application are awkward.

## Decision Outcome

Not yet decided. The open questions are:

* Is the localhost security exposure of Option B acceptable, or must it be
  mitigated (e.g. API key, origin check)?
* Is native messaging registration reliable enough across Windows, macOS,
  and Linux for Option A to be the primary path?
* Does Option C require a stable IPC mechanism that does not yet exist in
  JabRef?

## Pros and Cons of the Options

### Option A — Native messaging

* Good, because no network port is opened; OS-level security.
* Good, because the extension already uses native messaging for the
  AnchorHub hub — the pattern is familiar.
* Bad, because native messaging host registration differs across Windows
  (`HKCU` registry), macOS (`~/Library/Application Support`), and Linux
  (`~/.mozilla/`); all three must be maintained.
* Bad, because `jabrefHost.py` must be extended with an import mode and
  kept in sync with JabRef releases.

### Option B — HTTP endpoint

* Good, because `POST /libraries/current/entries` already exists in jabsrv;
  no new JabRef code required.
* Good, because the extension can call it directly without the hub.
* Bad, because any process on localhost can send arbitrary BibTeX without
  authentication.
* Neutral, because adding an API key or `Origin` check to jabsrv would
  mitigate the risk with moderate effort.

### Option C — GUI CLI flag

* Good, because it avoids both network exposure and native messaging
  registration complexity.
* Bad, because detecting and targeting a running JabRef instance requires
  a stable IPC channel (e.g. lock file + socket) that does not currently
  exist.
* Bad, because invoking a GUI application via CLI for a background import
  operation is unconventional and may produce unexpected UI side-effects.

## More Information

- Predecessor decision (offline path): [0003-jabref-offline-handling.md](0003-jabref-offline-handling.md)
- jabrefHost.py (Linux native messaging host): <https://github.com/JabRef/jabref/blob/main/jabgui/buildres/linux/jabrefHost.py>
- jabsrv `EntriesResource`: `jabsrv/src/main/java/org/jabref/http/server/resources/EntriesResource.java`
- Related plan: [`plan-browser-jabref.md`](../plan-browser-jabref.md)
