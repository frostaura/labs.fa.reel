---
name: fa-reel-watch
description: "The chat surface still has NO ceilings — no rate limiter, tier check or budget — and the OpenRouter spend cap is unconfirmed. Every in-app reaction re-judges the whole cross-reference pool at LLM cost. QA-copy databases pile up."
type: watch
last_verified: 2026-10-14
---

# Watch list

## Unbounded cost — the sharpest item here
- **The chat surface has no ceilings.** Ask Reel's phase 0 and 1 shipped; **phase 2 did not**: no rate limiter, no
  tier rungs (Free gets the whole pipeline), no chat budget, no kill switch, no narrative fencing, no export or
  erasure, secrets not rotated. At the measured per-turn cost one account can run up tens of dollars an hour (still no
  rate limiter anywhere in the backend, 2026-10-14). **A provider-side spend cap at OpenRouter is the cheapest
  mitigation** — recorded unclicked 2026-09-11; whether it is set now cannot be seen from the repo, so ask Dean.
- **Every in-app reaction re-judges the whole cross-reference pool.** The taste-profile hash carries the dislike
  centroid and loved anchors by design (a lens note must not survive a dismissal), so each reaction turns the scored
  pool unjudged and rewrites the top lens notes — budget-bounded, but the dominant LLM cost. **Next call:** a coarser
  verdict key (a verdict's neighbours do not move on a dismissal) or a daily re-judge cap.

## Owed work
- **The cross-reference pool is shortlist-only** — verdicts need a persisted score, so coverage grows one build at a
  time. **Next change: persist first-pass scores for the whole retrieved set.** Round 2 (the code's PR4) stays gated
  on the OUT arm's Wilson-upper threshold (`decisions-retrieval.md`).
- **`HeroEligibleCount` at the 7.5 floor is still zero on every build**, even after the catalogue seed. Watch for the
  first non-zero; until then the hero floor is being relaxed on every build.
- **The retrieval-recall and build-latency gates are unpaid.** The recall probe needs a before/after over a week on a
  budget covering part of it; the first cluster-aware build was slower while the eligible pool jumped in the same
  hour, so build p95 is unattributed; `hnsw.ef_search` is still at its default while cluster pulls ask for hundreds.
- **The 50-read human review bar for lens copy has not been met.** The audit endpoint exists, the reads have not
  happened, and the latest prompt version restarted the count. No prompt reaches anyone but the founder until it is.
- **Of the follow-ups the QA walks raised, three are still unstarted:** the "done" reason still has two shapes on the
  lexical and filmography paths; the ranking-seed embedding is bought per turn; the conditions summary's "+N more" is
  inert on the feed.
- **Two layout shifts are left on `/search`, both outside the turn, both wanting a design call first.** Page load
  still shifts as the conditions strip arrives and pushes the page down; a late `reranked` frame can grow a card by
  its why-line and move every row under it. Both fixes are product calls, so neither was taken.
- **Extraction-era pollution is still draining** — tone and era on the oldest rows stay gated off by a minimum
  trusted extractor version until the stale-extractor count reaches zero.
- **`PipelineStage` has no "not enough ratings" value**, so an account below the ratings floor is parked as `Degraded`
  — a new enum member plus a frontend case.

## Debris and hygiene
- **The QA walks' probe conversations are still in the live database** (`GET /api/search/conversations` lists them).
  Delete each through the UI or `DELETE /api/search/conversations/{id}` — never SQL, which leaves the turn record and
  the usage ledger disagreeing.
- **Throwaway `reel_test_*` databases have piled up on port 5433** — fixture copies plus the `w*`/`p2*` QA-walk
  copies, `reel_test_w9` among them (listed 2026-10-14). Drop the QA copies; one with background jobs on spends real
  provider calls.

## Standing
- **The declared quality bar is "Silver", whose definition includes *deployed*.** Reel is not, so the tier reads as a
  target rather than a met bar — and nothing in the scope says which it is.
- **The "≥5 external users" kill gate has no counter** — the criterion that decides this program is measured by hand.
  `docs/product-spec.md` also cites a brand-assets path that does not exist in this repo.
- **`AskReelEndpointTests.An_aborted_stream_leaves_a_failed_turn_and_the_prior_block_still_replays` is
  timing-sensitive in CI** — red once on 2026-09-16, green on rerun and on every `main` run since. Two reds in a row
  is a regression; candidate for the local-flaky quarantine if it recurs. Owner: unassigned.
- **The repo's `docs/deployment.md` still describes a weekly "rot check" run** that `ci.yml` no longer has (dropped
  before the PR #1 merge) — the doc is wrong, not the workflow.
