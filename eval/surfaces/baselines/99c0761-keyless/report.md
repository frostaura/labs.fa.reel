# Surface harness — surfaces-99c0761-keyless

- API build 99c0761245d7e9a312595d693753f30b3e9388df · snapshot `reel-2026-10-23` · built from artifact `0536acbf-a642-4a26-a68d-19a0b527c484`, serving `9b286afb-327e-417b-b9fe-29d5b95f2490` · one Fresh build (Succeeded) · 74 s
- Jobs drained before the first fetch: yes · model key: no OpenRouter key (model calls cannot succeed)
- Digest arrivals: not recorded (the recording predates seeding; its digest read the snapshot as stored)
- Exposures written by the run: 279; Trakt calls: 0; cost $0

- Coverage: every surface answered with data.

## Gates (task 8 targets)

| Gate | Value | Target | Status |
|---|---|---|---|
| Exclusion leaks on suggestion surfaces | 21 | <= 0 | FAIL |
| Stub why-lines (share of suggestion cards) | 0.004 | <= 0 | FAIL |
| Cross-rail repeats on the feed page | 0 | <= 0 | pass |
| Why-lines hedging against their own pick | 3 | <= 0 | FAIL |
| Non-Latin script in English why-lines | 0 | <= 0 | pass |
| Watched or rated titles on Saved and the Watch-tonight pin | 116 | <= 0 | FAIL |
| Surfaces answering with an HTTP error | 0 | <= 0 | pass |
| Trakt calls from the eval stack | 0 | <= 0 | pass |
| Why-lines with a clause citing a push IsCitable rejects | — | <= 0 | no data |
| False copy claims (claims audit) | — | <= 0 | no data |
| Claim-shaped copy the claims register does not cover | — | <= 0 | no data |

| Metric | Value |
|---|---|
| Suggestion cards fetched | 283 |
| Stub rate (rendered why-lines in the stub family) | 0.004 |
| Stub why-lines by kind (generic once-per-page / composed tail / aligned template) | 1 / 0 / 0 |
| Exclusion leaks (suggestion surfaces) | 21 |
| Cross-rail repeats (feed page) | 0 |
| Evidence share (anchored + Similar, n=126) | 0.603 |
| Random-pair cosine p90 (evidence bar) | 0.383 |
| Rendered why-lines hedging against their own pick | 3 |
| Why-lines with non-Latin script | 0 |
| Watched or rated titles on Saved / Watch tonight | 116 |
| Why-lines with a clause IsCitable rejects (of whyThis lines checked; unscored) | not recorded (recording predates contributions) |
| False copy claims (claims audit; of registered claims; unregistered) | not recorded (recording predates the claims audit) |

## Per surface

Copy is scored as rendered: the hero's hook, else its whyThis; a rail card's lensClause, else its whyThis; the Watch-tonight reason. "Read from" counts which field each scored line came from (a version-2 recording kept whyThis only).

| Surface | Blocks | HTTP errors | Empty blocks | Cards | Read from | Stub why | Hedging | Non-Latin | Leaks | Anchored | Evidence pass | Watched or rated (lookup surfaces) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| feed-hero | 1 | 0 | 0 | 5 | hook+lensClause 4, whyThis 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| feed-row | 4 | 0 | 0 | 40 | whyThis 26, lensClause 14 | 0 | 3 | 0 | 0 | 30 | 10 | 0 |
| more | 6 | 0 | 0 | 72 | whyThis 65 | 1 | 0 | 0 | 0 | 36 | 7 | 0 |
| genre-rail | 8 | 0 | 0 | 96 | whyThis 96 | 0 | 0 | 0 | 21 | 0 | 0 | 0 |
| similar | 5 | 0 | 0 | 60 | whyThis 60 | 0 | 0 | 0 | 0 | 60 | 59 | 0 |
| digest | 1 | 0 | 0 | 10 | lensClause 4, whyThis 6 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| saved | 1 | 0 | 0 | 150 | — | 0 | 0 | 0 | 0 | 0 | 0 | 116 |
| watch-tonight | 1 | 0 | 0 | 1 | reason 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| person | 5 | 0 | 0 | 134 | — | 0 | 0 | 0 | 0 | 0 | 0 | 35 |
| typeahead | 10 | 0 | 0 | 32 | — | 0 | 0 | 0 | 0 | 0 | 0 | 15 |

## Leaks (first 20 per surface)

- genre-rail: Night of the Living Dead: Re-Animation (genre:action)
- genre-rail: NCIS: New Orleans (genre:action)
- genre-rail: Boston Legal (genre:drama)
- genre-rail: World Trade Center (genre:thriller)
- genre-rail: The Good Shepherd (genre:thriller)
- genre-rail: Match Point (genre:thriller)
- genre-rail: Munich (genre:thriller)
- genre-rail: Unfaithful (genre:thriller)
- genre-rail: Frozen II (genre:adventure)
- genre-rail: Over the Hedge (genre:adventure)
- genre-rail: Shrek the Third (genre:adventure)
- genre-rail: Ice Age: Dawn of the Dinosaurs (genre:adventure)
- genre-rail: Cars 3 (genre:adventure)
- genre-rail: Puss in Boots (genre:adventure)
- genre-rail: Freaks of Nature (genre:science-fiction)
- genre-rail: The Fare (genre:science-fiction)
- genre-rail: Frozen II (genre:fantasy)
- genre-rail: Shrek the Third (genre:fantasy)
- genre-rail: That's So Raven (genre:fantasy)
- genre-rail: Love, Death & Robots (genre:fantasy)

## Hedging why-lines (first 40)

- because-you-loved:The Family Man · The Greatest [lensClause]: For you, the emotional intensity may be the draw, though this is a much slower drama than the action and adventure you often prefer.
- because-you-loved:The Family Man · Where the Wild Things Are [lensClause]: Since you lean toward brisk action and adventure, this is a more inward detour: Max imagines running away from his mom by sailing to a far-off land, where Ira…
- because-you-loved:The Great Wall · The Dark [lensClause]: Since you favor brisk action and adventure, this is a darker, slower detour: a flesh-eating undead girl haunts the remote woods where she was murdered, then…

## Jobs that ran during the harness

- BuildDigest Succeeded × 1
- BuildFeed Succeeded × 2
- EnrichCatalog Succeeded × 1
- PersonaliseTitles Succeeded × 2
- RefreshAvailability Succeeded × 2
- Train Succeeded × 1

## Ledger delta

| Provider | Model | Purpose | Calls | Prompt | Completion |
|---|---|---|---|---|---|
| Tmdb | — | — | 86 | 0 | 0 |
