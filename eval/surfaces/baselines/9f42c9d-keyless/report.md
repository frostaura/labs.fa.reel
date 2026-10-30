# Surface harness — ws7-r3-9f42c9d-keyless

- API build 9f42c9d799b3461466e885dc515e906109f247cc · snapshot `reel-2026-10-23` · built from artifact `0536acbf-a642-4a26-a68d-19a0b527c484`, serving `0536acbf-a642-4a26-a68d-19a0b527c484` · one Fresh build (Succeeded) · 39 s
- Jobs drained before the first fetch: yes · model key: no OpenRouter key (model calls cannot succeed)
- Digest arrivals: not recorded (the recording predates seeding; its digest read the snapshot as stored)
- Exposures written by the run: 144; Trakt calls: 0; cost $0

- Coverage: every surface answered with data.

## Gates (task 8 targets)

| Gate | Value | Target | Status |
|---|---|---|---|
| Exclusion leaks on suggestion surfaces | 0 | <= 0 | pass |
| Stub why-lines (share of suggestion cards) | 0.095 | <= 0 | FAIL |
| Cross-rail repeats on the feed page | 0 | <= 0 | pass |
| Why-lines hedging against their own pick | 0 | <= 0 | pass |
| Non-Latin script in English why-lines | 0 | <= 0 | pass |
| Watched or rated titles on Saved and the Watch-tonight pin | 0 | <= 0 | pass |
| Surfaces answering with an HTTP error | 0 | <= 0 | pass |
| Trakt calls from the eval stack | 0 | <= 0 | pass |
| Why-lines with a clause citing a push IsCitable rejects | — | <= 0 | no data |
| False copy claims (claims audit) | — | <= 0 | no data |
| Claim-shaped copy the claims register does not cover | — | <= 0 | no data |

| Metric | Value |
|---|---|
| Suggestion cards fetched | 169 |
| Stub rate (rendered why-lines in the stub family) | 0.095 |
| Stub why-lines by kind (generic once-per-page / composed tail / aligned template) | 12 / 4 / 0 |
| Exclusion leaks (suggestion surfaces) | 0 |
| Cross-rail repeats (feed page) | 0 |
| Evidence share (anchored + Similar, n=12; scored on keywords+cosine) | 1 |
| Random-pair cosine p90 (evidence bar) | 0.388 |
| Rendered why-lines hedging against their own pick | 0 |
| Why-lines with non-Latin script | 0 |
| Watched or rated titles on Saved / Watch tonight | 0 |
| Why-lines with a clause IsCitable rejects (of whyThis lines checked; unscored) | not recorded (recording predates contributions) |
| False copy claims (claims audit; of registered claims; unregistered) | not recorded (recording predates the claims audit) |

## Per surface

Copy is scored as rendered: the hero's hook, else its whyThis; a rail card's lensClause, else its whyThis; the Watch-tonight reason. "Read from" counts which field each scored line came from (a version-2 recording kept whyThis only).

| Surface | Blocks | HTTP errors | Empty blocks | Cards | Read from | Stub why | Hedging | Non-Latin | Leaks | Anchored | Evidence pass | Watched or rated (lookup surfaces) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| feed-hero | 1 | 0 | 0 | 5 | whyThis 3 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| feed-row | 2 | 0 | 0 | 16 | whyThis 13 | 4 | 0 | 0 | 0 | 6 | 6 | 0 |
| more | 4 | 0 | 1 | 36 | whyThis 32 | 3 | 0 | 0 | 0 | 0 | 0 | 0 |
| genre-rail | 8 | 0 | 0 | 96 | whyThis 78 | 8 | 0 | 0 | 0 | 0 | 0 | 0 |
| similar | 5 | 0 | 1 | 6 | whyThis 6 | 1 | 0 | 0 | 0 | 6 | 6 | 0 |
| digest | 1 | 0 | 0 | 10 | lensClause 2, whyThis 8 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
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
