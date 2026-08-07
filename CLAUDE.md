# fa.reel — Reel

## Research question
Can a per-user, content-based classic-ML recommender trained on one user's full Trakt history beat a popularity baseline by ≥20% relative precision@10 on a time-split holdout of that user's own ratings — and can that engine carry a commercial multi-tenant SaaS? The modelling half has an affirmative first answer; the commercial half is the live question.

## What this program is
A multi-tenant movies/TV recommender SaaS built on Trakt. A user links their Trakt profile; Reel ingests everything ever watched plus ratings and in-progress shows, derives LLM tone/pacing/theme attributes and plot embeddings, and trains a **per-user** ML.NET FastTree model that predicts that user's 0–10 rating over an 86-dimension feature vector. It serves an explainable feed restricted to titles the user has not finished, with live search and where-to-watch provider strips, and writes back to a managed Trakt list ("Reel — Up Next") that syndicates into Plex/Kodi/Infuse. Interesting now because LLM feature extraction makes content-based recommendation viable for a single user from day one — no cross-user data needed at launch.

## Stack & repo
`github.com/frostaura/fa.reel` (**private** — see the stealth decision in [`MEMORY.md`](MEMORY.md); the `LICENSE` file is still MIT but the repo is not published).  Mandated stack throughout: .NET 10 with `TreatWarningsAsErrors`, EF Core + Npgsql, React 19, Tailwind, shadcn/ui, Vite, Redux Toolkit. Solution `fa.reel.slnx` at root; hexagonal backend under `src/backend` with layering enforced by project references; frontend under `src/frontend`. ML is ML.NET FastTree in-process (pure managed, arm64-safe). Embeddings and LLM calls route through OpenRouter behind `IEmbeddingProvider`. Data sources: Trakt (history, ratings, watching) and TMDB (catalogue, search, metadata, watch providers). **One documented deviation:** the database image is `pgvector/pgvector:pg16` rather than the house `postgres:16-alpine`, because vector and trigram extensions must be compiled in — justified in a compose comment. Standard FrostAura deploy pattern targeting `reel.frostaura.net`. **Quality bar: Silver.**

## Owner & key people
Dean Martin (founder). Dev/test Trakt profile `deanmar09` (public).

## Milestones
1. **App skeleton** — Trakt OAuth wireup, background sync into Postgres, tenant model, verified founder ingest.
2. **In-app ML pipeline + edge proof** — feature extraction, training and time-split evaluation running as app jobs, beating the popularity baseline by ≥20% relative precision@10 on a leakage-clean split.
3. **MVP surface** — per `docs/product-spec.md`: picks hero and rows, card anatomy and expanded modal, reactions plus Trakt write-back to the managed list, both search modes, where-to-watch ladder, taste-DNA dashboard, onboarding reveal.
4. **Multi-tenant** — row-level isolation audited on real Postgres, entitlements enforced server-side, shared rate budget, and **≥5 external users**.
5. **Commercial gate** — Trakt commercial-API terms agreed; JustWatch deep-link decision made.

## Kill criteria
- The model cannot beat the popularity baseline by a meaningful margin after **3** modelling iterations.
- Trakt refuses commercial use, or prices it beyond the unit economics.
- **Fewer than 5 external users willing to link a profile within 60 days of MVP.**

## Graduation pathway
`Technologies/` once the recurring-revenue path is proven — the standard Labs → Technologies graduation pattern. `Ventures/` only on standalone-brand traction. If Trakt refuses commercial use, kill the product but keep the engine: the per-user feature/training/eval machinery is reusable elsewhere.

## Conventions & invariants
- **No manual pipelines (founder-locked).** OAuth wireup, ingestion, feature extraction, training and evaluation all run inside the app, driven by per-user config. Ad-hoc scripts and notebooks are never the path of record.
- **Eligibility rule (founder-locked): "never show things I've already seen."** Suggestion surfaces — feed hero and rows, Ask Reel, lexical and semantic search — show only titles with a **zero interaction footprint**: no watch history even partial, no rating (a rating implies it was seen, including titles rated without a logged play), not marked NotInterested. In-progress titles appear exclusively on the continue-watching surface. Enforced in `EligibilityQueryBuilder.EligibleTitles`. Typeahead intentionally still finds watched titles — that is lookup, not suggestion — badged via `ContentFilteredTitles`.
- **Identity is Sign in with Trakt only (founder-locked).** The OAuth callback *is* account creation; the app session is `reel_at`/`reel_rt` HttpOnly cookies. Row-level tenancy runs through `IAccountScoped` plus EF global query filters, and **a model test fails the build if a scoped entity ships without isolation.**
- **`FeatureSchema.cs` is explicitly versioned.** Changing feature order is a schema version bump, never an in-place edit — stored artifacts would otherwise silently misread their own inputs.
- **`TitleScore` is write-only** and never read by training or evaluation. The leakage guard is stated in its doc-comment; keep it that way.
- **The eval harness is deliberately hostile to itself** (`EvalHarness.cs`): 80/20 time split on `RatedAt`; holdout triple-filtered to newest, not-in-train, and not in `WatchedTitles` before the split; features built strictly as-of `splitAt`; positive threshold `max(8, trainMedian)` — "loved, not tolerated"; a symmetric popularity baseline over the identical pool; gate at ≥20% relative precision@10. It **persists its own caveat** that the popularity baseline uses present-day TMDB popularity because no historical snapshots exist. **That caveat used to claim the effect was symmetric between model and baseline; it is not, and the claim was corrected on 2026-08-07.** Present-day popularity is 100% of the baseline's ranking signal and roughly 1/86 of the model's, so the asymmetry runs *against* the model and the measured lift is understated, not flattered. Do not soften any of this to make a number look better — and note that the correction moved the number the harder way.
- **Where-to-watch:** TMDB watch-provider data is legal, but deep links are licence-restricted by JustWatch. Ship provider badges and provider-side search links; true deep links require a partnership. **No scraping.**
- The extraction model is founder-locked to `openai/gpt-5.4-mini`.
- nginx config is inlined as a compose `configs:` block for a stated reason — Portainer stacks cannot mount repo files. `/sse/` is proxied with buffering off and `proxy_read_timeout 86400s`.

## Build / test / run
Local Postgres publishes on **5433**, not 5432 (5432 is taken on the dev Mac). CI runs `dotnet format` as a gate, dual coverage ratchets (backend 10%, frontend 6% — honestly labelled a regression guard, not a target), `npm audit --audit-level=high` and `dotnet list package --vulnerable` as build-failing security gates, a real `pgvector/pgvector:pg16` integration service, and **native `ubuntu-24.04-arm`** builds rather than QEMU.

**Deploy — see [`docs/deployment.md`](docs/deployment.md).** Docker Hub publishing is live: every push to `main` ships multi-arch `reel-backend`/`reel-frontend` `:latest`. CI's `Docker Build & Stack Smoke` job (`make docker-ready`) gates those publish jobs and runs on PRs too, so broken Dockerfile or compose wiring can no longer reach a `:latest` push. Portainer deploy is the only leg still off, gated by the `DEPLOY_ENABLED` repo *variable* so it skips rather than fails while the stack is missing. `make` is the shared entrypoint for CI and local alike.

**Dev QA helper:** `POST /api/auth/dev/link` (Development plus `QA_HELPERS_ENABLED=true` only) links `TRAKT_DEV_PROFILE` using `TRAKT_ACCESS_TOKEN`/`REFRESH_TOKEN` from `.env`, giving the full pipeline and a session without the browser consent hop. The production path of record remains the OAuth callback.

**External accounts required:** Trakt (identity, history, write-back), TMDB v4, OpenRouter (LLM plus embeddings), Docker Hub, Portainer, Cloudflare. Stripe optional.

**Footguns, already paid for:** Trakt and TMDB sit behind Cloudflare, which 403s UA-less requests — the explicit User-Agent on the typed clients is load-bearing. All `DateTime`s normalize through a model-wide UTC converter, because Trakt's date-only fields arrive `Kind=Unspecified` and Npgsql refuses them.

## Where docs live
`docs/product-spec.md` holds every locked v1 UX decision and is binding. `docs/deployment.md` covers the pipeline, the required secrets and variables, and standing the Portainer stack up. `docs/concept.md` is the original pitch; `docs/screenshots/` holds the Playwright QA captures. Known drift between the spec and what exists is tracked in [`MEMORY.md`](MEMORY.md), not here.

## Never do this here
- Never surface a title with any interaction footprint on a suggestion surface.
- Never edit `FeatureSchema.cs` field order in place.
- Never read `TitleScore` from training or evaluation code.
- Never introduce a manual/notebook pipeline as the path of record.
- Never scrape watch-provider deep links.
- Never ship an `IAccountScoped` entity without its global query filter — the build should catch you, but do not rely on that.

## Upkeep
This `CLAUDE.md`, [`MEMORY.md`](MEMORY.md) and any local `.claude/skills/` here are kept current as changes land. `MEMORY.md` is updated at the end of any session that shipped, decided, discovered or abandoned something, and the program's row in [`../MEMORY.md`](../MEMORY.md) is updated in the same pass. A skill that no longer matches how work is actually done is fixed or deleted, not left to rot. The standard: [`/docs/operating/context-cascade.md`](../../../docs/operating/context-cascade.md).
