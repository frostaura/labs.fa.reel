---
name: labs-fa-reel-watch
description: "Chat still has NO ceilings — no tier, budget or rate limiter on main or the branch. build/trust-v1's eval scripts read the side folders by pre-rename paths: never run an eval until fixed. Reactions re-judge the whole pool."
type: watch
last_verified: 2026-11-20
---

# Watch list

## Unbounded cost — the sharpest item here
- **The chat surface has no ceilings.** Ask Reel's phase 0 and 1 shipped; **phase 2 did not**: no rate limiter, no
  tier rungs (Free gets the whole pipeline), no chat budget, no kill switch, no narrative fencing, no export or erasure, secrets not
  rotated. No rate limiter exists in the backend on `main` or on `build/trust-v1` (source, 2026-11-20). **A
  provider-side spend cap at OpenRouter is the cheapest mitigation** — recorded unclicked 2026-09-11; whether it is
  set cannot be seen from the repo, so ask Dean.
- **Every in-app reaction re-judges the whole cross-reference pool.** The taste-profile hash carries the dislike
  centroid and loved anchors by design (a lens note must not survive a dismissal), so each reaction turns the scored
  pool unjudged — budget-bounded, but the dominant LLM cost. **Next call:** a coarser verdict key or a daily cap.
- **`main` breaks its own `max_tokens` rule.** The fallback `OpenRouterSearchInterpreter` sends no `max_tokens` and never
  checks `finish_reason` (source, 2026-11-20). `build/trust-v1` routes it through a shared client its comment says
  bounds the completion (not inspected); the fix lands with that branch, or not at all.

## Owed work
- **`build/trust-v1`'s eval tooling reads the renamed side folders by their OLD paths — never run an eval until fixed.**
  `scripts/eval/judge.mjs` hard-codes `~/src/fa.reel-eval-sealed/askreel/judge/prompt.md` with no override, so the judge
  cannot find its sealed prompt; `ledger.mjs` and `guards.mjs` default to `~/src/fa.reel-ws/…` (`EVAL_LEDGER_ARCHIVE`,
  `EVAL_LOCKS_DIR` override); `build-at.sh` silently drops the build-slot semaphore; `docs/ask-reel-eval.md` and
  `slotApi.ts` cite them too. Outside git, `labs.fa.reel-ws/bin/build-slot` `mkdir -p`s the old `.locks` path, re-creating a
  ghost `~/src/fa.reel-ws`, and `lane.env` names it. On `main`: `fa.reel.slnx` (CI, `Makefile`), the `fa.reel-frontend`
  package and the Trakt/TMDB User-Agent `fa.reel/0.1 (+…/frostaura/fa.reel)`. All product changes (`git grep`, 2026-11-20).
- **The cross-reference pool is shortlist-only** — verdicts need a persisted score, so coverage grows one build at a
  time. **Next change: persist first-pass scores for the whole retrieved set.** Round 2 (the code's PR4) stays gated
  on the OUT arm's Wilson-upper threshold (`decisions-retrieval.md`).
- **`HeroEligibleCount` at the 7.5 floor was zero on every build**, even after the seed; until it is not, the hero
  floor relaxes on every build.
- **The retrieval-recall and build-latency gates are unpaid.** The recall probe needs a before/after over a week;
  build p95 is unattributed; `hnsw.ef_search` is still at its default while cluster pulls ask for hundreds.
- **The 50-read human review bar for lens copy has not been met.** The audit endpoint serves the fifty rows; the
  reads have not happened, and the latest prompt version restarted the count.
- **QA-walk follow-ups still unstarted:** the "done" reason has two shapes on the lexical and filmography paths; the
  ranking-seed embedding is bought per turn; the conditions summary's "+N more" is inert on the feed.
- **Two layout shifts left on `/search`, both product calls:** the conditions strip pushes the page down on load; a
  late `reranked` frame can grow a card by its why-line.
- **Extraction-era pollution is still draining** — tone and era on the oldest rows stay gated off by a minimum
  trusted extractor version until the stale-extractor count reaches zero.
- **`PipelineStage` has no "not enough ratings" value**, so an account below the ratings floor is parked as `Degraded`.

## Debris and hygiene
- **The QA walks' probe conversations are in the live database.** Delete through the UI or
  `DELETE /api/search/conversations/{id}` — never SQL, which leaves the turn record and the usage ledger disagreeing.
- **Throwaway `reel_test_*` databases pile up on port 5433** — 28 on 2026-10-22 (not re-counted: database down
  2026-11-20). Drop the QA copies; one with background jobs on spends real provider calls.
- **This iCloud clone holds gitignored data that syncs to Apple's cloud:** the live-credential `.env` (Trakt secret
  and tokens, TMDB, OpenRouter, the JWT signing key — rotate before any public launch), the founder's
  Trakt export in `research/data/` (ratings and watch history), and build output (`node_modules`, `bin`/`obj`,
  `dist`, `coverage`, `TestResults`, all dated 2026-09-09 or earlier). Removing any of it is Dean's call.

## Standing
- **The declared quality bar is "Silver", whose definition includes *deployed*.** Reel is not, so the tier reads as a
  target rather than a met bar — and nothing in the scope says which it is.
- **The "≥5 external users" kill gate has no counter** — it is measured by hand.
- **`AskReelEndpointTests.An_aborted_stream_leaves_a_failed_turn_and_the_prior_block_still_replays` is
  timing-sensitive in CI** — red once on 2026-09-16, green since. Two reds in a row is a regression. Owner: unassigned.
- **Repo docs and comments that are wrong, not the code:** `docs/deployment.md` still lists "weekly" in its pipeline
  table and keeps a "weekly rot check" section that `ci.yml` does not have; `docs/product-spec.md` cites a
  brand-assets path not in this repo; `docker-compose.yml`'s postgres comment says `vector(384)` where the schema is
  `vector(1536)`. Each is a product-repo edit, not a context one.
