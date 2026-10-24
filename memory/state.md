---
name: fa-reel-state
description: "In active founder use on the local compose stack; NOT deployed, zero external users, model loses to popularity. Deploy lacks DEPLOY_ENABLED + PORTAINER_STACK_ID. gpt-6-luna swap now committed in the dev clone, unpushed, one disk."
type: state
last_verified: 2026-10-22
---

# Current state

**The product.** A multi-tenant Trakt-backed movies/TV recommender: feed hero and rows, Ask Reel chat, lexical and
semantic search, taste-DNA dashboard, provider strips, Trakt write-back. It works end to end for exactly one account
— the founder's; the local `reel` database holds one account (inspected 2026-10-22). **Not deployed, zero external
users**, so milestone 4's "≥5 external users" gate and the kill criterion on it have never been evaluable
(`questions.md`). **In active founder use**: Ask Reel turns land in the local database through 2026-10-22.

**What serves it locally (inspected 2026-10-22).** The whole compose stack — `postgres`, `backend`, `frontend`,
`gateway` — is up with the backend healthy and `localhost:5173/health` answering 200; its backend image was built
2026-10-14 from the dev clone's then-uncommitted tree. No `dotnet run`/`dotnet watch` dev loop is running beside it,
which is the only safe combination (`gotchas-build.md`). The running backend's `OPENROUTER_MODEL` is
`openai/gpt-6-luna`; the usage ledger bills it through 2026-10-22 and last billed `gpt-5.4-mini` on 2026-10-03 —
the swap is paid for in practice, whatever the lock says (`questions.md`).

**CI and deploy (inspected 2026-10-22).** The last CI run is PR #1's squash-merge to `main` on 2026-09-19, green on
every quality, smoke and publish job; the Portainer redeploy job is **skipped, not failed** — the repo still has no
`DEPLOY_ENABLED` variable and no `PORTAINER_STACK_ID` secret (the other four deploy secrets exist). Published `main`
still carries Microsoft 10.0.10 and the `gpt-5.4-mini` defaults. Setup is `docs/deployment.md`.

**Repo topology (inspected 2026-10-22, after `git fetch`).** One private repo, two clones, and **they have
diverged** — each holds a commit the other lacks, so reconcile them before either pushes.
- **iCloud clone** (`FrostAura Global/Labs/projects/fa.reel`) — context-file home only; `main` tracking
  `origin/main`, ahead-only by the 2026-10-14 sweep's context commit (memory topics plus one `CLAUDE.md` line).
- **Dev clone** `~/src/fa.reel` — clean working tree, checked out on `merge-pr1`. The model swap that sat
  uncommitted since 2026-10-14 was **committed there on 2026-10-22** under Dean's identity (co-authored by Claude):
  every OpenRouter default `gpt-5.4-mini` → `gpt-6-luna` across compose files, the Portainer stack, `.env.example`
  and the adapters; the repo `CLAUDE.md` lock line rewritten to `gpt-6-luna`; the Microsoft 10.0.x set to 10.0.13
  with the `Microsoft.OpenApi` pin dropped; and two startup-resilience fixes — `DatabaseInitializer` retries a
  not-yet-ready database for up to three minutes, and a fatal startup error now exits so `restart: always` can
  recover (a Docker restart had left the API spinning, never listening). `origin/main` was then merged in, so the
  branch is ahead-only of `origin/main` — **unpushed, one disk only**; pushing is Dean's call. `fix-cron` is a
  squash leftover that reads as "not merged": **never merge it into `main`** — it predates months of `main` and
  would delete most of it. Deleting it is Dean's call.

## Where it stands, by half

**Modelling — settled negative, and now unmeasurable.** The per-user FastTree model does not beat the popularity
baseline. `FeatureSchema.Version` is **5** (source, 2026-10-22) after the v6 bump was reverted on its pre-registered
rule; **6 is burned, the next bump is 7**; the live database holds exactly one `Active` artifact, at schema 5. Two
runs over identical data returned NDCG figures further apart than any per-iteration effect, so the ledger is the
blocker — no schema bump against it (`watch.md`, `decisions-model.md`).

**Supply, not ranking, was the binding constraint.** Measured 2026-09-09, pre-seed: union retrieval recall could not
exceed **25.5%**. *(Dated; re-run `GET /api/dev/retrieval-recall` before quoting it.)* The bulk seed has since
landed; enrichment drains under system-wide daily ceilings.

**Delivered 2026-09-11:** Ask Reel chat and the feed rebuild, each closed by a headed QA walk (follow-ups: `watch.md`).
