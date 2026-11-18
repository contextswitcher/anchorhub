# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Calendar Versioning](https://calver.org/) with the scheme `YYYY-0M-0D`.

## [Unreleased]

### Changed

- Blog pages (hosts such as `blog.jetbrains.com`, `blogs.oracle.com`, `*.blogspot.com` or `*.wordpress.com`) no longer list every OneNote page that mentions the blog as a "same site" match.

[Unreleased]: https://github.com/contextswitcher/anchorhub/compare/v2026-11-17...HEAD

## [2026-11-17] - 2026-11-17

### Added

- Outlook adapter for classic desktop Outlook: `ankd outlook-capture` prints the selected or open item with an `anchor://outlook/message/…` link, and `ankd outlook-open <link>` opens that item again.
- Daily releases: each release on GitHub carries its section of this changelog and the `ankd` daemon for Windows, Linux and macOS.
- `just run-loop` (alias `just loop`), as in ContextSwitcher: keeps the installed `ankd` at the newest commit by pulling, installing and waiting for the next commit.

### Fixed

- Reinstalling on Windows while the browser has `ankd` running no longer fails on the locked file: the installer stops it, and the extension reconnects to the new one.

[2026-11-17]: https://github.com/contextswitcher/anchorhub/releases/tag/v2026-11-17
