# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Calendar Versioning](https://calver.org/) with the scheme `YYYY-0M-0D`.

## [Unreleased]

### Added

- `ankd version` prints the version of the build and the commands it accepts as one JSON line, so that koppter can tell whether this AnchorHub has the commands it needs; the release binaries carry their release date as version.

## [2026-11-19] - 2026-11-19

### Added

- `ankd outlook-sideline <link> on|off` adds or removes the Outlook category `Sideline` on an appointment (for a series: on the whole series), which koppter reads as "show, but plan as free".

### Changed

- `ankd outlook-busy` also prints the subject, the EntryID, whether the item is recurring, whether it has the `Sideline` category, and the color of its first category for each item.

[Unreleased]: https://github.com/contextswitcher/anchorhub/compare/v2026-11-19...HEAD
[2026-11-19]: https://github.com/contextswitcher/anchorhub/compare/v2026-11-18...v2026-11-19

## [2026-11-18] - 2026-11-18

### Added

- `ankd outlook-todos` prints the open flagged mails and Outlook tasks of the To-Do List of all accounts as one JSON line, and `ankd outlook-complete <link>` marks such an item done.
- `ankd outlook-blocks-sync` mirrors the time blocks that koppter plans into an Outlook calendar as tentative, private appointments (request as JSON on stdin), `ankd outlook-blocks-clear` removes them again, and `ankd outlook-busy` lists the busy times of a calendar; only items with both the `koppter` category and a `KoppterBlockId` property are ever deleted.

### Changed

- Blog pages (hosts such as `blog.jetbrains.com`, `blogs.oracle.com`, `*.blogspot.com`, `*.wordpress.com`, `*.medium.com`, `*.substack.com`, `*.ghost.io` or `*.hashnode.dev`) no longer list every OneNote page that mentions the blog as a "same site" match.
- `ankd outlook-open <link>` finds the item in all accounts when the link names no store.
- The installers say why the JabRef fulltext provider is disabled (fetching full texts moves to the JabRef browser-extension plugin) and how to build it in again.

[2026-11-18]: https://github.com/contextswitcher/anchorhub/compare/v2026-11-17...v2026-11-18

## [2026-11-17] - 2026-11-17

### Added

- Outlook adapter for classic desktop Outlook: `ankd outlook-capture` prints the selected or open item with an `anchor://outlook/message/…` link, and `ankd outlook-open <link>` opens that item again.
- Daily releases: each release on GitHub carries its section of this changelog and the `ankd` daemon for Windows, Linux and macOS.
- `just run-loop` (alias `just loop`), as in ContextSwitcher: keeps the installed `ankd` at the newest commit by pulling, installing and waiting for the next commit.

### Fixed

- Reinstalling on Windows while the browser has `ankd` running no longer fails on the locked file: the installer stops it, and the extension reconnects to the new one.

[2026-11-17]: https://github.com/contextswitcher/anchorhub/releases/tag/v2026-11-17
