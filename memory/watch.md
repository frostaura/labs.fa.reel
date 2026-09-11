---
name: fa-reel-watch
description: "The chat surface still has NO ceilings — no rate limiter, no tier check, no budget — and the provider-side spend cap is one unclicked founder action. Every in-app reaction re-judges the whole cross-reference pool at LLM cost."
type: watch
last_verified: 2026-09-11
---

# Watch list

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
  persisting first-pass scores for the whole retrieved set, and that is the next change.** The second verdict round
  (survivors re-judged, any demotion, a hero bar — the code calls it PR4) stays gated on the OUT arm clearing its
  Wilson-upper threshold (`decisions-retrieval.md`).
- **Do not run another schema bump against the current eval ledger** — at this holdout size it cannot separate a small
  win from noise. The next modelling move is a bigger or pooled holdout, or a within-account paired design.
- **`HeroEligibleCount` at the 7.5 floor is still zero on every build**, even after the catalogue seed. Watch for the
  first non-zero; until then the hero floor is being relaxed on every build.
- **The retrieval-recall and build-latency gates are unpaid.** The recall probe needs a before/after over a week on a
  budget covering part of it; the first cluster-aware build was slower while the eligible pool jumped in the same
  hour, so build p95 is unattributed; `hnsw.ef_search` is still at its default while cluster pulls ask for hundreds.
- **The 50-read human review bar for lens copy has not been met.** The audit endpoint exists, the reads have not
  happened, and the latest prompt version restarted the count. No prompt reaches anyone but the founder until it is.
- **Of the follow-ups the QA walks raised, three are still unstarted:** the "done" reason still has two shapes on the
  lexical and filmography paths; the ranking-seed embedding is bought per turn; the conditions summary's "+N more" is
  inert on the feed. The fourth — the banner's residual layout shift — was fixed and pushed on 2026-09-11.
- **Two layout shifts are left on `/search`, both outside the turn, both wanting a design call first.** Page load still
  shifts as the conditions strip arrives and pushes the page down; a late `reranked` frame can grow a card by its
  why-line and move every row under it. Reserving a strip whose height only the response knows, and fixing a uniform
  two-line why-box on every card, are product calls — which is why neither was taken.
- **Extraction-era pollution is still draining.** Tone and era on the oldest attribute rows are untrustworthy and
  gated off by a minimum trusted extractor version; watch the stale-extractor count reach zero before surfacing either
  field.
- **`PipelineStage` has no "not enough ratings" value**, so an account below the ratings floor is parked as
  `Degraded` — a new enum member plus a frontend case.

## Debris and hygiene
- **The QA walks' probe conversations are still in the live database.** Most of the panel's ids are in
  `~/.claude/plans/ask-reel-panel-2026-09-10/probe/test-conversation-ids.txt`; the rest come from
  `GET /api/search/conversations`. Delete each through the UI or `DELETE /api/search/conversations/{id}` — never SQL,
  which leaves the turn record and the usage ledger disagreeing.
- **`reel_test_w9` is still sitting on port 5433** — the QA-walk copies have been dropped. Drop it too; a throwaway
  copy with background jobs on spends real provider calls.
- **Interleaving is off and stays off until it is worth three months** of comparison at this traffic.

## Standing
- **Success metrics for the commercial half are instrumented nowhere.** The "≥5 external users" gate has no counter —
  the criterion that decides this program's fate is measured by hand. `docs/product-spec.md` also cites a brand-assets
  path that does not exist in this repo.
