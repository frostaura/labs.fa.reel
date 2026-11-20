# Deployment

Reel follows the standard FrostAura deploy pattern: **GitHub Actions → Docker Hub (multi-arch) → Portainer → Cloudflared**.

## Pipeline

Every push to `main` runs `.github/workflows/ci.yml`. Jobs gate in this order:

| Stage | Jobs | Runs on |
| --- | --- | --- |
| Quality | `Backend`, `Frontend`, `Frontend E2E`, `Backend Integration (Postgres + pgvector)`, `Security Scan` | PRs + `main` + weekly |
| Deployability | `Docker Build & Stack Smoke` | PRs + `main` + weekly |
| Publish | `Build {Backend,Frontend} ({amd64,arm64})` → `Create … Multi-arch Manifest` | `main` only |
| Deploy | `Re-pull image and redeploy on Portainer` | `main` only, gated (see below) |

### The weekly rot check

CI also runs on a schedule (Mondays 06:00 UTC). This is not redundant with the PR runs: the quality gates fail on *newly published* advisories, independent of whether anyone touched the code. NuGet's NU1903 audit breaks `dotnet restore` outright under `TreatWarningsAsErrors`, and `npm audit --audit-level=high` fails the security job. A repo sitting idle therefore stops being buildable without anyone finding out.

That is not hypothetical — it is what happened between 2026-06-18 and 2026-08-01. Two unrelated advisory sets landed (`System.Security.Cryptography.Xml`, `Microsoft.OpenApi`, and four npm packages) and nothing surfaced them until the next push, at which point a fresh `:latest` could not have been produced. The weekly run bounds that blind spot to seven days.

Publish jobs are push-only, so a scheduled run verifies without ever deploying.

The publish jobs build per-architecture on native runners (`ubuntu-latest` for amd64, `ubuntu-24.04-arm` for arm64) and then join them into a single multi-arch manifest — no QEMU emulation. Published tags:

- `${DOCKERHUB_USERNAME}/labs.fa.reel-backend:latest` (+ `:latest-amd64`, `:latest-arm64`)
- `${DOCKERHUB_USERNAME}/labs.fa.reel-frontend:latest` (+ `:latest-amd64`, `:latest-arm64`)

### The stack smoke gate

`Docker Build & Stack Smoke` runs `make docker-ready` on pull requests as well as `main`. It builds both images from the repo-root context exactly as the publish jobs do, brings the full compose stack up, and probes through the gateway:

- `/health` — backend reachable via nginx
- `/health/ready` — Postgres reachable **and** every EF migration applied against real pgvector
- `/` — the built SPA bundle is served

It also runs `docker compose -f docker-compose.prod.yml config -q`, because that file is the literal content behind the Portainer stack and nothing else in CI parses it.

This gate exists because the publish jobs are push-to-main only: without it, a broken Dockerfile or gateway route passed CI on the PR and was only discovered after a bad `:latest` had already shipped.

## Required GitHub configuration

Repository → Settings → Secrets and variables → Actions.

### Secrets

| Secret | Used by | Notes |
| --- | --- | --- |
| `DOCKERHUB_USERNAME` | publish jobs | Also the image namespace. |
| `DOCKERHUB_TOKEN` | publish jobs | Docker Hub access token with Read/Write. |
| `PORTAINER_URL` | deploy job | e.g. `https://portainer.example.net` |
| `PORTAINER_TOKEN` | deploy job | Portainer API key (`X-API-Key`). |
| `PORTAINER_ENDPOINT_ID` | deploy job | Numeric environment id. |
| `PORTAINER_STACK_ID` | deploy job | Numeric stack id for `reel.frostaura.net`. |

### Variables

| Variable | Value | Effect |
| --- | --- | --- |
| `DEPLOY_ENABLED` | `true` | Enables the Portainer deploy job. |

`DEPLOY_ENABLED` is deliberately a **variable**, not a secret, so the deploy job is *skipped* (not failed) while the stack doesn't exist — CI stays green through the build-out. Set it to `true` only once the Portainer stack and all four `PORTAINER_*` secrets are in place; the job hard-fails on a missing secret by design rather than silently deploying nothing.

## Standing up the Portainer stack

1. Create a new stack in Portainer named `reel`.
2. Paste the contents of `docker-compose.prod.yml` as the stack file. It pulls published images rather than building, and ships the gateway nginx config inline via a compose `config` — Portainer stacks cannot mount repo files.
3. Populate the stack environment variables. Required (the compose file refuses to start without them): `POSTGRES_PASSWORD`, `JWT_SECRET`, `TRAKT_CLIENT_ID`, `TRAKT_CLIENT_SECRET`, `TMDB_READ_ACCESS_TOKEN`. Everything else has a production-shaped default — see `.env.example` for the full surface.
4. Note the stack id from the Portainer URL and set it as `PORTAINER_STACK_ID`.
5. Point Cloudflared at `reel.frostaura.net → http://gateway:80` (the stack's `GATEWAY_PORT`, default `8090`, on the host).
6. Set `DEPLOY_ENABLED=true`.

The deploy job re-submits the stack's *existing* file content and env with `pullImage=true`, so redeploys never overwrite what an operator configured in Portainer — it only forces a fresh pull of `:latest`. It sleeps 30s first to let Docker Hub propagate the manifests.

## Local parity

The same `make` targets CI uses:

```bash
make docker-build     # build both images from the repo-root context, as CI does
make docker-ready     # build + up + health-probe the whole stack (the deployability gate)
make docker-down      # tear down, including volumes
make release-validation  # lint + build + test + e2e + docker-ready
```

`make docker-ready` supplies throwaway values for the two required compose vars so it runs unattended; override with your real `.env` for anything beyond a boot check.

## Notes

- `.dockerignore` keeps `bin/`, `obj/`, `node_modules/`, and `.env` out of the build context. Both Dockerfiles build from the repo root, so without it a local `dotnet build` would leak stale artifacts into the SDK stage.
- Local compose publishes Postgres on **5433** — 5432 is usually already taken on a dev machine. In-stack traffic still uses `postgres:5432`.
