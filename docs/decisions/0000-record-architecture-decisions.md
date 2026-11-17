---
status: accepted
date: 2026-04-19
generated-by:
  platform: claude-code
  agent: adr-writer-madr
  skill: adr-writer-madr
  model: claude-sonnet-4-6
---

# Record architecture decisions

## Context and Problem Statement

AnchorHub has several non-obvious architectural decisions (PowerShell COM bridge, UI automation via SendKeys, two-pass URL lookup) documented only in code comments and CLAUDE.md. As the project grows, the rationale behind these decisions risks being lost.

## Decision Outcome

Chosen option: "Use Markdown Architectural Decision Records (MADR) in `docs/decisions/`", because the lightweight format integrates naturally with the existing Markdown documentation, lives alongside the code in version control, and the MADR template provides just enough structure without overhead.
