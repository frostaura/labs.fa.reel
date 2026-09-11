---
name: fa-reel-state
description: "Works end to end for one account, NOT deployed, zero external users — the 5-user kill clock was never evaluable. The iCloud clone is a diverged fork of a pushed main; force-push destroys product work. The model still loses to the baseline."
type: state
last_verified: 2026-09-11
---

# Current state

**The product.** A multi-tenant Trakt-backed movies/TV recommender: feed hero and rows, Ask Reel natural-language
chat, lexical and semantic search, taste-DNA dashboard, provider strips, Trakt write-back. It works end to end for
exactly one account — the founder's. **It is not deployed and has zero external users**, so milestone 4's "≥5 external
users" gate and the kill criterion built on it have never been evaluable. Deploy is a founder action, not a build
task: `questions.md`.

**Repo topology — the iCloud clone is a DIVERGED FORK of a published `main` (both clones inspected against GitHub,
2026-09-11).** One private repo. `~/src/fa.reel` is the dev clone where code, builds and the dev loop live, and it is
**in sync with `origin/main`** — the founder pushed the Ask Reel CLS and live-search flake work from there. The copy
under `FrostAura Global/Labs/projects/fa.reel` is the **context-file home only**; it does not contain what was pushed,
and it carries context commits published `main` has never seen. **A plain push from here is rejected, and `--force`
would destroy pushed product work — never force.** Reconciling means `git fetch` here, then merging or rebasing the
context commits onto published `main`, and it raises a **modify/delete conflict**: those commits *delete* the old
single-file `memory/gotchas.md` and `memory/watch.md` that a pushed commit *modifies*, so the easy answer (take the
deletion) silently discards real gotcha and watch edits made in the dev clone. **The reconcile is the founder's call** —
do not fetch, merge, rebase or push on his behalf. **The iCloud clone's `origin/main` ref goes stale between fetches**,
so an ahead-count taken there is phantom until you `git fetch`.

**The test gate is real, and on 2026-09-11 it went green end to end on published `main`** — Backend, Frontend,
Security, Backend Integration and Frontend E2E all passed on the same run, the first all-green `main` since at least
early August. CI runs its integration job against a real `pgvector/pgvector:pg16` service with
`REEL_REQUIRE_INTEGRATION=1`, so a suite that silently skips its database tests fails the build rather than reporting
green; `dotnet format`, dual coverage ratchets, `npm audit` and `dotnet list package --vulnerable` are all
build-failing gates.

**Multi-arch images are published; nothing is deployed.** That run built and pushed backend and frontend images to
Docker Hub, and the Portainer redeploy step was **skipped, not failed**: the repo has no `DEPLOY_ENABLED` variable and
no `PORTAINER_STACK_ID` secret (the other Portainer secrets exist). So the deploy is two settings and a founder
decision away, not an engineering task — `questions.md`.

## Where it stands, by half

**Modelling — settled negative, and now unmeasurable.** The per-user FastTree model does not beat the popularity
baseline; iterations run on the *repaired* instrument have all come back non-positive. `FeatureSchema.Version` is back
at **5** after the v6 bump was reverted on its own pre-registered rule (`decisions-model.md`); **6 is burned and the
next bump is 7**. The eval ledger itself is now the blocker: two runs over identical data returned materially
different NDCG figures, a spread wider than any per-iteration effect, so a single iteration's number is evidence of
nothing. Do not run another schema bump against this instrument — `watch.md`.

**Supply, not ranking, was the binding constraint.** Measured 2026-09-09, pre-seed: union retrieval recall could not
exceed **25.5%** — only a small minority of the titles Dean later rated ≥8 were in the catalogue the day before he
rated them. *(Dated; re-run `GET /api/dev/retrieval-recall` before quoting it.)* The bulk seed has since completed and
the eligible pool grew by about an order of magnitude; enrichment still drains under system-wide daily ceilings, and
`HeroEligibleCount` at the 7.5 floor is still zero on every build.

**The v6 revert's designed degradation has completed.** Reverting the bump superseded the v6 artifact and left the
account with no `Active` model; the boot sweep enqueued the retrain as designed. Verified on the live `reel` database
2026-09-11: there is again exactly one `Active` artifact, at schema 5. Nothing is owed here.

**Delivered 2026-09-11, in two programmes — Ask Reel chat (server-owned turn record, continuity, negation handling,
honest cost accounting) and the feed (two-row hero, taste-cluster retrieval over the whole rating history,
cross-reference verdicts serving keep-only).** Each closed with a headed QA walk the founder followed, and every row
those walks raised was fixed before they ended — but the follow-ups the QA walks raised are mostly still unstarted, so
the walks are closed and the surface is not (`decisions-*` for what was decided, `watch.md` for what is owed).

**The dev loop** runs from `~/src/fa.reel`: Vite on 5173, the API on 5054 under plain `dotnet run`, Postgres in
Docker on 5433, gateway container deliberately stopped. Traps: `gotchas-build.md`.
