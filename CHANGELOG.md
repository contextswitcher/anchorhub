# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Calendar Versioning](https://calver.org/) with the scheme `YYYY-0M-0D`.

## [Unreleased]

### Added

- Outlook adapter for classic desktop Outlook: `ankd outlook-capture` prints the selected or open item with an `anchor://outlook/message/…` link, and `ankd outlook-open <link>` opens that item again, searching all accounts when the link names no store.
- `ankd outlook-todos` prints the open flagged mails and Outlook tasks of the To-Do List of all accounts as one JSON line, and `ankd outlook-complete <link>` marks such an item done.
- Daily releases: each release on GitHub carries its section of this changelog and the `ankd` daemon for Windows, Linux and macOS.

[Unreleased]: https://github.com/contextswitcher/anchorhub/commits/experimental
