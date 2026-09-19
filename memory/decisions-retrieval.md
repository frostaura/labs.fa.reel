---
name: fa-reel-decisions-retrieval
description: "Cross-reference LLM verdicts nudge UP on Keep only — the OUT arm still hides a sixth of future loves, so nothing is demoted and the second verdict round stays gated. Retrieval is multi-centroid; a Preserve rebuild may never grow a rail."
type: decision
last_verified: 2026-09-11
---

# Live decisions — retrieval, verdicts and feed-build brakes

## Cross-reference verdicts (LLM judging the shortlist)
- **Keep nudges UP; nothing is demoted, the hero is never barred, and no model opinion may enter
  `EligibilityQueryBuilder`.** *Why (measured on the founder's own holdout, 2026-09-11):* under prompt v2 the KEEP
  verdict carries real signal — keeps were later loved roughly a third of the time against a base rate near a quarter
  — but its OUTs still catch a sixth of the titles he went on to love, worse than v1's own true-rule-out rate. An OUT
  that removes a future love is permanently unobservable, and eligibility encodes founder-locked USER statements, not
  opinions. The second verdict round — round 2 over the survivors, any demotion, a hero bar, which the job code calls
  **PR4** — stays gated on an OUT arm whose Wilson **upper** bound on false rule-outs clears 15%; v2 does not.
- **`Round` is in the verdict key and the model artifact is not.** *Why:* without the round, round 2 overwrites round
  1 and "two keeps" has nothing to count; with the artifact in the key the whole cache goes cold every retrain night.
  A profile-hash move demotes verdicts to stale at half strength rather than invalidating them, so a hash step costs a
  drain, not a re-judgement.
- **The `rerank` purpose finally has a ceiling, split four ways, with background builds stopping at 70%.** *Why:* it
  was the only LLM spend with no cap, and a single family ceiling a feed build could exhaust would starve the
  founder's own chat and look intentional.

## Retrieval and feed-build brakes
- **Two rows of tonight's picks are one column-major CSS grid with `HeroSize` untouched.** *Why:* one scroll container
  shares `scrollLeft` by construction, column-major keeps DOM order = rank order, and `HeroSize` also sets the
  floor-relax minimum, so the second row pages from `/feed/more`.
- **Multi-centroid (taste-cluster) retrieval keeps the drift arm and floors every cluster at 25 pulls.** *Why:* a
  single mean vector over hundreds of loved titles reached only a fraction of them; the per-cluster floor stops a
  dominant cluster reproducing single-centroid behaviour at extra cost, and the drift arm is the only signal faster
  than the 365-day half-life.
- **A Preserve rebuild may lose or replace a rail but never GROW one.** *Why:* a rail dropped below its minimum left
  no `FeedItem`, read as a vacancy on every rebuild, and was re-drawn from a loved pool headed by the title just rated
  — so rating a hero pick dealt a "Because you loved <that title>" rail. New loves get their rail on the next Fresh
  build.
- **A BuildFeed is not claimable while the same account has a DeltaSync pending or running, and three brakes bound the
  cross-reference loop** (a minimum unjudged count unless nothing at the key was judged, a minimum judged count to
  earn a rebuild, and a build triggered by a pass never queues another pass). *Why:* one refresh click was producing
  four builds and four passes — the manual build raced the sync on stale data, and pass → rebuild → pass had no
  natural end.

## Negation in discovery
- **A negation can only remove something if it shapes DISCOVERY, and three things hold that together — do not undo
  them piecemeal.** *Why each part exists:* exclusions ride on the provider's without-genre/without-keyword
  parameters, because filtering after retrieval only shrinks an already-wrong pool; the pool is partitioned
  clean-versus-contradicting *before* the cut, with contradicting titles kept as a reserve, so a negation can never
  empty the screen; and the ranking query vector is built from the interpreted POSITIVE signal, never the raw
  sentence — an embedding model has no notion of "not", so embedding "horror but no ghosts" pulls the vector *toward*
  ghosts. The tokenizer's clause boundaries are load-bearing for the same reason.
