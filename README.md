# Reel

**Link your Trakt profile; get live, explainable movie & TV recommendations you haven't seen yet — with where to watch.**

Reel ingests everything you've ever watched and rated on [Trakt](https://trakt.tv), extracts rich taste features (LLM-derived tone/pacing/theme attributes, plot embeddings, cast/crew affinity, taste drift), trains a per-user model that predicts *your* rating for titles you haven't seen, and serves an explainable feed: every pick carries a predicted rating ("8.4 for you") and a why-this sentence backed by the model's actual feature contributions.

A [FrostAura Labs](https://github.com/frostaura) program.

## How it works

1. **Sign in with Trakt** — your Trakt account *is* your Reel account; signup and profile-linking are one gesture.
2. **Live build-up** — Reel ingests your full history while you watch your taste DNA assemble on screen.
3. **Tonight's picks** — a hero of 3–5 confident picks plus rows (Continue Watching, Because You Loved X), all restricted to titles you haven't finished.
4. **Explainable by contract** — the predicted rating is a falsifiable promise; the model is evaluated per user on a leakage-clean time split against a popularity baseline, in-app.

## Stack

- **Backend:** .NET 10, ASP.NET Core Minimal APIs + SSE, EF Core 10, PostgreSQL 16 + pgvector. Hexagonal (Domain / Application / Infrastructure / Api).
- **ML:** ML.NET FastTree regression per user, in-process; ONNX MiniLM embeddings; LLM attribute extraction via OpenRouter.
- **Frontend:** React 19 + TypeScript, Redux Toolkit (RTK Query), Tailwind CSS, shadcn/ui, Vite, PWA.
- **Deploy:** Docker Compose; GitHub Actions → multi-arch Docker Hub → Portainer → Cloudflared.

## Local development

```bash
cp .env.example .env       # fill in Trakt / TMDB / OpenRouter keys
docker compose up -d       # postgres + backend + frontend + gateway
# or run hot:
dotnet run --project src/backend/FrostAura.Reel.Api
cd src/frontend && npm install && npm run dev   # http://localhost:5173
```

Create a Trakt API app at <https://trakt.tv/oauth/applications> with redirect URI `http://localhost:5173/auth/trakt/callback` for dev.

### Tests

```bash
# Unit suite — no database needed.
dotnet test src/backend/FrostAura.Reel.Tests/FrostAura.Reel.Tests.csproj

# Integration suite — needs a real pgvector Postgres. Point it at a THROWAWAY database:
# the fixture drops and re-migrates it on every run.
REEL_TEST_CONNECTION_STRING="Host=localhost;Port=5433;Database=reel_test;Username=reel;Password=…" \
  dotnet test src/backend/FrostAura.Reel.Tests/FrostAura.Reel.Tests.csproj \
    --filter "FullyQualifiedName~IntegrationTests"
```

`REEL_SKIP_LOCAL_FLAKY=1` skips whatever is marked `[LocalFlakyFact]` as a known local-only
failure. **Nothing is marked today** — the quarantine emptied on 2026-09-10 and the whole suite
is green on the dev Mac, so the variable is no longer part of the recommended command. Every row
that ever sat in that quarantine turned out to be a real defect (a stale fixture, or an assertion
left behind by a deliberate contract change), never a machine difference. **The variable is set
nowhere in CI and must never be** — CI's `pgvector/pgvector:pg16` service is authoritative.

Integration tests skip silently when `REEL_TEST_CONNECTION_STRING` is absent, so a plain local
`dotnet test` stays green without a database. CI sets `REEL_REQUIRE_INTEGRATION=1`, which turns
that silence into a hard failure — a suite that reports green having run nothing is worse than a
red one.

## Deployment

```bash
make docker-ready        # build both images + boot the stack + health-probe it (the CI gate)
make release-validation  # lint + build + test + e2e + docker-ready
```

Pushes to `main` build multi-arch images on native amd64/arm64 runners, publish `labs.fa.reel-backend` / `labs.fa.reel-frontend` to Docker Hub, and redeploy the Portainer stack behind `reel.frostaura.net`. Required secrets, the `DEPLOY_ENABLED` gate, and Portainer stack setup are documented in [`docs/deployment.md`](docs/deployment.md).

## Data sources & attribution

- **Trakt** — watch history, ratings, in-progress shows, write-back (ratings, watchlist, the managed "Reel — Up Next" list).
- **TMDB** — catalog metadata, search, images, trailers, watch providers. *This product uses the TMDB API but is not endorsed or certified by TMDB.* Watch-provider data on TMDB is powered by JustWatch.

## License

[MIT](LICENSE) — © FrostAura. Open by default, per FrostAura engineering doctrine.
