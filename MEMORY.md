# MEMORY — fa.reel

_Last verified: 2026-07-24 by the Labs tree-audit pass (git log, upstream tracking, working tree, `ci.yml`, `deploy/portainer-stack.yml`, test counts and package manifests inspected directly)._

## Current state
**Technically proven, commercially unproven, engineering-ready to deploy — and still on one machine.** Branch `main` at `2767cf9` (2026-07-24), which landed the month-old working set: hybrid retrieval, the online residual model, feed pagination + refresh coordinator, search intent parsing, the managed-list reconciler, the `Microsoft.OpenApi` security override, `deploy/portainer-stack.yml` and the QA screenshots (now under `docs/screenshots/`, previously loose at repo root). **Ten commits are unpushed**; `origin/main` is still at `513bd10` (2026-06-18), so the public repo is over a month behind and every line since exists only on the founder's Mac plus iCloud.

The M2 edge proof passed 2026-06-12 — precision@10 of 70% against a 50% popularity baseline, a 40% relative lift on a leakage-clean time split. **Caveat, stated plainly:** n = 1 (the founder's account) over roughly 10–30 holdout titles, so 70% vs 50% is 7 hits versus 5. The framework is sound; the sample is thin. "Iteration 1 of 3" is the right posture. M4 (multi-tenant) is code-complete with **zero external users**.

CI is real and among the strongest in the portfolio: `dotnet format` gate, 84 backend test methods with a 10% coverage ratchet, frontend lint/build/tests with a 6% ratchet, a Playwright e2e job, `npm audit --audit-level=high` plus a `dotnet list package --vulnerable` gate, an integration job against a real `pgvector/pgvector:pg16` service, native `ubuntu-24.04-arm` image builds and a multi-arch manifest merge. The Portainer redeploy job exists and is skipped (not failed) while the repo variable `DEPLOY_ENABLED` is unset.

## Live decisions
- **Deploy (founder, 2026-07-24).** The external-user kill criterion is evaluated against a *deployed* product, so the trip-by-timer-for-logistical-reasons scenario is void. Remaining founder steps: push, create the Portainer stack from `deploy/portainer-stack.yml`, set `DOCKERHUB_TOKEN` / `PORTAINER_*` secrets and the `DEPLOY_ENABLED` variable, point Cloudflared at `reel.frostaura.net`.
- Extraction model `openai/gpt-5.4-mini`, no manual pipelines, eligibility = zero-interaction-footprint, identity = Sign in with Trakt only — all founder-locked 2026-06-12 and carried into `CLAUDE.md`.
- **Collaborative filtering shipped** 2026-06-18 (item-based via TMDB recommendations, in `CandidateGenerator.cs`). The old "deferred to ~200+ tenants" framing is dead.
- **Stripe is scaffolded and deploy-ready**, flag-gated off with blank keys.
- **Embeddings are remote, not local** — `EMBEDDINGS_PROVIDER=openai`, `text-embedding-3-small`, **1536 dims** via OpenRouter. Local ONNX MiniLM at 384d is no longer the default.
- **Vulnerable transitives get an explicit pin, not a suppression.** Two now: `Microsoft.OpenApi` 2.7.5 (GHSA-v5pm-xwqc-g5wc via AspNetCore.OpenApi 10.0.9) and `System.Security.Cryptography.Xml` 10.0.10 (GHSA-8q5v-6pqq-x66h et al. via DataProtection.EntityFrameworkCore 10.0.9). Both carry a comment saying to remove them once upstream ships a patched transitive. The `security` CI job fails the build otherwise.

## Gotchas
- **`.env` holds live credentials inside iCloud Drive** — Trakt client secret, TMDB token, OpenRouter key, JWT signing secret and real Trakt access/refresh tokens. Correctly gitignored, but continuously synced to Apple's cloud and every device on the account. Rotate before public launch, and decide deliberately about keeping live secrets here at all.
- **The repo's `.git` carries stale lock files that this workspace cannot delete** (`Operation not permitted` on the iCloud mount). Any agent that cannot commit here should check `.git/*.lock` first — see the division `MEMORY.md`; the founder clears them with one `rm` on the Mac.
- Local Postgres publishes on **5433**, not 5432.
- iCloud filename-conflict duplicates keep appearing under `src/backend/**/bin/` and `obj/` (`... 2.dll`, `... 2.pdb`) — a direct symptom of building inside iCloud Drive. Build output only; harmless but noisy.
- `OPENAI_API_KEY` is vestigial; compose actually passes `EMBEDDINGS_API_KEY`.
- Of six Playwright e2e specs only `smoke.spec.ts` gates CI. Images publish under `latest` only — no semver.
- The compose comment still says `vector(384)` while the configured provider emits 1536 dims.
- Tailwind is **v3.4.10**, not v4.

## Open questions
- **The kill-criterion clock.** "Fewer than 5 external users within 60 days of MVP" lands ~2026-08-11 anchored at the edge proof, ~2026-08-18 anchored at the last MVP-surface commit. The anchor has still never been fixed. The deploy decision is made; whether the clock restarts at first-serve or runs from the original anchor is not. Founder.
- **Trakt commercial terms** remain an open M5 gate and could kill the product outright regardless of model quality.
- **The stealth exception is still unrecorded.** The repo is public while Labs is stealth-by-default and new repos are private by default. Record the exception or flip it private — founder call, and it should be made before the first push in over a month.
- The JustWatch deep-link decision is still unmade.

## Watch list
- `docs/product-spec.md` §"FrostAura family look" cites `/docs/brand/assets/` as the canonical asset location; no such directory exists in this repo.
- Success metrics for the commercial half are instrumented nowhere — the "≥5 external users" gate has no counter behind it.
