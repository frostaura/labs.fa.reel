# Deployment

Reel follows the standard FrostAura deploy pattern: **GitHub Actions → Docker Hub (multi-arch) → Portainer → Cloudflared**, same shape as `fa.lifeos`.

## Pipeline

Every push to `main`, every pull request and every manual dispatch runs `.github/workflows/ci.yml`. There is no scheduled run: FrostAura runs no cron CI anywhere, so a newly published advisory surfaces on the next push or on a manual dispatch. Jobs gate in this order:

| Stage | Jobs | Runs on |
| --- | --- | --- |
| Quality | `Backend`, `OpenAPI contract`, `Frontend`, `Frontend E2E`, `Backend Integration (Postgres + pgvector)`, `Security Scan` | PRs + `main` + dispatch |
| Deployability | `Docker Build & Stack Smoke` | PRs + `main` + dispatch |
| Publish | `Build {Backend,Frontend} ({amd64,arm64})` → `Create … Multi-arch Manifest` | `main` only |
| Deploy | `Re-pull image and redeploy on Portainer` | `main` only, gated (see below) |

Every quality and deployability step is a `make ci-*` target; the workflow holds no gate logic of its own. See [Local CI](#local-ci).

The publish jobs build per-architecture on native runners (`ubuntu-latest` for amd64, `ubuntu-24.04-arm` for arm64) and then join them into a single multi-arch manifest — no QEMU emulation. Published tags:

- `${DOCKERHUB_USERNAME}/reel-backend:latest` (+ `:latest-amd64`, `:latest-arm64`)
- `${DOCKERHUB_USERNAME}/reel-frontend:latest` (+ `:latest-amd64`, `:latest-arm64`)

### The stack smoke gate

`Docker Build & Stack Smoke` runs `make docker-ready` on pull requests as well as `main`. It builds both images from the repo-root context exactly as the publish jobs do, brings the full compose stack up, and probes through the gateway:

- `/health` — backend reachable via nginx
- `/health/ready` — Postgres reachable **and** every EF migration applied against real pgvector
- `/` — the built SPA bundle is served

It also runs `make ci-compose`, which parses all three descriptors with only their required variables supplied, because the prod and Portainer files are the literal content behind a stack and nothing else parses them. The smoke runs as compose project `reel-ci` on ports 18090/15433, so it never collides with a stack already running on the same machine.

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
5. Point Cloudflared at `reel.frostaura.net → http://gateway:80` (the stack's `GATEWAY_PORT`, default `8090`, on the host). See [Network exposure](#network-exposure) before choosing which.
6. Name the backup destination: set `BACKUP_HOST_DIR` to an absolute host path, ideally a mount of off-host storage. Until it is set, dumps go to the stack's `reel_backups` volume. See [Backups and restore](#backups-and-restore).
7. Set `DEPLOY_ENABLED=true`.

The deploy job re-submits the stack's *existing* file content and env with `pullImage=true`, so redeploys never overwrite what an operator configured in Portainer — it only forces a fresh pull of `:latest`. It sleeps 30s first to let Docker Hub propagate the manifests.

## Network exposure

Every descriptor publishes its ports on `127.0.0.1` unless a bind variable opts in:

| Variable | Default | Port |
| --- | --- | --- |
| `GATEWAY_BIND` | `127.0.0.1` | gateway, `GATEWAY_PORT` (8090; 5173 in the founder's `.env`) |
| `POSTGRES_BIND` | `127.0.0.1` | dev Postgres, `POSTGRES_HOST_PORT` (5433). Not published at all in prod. |

Why: on 2026-10-22 the dev stack published its gateway on `0.0.0.0` with the host firewall off, and an unauthenticated request from another LAN host was answered by `/api/dev/residual-replay`, while `POST /api/auth/dev/link` would have minted a Founder session (J-1). Postgres has its own variable so that opening the gateway for phone QA never opens the database with it.

- **Phone QA on the LAN:** set `GATEWAY_BIND=0.0.0.0`, recreate the gateway, and unset it again afterwards.
- **Cloudflared** reaches the gateway at `http://gateway:80` inside the stack network, or through this host's loopback. Set `GATEWAY_BIND` only if the tunnel runs somewhere that needs another interface.

The QA helpers are also locked in code, independently of the bind address:

- `POST /api/auth/dev/link` answers only a caller on this machine whose request no proxy forwarded. Behind the gateway every request is forwarded, so the helper is reachable only from a host-side `dotnet run`.
- Every `/api/dev/*` route requires a signed-in Founder on this machine. An anonymous caller gets 401, any other tier 403, and a Founder session used from elsewhere gets 403 `loopback_only`.
- `{"noCascade":true}` on dev/link issues a session for the already-linked dev account and nothing else: no Trakt call, no tier write and no sync job.
- dev/link sessions are stored with the user agent `reel-dev-link …`, so QA sessions can be excluded by identity (FD-18).

## Configuration reaches the container

Compose passes the backend an explicit `environment:` list. A key missing from that list never reaches the container. The process then runs the code default without failing or logging anything. That is how the live stack ran the cross-reference ceiling at 80 calls a day while `.env` said 40 (K-5). The rules:

- **Every key the code reads is in all four descriptors** (dev, prod, Portainer and `docker-compose.eval.yml`) and in `.env.example`. `ComposeEnvParityTests` builds the key list by scanning the production sources, so a new key fails the build until it is plumbed. The same test fails on:
  - a key nothing reads;
  - a QA key (`QA_HELPERS_ENABLED`, `TRAKT_ACCESS_TOKEN`, `TRAKT_REFRESH_TOKEN`, `TRAKT_DEV_PROFILE`, `TestHelpers__ModelStubMode`) in a production descriptor;
  - a blank default on a key read with `GetValue<bool|int>`, which throws on an empty string instead of falling back.
- **Blank means "code default"** for keys read through `FeatureFlags`, such as the LLM ceilings and the model switches. The values the process actually resolved are printed in one startup line:

  ```bash
  docker logs <backend> 2>&1 | grep 'Effective LLM ceilings'
  ```

- **New typed settings** derive from `ValidatedOptions` and are registered with `AddValidatedOptions<T>()`. An absent key takes its default. A present but invalid value stops the host at startup, with the key named and the value never echoed.
- To check a running container, list key names only and never values: `docker exec <backend> printenv | cut -d= -f1 | sort`.

## Logs

Every service logs through Docker's `local` driver: 10 files of 50 MB each per container, from the shared `x-logging` anchor in each descriptor. The json-file default kept about 30 minutes of the backend's log on the live host (J*2). `ComposeHardeningTests` pins the anchor and its use on every service.

## Backups and restore

**What runs.** A `backup` service in every deployable descriptor:

- **Image.** `pgvector/pgvector:pg16`, the same image as the database, so `pg_dump` always matches the server's major version.
- **Script.** `deploy/backup/reel-backup.sh`. The dev stack mounts it. The prod and Portainer files carry identical inline copies, pinned by `BackupDescriptorParityTests`.
- **Nightly dump.** `pg_dump -Fc` at `BACKUP_HOUR_UTC` (default 02:00 UTC) into `daily/`. A host that was asleep at that hour dumps when it wakes, and a stack with no dump yet takes one at once.
- **Retention.** 14 daily and 8 weekly dumps. The weekly copy is a hard link taken on Sundays, or whenever the newest weekly copy is more than 6 days old.
- **Verification.** Each dump is written as `.partial`, checked with `pg_restore --list`, and only then renamed. The result goes to `last-success` or `last-failure` in the backup directory.
- **Health.** The container turns unhealthy when the newest successful dump is more than 26 hours old.
- **Destination.** `BACKUP_HOST_DIR`: `./.backups` beside the dev compose file (outside Docker's VM, and outside `docker compose down -v`), or the `reel_backups` volume in prod and Portainer until an absolute host path is named. Copying dumps off-host waits on FD-12.

**The dump before a migration.** Before `DatabaseInitializer` applies pending migrations, the API writes `<id>.request` to the shared `backup_requests` volume and waits for the backup service's answer:

- `<id>.done` names the dump, and migration proceeds.
- `<id>.failed`, or no answer within `BACKUP_PRE_MIGRATION_TIMEOUT_SECONDS` (default 1800), makes the API refuse to migrate and exit, so the container restarts and asks again. `BACKUP_PRE_MIGRATION_REQUIRED=false` downgrades this to a warning.
- Pre-migration dumps are kept in `pre-migration/` (the last 10).
- The 30-minute default covers the worst case: a request that arrives while the sidecar is mid-way through a nightly or first-start dump waits for that dump and then gets a fresh one of its own. The 930 MB live database is estimated at about 6 minutes per dump, from the drill's 359 MB in 157 s.

A Portainer redeploy recreates only services whose image changed, so a dump taken when the sidecar started would miss exactly the redeploy that carries a migration. Only the process about to migrate knows that it is about to.

**Take a dump now:**

```bash
docker compose exec backup /bin/sh /usr/local/bin/reel-backup.sh once
```

**Restore runbook.** Restore into a new database, check it, then repoint. Never restore over the live database.

1. Pick the dump: `ls -l <BACKUP_HOST_DIR>/{daily,weekly,pre-migration}`. For a migration rollback, take the newest `pre-migration/` dump older than the deploy.
2. Stop the writers: `docker compose stop backend`.
3. Create the target database and restore into it:

   ```bash
   docker compose exec postgres createdb -U reel reel_restore
   docker compose exec backup pg_restore -d reel_restore --no-owner --exit-on-error /backups/daily/reel-YYYY-MM-DD.dump
   ```

4. Check row counts against the dump's source, or against the last known numbers. Check at least `UserRatings`, `ExposureEvents` and `TraktConnections`; `"__EFMigrationsHistory"` must match the image you will run.
5. Repoint by renaming: `ALTER DATABASE reel RENAME TO reel_before_restore; ALTER DATABASE reel_restore RENAME TO reel;` (run from the `postgres` database, with no connections to either).
6. Start the backend: `docker compose start backend`. Then `curl -fsS localhost:${GATEWAY_PORT}/health/ready` must return `{"status":"ready"}`.
7. Keep `reel_before_restore` until the founder has used the restored stack, then drop it.

The Data Protection keys live in the same database, so a restored database still decrypts its own Trakt tokens. A restore onto a different database does not.

**Drill record.** Run 2026-10-24 on the isolated eval Postgres (:5434), using the prod descriptor's inline copy of the script against `reel_ws8`, a sanitised copy of the founder's data:

| Step | Result |
| --- | --- |
| Dump | 358,923,660 bytes in 157 s |
| Restore | 8 min 31 s, mostly HNSW index builds |
| Row counts | `UserRatings` 3,850 = 3,850 · `ExposureEvents` 17,729 = 17,729 · `TraktConnections` 1 = 1 |
| All 46 tables | Identical counts |
| Indexes | 121 = 121 |
| API boot on the restored database | `/health/ready` 200 in about 8 s |
| Pre-migration handshake, fresh database | 25 pending migrations: dump requested, answered and written, then migrated, `/health/ready` 200 |
| Pre-migration handshake, backup service stopped | The API refused the 25 migrations after its 30 s timeout and exited 1. Nothing was migrated, and the request was withdrawn. |

The drill passed. Together with the LAN lockdown, it is a prerequisite for the first live rebuild (G0). Repeat it on the live host before that rebuild.

## Local CI

`make ci` runs every CI gate with the same commands the workflow runs. `make -k ci` reports every red gate rather than stopping at the first. On this Mac, pass `DOTNET=~/.dotnet/dotnet`.

| Target | Gate |
| --- | --- |
| `ci-format` | `dotnet format --verify-no-changes` |
| `ci-build` | `--no-incremental` build with warnings as errors |
| `ci-test` | Unit tests and the backend coverage ratchet |
| `ci-integration` | Integration suite. Needs `REEL_TEST_CONNECTION_STRING` pointing at a throwaway database, and fails if no test ran. |
| `ci-replay` | WS-7's Ask Reel replay suite (`[Trait("Suite", "Replay")]`). Fails if no test ran. |
| `ci-openapi` | Regenerates the OpenAPI document and TS types into `.tmp/ci` and diffs them against the committed ones |
| `ci-audit` | npm audit gate and vulnerable NuGet packages |
| `ci-frontend-lint` / `-typecheck` / `-test` | ESLint (`--max-warnings 0`), `tsc`, and vitest with the coverage ratchet |
| `ci-frontend-build` | `vite build` that also fails on any printed warning, because Vite warns and still exits 0 |
| `ci-knip` | Unused files, exports and dependencies |
| `ci-e2e` | Playwright, Chromium and WebKit projects |
| `ci-trivy` | `trivy fs`: HIGH and CRITICAL vulnerable dependencies and committed secrets |
| `ci-compose` / `ci-docker` | Descriptors parse, then the image build and stack smoke as project `reel-ci` |

A missing prerequisite fails its gate with the reason. None of them skip.

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
- Local compose publishes Postgres on **5433**, on loopback, because 5432 is usually already taken on a dev machine. In-stack traffic still uses `postgres:5432`.
