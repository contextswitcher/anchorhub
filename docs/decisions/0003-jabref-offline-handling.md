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

# Handle JabRef Being Offline in the Browser Extension

## Context and Problem Statement

The browser extension interacts with JabRef for two operations: checking
whether a DOI is already in the library, and adding a new BibTeX entry.
JabRef may not be running when the user is browsing. The extension must
degrade gracefully and still provide value in the offline case. When JabRef
is running, the preferred path is always to go through JabRef directly so
the GUI reflects changes immediately.

## Considered Options

* **Option 1 — Start JabRef on demand**: Use the OS-level native messaging
  host (`jabrefHost.py` / equivalent) to launch JabRef automatically when
  needed.
* **Option 2 — BibTeX queue with deferred push**: Store BibTeX in
  `browser.storage.local` when offline; push via
  `POST /libraries/current/entries` when JabRef reconnects.
* **Option 3 — Queue + JabKit for all offline operations**: JabKit handles
  both read and write without JabRef running:
  - **DOI lookup**: `jabkit search --input <bib-file> --query "doi:<doi>"`
  - **BibTeX fetch**: `jabkit doi-to-bibtex <doi>`
  - **Add entry**: `jabkit import --input <bib-file> <bibtex>` *(command to
    be added to JabKit)*

## Decision Outcome

Chosen option: **Option 3** for the offline path, because it handles DOI
lookup, BibTeX fetching, and entry import without requiring the JabRef GUI.
JabKit's existing `search` and `doi-to-bibtex` commands already provide
two of the three primitives; `import` is a natural addition.

When JabRef GUI **is** running, it takes priority over JabKit for all
operations so changes are reflected immediately in the open library. The
mechanism for communicating with the running GUI is a separate open
decision — see [0004-jabref-online-add-mechanism.md](0004-jabref-online-add-mechanism.md).

Option 1 is fragile across OS/desktop environments and starts a heavy GUI
for a background task. Option 2 alone cannot answer "already in library?"
while offline.

### Consequences

* Good, because all three offline operations (lookup, fetch, add) are
  covered by JabKit without requiring the GUI.
* Good, because `jabkit doi-to-bibtex` reuses JabRef's CrossRef integration
  and citation-key generation, avoiding a raw `doi.org` HTTP call from the
  browser.
* Good, because queued or directly-written entries are never lost regardless
  of GUI state.
* Bad, because the user must configure and **maintain** a list of `.bib`
  files (for both search and import targets); this list must be kept in sync
  as libraries are added, renamed, or moved.
* Bad, because JabKit requires Java / JBang, adding a runtime dependency.
* Bad, because `.bib` files read by JabKit may lag behind JabRef's
  in-memory state if the user has unsaved changes open in JabRef.
* Bad, because `jabkit import` does not yet exist and must be implemented.

## More Information

- Follow-up decision (online add mechanism): [0004-jabref-online-add-mechanism.md](0004-jabref-online-add-mechanism.md)
- Related plan: [`plan-browser-jabref.md`](../plan-browser-jabref.md)
- JabKit `search`: `jabkit/src/main/java/org/jabref/toolkit/commands/Search.java`
- JabKit `doi-to-bibtex`: `jabkit/src/main/java/org/jabref/toolkit/commands/DoiToBibtex.java`
