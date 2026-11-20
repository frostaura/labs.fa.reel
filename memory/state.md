---
name: labs-fa-reel-state
description: "Not deployed, zero external users, model loses to popularity. Repo PUBLIC since 2026-11-20; rename shipped on both branches; CI disabled, never green on the fix, no image on Docker Hub. Ask Reel v2 on build/trust-v1, unmerged."
type: state
last_verified: 2026-11-20
---

# Current state

**The product** works end to end for exactly one account — the founder's (local `reel` database, inspected
2026-10-22; last Ask Reel turns that day; unreachable 2026-11-20 to re-check). **Not deployed, zero external users**,
so milestone 4's gate and the kill criterion on it have never been evaluable (`questions.md`).

**Nothing serves locally (re-probed late 2026-11-20):** Docker down, `:5173`/`:5054` `/health` answer `000`. On
2026-10-22 the compose stack ran `openai/gpt-6-luna` (billed through that day; last `gpt-5.4-mini` bill 2026-10-03).
**The repo is PUBLIC** (`gh repo list`, late 2026-11-20; founder decision — `decisions-product.md`).

**CI, images and deploy (`gh`, Docker Hub API, late 2026-11-20).** The `CI` workflow is **`disabled_manually`** by
the founder. Three 2026-11-20 runs on `main` failed (NU1902/NU1903 restore, Playwright install); `b6f8e2f7` moved
the Microsoft set to 10.0.13 and pinned x64 runners to `ubuntu-24.04`, its own run still failed, and the run for
`054bceea` (the lockfile repair) was **cancelled** — so **no run has gone green on the fixed `main`**; the last
green is PR #1 (2026-09-19). `3cf9db7` and every `build/trust-v1` push started no run. **No Reel image exists on
Docker Hub** — the `frostaura` namespace is empty, so nothing could deploy or roll back even with the stack. The
redeploy job would still skip: no repo variables (`DEPLOY_ENABLED` unset); Docker Hub and Portainer
URL/token/endpoint secrets exist, `PORTAINER_STACK_ID` does not. Setup: `docs/deployment.md`.

**Renamed 2026-11-20 (founder), product side shipped the same night.** `fa.reel` → **`labs.fa.reel`**: this
directory, the repo (old URL redirects), the `~/src` folders `labs.fa.reel`, `-ws`, `-eval-sealed`, `-live`, and in
the product the images (`labs.fa.reel-backend`/`-frontend`; `-gateway` exists only on `build/trust-v1`), User-Agent,
frontend package, solution `labs.fa.reel.slnx` and the eval scripts' paths — `git grep` finds no old name on either
branch (late 2026-11-20). Compose pins `name: fareel` (`gotchas-build.md`); `FrostAura.Reel.*` namespaces stay.

**Repo topology (re-probed late 2026-11-20: `git fetch` in both clones, `git ls-remote origin`).** One repo, two
clones, no stash; the dev clone trails `origin/main` only by context commits pushed from this one — `git pull` there.
- **The remote holds two branches:** `main` (last product commit `3cf9db7`) and **`build/trust-v1`** (`d63de74`), the integration
  branch of an Ask Reel v2 build: several hundred commits dated 2026-10-22 to 2026-10-31 (answer quality, a golden
  eval, hardening rounds labelled up to `HREV8`, gateway image and cloudflared in the stack), plus the 2026-11-20
  rename commits. It is diverged from `main`; **the open call is merge or retire.**
- **Clones:** this iCloud directory (`main`, context files only) and the dev clone `~/src/labs.fa.reel` (`main`, plus
  `build/trust-v1` tracking its twin). One worktree, `~/src/labs.fa.reel-live`, **detached at `22df526f`** — the
  branch tip before the rename commits.
- **Still one-disk, and in no repository:** `~/src/labs.fa.reel-eval-sealed/`, the **sealed eval holdout —
  irreplaceable**; and `~/src/labs.fa.reel-ws/`, the lane tooling, dumps, ledger archive and a `lane.env` (its
  `bin/build-slot` and `lane.env` comment now name the new path; no ghost `~/src/fa.reel*` folder exists). No backup
  of either exists, by founder decision; whether they become durable is Dean's call.
- **`build/trust-v1` carries its own forked context store** (a rewritten `CLAUDE.md`; topics `deploy`,
  `state-ask-eval`, `decisions-ask-v2`, `decisions-ask-eval`). A merge conflicts on every context file — reconcile
  topic by topic. This store records `main`; that branch's topics were not imported.

## Where it stands, by half

**Modelling — settled negative, and now unmeasurable.** The per-user FastTree model does not beat the popularity
baseline. `FeatureSchema.Version` is **5** on `main` after the v6 bump was reverted on its pre-registered rule;
**6 is burned, the next bump is 7**. Two runs over identical data returned NDCG figures further apart than any
per-iteration effect, so the ledger is the blocker (`watch.md`, `decisions-model.md`). The ledger has run to
iteration 17 against a 3-iteration kill criterion, with no recorded decision to continue (`questions.md`).

**Supply, not ranking, was the binding constraint.** 2026-09-09, pre-seed: union retrieval recall capped at **25.5%**
(dated — re-run `GET /api/dev/retrieval-recall` before quoting). The bulk seed has landed; enrichment drains. The
last feature delivery to `main` was Ask Reel chat and the feed rebuild, 2026-09-11; everything since is on the branch.
