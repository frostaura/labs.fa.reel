# Surface harness — 20429ff-keyless

- API build d86e65a0b636674180d0f37bb106f81ebeddad14 · snapshot `reel-2026-10-23` · built from artifact `0536acbf-a642-4a26-a68d-19a0b527c484`, serving `0536acbf-a642-4a26-a68d-19a0b527c484` · one Fresh build (Succeeded) · 67 s
- Jobs drained before the first fetch: yes · model key: no OpenRouter key (model calls cannot succeed)
- Digest arrivals: 60 title(s) seeded as arrived yesterday (eval/surfaces/seed-arrivals.sql, 60 requested), digest build Succeeded
- Exposures written by the run: 135; Trakt calls: 0; cost $0

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
| Why-lines with a clause citing a push IsCitable rejects | 0 | <= 0 | pass |
| False copy claims (claims audit) | 15 | <= 0 | FAIL |
| Claim-shaped copy the claims register does not cover | 0 | <= 0 | pass |

| Metric | Value |
|---|---|
| Suggestion cards fetched | 171 |
| Stub rate (rendered why-lines in the stub family) | 0 |
| Stub why-lines by kind (generic once-per-page / composed tail / aligned template) | 0 / 0 / 0 |
| Exclusion leaks (suggestion surfaces) | 0 |
| Cross-rail repeats (feed page) | 0 |
| Evidence share (anchored + Similar, n=14; scored on keywords+cosine) | 1 |
| Random-pair cosine p90 (evidence bar) | 0.383 |
| Rendered why-lines hedging against their own pick | 0 |
| Why-lines with non-Latin script | 0 |
| Watched or rated titles on Saved / Watch tonight | 0 |
| Why-lines with a clause IsCitable rejects (of whyThis lines checked; unscored) | 0 of 59; 0 without a stored score |
| False copy claims (claims audit; of registered claims; unregistered) | 15 of 194; 0 unregistered |

## Per surface

Copy is scored as rendered: the hero's hook, else its whyThis; a rail card's lensClause, else its whyThis; the Watch-tonight reason. "Read from" counts which field each scored line came from (a version-2 recording kept whyThis only).

| Surface | Blocks | HTTP errors | Empty blocks | Cards | Read from | Stub why | Hedging | Non-Latin | Leaks | Anchored | Evidence pass | Watched or rated (lookup surfaces) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| feed-hero | 1 | 0 | 0 | 5 | hook 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| feed-row | 2 | 0 | 0 | 16 | whyThis 7 | 0 | 0 | 0 | 0 | 6 | 6 | 0 |
| more | 4 | 0 | 1 | 36 | whyThis 16 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| genre-rail | 8 | 0 | 0 | 96 | whyThis 19 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| similar | 5 | 0 | 1 | 8 | whyThis 8 | 0 | 0 | 0 | 0 | 8 | 8 | 0 |
| digest | 1 | 0 | 0 | 10 | whyThis 9, lensClause 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| saved | 1 | 0 | 0 | 34 | — | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| watch-tonight | 1 | 0 | 0 | 1 | reason 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| person | 5 | 0 | 0 | 187 | — | 0 | 0 | 0 | 0 | 0 | 0 | 75 |
| typeahead | 10 | 0 | 1 | 29 | — | 0 | 0 | 0 | 0 | 0 | 0 | 20 |

## False copy claims (claims audit at a415c33)

- movie-rating-logs-a-play (WS-10A): 1 line(s)
- free-tier-upsell (WS-10A): 1 line(s)
- first-model-tonight-below-the-floor (WS-10B): 1 line(s)
- verdict-goes-into-tonights-retrain (WS-10A): 2 line(s)
- exclusions-are-airtight (WS-5): 5 line(s)
- cost-meter-counts (WS-3): 1 line(s)
- every-pick-has-a-reason (WS-10B): 2 line(s)
- narrative-before-every-pick (WS-10B): 1 line(s)
- ask-reel-permanently-excludes (WS-2): 1 line(s)

## Jobs that ran during the harness

- BuildDigest Succeeded × 1
- BuildFeed Succeeded × 1
- PersonaliseTitles Succeeded × 1
- RefreshAvailability Succeeded × 2

## Ledger delta

| Provider | Model | Purpose | Calls | Prompt | Completion |
|---|---|---|---|---|---|
| Tmdb | — | — | 84 | 0 | 0 |
