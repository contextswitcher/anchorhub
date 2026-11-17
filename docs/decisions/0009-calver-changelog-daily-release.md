---
status: accepted
date: 2026-11-17
decision-makers: [AnchorHub maintainers]
---

# CalVer changelog with a daily release, as in koppter and ContextSwitcher

## Context and Problem Statement

Changes were pushed straight to `main` without versions or a change history.
koppter and ContextSwitcher, which live in the same GitHub organization, already release the same way: work goes to `experimental`, and a scheduled workflow releases it.
How are AnchorHub's versions numbered, and how does a change reach a release?

## Considered Options

* Keep a Changelog with CalVer day releases, checked by heylogs, released by a daily workflow
* Keep a Changelog with SemVer releases cut by hand
* No releases; users build from `main`

## Decision Outcome

Chosen option: "Keep a Changelog with CalVer day releases", because it is the cycle of the sibling projects, a date is a version number that needs no decision, and [heylogs](https://github.com/nbbrd/heylogs) turns format errors into build failures.

Work is committed and pushed to `experimental`.
Every morning, `.github/workflows/release.yml` merges `experimental` into `main`.
If `CHANGELOG.md` has an `Unreleased` section, the workflow renames it to the previous day, the day the changes were made, tags `main` with `v<date>`, and publishes a GitHub release with that section and the self-contained `ankd` binaries for `win-x64`, `linux-x64` and `osx-arm64`.

### Consequences

* Good, because releasing needs no manual step, and the three projects follow one cycle.
* Good, because users get a prebuilt `ankd` instead of installing the .NET SDK.
* Bad, because at most one release per day is possible.
* Bad, because the install scripts still build from source; they do not use the released binaries yet.

### Confirmation

The `changelog` job of `.github/workflows/build.yml` fails on a changelog that violates the format or the versioning scheme.

## Pros and Cons of the Options

### Keep a Changelog with SemVer releases cut by hand

* Good, because it is the most common combination.
* Bad, because every release needs a decision on a version number that carries no information here, and a manual step.

### No releases; users build from `main`

* Good, because nothing needs to be maintained.
* Bad, because users cannot tell what changed or how old a build is.
