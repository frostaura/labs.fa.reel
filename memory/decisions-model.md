---
name: fa-reel-decisions-model
description: "The ledger rule was pre-registered and honoured: schema v6 reverted on no lift, so 6 is burned and the next bump is 7. The instrument, not the idea, failed. The calibrator is applied last; the exposure log is append-only."
type: decision
last_verified: 2026-09-11
---

# Live decisions — the model and the eval ledger

## The v6 bump and its pre-registered stopping rule
- **Iteration 17 (FeatureSchema v6) had a stopping rule written *before* the run:** ship only if the mean relative
  NDCG lift improved over iteration 16 **and** `NdcgLiftCiLow` did not move further negative; otherwise revert. *Why:*
  iterations 13–16 had each been read after the fact, and a bump that costs every account a retrain needs a rule that
  cannot be renegotiated once the number is on screen. `INTERLEAVE_ENABLED` stays off across any such comparison — a
  pool change mid-comparison silently invalidates it.
- **Outcome: reverted, unrenegotiated.** No lift, CI not improved. `FeatureSchema.Version` is back at 5; **the version
  history keeps the v6 entry with the measurements that killed it, so 6 is a burned number and the next bump is 7** —
  otherwise two different vectors answer to one integer in stored `ModelArtifact.FeatureSchemaJson` rows.
- **The instrument failed, not the idea.** Repeat runs over identical data returned materially different NDCG figures
  — a spread wider than the effect being looked for. Read iteration 17 as *unmeasurable at this holdout size*, never
  as "whole-history features carry no signal". Re-proposing them is legitimate; re-proposing them against this ledger
  is not.
- **What v6 was, kept as the record of what was built:** five appended whole-history scalars — a watched-but-unrated
  centroid, a rewatch centroid and the watched-genre share. *Why the bump at all:* every user-side feature before it
  read RATED titles only, so the titles Dean watched and finished without rating, and the ones he rewatched, fed
  nothing at all.
- **The v6 kNN pair was built, measured and dropped on a cost gate — not on taste.** A full eval run went from ~3 s to
  12–13 s with the pair added, against a stated 2× ceiling; the cost is building the search index, not scanning it,
  and cluster pruning bought back a real ~4× that was still not enough. The claim it carried is unresolved and worth
  revisiting behind a **materialised, incrementally-maintained** neighbour index on its own bump — never by re-adding
  it to the per-build recompute.
- **"Watched as of X" is `LastWatchedAt <= X`, everywhere.** *Why:* `FirstSyncedAt` is immutable but it is the INGEST
  date, and nearly all the founder's watched rows share one bulk-link timestamp, so an as-of filter on it is
  all-or-nothing at the day Trakt was linked. `LastWatchedAt` only ever moves forward, and a forward move can only
  push a title OUT of an as-of pool. Two definitions of "watched" in one codebase is drift this repo keeps paying for.
- **Leave-one-out subtraction is stated honestly about where it actually fires.** It is a live subtraction for the
  rewatch pool (rated titles are routinely members) and structurally unreachable for the eval harness's own holdout;
  it is kept because the title-detail path scores watched-and-unrated titles directly. *Why say so:* a guard claimed
  to protect something it structurally cannot is how a guard becomes decoration.

## Serving, calibration and the closed loop
- **The calibrator is applied last and never inside training or evaluation, and the residual bias is never normalised
  away without a replacement.** *Why:* the residual offset was compensating a base model that over-predicts by one to
  nearly three points; removing it alone makes every displayed number jump and get *less* honest. The rule itself is
  in [`../CLAUDE.md`](../CLAUDE.md).
- **Exposure logging is append-only per serve, with a derived rollup and a temporal join as the primary attribution.**
  *Why:* only about 2% of ratings are made in-app, and a de-duplicated-on-build design loses exactly the drift the
  founder complains about and never sees the live refill path. No client viewport beacon.
- **One score resolution across `FeedItem`, `TitleScore` and the exposure log (2 dp).** *Why:* a calibration join
  inherits the coarsest side, and 1 dp put a quantisation floor under residuals of the same order as the deltas being
  measured.
- **`PersonAffinities` has a writer, and it forks no maths.** *Why:* the entity, index, filter and a reader all
  shipped with no writer, so one discovery arm contributed nothing to any feed build — silently. Affinity and pool
  size come from the same path the why-sentences read, so a stored row cannot quote a different number than the card.
  `FeatureVectorBuilder` must never read this table; that is the leakage guarantee.
