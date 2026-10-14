---
name: fa-reel-state
description: "Works end to end for one account; NOT deployed, zero external users, model still loses to popularity. CI green with a stack-smoke gate; deploy still lacks DEPLOY_ENABLED + PORTAINER_STACK_ID. Dev clone holds uncommitted model-swap work."
type: state
last_verified: 2026-10-14
---

# Current state

**The product.** A multi-tenant Trakt-backed movies/TV recommender: feed hero and rows, Ask Reel chat, lexical and
semantic search, taste-DNA dashboard, provider strips, Trakt write-back. It works end to end for exactly one account
— the founder's; the local `reel` database holds one account (inspected 2026-10-14). **Not deployed, zero external
users**, so milestone 4's "≥5 external users" gate and the kill criterion on it have never been evaluable
(`questions.md`). Last commit 2026-09-19; last Ask Reel turn in the local database 2026-09-21.

**CI and deploy (inspected 2026-10-14).** PR #1 was squash-merged to `main` on 2026-09-19: a `Docker Build & Stack
Smoke` job now gates every publish job and runs on PRs too, react-router moved to 8.3 (the npm-audit waiver is gone),
the Microsoft 10.0.x set moved to 10.0.10, and CI lost its weekly cron. That run is green on every quality, smoke
and publish job; the Portainer redeploy job is **skipped, not failed** — the repo still has no `DEPLOY_ENABLED`
variable and no `PORTAINER_STACK_ID` secret (the other four deploy secrets exist). Setup is `docs/deployment.md`.

**Repo topology (inspected 2026-10-14).** One private repo, two clones; no code commit exists only locally.
- **iCloud clone** (`FrostAura Global/Labs/projects/fa.reel`) — context-file home only; `main` tracking `origin/main`,
  fast-forwarded to the PR #1 merge by the 2026-10-14 sweep, then ahead-only by that sweep's context commit — safe
  to push, which is Dean's call. Re-measure before acting.
- **Dev clone** `~/src/fa.reel` — fetched and knows the published tip; every local branch except the two below is
  already merged into `origin/main`. Checked out on `merge-pr1`, tracking the PR
  branch, which is **content-identical to `main`** (squash-merged); its local `main` is behind `origin/main`. Two
  squash leftovers, `merge-pr1` and `fix-cron` (both contained in the pushed PR branch), read as "not merged": **never merge either into `main`** —
  `fix-cron` predates months of `main` and would delete most of it. Deleting them is Dean's call.
- **Uncommitted, one-disk work in the dev clone, written 2026-10-14 ~11:15 local**, never `git add`ed: swaps the
  extraction/search/lens default model `openai/gpt-5.4-mini` → `openai/gpt-6-luna` across compose files, the
  Portainer stack, `.env.example`, the OpenRouter adapters and the dev clone's own `CLAUDE.md` (whose comment calls
  it "founder-locked 2026-10-14"), and moves the Microsoft 10.0.x package set to 10.0.13, dropping the
  `Microsoft.OpenApi` direct pin. The published rule still names gpt-5.4-mini. **Only Dean decides its fate**
  (`questions.md`); `git clean`/`checkout`/`stash` there destroys it with no reflog.
- A local compose image was built from that dirty tree the same morning and the compose stack, backend included,
  left up; at inspection the backend had died on its startup migration check and the gateway answered 502.
- The 2026-09-16 modify/delete hazard on the old single-file `memory/gotchas.md`/`decisions.md` is **gone** — the
  dev clone's checkout carries the split topics.

## Where it stands, by half

**Modelling — settled negative, and now unmeasurable.** The per-user FastTree model does not beat the popularity
baseline. `FeatureSchema.Version` is **5** (source, 2026-10-14) after the v6 bump was reverted on its pre-registered
rule; **6 is burned, the next bump is 7**; the live database holds exactly one `Active` artifact, at schema 5. Two
runs over identical data returned NDCG figures further apart than any per-iteration effect, so the ledger is the
blocker — no schema bump against it (`watch.md`, `decisions-model.md`).

**Supply, not ranking, was the binding constraint.** Measured 2026-09-09, pre-seed: union retrieval recall could not
exceed **25.5%**. *(Dated; re-run `GET /api/dev/retrieval-recall` before quoting it.)* The bulk seed has since
landed; enrichment drains under system-wide daily ceilings.

**Delivered 2026-09-11, in two programmes** — Ask Reel chat and the feed rebuild — each closed by a headed QA walk;
the follow-ups those walks raised are mostly unstarted (`watch.md`).

**Dev loop:** `~/src/fa.reel`, Vite 5173, API 5054 under plain `dotnet run`, Postgres in Docker on 5433, compose
`backend` stopped. Traps: `gotchas-build.md`.
