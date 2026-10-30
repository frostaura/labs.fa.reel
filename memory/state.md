---
name: fa-reel-state
description: "Works end to end for one account. CI green end to end and images published, but NOT deployed and zero external users — the 5-user kill clock was never evaluable. The model still loses to the popularity baseline."
type: state
last_verified: 2026-09-16
---

# Current state

**The product.** A multi-tenant Trakt-backed movies/TV recommender: feed hero and rows, Ask Reel natural-language
chat, lexical and semantic search, taste-DNA dashboard, provider strips, Trakt write-back. It works end to end for
exactly one account — the founder's. **It is not deployed and has zero external users**, so milestone 4's "≥5 external
users" gate and the kill criterion built on it have never been evaluable. Deploy is a founder action, not a build
task: `questions.md`.

**Repo topology — re-inspected 2026-09-16, after the push.** One private repo, two clones. The copy
under `FrostAura Global/Labs/projects/fa.reel` is the **context-file home only**, and on 2026-09-16 the
founder authorised a tree-wide commit and push: **this clone is what went up**, and it re-read locally
afterwards as `main`, upstream `origin/main`, **zero ahead and zero behind**, clean tree. `~/src/fa.reel`
is the dev clone where code, builds and the dev loop live, and it is now **behind published `main` and
does not know it** — its `origin/main` ref predates the push, so `rev-list --left-right --count` there
reports 0/0 while the published tip is not even an object it holds (confirmed read-only 2026-09-16).
**Fetch there before believing any count, and never `--force` from either clone.** **The reconcile is
not free and will recur:** the context commits deleted the old single-file `memory/gotchas.md` and
`memory/decisions.md` — those two and no others — while the dev clone still edits them, so every merge
raises a modify/delete conflict on exactly those paths. Keep the deletion and fold the dev clone's
edits into the split topics first. **`memory/watch.md` is NOT one of them**: it is live in both clones
and was merged as a modification, so deleting it destroys a live topic.

**The test gate is real, and on 2026-09-11 it went green end to end on published `main`** — Backend, Frontend,
Security, Backend Integration and Frontend E2E all passed on the same run, the first all-green `main` since at least
early August. CI runs its integration job against a real `pgvector/pgvector:pg16` service with
`REEL_REQUIRE_INTEGRATION=1`, so a suite that silently skips its database tests fails the build rather than reporting
green; `dotnet format`, dual coverage ratchets, `npm audit` and `dotnet list package --vulnerable` are all
build-failing gates.

**Multi-arch images are published; nothing is deployed.** The deploy chain, the single-exposure production
topology and exactly what the founder still has to create are in `deploy.md` — a founder decision, not an
engineering task (`questions.md`).

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

**The dev loop** runs from `~/src/fa.reel`: Vite on 5173, the API on 5054 under plain `dotnet run`, Postgres in Docker on 5433, gateway container deliberately stopped. Traps: `gotchas-build.md`.
