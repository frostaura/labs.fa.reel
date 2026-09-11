---
name: fa-reel-state
description: "Works end to end for one account, NOT deployed, zero external users — the 5-user kill clock was never evaluable. The two clones on this Mac have DIVERGED; reconcile before any push. The model still loses to the popularity baseline."
type: state
last_verified: 2026-09-11
---

# Current state

**The product.** A multi-tenant Trakt-backed movies/TV recommender: feed hero and rows, Ask Reel natural-language
chat, lexical and semantic search, taste-DNA dashboard, provider strips, Trakt write-back. It works end to end for
exactly one account — the founder's. **It is not deployed and has zero external users**, so milestone 4's "≥5 external
users" gate and the kill criterion built on it have never been evaluable. Deploy is a founder action, not a build
task: `questions.md`.

**Repo topology — the two clones have DIVERGED from each other (both inspected against GitHub, 2026-09-11).** One
private repo; `~/src/fa.reel` is the dev clone where code, builds and the dev loop live, and the copy under
`FrostAura Global/Labs/projects/fa.reel` is the **context-file home only**. Both sit directly on published `main`, but
the dev clone holds unpushed **product** commits (the Ask Reel layout-shift fix and its merge) and the iCloud clone
holds the unpushed context commit that split this memory store — **neither holds the other's**. **So "fully pushed" is
false**, and a push from either leaves the other a diverged fork. Reconcile first, deliberately: the context commit
*deletes* memory files the dev clone's commits *modify*, so the reconcile raises a modify/delete conflict whose easy
answer (take the deletion) silently discards real gotcha and watch edits. Neither clone is behind `origin` and no
product file is deleted in either unpushed range; the clones exchange commits by pulling from each other's local path,
and the push stays the founder's call. **The iCloud clone's `origin/main` ref goes stale between fetches**, so an
ahead-count taken there is phantom until you `git fetch`.

**The test gate is real.** Backend xUnit and frontend vitest both ran green on `main` on 2026-09-11, at the commit
the published branch now sits on — before the dev clone's unpushed commits. CI runs a separate integration job against
a real `pgvector/pgvector:pg16` service with `REEL_REQUIRE_INTEGRATION=1`, so a suite that silently skips its database
tests fails the build rather than reporting green. `dotnet format`, dual coverage ratchets, `npm audit` and
`dotnet list package --vulnerable` are all build-failing gates.

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

**Delivered 2026-09-11, in two programmes, each closed by a headed QA walk the founder followed with zero open rows
at the end:** the Ask Reel chat programme (server-owned turn record, continuity, negation handling, honest cost
accounting), and the feed programme (two-row hero, taste-cluster retrieval over the whole rating history,
cross-reference verdicts serving keep-only). What they decided is in the `decisions-*` topics; what they left owed is
in `watch.md`.

**The dev loop** runs from `~/src/fa.reel`: Vite on 5173, the API on 5054 under plain `dotnet run`, Postgres in
Docker on 5433, gateway container deliberately stopped. Traps: `gotchas-build.md`.
