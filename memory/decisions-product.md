---
name: fa-reel-decisions-product
description: "Founder calls with their why: repo flipped PRIVATE, deploy decided 2026-07-24 and still not executed (a trip-by-timer kill is void), model-first then product, honest from day one, filters as a cost meter the founder sets himself."
type: decision
last_verified: 2026-10-14
---

# Live decisions — product and founder calls

## Programme and repo
- **The repo is PRIVATE (founder, 2026-08-07).** *Why:* Labs is stealth-by-default and the public exception was
  never recorded. Two residues still say otherwise and neither is an agent's to change: `LICENSE` is MIT and the
  README's licence line claims "open by default" as doctrine (both verified 2026-10-14). Going public again, or
  correcting that line, is a fresh founder call needing a recorded stealth exception.
- **Deploy was decided 2026-07-24 and has never been executed.** *Why it matters:* the external-user kill criterion is
  evaluated against a *deployed* product, so a kill tripped by a timer on an undeployed product is void. What is left is
  a founder action, not an engineering one, and **what specifically is left moves — `state.md` holds it, this file does
  not.** The blocking defect in `deploy/portainer-stack.yml` has since been fixed
  (`decisions-engineering.md`); the remaining steps — create the Portainer stack, set `PORTAINER_STACK_ID`, point
  Cloudflared at `reel.frostaura.net`, set `DEPLOY_ENABLED` — are written out in the repo's `docs/deployment.md`.
- **Both halves, model first (2026-09-09).** *Why:* the ledger, a near-zero correlation between served predictions and
  later ratings, and the retro loop all said the engine had no measurable edge. Dean chose to repair the serving
  arithmetic and the evaluation instrument **before** any user-facing number changed, then run the product programme.
  The negative is treated as a real negative, not instrument noise — repairing the instrument tells us where and how
  much, not whether.
- **Honest from day one.** *Why:* publish the real track record and the "what might bug you" caution immediately,
  framed as "here is how wrong I am and what I am doing about it". A calibrated number that is honest but *constant*
  is explicitly not acceptable — hence the calibrator collapse guard.
- **Filters are a cost meter; Dean decides in-app.** *Why:* the plan must never change a content rule on his behalf.
  Discovery is filter-aware and the conditions editor shows each rule's marginal cost; no rule is ever changed
  automatically.
- **No hero exploration slot.** *Why:* a few hundred eligible titles left nothing to explore. Revisit post-seed.
- Extraction model, no manual pipelines, zero-footprint eligibility and Trakt-only identity: founder-locked
  2026-06-12, rules in [`../CLAUDE.md`](../CLAUDE.md); the unconfirmed `gpt-6-luna` swap is open in `questions.md`.
- **Collaborative filtering shipped** 2026-06-18 (item-based, via TMDB recommendations); the old "deferred to ~200
  tenants" framing is dead. **Stripe is scaffolded and deploy-ready**, flag-gated off with blank keys. **Embeddings
  are remote** (`text-embedding-3-small`, 1536 dims, via OpenRouter); the local 384-dim ONNX model is no longer the
  default.

## Product remediation (2026-08-10) — each with its why
- **Trakt history write-back is movie-only.** *Why:* a bare show object on Trakt `sync/history` marks **every aired
  episode** watched — rating a show wrote hundreds of fake plays into the founder's real history. Shows sync ratings
  only; the movie path stays reversible via `RemoveFromHistory` anchored on `WatchedTitle.ReelLoggedWatchedAt`.
- **The stable-tonight contract.** *Why:* two contradictory complaints ("rows never change" / "hero churned three
  times an hour") resolve as one rule — identical within a UTC day, guaranteed different across days, immediate on a
  user action. Do not re-tune one side without re-checking the other.
- **Negations quarantine, never mix.** *Why:* the reserve top-up used to refill unflagged, so "no horror" showed
  horror cards whose own captions admitted the violation. Contradicting titles now appear only under a labelled
  "Outside your request" channel.
- **Dislike reasons demote by similarity, not just exclusion** (capped, decaying, at every scoring surface — a
  reaction used to hide exactly one title); **why-lines are anchored and never self-defeating**; **PIN unlock is a
  DataProtection token, never the cached raw PIN**.

## UI calls from the 2026-09-11 QA walks
- **The cross-reference pill names the pass ("first pass"), never a denominator.** *Why:* a denominator the user never
  sees reached is the opposite of the honest indicator that was asked for.
- **The lens's own subject title is excluded from the invented-anchor scan.** *Why:* "<Film> gives you…" is the most
  natural lens sentence and was the majority of live refusals.
- **`/lab` is a `RequireFounder` route, not just a hidden link,** and taste pockets under 3% of loved weight are not
  shown — *why:* a weekend is not a taste.
