---
name: fa-reel-state
description: "Not deployed, zero external users, model loses to popularity. main is red since the 2026-11-20 push (NU1903 advisories). Ask Reel v2 sits on build/trust-v1, pushed but unmerged; the sealed holdout is still one-disk, outside git."
type: state
last_verified: 2026-11-20
---

# Current state

**The product.** A multi-tenant Trakt-backed movies/TV recommender: feed hero and rows, Ask Reel chat, lexical and
semantic search, taste-DNA dashboard, provider strips, Trakt write-back. It works end to end for exactly one account
— the founder's (local `reel` database, inspected 2026-10-22). **Not deployed, zero external users**, so milestone
4's gate and the kill criterion on it have never been evaluable (`questions.md`). Last evidence of founder use: Ask
Reel turns in the local database through 2026-10-22; the database was unreachable on 2026-11-20 to re-check.

**Nothing serves locally (inspected 2026-11-20).** The Docker daemon is not running, `localhost:5173/health` and
`:5054/health` answer nothing (`000`), and no Reel API or Vite process exists. On 2026-10-22 the whole compose stack
was up, its backend running `openai/gpt-6-luna` — billed through that day, last `gpt-5.4-mini` bill 2026-10-03.

**CI and deploy (`gh`, 2026-11-20).** Repo PRIVATE. **`main` is red:** the 2026-11-20 pushes of context commits `39452d8`
and later `b7342a8` (the first pushes since PR #1's 2026-09-19 squash-merge, the last green run) both failed CI; the
`build/trust-v1` push started no run — Backend `Restore` on NU1902/NU1903
advisories against `System.Security.Cryptography.Pkcs`/`.Xml` under `TreatWarningsAsErrors`, and Frontend E2E at its
Playwright-browser install. This is the accepted idle-repo blind spot in `decisions-engineering.md` arriving; the
remedy is the explicit-pin rule there, a product-repo change. The redeploy job skips, not fails: the repo has **no
variables at all** (`DEPLOY_ENABLED` unset); the Docker Hub and Portainer URL/token/endpoint secrets exist,
`PORTAINER_STACK_ID` does not. Published `main` carries Microsoft 10.0.10 and the `gpt-5.4-mini` defaults. Setup: `docs/deployment.md`.

**Repo topology (re-probed the evening of 2026-11-20: `git fetch` in both clones, `git ls-remote origin`).** One
private repo, two clones, both level with the remote, clean, no stash.
- **The remote holds two branches:** `main` and **`build/trust-v1`** (`22df526`, 2026-10-31), pushed as a new branch
  that evening by the founder, not an agent. It is the integration branch of an Ask Reel v2 build carrying several
  hundred commits dated 2026-10-22 to 2026-10-31: answer quality, a golden eval, hardening rounds (commits labelled up
  to `HREV8`), deploy-chain work (gateway image, cloudflared in the stack). **It is no longer one-disk; the open call
  is merge or retire.** It is diverged from `main`, which carries only context commits the branch lacks.
- **iCloud clone** (this directory) — `main`, level with `origin/main`; context files only.
- **Dev clone** `~/src/fa.reel` — `main` fast-forwarded to `origin/main`, plus `build/trust-v1` tracking its remote
  twin. One worktree, `~/src/fa.reel-live`, detached at `22df526`. The earlier `hrev*` scratch folders beside it were
  deleted 2026-11-20.
- **Still one-disk, and in no repository** (inspected 2026-11-20, `git rev-parse` fails in both):
  `~/src/fa.reel-eval-sealed/`, the **sealed eval holdout — irreplaceable**; and `~/src/fa.reel-ws/`, the lane tooling,
  dumps, ledger archive and a `lane.env`. No undo archive or backup of either exists, by founder decision; whether
  and where they become durable is Dean's call.
- **`build/trust-v1` carries its own context store**, edited on that branch: a rewritten `CLAUDE.md` (gateway as an
  image, gpt-6-luna lock) and four topics this store lacks (`deploy`, `state-ask-eval`, `decisions-ask-v2`,
  `decisions-ask-eval`), forked from an older copy of this one. **Merging it into `main` conflicts on every context
  file**; reconcile topic by topic, never by taking one side. This store records `main`; that branch's own topics
  are the record of its build and were not imported here (unverified, and copying them makes the conflict worse).

## Where it stands, by half

**Modelling — settled negative, and now unmeasurable.** The per-user FastTree model does not beat the popularity
baseline. `FeatureSchema.Version` is **5** on `main` (source, 2026-11-20) after the v6 bump was reverted on its
pre-registered rule; **6 is burned, the next bump is 7**; on 2026-10-22 the live database held exactly one `Active`
artifact, at schema 5. Two runs over identical data returned NDCG figures further apart than any per-iteration
effect, so the ledger is the blocker — no schema bump against it (`watch.md`, `decisions-model.md`). The ledger has
run to iteration 17 against a 3-iteration kill criterion, with no recorded decision to continue (`questions.md`).

**Supply, not ranking, was the binding constraint.** Measured 2026-09-09, pre-seed: union retrieval recall could not
exceed **25.5%**. *(Dated; re-run `GET /api/dev/retrieval-recall` before quoting it.)* The bulk seed has since
landed; enrichment drains under system-wide daily ceilings.

**Delivered to `main` 2026-09-11:** Ask Reel chat and the feed rebuild, each closed by a headed QA walk (follow-ups:
`watch.md`). Everything since is on the unmerged `build/trust-v1` above.
