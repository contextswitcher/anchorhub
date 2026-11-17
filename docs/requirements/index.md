# Requirements

Requirements describe what AnchorHub must do.
They are written in [OpenFastTrace](https://github.com/itsallcode/openfasttrace) (OFT) format so that implementations and tests can be linked back to them.

## Format

A requirement is a Markdown heading followed **immediately** (no blank line) by its identifier, then a description, then `Needs: impl`.

```
### Descriptive Title
`req~category.name~revision`

Description of what must be true.

Needs: impl
```

The identifier structure is `req~<category>.<slug>~<revision>`.
Increment the revision when the requirement changes meaning.

Implementations reference back with an inline comment:

```csharp
// [impl->req~ank.exact-match~1]
```

## Categories

| Prefix | Area |
|---|---|
| `onl.match` | URL normalisation and matching |
| `onl.icon` | Toolbar icon states and colours |
| `onl.popup` | Popup window content and controls |
| `onl.nav` | Page navigation and in-page search |
| `onl.cache` | Result caching |

## Requirements

See [anchorhub.md](anchorhub.md).
