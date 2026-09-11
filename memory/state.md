---
name: fa-reel-state
description: "Works end to end for one account, NOT deployed, zero external users — the 5-user kill clock was never evaluable. Suites real and green on main at 0fc657e. Ahead-only, never diverged. The model still loses to the popularity baseline."
type: state
last_verified: 2026-09-11
---

# Current state

**The product.** A multi-tenant Trakt-backed movies/TV recommender: feed hero and rows, Ask Reel natural-language
chat, lexical and semantic search, taste-DNA dashboard, provider strips, Trakt write-back. It works end to end for
exactly one account — the founder's. **It is not deployed and has zero external users**, so milestone 4's "≥5 external
users" gate and the kill criterion built on it have never been evaluable. Deploy is a founder action, not a build
task: `questions.md`.

**Repo topology (inspected 2026-09-11).** One private GitHub repo, **two clones on this Mac**: `~/src/fa.reel` is the
dev clone where code, builds and the dev loop live, and the copy under `FrostAura Global/Labs/projects/fa.reel` is the
**context-file home only**. Both clones track the same `origin`, **neither has ever diverged from it, and neither
carries a product deletion it has not pushed** — this repo is ahead-only, so a plain `git push` is always safe and is
purely the founder's call. Everything the three 2026-09-11 programmes produced is on `origin/main`; what sits ahead
after that is context commits. Two cautions that recur. **The iCloud clone's `origin/main` ref goes stale between
fetches**, so `git rev-list --left-right @{u}...HEAD` there reports a large phantom "ahead" until you `git fetch` —
fetch before believing any ahead-count taken in that clone. And **a context commit that splits a memory topic shows
as deletions in the range**; that is the split, not a destructive push. The clones exchange commits by pulling from
each other's local path, so after committing in one, pull in the other.

**The test gate is real.** Backend xUnit and frontend vitest both run green on `main` at `0fc657e` (run 2026-09-11).
CI runs a separate integration job against a real `pgvector/pgvector:pg16` service with `REEL_REQUIRE_INTEGRATION=1`,
so a suite that silently skips its database tests fails the build rather than reporting green. `dotnet format`, dual
coverage ratchets, `npm audit` and `dotnet list package --vulnerable` are all build-failing gates.

## Where it stands, by half

**Modelling — settled negative, and now unmeasurable.** The per-user FastTree model does not beat the popularity
baseline; iterations run on the *repaired* instrument have all come back non-positive. `FeatureSchema.Version` is back
at **5** after the v6 bump was reverted on its own pre-registered rule (`decisions-model.md`); **6 is burned and the
next bump is 7**. The eval ledger itself is now the blocker: two runs over identical data returned materially
different NDCG figures, a spread wider than any per-iteration effect, so a single iteration's number is evidence of
nothing. Do not run another schema bump against this instrument — `watch.md`.

**Supply, not ranking, was the binding constraint.** Measured 2026-09-09, before the catalogue seed: union retrieval
recall could not exceed **25.5%** — only a small minority of the titles Dean later rated ≥8 were in the catalogue the
day before he rated them. *(That figure is dated and pre-seed; re-run `GET /api/dev/retrieval-recall` before quoting
it.)* The bulk seed has since completed and the eligible pool grew by roughly an order of magnitude; enrichment still
drains under system-wide daily ceilings, and `HeroEligibleCount` at the 7.5 floor is still zero on every build.

**The v6 revert's designed degradation has completed.** Reverting the bump superseded the v6 artifact and left the
account with no `Active` model; the boot sweep enqueued the retrain as designed. Verified on the live `reel` database
2026-09-11: there is again exactly one `Active` artifact, at schema 5. Nothing is owed here.

**Delivered 2026-09-11, in three programmes:** the Ask Reel chat programme (server-owned turn record, continuity,
negation model, honest accounting) with its own headed QA walk; Programme 2 (two-row hero, taste clusters over the
whole history, cross-reference verdicts serving keep-only) with a second headed QA walk over the release log. Both
walks were driven in a headed browser the founder followed, every defect fixed red-first and re-driven on screen, and
both closed with zero open rows. What those walks decided is in the `decisions-*` topics; what they left owed is in
`watch.md`.

**The dev loop** runs from `~/src/fa.reel`: Vite on 5173, the API on 5054 under plain `dotnet run`, Postgres in Docker
on 5433, the gateway container deliberately stopped. Traps: `gotchas-build.md`.
