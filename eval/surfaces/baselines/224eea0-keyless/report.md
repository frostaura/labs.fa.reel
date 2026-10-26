# Surface harness — 224eea0-keyless

- API build 224eea004c68ec9a06299ced8470a35355101c35 · snapshot `reel-2026-10-23` · built from artifact `0536acbf-a642-4a26-a68d-19a0b527c484`, serving `0536acbf-a642-4a26-a68d-19a0b527c484` · one Fresh build (Succeeded) · 45 s
- Jobs drained before the first fetch: yes · model key: no OpenRouter key (model calls cannot succeed)
- Digest arrivals: 60 title(s) seeded as arrived yesterday (eval/surfaces/seed-arrivals.sql, 60 requested), digest build Succeeded
- Exposures written by the run: 134; Trakt calls: 0; cost $0

- Coverage: every surface answered with data.

## Gates (task 8 targets)

| Gate | Value | Target | Status |
|---|---|---|---|
| Exclusion leaks on suggestion surfaces | 0 | <= 0 | pass |
| Stub why-lines (share of suggestion cards) | 0 | <= 0 | pass |
| Cross-rail repeats on the feed page | 0 | <= 0 | pass |
| Why-lines hedging against their own pick | 0 | <= 0 | pass |
| Non-Latin script in English why-lines | 0 | <= 0 | pass |
| Watched or rated titles on Saved and the Watch-tonight pin | 0 | <= 0 | pass |
| Surfaces answering with an HTTP error | 0 | <= 0 | pass |
| Trakt calls from the eval stack | 0 | <= 0 | pass |

| Metric | Value |
|---|---|
| Suggestion cards fetched | 169 |
| Stub rate (rendered why-lines in the stub family) | 0 |
| Stub why-lines by kind (generic once-per-page / composed tail / aligned template) | 0 / 0 / 0 |
| Exclusion leaks (suggestion surfaces) | 0 |
| Cross-rail repeats (feed page) | 0 |
| Evidence share (anchored + Similar, n=12) | 1 |
| Random-pair cosine p90 (evidence bar) | 0.390 |
| Rendered why-lines hedging against their own pick | 0 |
| Why-lines with non-Latin script | 0 |
| Watched or rated titles on Saved / Watch tonight | 0 |
| False copy claims | not measured yet (task 9) |

## Per surface

Copy is scored as rendered: the hero's hook, else its whyThis; a rail card's lensClause, else its whyThis; the Watch-tonight reason. "Read from" counts which field each scored line came from (a version-2 recording kept whyThis only).

| Surface | Blocks | HTTP errors | Empty blocks | Cards | Read from | Stub why | Hedging | Non-Latin | Leaks | Anchored | Evidence pass | Watched or rated (lookup surfaces) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| feed-hero | 1 | 0 | 0 | 5 | whyThis 3 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| feed-row | 2 | 0 | 0 | 16 | whyThis 12 | 0 | 0 | 0 | 0 | 6 | 6 | 0 |
| more | 4 | 0 | 1 | 36 | whyThis 29 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| genre-rail | 8 | 0 | 0 | 96 | whyThis 70 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| similar | 5 | 0 | 1 | 6 | whyThis 6 | 0 | 0 | 0 | 0 | 6 | 6 | 0 |
| digest | 1 | 0 | 0 | 10 | whyThis 9, lensClause 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| saved | 1 | 0 | 0 | 34 | — | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| watch-tonight | 1 | 0 | 0 | 1 | reason 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| person | 5 | 0 | 0 | 162 | — | 0 | 0 | 0 | 0 | 0 | 0 | 55 |
| typeahead | 10 | 0 | 1 | 29 | — | 0 | 0 | 0 | 0 | 0 | 0 | 16 |

## Jobs that ran during the harness

- BuildDigest Succeeded × 1
- BuildFeed Succeeded × 1
- PersonaliseTitles Succeeded × 1
- RefreshAvailability Succeeded × 1

## Ledger delta

| Provider | Model | Purpose | Calls | Prompt | Completion |
|---|---|---|---|---|---|
| Tmdb | — | — | 46 | 0 | 0 |
