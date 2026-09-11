---
name: fa-reel-watch
description: "The chat surface still has NO ceilings — no rate limiter, no tier check, no budget — and the provider-side spend cap is one unclicked founder action. Every in-app reaction re-judges the whole cross-reference pool at LLM cost."
type: watch
last_verified: 2026-09-11
---

# Watch list

_Cleared items are deleted here, not struck through — a list carrying resolved rows stops being read._

## Unbounded cost — the sharpest item here
- **The chat surface has no ceilings.** Ask Reel's phase 0 and 1 shipped; **phase 2 did not**: no rate limiter, no
  tier rungs (Free gets the whole pipeline), no chat budget, no kill switch, no narrative fencing, no export or
  erasure, secrets not rotated. At the measured per-turn cost one account can run up tens of dollars an hour. **The
  provider-side spend cap at OpenRouter is a single unclicked founder action** and is the cheapest mitigation
  available. Phases 3 and 4 have not started.
- **Every in-app reaction re-judges the whole cross-reference pool.** The taste profile hash carries the
  dislike-centroid version and the loved anchors by design — a lens note must not survive a dismissal — so a dismissal
  or a high rating changes the key, the cross-reference treats the entire scored pool as unjudged at the new key, and
  the top lens notes are rewritten. Budget-bounded and honest, but the dominant LLM cost today. **The next call is a
  coarser key for verdicts** (the neighbours a verdict rests on do not change on a dismissal) or a daily cap on
  re-judging.

## Owed work
- **The cross-reference pool is shortlist-only.** Verdicts need a persisted score, so a pass covers the current
  shortlist rather than the whole eligible catalogue and coverage grows one build at a time. **Widening it means
  persisting first-pass scores for the whole retrieved set, and that is the next change.** PR4 — round 2, any
  demotion, a hero bar — stays gated on the OUT arm clearing its Wilson-upper threshold (`decisions-retrieval.md`).
- **Do not run another schema bump against the current eval ledger** — at this holdout size it cannot separate a small
  win from noise. The next modelling move is a bigger or pooled holdout, or a within-account paired design.
- **`HeroEligibleCount` at the 7.5 floor is still zero on every build**, even after the catalogue seed. Watch for the
  first non-zero; until then the hero floor is being relaxed on every build.
- **The retrieval-recall and build-latency gates are unpaid.** The recall probe needs a before/after over a week and
  its budget covers only part of the window; the first cluster-aware build was materially slower while the eligible
  pool jumped in the same hour, so build p95 is unattributed; `hnsw.ef_search` is still at its default while cluster
  pulls can ask for hundreds. The seed sweep waits on two weeks of per-arm eligibility data.
- **The 50-read human review bar for lens copy has not been met.** The audit endpoint exists, the reads have not
  happened, and the latest prompt version restarted the count. No prompt reaches anyone but the founder until it is.
- **Ask Reel follow-ups handed back by the fixers, none started:** the "done" reason still has two shapes on the
  lexical and filmography paths; the ranking-seed embedding is bought per turn; the residual layout shift is the
  banner leaving flow; the conditions summary's "+N more" is inert on the feed.
- **Extraction-era pollution is still draining.** Tone and era on the oldest attribute rows are untrustworthy and
  gated off by a minimum trusted extractor version; watch the stale-extractor count reach zero before surfacing either
  field.
- **`PipelineStage` has no "not enough ratings" value**, so an account below the ratings floor is parked as `Degraded`
  — a new enum member plus a frontend case.

## Debris and hygiene
- **The Ask Reel panel's probe conversations are still in the live database.** Delete them through the UI or the
  delete endpoint, never SQL. Their ids are in the panel's plan folder.
- **A pile of throwaway `reel_test_*` databases sits on port 5433**, including the QA copy that ran a whole walk with
  background jobs on and spent real provider calls. Drop them.
- **One upstream test red is left** — the semantic-arm order flake (`gotchas-tests.md`).
- **Interleaving is off and stays off until it is worth three months** of comparison at this traffic.

## Standing
- **Success metrics for the commercial half are instrumented nowhere.** The "≥5 external users" gate has no counter —
  the criterion that decides this program's fate is measured by hand. `docs/product-spec.md` also cites a brand-assets
  path that does not exist in this repo.
