# Manual test URLs

Live URLs that exercise specific code paths. Use these to spot regressions
when site layouts shift, when adding a new fallback rule, or after touching
any of the in-page overlay code.

For each entry: visit the URL with the extension loaded, check the
expected behaviour, then update or extend this list when the site
changes or a new bug appears. Add a new line whenever a bug is fixed
against a specific URL — that URL is the cheapest reproducer.

## Toolbar icon — URL fallbacks

Each row hits a different fallback path in `MessageHandler.Lookup`.

| URL | Helper / path | Expected |
|---|---|---|
| `https://github.com/peter-evans/create-pull-request` | ForgeUrlHelper | exact or `<host>/<owner>/<repo>` related |
| `https://gist.github.com/<user>/<id>` | gist-fallback | `gist.github.com/<user>` related |
| `https://www.linkedin.com/in/julia-hertel-2014a81ab/` | LinkedInUrlHelper | person-name related |
| `https://www.linkedin.com/company/tragai/` | LinkedInUrlHelper company | company URL or slug related |
| `https://www.reddit.com/r/docker/` | RedditUrlHelper | `reddit.com/r/docker` related |
| `https://alternativeto.net/software/logseq/` | AppCatalogUrlHelper | slug `logseq` related |
| `https://noteapps.info/apps/obsidian/` | AppCatalogUrlHelper | slug related |
| `https://www.w3.org/TR/owl-overview/` | W3CUrlHelper | spec acronym related |
| `https://www.amazon.de/Production-Workflow-Techniques-Frank-Leymann/dp/0130217530` | AmazonUrlHelper | title slug related |
| `https://www.idealo.de/preisvergleich/OffersOfProduct/202837261_-mx-anywhere-3s-grafit-logitech.html` | IdealoUrlHelper | product name related |
| `https://en.wikipedia.org/wiki/Albert_Einstein` | WikipediaUrlHelper | article title related |
| `https://dblp.org/pid/k/BarbaraKonig1.html` | dblp fallback | ORCID or name related (DOM scrape) |
| `https://www.researchgate.net/profile/David-Benavides-7` | ResearchGateUrlHelper | researcher name related |
| `https://aguapedia.academia.edu/FranciscoBenitez` | AcademiaEduUrlHelper | researcher name related (CamelCase split) |

## Toolbar icon — host-fallback skip list

These URLs should **not** trigger a host-only fallback. If pass 1 has no
match (and no helper applies) the icon stays grey rather than showing
a host-anchored false positive.

| URL | Reason |
|---|---|
| `https://www.google.com/search?q=foo` | search engine — pass 1 + host both skipped |
| `https://dl.acm.org/doi/10.1145/12345` | academic index |
| `https://fis.uni-bamberg.de/entities/publication/<id>` | institutional FIS |
| `https://www.spiegel.de/<article>` | news site |
| `https://fbicloud.informatik.uni-hamburg.de/...` | Nextcloud |
| `https://aguapedia.academia.edu/FranciscoBenitez` | academia.edu institution subdomain |

## In-page overlay markers

Reload the extension, visit each URL, and confirm markers appear in the
expected position with the expected status mix. Markers should update
progressively (one at a time) rather than all at once.

### GitHub topics / search

| URL | Expected |
|---|---|
| `https://github.com/topics/architecture-decision-records` | one marker per repo card, left of the star button; mix of green/teal/grey |
| `https://github.com/search?q=adr&type=repositories` | same as topics; pagination loads more cards |

### GitHub profile sidebar

| URL | Expected |
|---|---|
| `https://github.com/peter-evans` | one marker after each external link in the bio sidebar (homepage, LinkedIn, BlueSky, X, …) — link's literal href is the lookup key |

### AlternativeTo

| URL | Expected | Stress |
|---|---|---|
| `https://alternativeto.net/software/qiqqa/` | one marker per alternative app, next to its title (not the icon) | small list |
| `https://alternativeto.net/software/weava/?p=5` | markers on every card on this page, including ones rendered after lazy-load | pagination + lazy-load |
| `https://alternativeto.net/browse/all/?tag=research-manager` | markers across the full browse list | different layout from `/software/<slug>/` |

### ResearchGate

| URL | Expected |
|---|---|
| `https://www.researchgate.net/lab/Diverso-Lab-International-Computing-David-Benavides` | one marker after each lab member's name; tracking query is stripped before lookup |

### Person-name scan (any host)

Plain-text names under person-y headings (Organizers / Committee / Speakers /
Members …) — markers appear only when OneNote actually has a hit for the name,
so misses stay invisible.

| URL | Expected |
|---|---|
| `https://fosd.github.io/FOSD2026/` | marker at the end of each *Organizers* and *Steering Committee* `<li>` whose name is in OneNote; italic *speaker (affiliation)* under each *Keynote* heading also marked |
| `https://fg-arc.gi.de/veranstaltung/architekturen-2026` | speaker names marked in **both** the Programme section (bold-only `<strong>Name (affiliation)</strong>`) and the *Zusammenfassungen der Vorträge* section (bold + italic `<strong><em>Name (affiliation)</em></strong>`); multi-name lines like *Soliman + Keim* yield one marker per person, each placed right after its own affiliation parens; title prefixes (`Dr.`, `Jun.-Prof. Dr.`) are stripped from the lookup name |

#### Known failure modes — debugging the person-name scan

Because misses produce no marker, the scan is hard to diagnose by eye.
Enable verbose logging in the page console:

```javascript
localStorage.setItem("onl-debug", "1"); location.reload();
```

Then watch the browser console (`Ctrl+Shift+K` in Firefox). Each scan
tick logs which headings matched, container item counts, candidates
extracted, and the lookup outcome per name.

Disable with:

```javascript
localStorage.removeItem("onl-debug"); location.reload();
```

Symptom-to-knob map (all in `extension/content/overlay.js`):

| Symptom | Likely cause | Knob |
|---|---|---|
| Heading not detected | section title uses an unsupported word | extend `PERSON_HEADING_RE` |
| Heading matched, "no list/table followed" in log | names are in `<p>` tags or `<dl>` not `<ul>/<ol>/<table>` | broaden `findPersonContainers` |
| Item present, no candidate extracted | name has unusual diacritics / hyphens / particles | adjust `NAME_RE` (ASCII range, particle list) |
| "container dropped: only 1 candidate" in log | legitimate single-person section | lower the `< 2` threshold or add a per-heading exception |
| Marker on a non-person phrase | stop-list miss | add the phrase (lowercase) to `PERSON_STOP_PHRASES` |
| Marker missing on a known-good name | OneNote search doesn't return the name as a phrase | run `dotnet run --project hub/AnchorHub.Hub -- lookup-name "John Doe"` to confirm hub-side hit |
| Two markers on the same item | URL-marker dedup is failing | verify `.onl-marker` selector still matches the host scanner's marker class |
| Italic name not picked up | text isn't exactly `Name (Affiliation)` | adjust `ITALIC_NAME_RE` |

The CLI `lookup-name` command is the cheapest way to bisect: if it
returns hits but the marker is missing, the failure is in the content
script; if it returns nothing, the failure is in the OneNote index
itself (the page genuinely doesn't reference that name as a phrase).

## In-page overlay popup

For any marker on the pages above:

1. Click the marker — an iframe-hosted popup appears, centered on
   the page, with a translucent backdrop.
2. The popup queries OneNote for the marker's URL and shows matches
   (or "Not found in OneNote.").
3. Click outside, or press Escape — the overlay closes.
4. Click "Copy link" (when shown) — the URL is on the clipboard;
   the button briefly reads "Copied".
5. Click a match row — OneNote navigates to that page and opens
   Ctrl+F pre-filled with the search term.

## Known reproducers

URLs preserved from past bug investigations. Re-check these when
touching nearby code.

| URL | Issue | Status |
|---|---|---|
| `https://www.adac.de/rund-ums-fahrzeug/autokatalog/marken-modelle/cupra/cupra-el-born/` | popup showed an unrelated mobilize page as an "exact match" — suspected OneNote quoted-phrase over-match | open |
