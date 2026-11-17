---
status: accepted
date: 2026-07-14
decision-makers: [AnchorHub maintainers]
generated-by:
  platform: claude-code
  agent: (none — author-driven)
  skills: []
  model: claude-opus-4-8
---

# ContextSwitcher adapter: read task files in the daemon, deep-link via `contextswitcher://`

## Context and Problem Statement

[ContextSwitcher](https://github.com/contextswitcher/contextswitcher) is a
desktop task-switcher (Mylyn-for-the-desktop): one click switches a whole
working context — remote tmux window, JetBrains project, browser tabs. A
task is a Markdown file with YAML frontmatter under
`~/.contextswitcher/tasks`; its `browser:` section lists the `urls` that
belong to the task.

AnchorHub's premise is bidirectional links between browser pages and items
in connected applications. ContextSwitcher tasks are a natural fit: if the
page you're on is in some task's `browser.urls`, AnchorHub should recognise
it and let you jump back into ContextSwitcher — the same "you're standing on
an anchor" experience the OneNote adapter gives.

Two design questions:

1. **Detection** — how does the extension learn that the current URL belongs
   to a task? The extension is sandboxed and cannot read the task files.
2. **Navigation** — how does the user get from the page back into the app?

## Considered Options

### Detection

* **Option A — daemon reads the task files (chosen).** A new
  `contextSwitcherLookup` native action scans `~/.contextswitcher/tasks`,
  parses each task's frontmatter, and matches the normalised current URL
  against `browser.urls`.
* **Option B — extension queries ContextSwitcher's loopback WebSocket
  server** (the one ContextSwitcher runs for its own Firefox extension,
  port 17872). Rejected: that server's protocol only does `focus-url` /
  `close-url` — it has no "which task owns this URL?" query, so this would
  *also* require a planned interface, plus it needs ContextSwitcher running
  and the shared token from `settings.yaml` (which the extension cannot
  read), and it only works while the app is up.

### Navigation

* **Option A — `contextswitcher://` OS protocol deep link (chosen).**
  `contextswitcher://switch/<id>` runs the task's switch;
  `contextswitcher://task/<id>` focuses and selects it. This is a *planned*
  ContextSwitcher interface (contextswitcher#47, not yet shipped): the
  grammar and id format are specified in the issue, so AnchorHub codes
  against it now and it lights up once ContextSwitcher registers the
  protocol handler. Until then the buttons simply do nothing useful — no
  regression.
* **Option B — reuse the WebSocket `focus-url`** to focus the tab. Rejected:
  that navigates the *browser*, not the app; it doesn't switch the whole
  context, which is the point of ContextSwitcher.

## Decision Outcome

**Detection in the daemon (Option A), navigation via `contextswitcher://`
(Option A).**

Sub-decisions:

### 1. Match on the normalised URL, reusing `UrlNormalizer`

Both the stored `browser.urls` entries and the current URL are run through
`AnchorHub.Core.UrlNormalizer` before comparison, so trailing slashes,
default ports, host case, and query/fragment differences don't cause a
miss — consistent with how the OneNote adapter treats URLs.

### 2. Task id derivation mirrors ContextSwitcher exactly

The reported `taskId` is the file path relative to the tasks directory,
forward-slash separated with `.md` stripped (e.g. `jabref/fix-npe`) — the
same derivation ContextSwitcher's `TaskFileParser.relativeId` uses. This is
what lets the id round-trip into the deep link. Only the tasks directory and
its immediate subfolders are scanned (a subfolder is a project group), and
the reserved `TEMPLATE.md` / `CONTEXTSWITCHER.md` files are skipped — again
matching ContextSwitcher's `TaskRepository`.

### 3. Tasks directory resolved the way ContextSwitcher resolves it

`tasksDir` from `~/.contextswitcher/settings.yaml`, falling back to
`~/.contextswitcher/tasks`. `ANCHORHUB_CS_TASKS_DIR` overrides both (tests,
non-standard layouts). settings.yaml is a flat map, so a one-key line scan
suffices — no YAML dependency for path resolution.

### 4. Cross-platform, no COM — served by every daemon build

The adapter is pure file IO plus YAML parsing (YamlDotNet, mirroring the
SnakeYAML output ContextSwitcher writes). Unlike the OneNote COM bridge it
compiles and runs on the plain `net8.0` (Linux forwarder) target too, so
the action lives outside the `#if WINDOWS` fence and does not touch the
OneNote backend.

### 5. Fresh scan per lookup; no daemon-side cache

The daemon re-scans on each `contextSwitcherLookup`. Task directories hold a
few dozen small files; lookups are debounced and the extension caches
results per URL for 24 h, so the cost is negligible and correctness (picking
up task-file edits immediately) is preferred over a cache-invalidation
scheme.

### 6. Popup surface only — the toolbar icon is untouched

ContextSwitcher results appear as their own popup section (title, id,
status, two deep-link buttons), not in the toolbar icon or badge. This keeps
the OneNote-driven icon pipeline (colours/shapes/badge) unchanged and avoids
contending with the JabRef badge for the single badge-text slot. The adapter
defaults to enabled and degrades quietly: no tasks, adapter off, or daemon
unreachable → the section is hidden.

## Consequences

* Good: detection works whether or not ContextSwitcher is running, needs no
  shared token, and reuses AnchorHub's existing native-messaging channel.
* Good: the id round-trips into a stable deep link that survives task renames
  of the *title* (the id is the file path, not the title).
* Bad: AnchorHub now knows ContextSwitcher's on-disk task format; a
  breaking change there (pre-v1.0, no compat guarantees) would need a
  matching update here.
* Bad: the navigation buttons are inert until ContextSwitcher ships the
  `contextswitcher://` handler (contextswitcher#47). Accepted — coding
  against the planned interface is deliberate.

## More Information

- ContextSwitcher deep-link grammar: contextswitcher#47
  (`contextswitcher://task/<id>`, `contextswitcher://switch/<id>`).
- Requirements: `req~ank.contextswitcher.task-match~1`,
  `req~ank.contextswitcher.deeplink~1` in
  [`docs/requirements/anchorhub.md`](../requirements/anchorhub.md).
- Implementation: `hub/AnchorHub.Hub/ContextSwitcher/`,
  `contextSwitcherLookup` in `MessageHandler.cs`, the ContextSwitcher
  section in `extension/popup.js`.
