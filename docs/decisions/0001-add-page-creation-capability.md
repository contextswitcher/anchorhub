---
status: proposed
date: 2026-04-19
decision-makers: [AnchorHub maintainers]
generated-by:
  platform: claude-code
  agent: adr-writer-madr
  skill: adr-writer-madr
  model: claude-sonnet-4-6
---

# Add page-creation capability to the native messaging hub

## Context and Problem Statement

AnchorHub currently operates as a read-only tool: it searches OneNote for URLs matching the active browser tab and displays results. Users who find that a page is *not* linked in OneNote must manually open OneNote, navigate to the correct section, create a page, and paste the URL. This breaks the workflow that the extension is designed to streamline.

The question is whether and how to extend the hub to create OneNote pages programmatically, and what implications this has for the system's trust model and data safety.

## Decision Drivers

* Users want a single-click workflow to link the current browser tab's URL to a OneNote page
* The existing PowerShell COM bridge already holds a live `OneNote.Application` COM object capable of write operations
* Write operations change the security posture: the extension can now modify user data, not just read it
* OneNote's COM API requires well-formed XML with proper namespace handling for page creation
* The notebook/section hierarchy must be exposed to the extension so users can choose where to create pages

## Considered Options

* **Option 1: Extend the existing PowerShell COM bridge with `addPage` and `listNotebooks` actions**
* **Option 2: Use OneNote's REST API (Microsoft Graph) for page creation**
* **Option 3: Keep the extension read-only and rely on users to create pages manually**

## Decision Outcome

Chosen option: "Extend the existing PowerShell COM bridge with `addPage` and `listNotebooks` actions", because it reuses the proven IPC architecture, requires no additional authentication (the COM object inherits the user's desktop session), and keeps the extension fully offline with no cloud dependency.

### Consequences

* Good, because users can create a linked OneNote page without leaving the browser
* Good, because no OAuth/Graph API token management is needed
* Good, because the existing persistent PowerShell process handles both read and write operations with no additional process overhead
* Bad, because the extension now has write access to the user's entire OneNote notebook hierarchy — a bug or malicious input could create unwanted pages
* Bad, because page XML construction requires careful CDATA escaping of user-controlled input (URL, page title) to prevent XML injection
* Bad, because the `listNotebooks` call with `GetHierarchy('', 3)` fetches the full notebook tree on every invocation, which may be slow for users with many notebooks

### Confirmation

* The `addPage` PowerShell action constructs page XML with `[System.Security.SecurityElement]::Escape()` for the page ID and CDATA wrapping with `]]>` splitting for title and URL fields — verify this handles all edge cases
* Integration test: create a page, then search for its URL to confirm round-trip works
* The extension popup should confirm the target section with the user before calling `addPage` (no silent page creation)

## Pros and Cons of the Options

### Option 1: Extend the existing PowerShell COM bridge

* Good, because it reuses the established IPC pattern (JSON request over stdin, Base64 response over stdout)
* Good, because COM page creation is instant and works offline
* Good, because `GetHierarchy` provides the full notebook/section tree needed for a section picker UI
* Neutral, because two new records (`NotebookInfo`, `SectionInfo`) are added to the data model, but they are simple and well-scoped
* Bad, because XML construction in embedded PowerShell string concatenation is fragile and hard to unit test
* Bad, because the COM `CreateNewPage` + `UpdatePageContent` two-step can leave an empty page if the second call fails

### Option 2: Use OneNote's REST API (Microsoft Graph)

* Good, because it provides a well-documented REST interface with JSON payloads
* Good, because it would enable cross-platform support in the future
* Bad, because it requires OAuth 2.0 authentication, token refresh, and Microsoft app registration
* Bad, because it introduces a cloud dependency for a tool that currently works entirely offline
* Bad, because Graph API has rate limits and requires internet connectivity

### Option 3: Keep the extension read-only

* Good, because it maintains the current simple trust model (read-only access)
* Good, because no XML construction or escaping concerns
* Bad, because users must manually switch to OneNote and create pages, undermining the extension's value proposition

## More Information

The implementation adds two new PowerShell actions to the embedded script in `OneNoteClient.cs`:

- `listNotebooks`: calls `GetHierarchy('', 3)` (hsSections scope) and returns the full notebook/section tree
- `addPage`: calls `CreateNewPage` followed by `UpdatePageContent` with XML containing the page title and URL

C# wrapper methods `ListNotebooks()` and `AddPage(string sectionId, string title, string url)` are exposed on `OneNoteClient`. `MessageHandler.cs` routes the `listNotebooks` and `addPage` actions but the handler method implementations are not yet complete — this is work in progress.

Key file: `hub/AnchorHub.Hub/NativeMessaging/OneNoteClient.cs`
