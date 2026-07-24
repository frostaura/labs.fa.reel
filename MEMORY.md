# MEMORY — fa.reel

_Last verified: 2026-07-20 by Labs audit (git log, remote tracking and working tree inspected directly)._

## Current state
**Technically proven, commercially stalled, and less safe than previously recorded.** Last commit 2026-06-19; last file touched 2026-07-06. **Nine commits are unpushed** — `origin/main` is at `513bd10` (2026-06-18, "Phase D batch 14: in-app rating picker") while local `main` is at `c4b39c7`, nine commits ahead, all dated 2026-06-19. Those nine include the Trakt 401 self-heal, the seen/rated feed-exclusion fix, the Reel product mark, the Trakt history-removal reconciliation and the on-demand sync control. Combined with the uncommitted working set below, **essentially all work since 2026-06-18 exists only on one Mac.** The M2 edge proof **passed on 2026-06-12** — precision@10 of 70% against a 50% popularity baseline, a 40% relative lift on a leakage-clean time split. M4 (multi-tenant) is code-complete but has **zero external users**, because the Portainer deploy job is explicitly disabled pending three founder-action items unchanged since 2026-06-12: a `DOCKERHUB_TOKEN` repo secret, a Portainer stack id, and a Cloudflared hostname for `reel.frostaura.net`. Collaborative filtering, the in-app rating picker and Stripe billing have all shipped since the last `CLAUDE.md` refresh.

**Caveat on the headline result, stated plainly:** n = 1 (the founder's account), precision@10 measured on roughly 10–30 holdout titles. 70% vs 50% is **7 hits versus 5** — a two-title difference. The framework is sound; the sample is thin. "Iteration 1 of 3" is the right posture.

## Live decisions
- Extraction model `openai/gpt-5.4-mini`, no manual pipelines, eligibility = zero-interaction-footprint, identity = Sign in with Trakt only — all founder-locked 2026-06-12 and carried into `CLAUDE.md`.
- **Collaborative filtering shipped** on 2026-06-18 (`2821e85`), item-based via TMDB recommendations, live in `CandidateGenerator.cs`. The old "deferred to ~200+ tenants" framing is dead.
- **Stripe is scaffolded, merged and deploy-ready** (`a6c2979`, `Stripe.net 52.0.0`, `BillingEndpoints.cs`, migration `AccountStripeLinkage`) — flag-gated off with all keys blank.
- **Embeddings are remote, not local.** Both compose files and `.env.example` default to `EMBEDDINGS_PROVIDER=openai`, `text-embedding-3-small`, **1536 dims**, routed via OpenRouter. Local ONNX MiniLM at 384d is no longer the default.

## Gotchas
- **`.env` holds live credentials inside iCloud Drive.** It is correctly gitignored and untracked, but it sits on disk with a live Trakt client secret, TMDB token, OpenRouter key, JWT signing secret and real Trakt access/refresh tokens — continuously synced to Apple's cloud and to every device on the account. The product spec already lists "rotate the client secret before public launch" as outstanding. Rotate, and decide deliberately about keeping live secrets in iCloud.
- **~3.7 MB of untracked Playwright screenshots sit at repo root** (`home-initial.png`, `reel-header-sync-indicator.png`, `trakt-signin.png`) and are **not gitignored** — a `git add -A` sweeps them straight into history. Delete or gitignore them *before* committing the working set.
- Six iCloud filename-conflict duplicates exist under `bin/Debug/` (`...deps 2..7.json`) — a direct symptom of building inside iCloud Drive.
- `OPENAI_API_KEY` is vestigial; compose actually passes `EMBEDDINGS_API_KEY`.
- Only 1 of 5 Playwright e2e specs (`smoke.spec.ts`) actually gates CI. Images publish under `latest` only — no semver.
- The compose comment still says `vector(384)` while the configured provider emits 1536 dims.

## Open questions
- **The kill-criterion clock expires in roughly three weeks.** The criterion is "fewer than 5 external users within 60 days of MVP"; anchored at the 2026-06-12 edge proof that is ~2026-08-11, anchored at the last MVP-surface commit (2026-06-19) it is ~2026-08-18. The anchor has never been fixed, which is itself worth deciding. Either way, with no deploy the criterion trips at zero external users for **logistical** reasons rather than product reasons. That should be a conscious founder decision — deploy and get the five users, or kill deliberately — not a default. **This is the most time-critical item in Labs.**
- **Trakt commercial terms** remain an open M5 gate and could kill the product outright regardless of model quality.
- The JustWatch deep-link decision is still unmade.

## Watch list
- **TOP ACTION: commit the working set, then push.** 65 modified files / **+2,425 −380 uncommitted for a month**, including a fix for a **high-severity vulnerable transitive dependency** (`Microsoft.OpenApi 2.7.5` override for GHSA-v5pm-xwqc-g5wc via AspNetCore.OpenApi 10.0.9). The `security` CI gate would fail on `main` as it stands. **Do not assume committed history is safe here — nine commits are also unpushed** (see Current state); the remote is a month stale. Push is the second half of this action, not optional.
- The uncommitted set is a coherent, tested feature program, not a version bump. Four themes worth preserving: **(a) hybrid retrieval** — `CandidateRetriever.cs` fixes what its own comment calls *"popularity-gated recall"*, where every feed surface previously seeded its pool by `OrderByDescending(TmdbPopularity).Take(N)` **before** the model scored anything, making "deep cuts" merely the least-popular of the popular; now 700 popularity plus 500 pgvector cosine-kNN. **(b) Online residual learning** — a per-account bounded-SGD linear head over the frozen FastTree base, zero-initialised so it is a no-op on fresh accounts, pinned to `BaseArtifactId` so a retrain auto-resets it. **(c) Infinite/paginated feed** with a `FeedRefreshCoordinator` rebuilding in seconds off the Active artifact, no retrain in the hot path. **(d) `TitleLanguageBackfillService`** repairing a real bug where `Title.Language` was only ever set from Trakt, so every TMDB-discovered title had a null language and slipped past the language allow-list.
- Tailwind is **v3.4.10**, not v4 as previously claimed.
- Backend test count is now **84 test methods**, not the 48 + 16 + 6 previously recorded.
- `docs/product-spec.md` §11 references `docs/brand/assets/` — that directory does not exist.
