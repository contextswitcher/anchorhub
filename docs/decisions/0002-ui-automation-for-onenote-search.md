---
status: accepted
date: 2026-04-19
generated-by:
  platform: claude-code
  agent: adr-writer-madr
  skill: adr-writer-madr
  model: claude-sonnet-4-6
---

# Use Win32 P/Invoke and SendKeys for OneNote in-page search and global search

## Context and Problem Statement

When a user clicks a match in the popup, AnchorHub navigates to the matching page via the COM `NavigateTo` method. However, the user still needs to locate the URL on the page visually. The OneNote COM API provides no method to open the find-on-page bar (Ctrl+F) or the global search pane (Ctrl+E) programmatically. The question is how to automate these UI actions that have no COM API equivalent.

## Considered Options

* **Option 1: Win32 `SetForegroundWindow` + `WScript.Shell.SendKeys` to simulate keyboard shortcuts**
* **Option 2: Use UI Automation (UIA) framework to interact with OneNote's UI elements**

## Decision Outcome

Chosen option: "Win32 `SetForegroundWindow` + `WScript.Shell.SendKeys`", because it is simpler to implement within the existing PowerShell script, requires no additional dependencies, and the target key combinations (Ctrl+F, Ctrl+E) are stable across OneNote versions.

### Consequences

* Good, because users see the find bar pre-filled with the search term immediately after clicking a match, providing a seamless experience
* Good, because the implementation fits naturally in the existing PowerShell script with a one-time `Add-Type` for the Win32 imports
* Bad, because hardcoded `Start-Sleep` delays (400–800 ms) are needed between `NavigateTo`, `SetForegroundWindow`, and `SendKeys` — these are timing heuristics that may fail on slow machines or when OneNote is loading
* Bad, because `SendKeys` is fragile: it targets whichever window has focus at execution time, so a race condition could send keystrokes to the wrong application
* Bad, because keyboard shortcuts may differ in non-English OneNote locales (though Ctrl+F and Ctrl+E are generally stable)
