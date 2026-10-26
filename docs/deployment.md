# Deployment

Reel follows the standard FrostAura deploy pattern: **GitHub Actions → Docker Hub (multi-arch) → Portainer → Cloudflared**, same shape as `fa.lifeos`.

## Pipeline

Every push to `main`, every pull request and every manual dispatch runs `.github/workflows/ci.yml`. There is no scheduled run: FrostAura runs no cron CI anywhere, so a newly published advisory surfaces on the next push or on a manual dispatch. Jobs gate in this order:

| Stage | Jobs | Runs on |
| --- | --- | --- |
| Quality | `Backend`, `OpenAPI contract`, `Frontend`, `Frontend E2E`, `Backend Integration (Postgres + pgvector)`, `Security Scan` | PRs + `main` + dispatch |
| Deployability | `Docker Build & Stack Smoke` | PRs + `main` + dispatch |
| Publish | `Build {Backend,Frontend} ({amd64,arm64})` → `Create … Multi-arch Manifest` | `main` only |
| Deploy | `Pin, redeploy on Portainer and verify /health` | `main` only, gated (see below) |

Every quality and deployability step is a `make ci-*` target; the workflow holds no gate logic of its own. See [Local CI](#local-ci).

The publish jobs build per-architecture on native runners (`ubuntu-latest` for amd64, `ubuntu-24.04-arm` for arm64) and then join them into a single multi-arch manifest — no QEMU emulation. Published tags:

- `${DOCKERHUB_USERNAME}/reel-backend:sha-<short>` and `:latest` — one manifest, two names (+ `:sha-<short>-amd64`, `:sha-<short>-arm64`, `:latest-amd64`, `:latest-arm64`)
- `${DOCKERHUB_USERNAME}/reel-frontend:sha-<short>` and `:latest`, likewise

`sha-<short>` is the first seven characters of the commit. It is what a deploy pins and what a rollback names; `latest` only serves a stack that pins nothing. Both Dockerfiles run their build stage on the build machine's own architecture (`--platform=$BUILDPLATFORM`) and the backend cross-publishes `linux-$TARGETARCH`, with restore in its own cached layer. The backend image runs as the unprivileged `app` user (`USER $APP_UID`) and stamps the commit into the assembly from the `GIT_SHA` build argument, so `/health` answers `{"status":"ok","commit":"<sha>"}`.

### The stack smoke gate

`Docker Build & Stack Smoke` runs `make docker-ready` on pull requests as well as `main`. It builds both images from the repo-root context exactly as the publish jobs do, brings the full compose stack up, and probes through the gateway:

- `/health` — backend reachable via nginx, and reporting the commit `make` passed as `GIT_SHA`
- `/health/ready` — Postgres reachable **and** every EF migration applied against real pgvector
- `/` — the built SPA bundle is served

It also runs `make ci-compose`, which parses both descriptors with only their required variables supplied, because the prod file is the literal content behind a stack and nothing else parses it. The smoke runs as compose project `reel-ci` on ports 18090/15433, so it never collides with a stack already running on the same machine.

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
| `DEPLOY_HEALTH_URL` | optional | What the deploy job polls; default `https://reel.frostaura.net/health`. |

`DEPLOY_ENABLED` is deliberately a **variable**, not a secret, so the deploy job is *skipped* (not failed) while the stack doesn't exist — CI stays green through the build-out. Set it to `true` only once the Portainer stack and all four `PORTAINER_*` secrets are in place; the job hard-fails on a missing secret by design rather than silently deploying nothing.

## Standing up the Portainer stack

1. Create a new stack in Portainer named `reel`.
2. Paste the contents of `docker-compose.prod.yml` as the stack file — the only production descriptor (the former `deploy/portainer-stack.yml` copy drifted from it and was removed, K-14). It pulls published images rather than building, and ships the gateway nginx config and the backup script inline as compose `configs` — Portainer stacks cannot mount repo files. **An existing stack must be re-pasted once** after this change: the deploy job refuses a stack file that does not read `REEL_TAG`.
3. Populate the stack environment variables. Required (the compose file refuses to start without them): `POSTGRES_PASSWORD`, `JWT_SECRET`, `TRAKT_CLIENT_ID`, `TRAKT_CLIENT_SECRET`, `TMDB_READ_ACCESS_TOKEN`. Everything else has a production-shaped default — see `.env.example` for the full surface.
4. Note the stack id from the Portainer URL and set it as `PORTAINER_STACK_ID`.
5. Point Cloudflared at the gateway. A `cloudflared` running on the Docker host reaches it at `http://127.0.0.1:${GATEWAY_PORT}` (default `8090`). See [Network exposure](#network-exposure) for any other placement.
6. Name the backup destination: `BACKUP_HOST_DIR` is **required**, an absolute host path, ideally a mount of off-host storage. The stack refuses to start without it. See [Backups and restore](#backups-and-restore).
7. Set `DEPLOY_ENABLED=true`.

## Deploys and rollback

The deploy job re-submits the stack's *existing* file content and env with `pullImage=true`, so a redeploy never overwrites what an operator configured in Portainer — with one change: it sets the stack variable `REEL_TAG` to `sha-<short>` of the commit it just published. The prod file pulls `reel-backend:${REEL_TAG:-latest}` and `reel-frontend:${REEL_TAG:-latest}`, so the stack always names the exact images it runs (K-9).

It then polls `DEPLOY_HEALTH_URL` every 20 s until `/health` reports the commit it shipped, and **fails the job** if that has not happened within 45 minutes (J-22). Portainer accepting a redeploy is not the stack serving it: a backend that crash-loops on a migration, or a gateway still answering from the old container, both look like success to the PUT. The window covers the pre-migration dump and the migrations; the backend healthcheck's `start_period` is 40 minutes for the same reason.

**Rollback.** In Portainer, set the stack's `REEL_TAG` to the previous `sha-<short>` (Docker Hub lists them) and update the stack with "Re-pull image" on. Every migration in trust-v1 is additive, so an older image runs against a newer schema; rolling back *past* a migration's data change, or to the pre-program image, means restoring the pre-migration dump instead ([Backups and restore](#backups-and-restore)). A failed Portainer update keeps the previous stack file and env: read the stored stack file and its `UpdateDate` before assuming an edit took.

**The trust-v1 PR.** Nothing in this program pushes. The branch `build/trust-v1` carries everything from `7f39d1e` onward; the founder pushes it (its old upstream no longer exists, so the push sets a new one) and opens the PR into `main`, which runs every gate above before the publish and deploy jobs can.

## Network exposure

Every descriptor publishes its ports on `127.0.0.1` unless a bind variable opts in:

| Variable | Default | Port |
| --- | --- | --- |
| `GATEWAY_BIND` | `127.0.0.1` | gateway, `GATEWAY_PORT` (8090; 5173 in the founder's `.env`) |
| `POSTGRES_BIND` | `127.0.0.1` | dev Postgres, `POSTGRES_HOST_PORT` (5433). Not published at all in prod. |

Why: on 2026-10-22 the dev stack published its gateway on `0.0.0.0` with the host firewall off, and an unauthenticated request from another LAN host was answered by `/api/dev/residual-replay`, while `POST /api/auth/dev/link` would have minted a Founder session (J-1). Postgres has its own variable so that opening the gateway for phone QA never opens the database with it.

- **Phone QA on the LAN:** set `GATEWAY_BIND=0.0.0.0`, recreate the gateway, and unset it again afterwards.
- **Cloudflared.** No descriptor attaches a tunnel container to the stack network, so the gateway is reachable only through its published port:
  - `cloudflared` as a process on the Docker host: target `http://127.0.0.1:${GATEWAY_PORT}`. The loopback default works.
  - `cloudflared` in a container on the same host: it cannot reach the host's 127.0.0.1. Attach it to the stack network and target `http://gateway:80`, or set `GATEWAY_BIND` to the Docker bridge address it uses.
  - `cloudflared` on another machine: set `GATEWAY_BIND` to the interface it connects to, and firewall that port to the tunnel host.
- **Before the first rebuild on these descriptors (G0)**, read the live tunnel's ingress target (Cloudflare Zero Trust → Networks → Tunnels → the tunnel → Public hostnames, or `cloudflared tunnel info`) and match it to the list above. On 2026-10-24 no `cloudflared` process ran on the founder's Mac and no local ingress file existed, so the target could not be confirmed from that machine. If the tunnel reaches the gateway on a LAN address, the loopback default takes `reel.frostaura.net` offline until `GATEWAY_BIND` is set.

The QA helpers are also locked in code, independently of the bind address:

- `POST /api/auth/dev/link` answers only a caller on this machine. The peer must be loopback, and every hop a proxy recorded (`X-Forwarded-For`, `X-Original-For`, `X-Real-IP`, `Forwarded: for=`, `X-Forwarded-Host`) must be loopback too; any `CF-Connecting-IP` is refused. The compose gateway's hop is a Docker bridge address, so the helper is never reachable through it. Through Vite it is reachable from a browser on this machine and from nowhere else.
- That last guarantee depends on Vite recording its client. A proxy that forwards without `X-Forwarded-For` looks exactly like a direct local caller, because its own hop to the API is loopback. On 2026-10-24 Vite's proxy did exactly that and listened on `0.0.0.0`, so a LAN host minted a Founder session through it. `make ci-vite-proxy` fails until the dev server listens on loopback by default (`VITE_HOST` opts in) and every proxy entry sets `xfwd: true`.
- Every `/api/dev/*` route requires a signed-in Founder on this machine. An anonymous caller gets 401, any other tier 403, and a Founder session used from elsewhere gets 403 `loopback_only`.
- `{"noCascade":true}` on dev/link issues a session for the already-linked dev account and nothing else: no Trakt call, no tier write and no sync job.
- dev/link sessions are stored with the user agent `reel-dev-link …`, so QA sessions can be excluded by identity (FD-18).

## Edge security

What the API and the gateway do about who is calling (WS-8 P1). Every setting here is in all descriptors and `.env.example`.

- **Forwarded headers.** cloudflared terminates TLS and sends `X-Forwarded-Proto: https`; the gateway passes the visitor's scheme through (`$http_x_forwarded_proto`, falling back to its own `$scheme`) instead of its own `http`. The API believes `X-Forwarded-For`/`X-Forwarded-Proto` only from `TRUSTED_PROXY_NETWORKS` (default loopback plus `172.16.0.0/12`, Docker's bridge range — the API's port is never published off the host, so nothing else stands there). A host whose Docker address pool differs sets the variable; a malformed entry stops startup.
- **Cookies and HSTS.** Outside Development the session cookies are always `Secure`, whatever the request claims (J-7). HSTS (one year, this host only) is sent by the gateway to HTTPS visitors and by the API outside Development; the gateway hides the API's copy so there is one header.
- **Refresh rotation.** One conditional `UPDATE` revokes the presented refresh session and names its successor; it must affect exactly one row. A concurrent refresh of the same token within 10 s gets an access token on the successor and keeps the winner's refresh cookie; a token revoked by logout is refused (J-10). Three concurrent refreshes therefore make exactly one session.
- **Refresh reuse (J\*4).** A rotated refresh token presented again more than 10 s after its rotation can only be a copy, so the API refuses it and revokes every live session descended from it — the copier's and the owner's — with one warning line naming the account (`Refresh token reuse for account …`). The owner signs in again. The account's sessions on other devices are separate families and survive. A token revoked by logout is simply refused.
- **Ask limits (FD-8).** Per account, at the edge, before a turn is written: `ASK_TURNS_CONCURRENT` (1), `ASK_TURNS_PER_MINUTE` (6), `ASK_TURNS_PER_HOUR` (20) and a rolling 24-hour limit by tier — `ASK_TURNS_PER_DAY` (Paid, 100), `ASK_TURNS_PER_DAY_FOUNDER` (300), `ASK_TURNS_PER_DAY_FREE` (10). A window refusal is `429` with `Retry-After` and a `code` (`ask_rate_minute`, `ask_rate_hour`, `ask_rate_day`); a second turn while one is still streaming is `409 turn_in_progress`, the answer the Ask handler already gives a busy thread and the client already shows as "one moment". The windows live in process memory, so a restart forgets them; the per-purpose LLM ceilings in the ledger remain the durable backstop. Only the eval stack raises them, for its own account.
- **Settings PIN.** Five wrong PINs in 15 minutes lock every PIN check on the account (verify, change and removal) with `429 pin_locked` and `Retry-After`; during the lock even the right PIN is refused unread. The filters gate takes only an unlock token now — the raw `X-Settings-Pin` fallback there was a second, unlimited PIN oracle (J-19).
- **`/api/dev/*`** runs at most two requests at once (`429 dev_busy`).
- **Push.** A subscription endpoint must be HTTPS on port 443 at a known push service (FCM, Mozilla autopush, WNS, Apple); anything else is `400 push_endpoint_not_allowed`, and the sender retires a stored row that fails the same rule rather than POSTing to it (J-9). An account keeps at most 10 subscriptions; a new browser retires the one heard from least recently.
- **Gateway.** `server_tokens off`; `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy` and `Permissions-Policy` on every response; gzip for JSON and static text but never for a stream; `*.map` is 404; `/api/search/ask` has its own location (no gzip, no buffering, an hour to finish). The frontend container serves `/assets/` as `try_files $uri =404` with a year's `immutable` caching, and `index.html`, `sw.js` and the SPA shell as `no-cache`. `deploy/probe-gateway.sh` runs the production copy against stub upstreams. `deploy/probe-ask-stream.sh` runs the same copy in a native nginx (no Docker) against a stub API that streams an Ask turn with the API's 5 s heartbeat, and fails unless every frame reaches the client within 1 s of being written and uncompressed; with the Ask location removed it fails, every frame held back to the end of the turn. The same check through cloudflared needs the live host.

### Founder tier

`FOUNDER_TRAKT_SLUGS` (comma-separated Trakt slugs, case-insensitive) is the only way the Founder tier reaches an account outside Development (K-10, FD-11). A listed profile is raised to Founder at every Trakt sign-in, new account or returning; blank grants nobody, and a value that cannot be a slug stops the host at startup with the key named. The QA sign-in (`/api/auth/dev/link`) still grants Founder to the dev profile, but only in Development, on loopback.

The list raises and never demotes: Founder is also granted by hand during the beta, and a config edit should not take access away silently. To demote, change the row deliberately:

```sql
UPDATE "Accounts" SET "Tier" = 'Free' WHERE "TraktUserSlug" = '<slug>';
```

Seeding a production catalogue (restoring `Titles`, `TitleEmbeddings` and `TitleAttributes` from dev) waits on FD-11; until then a fresh production database fills through the normal catalogue jobs.

### Key-ring certificate

The Data Protection key ring encrypts every stored Trakt token, the VAPID private key and the settings-unlock tokens, and it lives in the `DataProtectionKeys` table. Unencrypted, a database dump (the nightly ones included) is enough to decrypt all of them. Setting `DATAPROTECTION_CERT_PATH` makes every new key encrypted to that certificate (J-12); production logs a warning at startup while it is unset.

1. **Create** a PKCS#12 certificate with a private key, on a trusted machine, never on the Pi's shared storage:
   ```bash
   openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes -subj "/CN=reel-keyring" -keyout keyring.key -out keyring.crt
   openssl pkcs12 -export -inkey keyring.key -in keyring.crt -out reel-keyring.pfx
   ```
   Use a strong export password; it becomes `DATAPROTECTION_CERT_PASSWORD`. Delete `keyring.key` once the `.pfx` exists.
2. **Custody.** Keep two copies of the `.pfx` and its password in the founder's password manager and one offline copy. Never next to `BACKUP_HOST_DIR`: a certificate stored beside the dumps protects nothing.
3. **Install.** Put the file on the host outside any backup path, add a read-only bind to the backend in the stack (`- /etc/reel/keyring/reel-keyring.pfx:/run/reel-keyring/reel-keyring.pfx:ro`), set `DATAPROTECTION_CERT_PATH=/run/reel-keyring/reel-keyring.pfx` and the password, and redeploy. Keys written before this stay readable (they are stored in the clear and are read as they are); only keys created from then on are encrypted, and the unencrypted ones age out with the default 90-day key lifetime.
4. **Rotate.** Add the outgoing file to `DATAPROTECTION_OLD_CERT_PATHS` (comma-separated, same password), point `DATAPROTECTION_CERT_PATH` at the new one, redeploy. Old certificates stay listed until every key they encrypted has expired.
5. **Loss.** Losing the certificate loses everything protected by the keys it encrypted: every stored Trakt token (each account must reconnect Trakt) and the stored VAPID private key. There is no recovery; that is what step 2 is for.

## Configuration reaches the container

Compose passes the backend an explicit `environment:` list. A key missing from that list never reaches the container. The process then runs the code default without failing or logging anything. That is how the live stack ran the cross-reference ceiling at 80 calls a day while `.env` said 40 (K-5). The rules:

- **Every key the code reads is in all three descriptors** (dev, prod and `docker-compose.eval.yml`) and in `.env.example`. `ComposeEnvParityTests` builds the key list by scanning the production sources, so a new key fails the build until it is plumbed. The same test fails on:
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

Every service logs through Docker's `local` driver: 10 files of 100 MB each per container, from the shared `x-logging` anchor in each descriptor. The json-file default kept about 30 minutes of the backend's log on the live host (J*2).

- **Retention.** At least 72 hours per container. The rotation is sized for the 200 KB/min idle-volume ceiling, counted uncompressed: 72 hours needs 844 MB, and 10 x 100 MB holds about 85 hours. The `local` driver also compresses rotated files, so real retention is longer. `ComposeHardeningTests` checks this arithmetic for every deployable descriptor, and checks that every service uses the anchor.
- **Request ids.** Each proxied request gets the gateway's `$request_id`. It appears as `rid=` in the gateway's access log, as `RequestId` in every JSON log line the API writes for that request, and in the `X-Request-Id` response header. A caller that reaches the API directly may send its own `X-Request-Id`, which is used only if it is a plain token of at most 64 characters. To trace one request:

  ```bash
  docker logs <backend> 2>&1 | grep '"RequestId":"<id>"'
  ```

## Backups and restore

**What runs.** A `backup` service in every deployable descriptor:

- **Image.** `pgvector/pgvector:pg16`, the same image as the database, so `pg_dump` always matches the server's major version.
- **Script.** `deploy/backup/reel-backup.sh`. The dev stack mounts it. The prod and Portainer files carry identical inline copies, pinned by `BackupDescriptorParityTests`.
- **Nightly dump.** `pg_dump -Fc` at `BACKUP_HOUR_UTC` (default 02:00 UTC) into `daily/`. A host that was asleep at that hour dumps when it wakes, and a stack with no dump for today takes one once it has been up for `BACKUP_FIRST_DUMP_GRACE_SECONDS` (default 120). During that grace period it serves only pre-migration requests, and a pre-migration dump also becomes today's daily dump, so a fresh stack takes one full dump, not two.
- **Retention.** 14 daily and 8 weekly dumps. The weekly copy is a hard link taken on Sundays, or whenever the newest weekly copy is more than 6 days old.
- **Verification.** Each dump is written as `.partial`, checked with `pg_restore --list`, and only then renamed. The result goes to `last-success` or `last-failure` in the backup directory.
- **Health.** The container turns unhealthy when the newest successful dump is more than 26 hours old.
- **Destination.** `BACKUP_HOST_DIR`:
  - Dev: defaults to `~/.reel/backups`. That is outside Docker's VM, outside `docker compose down -v`, and outside the repository, so dumps of the founder's data never sit in a working tree.
  - Prod and Portainer: required, an absolute host path. It is never a volume of the stack, because `down -v` or removing the stack with its volumes would delete the dumps together with the database. `make ci-compose` fails if either file parses without it.
  - Copying dumps off-host waits on FD-12. Until then they are host-local, which is FD-12's default.

**The dump before a migration.** Before `DatabaseInitializer` applies pending migrations, the API writes `<id>.request` to the shared `backup_requests` volume and waits for the backup service's answer:

- `<id>.done` names the dump, and migration proceeds.
- `<id>.failed`, or no answer within `BACKUP_PRE_MIGRATION_TIMEOUT_SECONDS` (default and maximum 1800), makes the API refuse to migrate and exit, so the container restarts and asks again. `BACKUP_PRE_MIGRATION_REQUIRED=false` downgrades this to a warning.
- Pre-migration dumps are kept in `pre-migration/` (the last 10).
- The 30-minute wait covers the worst case: a request that arrives while the sidecar is mid-way through a nightly dump waits for that dump and then gets a fresh one of its own. The 930 MB live database is estimated at about 6 minutes per dump, from the drill's 359 MB in 157 s.
- **The backend's healthcheck is sized to that wait.** The listener opens only after the dump and the migrations, so the backend's `start_period` is 40 minutes: the 30-minute maximum plus 10 for migrations. The first passing probe ends the period, so an already-migrated database is healthy in seconds. With the old 120 s the backend went unhealthy after about 7 minutes, and `up` never started the gateway. `ComposeHardeningTests` pins `start_period` against the maximum, and the options class refuses a longer timeout.
- **Ownership.** The request directory is mode `0777` with no sticky bit, and each answer is handed to the uid that wrote the request, so an API running as a non-root user can delete it. If an answer still cannot be deleted, the API proceeds on the answer it read, and the sidecar drops uncollected answers after a day.

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
| Sidecar and backend started together, 2026-10-24 (after the review's D2) | The sidecar dumped `reel_ws8` while the API faced an empty database with 25 pending migrations. The request landed 10 s after the sidecar started and was answered after **one** dump (79 s warm, 108 s cold); `/health/ready` 200 at 96 s. That dump is also today's daily and weekly copy (hard links), and no second dump followed. The previous script, same drill: the request waited for the first-start dump (57 s) and then took its own (61 s). |
| Invalid setting on an empty database | `JWT_ACCESS_TOKEN_EXPIRY_MINUTES=2000`: exit 1 naming the key, the value not logged, no table created. |

The drill passed. Together with the LAN lockdown, it is a prerequisite for the first live rebuild (G0). Repeat it on the live host before that rebuild.

## Local CI

`make ci` runs every CI gate with the same commands the workflow runs. `make -k ci` reports every red gate rather than stopping at the first. On this Mac, pass `DOTNET=~/.dotnet/dotnet`.

| Target | Gate |
| --- | --- |
| `ci-format` | `dotnet format --verify-no-changes` |
| `ci-build` | `--no-incremental` build with warnings as errors |
| `ci-test` | Unit tests and the backend coverage ratchet. Excludes every Postgres test (see `ci-integration`). |
| `ci-integration` | Integration suite: the `IntegrationTests` namespace plus the Postgres classes outside it, which are listed in `deploy/ci/database-test-classes.txt`. `deploy/ci/test-filter.sh` builds both filters from that list. `CiTestSelectionTests` fails if the list misses a database class, or if any test class runs in both halves or in neither. Needs `REEL_TEST_CONNECTION_STRING` pointing at a throwaway database, and fails if no test ran. |
| `ci-replay` | WS-7's Ask Reel replay suite (`[Trait("Suite", "Replay")]`). Fails if no test ran. |
| `ci-openapi` | Regenerates the OpenAPI document and TS types into `.tmp/ci` and diffs them against the committed ones |
| `ci-audit` | npm audit gate and vulnerable NuGet packages |
| `ci-frontend-lint` / `-typecheck` / `-test` | ESLint (`--max-warnings 0`), `tsc`, and vitest with the coverage ratchet |
| `ci-frontend-build` | `vite build` that also fails on any printed warning, because Vite warns and still exits 0 |
| `ci-knip` | Unused files, exports and dependencies |
| `ci-vite-proxy` | The dev server listens on loopback unless `VITE_HOST` opts in, and every proxy entry records its client (`xfwd`), so the API's loopback guard can tell a LAN caller from this machine |
| `ci-e2e` | Playwright, Chromium and WebKit projects |
| `ci-trivy` | `trivy fs`: HIGH and CRITICAL vulnerable dependencies and committed secrets. Runs Trivy's image, pinned by digest in the Makefile, on Docker, both on a laptop and in the workflow. A `trivy` on PATH is used only if it is the same version. |
| `ci-compose` / `ci-docker` | Descriptors parse with their required variables and refuse without them (prod without `BACKUP_HOST_DIR`), then the image build and stack smoke as project `reel-ci` |

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
