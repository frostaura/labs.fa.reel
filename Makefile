.PHONY: up down build test lint lint-backend lint-frontend e2e docker-build docker-ready docker-down release-validation qa \
	ci ci-backend ci-frontend ci-format ci-build ci-test ci-integration ci-replay ci-openapi ci-audit \
	ci-frontend-lint ci-frontend-typecheck ci-frontend-test ci-frontend-build ci-knip ci-design-ratchet ci-vite-proxy ci-e2e ci-a11y \
	ci-trivy ci-compose ci-docker

# Compose needs these two before it will interpolate (both are `:?` required in the compose
# files). Real values live in .env locally / Portainer in prod; these defaults exist so
# `make docker-ready` runs unattended in CI without a secret. Never use them for anything real.
POSTGRES_PASSWORD ?= reel_local_pw
JWT_SECRET ?= reel_local_jwt_secret_at_least_32_chars
GATEWAY_PORT ?= 8090
# GIT_SHA is stamped into the backend image so /health reports the commit (A-17).
GIT_SHA ?= $(shell git rev-parse HEAD 2> /dev/null)
COMPOSE_ENV = POSTGRES_PASSWORD=$(POSTGRES_PASSWORD) JWT_SECRET=$(JWT_SECRET) GATEWAY_PORT=$(GATEWAY_PORT) GIT_SHA=$(GIT_SHA)

up:
	$(COMPOSE_ENV) docker compose up -d --build

# COMPOSE_ENV is needed even to tear down: `docker compose down` still interpolates the compose
# file, and POSTGRES_PASSWORD/JWT_SECRET are `:?` required — a bare `down` errors out.
down:
	$(COMPOSE_ENV) docker compose down

lint: lint-backend lint-frontend

lint-backend:
	dotnet format fa.reel.slnx --verify-no-changes --verbosity minimal

lint-frontend:
	cd src/frontend && npm run lint

build:
	dotnet build fa.reel.slnx --nologo

test:
	dotnet test src/backend/FrostAura.Reel.Tests/FrostAura.Reel.Tests.csproj --nologo
	cd src/frontend && npm run test -- --run

e2e:
	cd src/frontend && npm run test:e2e

# Builds both images exactly as CI publishes them (repo root context, per-service Dockerfile).
# Catches a broken Dockerfile before it reaches a push-to-main publish job.
docker-build:
	$(COMPOSE_ENV) docker compose build

# The deployability gate: bring the real stack up and prove it serves.
# Probes go through the gateway, so a pass means backend + postgres/pgvector migrations +
# nginx routing + the frontend bundle are all wired correctly — not just that images built.
docker-ready:
	@$(COMPOSE_ENV) docker compose up -d --build
	@echo "Waiting for the stack to become ready on :$(GATEWAY_PORT)..."
	@for attempt in $$(seq 1 60); do \
		if curl -fsS http://localhost:$(GATEWAY_PORT)/health >/dev/null 2>&1 \
			&& curl -fsS http://localhost:$(GATEWAY_PORT)/health/ready >/dev/null 2>&1 \
			&& curl -fsS http://localhost:$(GATEWAY_PORT)/ >/dev/null 2>&1; then \
			echo "Stack ready: gateway → backend (/health, /health/ready) and frontend all responding."; \
			commit=$$(curl -fsS http://localhost:$(GATEWAY_PORT)/health | sed -E 's/.*"commit":"([^"]*)".*/\1/'); \
			if [ -n "$(GIT_SHA)" ] && [ "$$commit" != "$(GIT_SHA)" ]; then \
				echo "docker-ready failed: /health reports commit '$$commit', not $(GIT_SHA) (A-17)." >&2; \
				exit 1; \
			fi; \
			echo "/health reports commit $$commit."; \
			exit 0; \
		fi; \
		sleep 3; \
	done; \
	echo "docker-ready failed: the stack did not become reachable in time." >&2; \
	$(COMPOSE_ENV) docker compose ps >&2; \
	$(COMPOSE_ENV) docker compose logs --tail 100 >&2; \
	exit 1

docker-down:
	$(COMPOSE_ENV) docker compose down -v --remove-orphans

release-validation: lint build test e2e docker-ready

qa: release-validation

# ══════════════════════════════════════════════════════════════════════════════════════════════
# make ci — every CI gate, run by exactly the commands .github/workflows/ci.yml runs (I-15, I-18).
# ══════════════════════════════════════════════════════════════════════════════════════════════
# ci.yml calls these targets rather than repeating the commands, so a green `make ci` on a laptop
# and a green CI run are the same claim. Nothing here calls a paid provider: the integration and
# replay suites run on ReelApiFactory, which refuses live keys.
#
#   make ci                         all of it, stopping at the first red gate
#   make -k ci                      all of it, reporting every red gate
#   make ci DOTNET=~/.dotnet/dotnet a dotnet that is not on PATH (this Mac's user-local SDK)
#
# Needs: REEL_TEST_CONNECTION_STRING pointing at a THROWAWAY database (the fixture drops it), the
# frontend's node_modules, Playwright's chromium and webkit, and Docker for ci-docker and ci-trivy.
# A missing prerequisite FAILS its gate with the reason — a gate that skips is a gate that lies.

DOTNET ?= dotnet
SLN := fa.reel.slnx
TEST_PROJECT := src/backend/FrostAura.Reel.Tests/FrostAura.Reel.Tests.csproj
CI_OUT ?= $(CURDIR)/.tmp/ci
# Ratchets, not targets (regression guards; raise them as the suites grow).
BACKEND_COVERAGE_MIN ?= 10
FRONTEND_COVERAGE_MIN ?= 6
# The CI image-smoke project: its own name and ports, so it never collides with a stack already
# running on this machine (the live stack publishes 5433).
CI_COMPOSE_ENV = $(COMPOSE_ENV) COMPOSE_PROJECT_NAME=reel-ci GATEWAY_PORT=18090 POSTGRES_HOST_PORT=15433 BACKUP_HOST_DIR=$(CI_OUT)/backups
export MSBuildEnableWorkloadResolver := false

ci: ci-backend ci-frontend ci-trivy ci-compose ci-docker

ci-backend: ci-format ci-build ci-test ci-integration ci-replay ci-openapi ci-audit

ci-frontend: ci-frontend-lint ci-frontend-typecheck ci-frontend-test ci-frontend-build ci-knip ci-design-ratchet ci-vite-proxy ci-e2e

ci-format:
	$(DOTNET) format $(SLN) --verify-no-changes --verbosity minimal

# --no-incremental: an incremental build of an unchanged project does not re-print its warnings,
# and warnings are what this gate exists to catch. TreatWarningsAsErrors is also set in
# Directory.Build.props; it is repeated here so no project can opt itself out.
ci-build:
	$(DOTNET) build $(SLN) --nologo --no-incremental -p:TreatWarningsAsErrors=true -warnaserror

# The backend suite in two halves that are complements by construction: ci-integration takes the
# IntegrationTests namespace plus every Postgres class outside it (deploy/ci/database-test-classes.txt),
# ci-test takes the rest. Tests/Harness/CiTestSelectionTests proves the split against the compiled
# assembly, so a database test can never again pass in the unit half having executed nothing (CR-11).
ci-test:
	rm -rf $(CI_OUT)/backend-coverage
	filter=$$(sh deploy/ci/test-filter.sh unit) || exit 1; \
	$(DOTNET) test $(TEST_PROJECT) --nologo -v q --filter "$$filter" \
		--settings src/backend/coverlet.runsettings --results-directory $(CI_OUT)/backend-coverage
	python3 deploy/ci/coverage-gate.py cobertura $(CI_OUT)/backend-coverage $(BACKEND_COVERAGE_MIN)

ci-integration:
	@test -n "$$REEL_TEST_CONNECTION_STRING" || { echo "ci-integration: set REEL_TEST_CONNECTION_STRING to a THROWAWAY database (the fixture drops it)" >&2; exit 1; }
	filter=$$(sh deploy/ci/test-filter.sh integration) || exit 1; \
	REEL_REQUIRE_INTEGRATION=1 sh deploy/ci/require-tests.sh \
		$(DOTNET) test $(TEST_PROJECT) --nologo -v q --filter "$$filter"

# WS-7's deterministic Ask Reel replay suite: the scorer, compare and guard tests over the
# committed recordings, plus the open scenario set's structural check. Node only — no database,
# no API and zero paid calls (about a second).
ci-replay:
	node --test 'scripts/eval/*.test.mjs'
	node scripts/eval/askreel.mjs check

# The OpenAPI contract: regenerate the document from the build (no running API) into a scratch
# directory and compare it, and the TypeScript generated from it, with what is committed. A diff
# means an endpoint changed without `npm run gen:api` being rerun and committed.
ci-openapi:
	rm -rf $(CI_OUT)/openapi && mkdir -p $(CI_OUT)/openapi
	$(DOTNET) build src/backend/FrostAura.Reel.Api/FrostAura.Reel.Api.csproj --nologo -v q --no-incremental \
		-p:OpenApiGenerateDocuments=true -p:OpenApiDocumentsDirectory=$(CI_OUT)/openapi
	diff -u src/frontend/openapi/reel-api.json $(CI_OUT)/openapi/reel-api.json
	cd src/frontend && npx --no-install openapi-typescript $(CI_OUT)/openapi/reel-api.json -o $(CI_OUT)/openapi/openapi.ts
	diff -u src/frontend/src/store/generated/openapi.ts $(CI_OUT)/openapi/openapi.ts

ci-audit:
	cd src/frontend && npm run audit:gate
	$(DOTNET) list $(SLN) package --vulnerable --include-transitive --format json | python3 deploy/ci/nuget-vulnerable-gate.py

ci-frontend-lint:
	cd src/frontend && npm run lint

ci-frontend-typecheck:
	cd src/frontend && npm run typecheck

ci-frontend-test:
	cd src/frontend && npm run test -- --run --coverage
	python3 deploy/ci/coverage-gate.py istanbul src/frontend/coverage/coverage-summary.json $(FRONTEND_COVERAGE_MIN)

# Vite and its plugins print warnings and still exit 0; this gate fails on the printed warning.
ci-frontend-build:
	cd src/frontend && sh ../../deploy/ci/no-build-warnings.sh npm run build

# Unused files, exports and dependencies (WS-9 owns the knip config and dependency).
ci-knip:
	cd src/frontend && npx --no-install knip

# WS-9's design ratchet (WS-9 CR-4): raw colours, arbitrary values and the other counted debts may
# only fall. A count above scripts/design-ratchet-baseline.json is a failure; lowering the baseline is the owner's
# last step after paying a debt down (`npm run design:baseline`), never a way to pass this gate.
ci-design-ratchet:
	cd src/frontend && npm run design:ratchet

# The dev proxy must not make a LAN caller look like this machine (J-1; WS-8 review 2026-10-24): the
# dev server listens on loopback unless VITE_HOST opts in, and every proxy entry sets xfwd so the API's
# loopback guard sees the real client. Starts the repo's own Vite config against a local echo server.
ci-vite-proxy:
	cd src/frontend && node ../../deploy/ci/vite-proxy-probe.mjs

# The dev-server specs in both engines (Safari is where the founder's installed PWA runs; WebKit runs in
# batches of at most 40 tests per browser, because one WebKit browser hangs on its 64th page), with service
# workers blocked by the Playwright config; then the *.preview.spec.ts specs against the production
# build (Chromium), which test the real service worker (WS-9 CR-3). PW_PORT keeps parallel runs apart.
ci-e2e:
	cd src/frontend && CI=1 npm run test:e2e
	cd src/frontend && CI=1 npx --no-install playwright test -c playwright.preview.config.ts

# The accessibility spec alone, in both engines (WS-9 CR-4): the full axe rule set per route against
# tests-e2e/a11y-baseline.json, keyboard paths and reduced motion. `ci-e2e` already runs it with
# everything else, so `ci` does not list it twice; this target is the quick local check after a
# surface change.
ci-a11y:
	cd src/frontend && CI=1 npx --no-install playwright test tests-e2e/a11y.spec.ts --project=chromium --project=webkit

# Known-vulnerable dependencies and committed secrets across the whole tree. HIGH and CRITICAL with
# a fix available fail the gate. (Misconfiguration scanning joins once the images run non-root.)
#
# One pinned Trivy, here and in ci.yml alike (R1-33): the release's own image by its multi-arch
# digest, so a re-pointed tag cannot change what runs, and the GitHub runner and a laptop run the
# same binary. A `trivy` already on PATH is used only when it is exactly this version — a different
# Trivy reads a different rule set and makes `make ci` and CI two different claims. With neither, the
# gate fails and says how to get one: a gate that skips is a gate that lies.
TRIVY_VERSION := 0.58.1
TRIVY_IMAGE := aquasec/trivy:$(TRIVY_VERSION)@sha256:ab70a02200597efa04748f210f793936eb647cbcdb0ea69cc30b226d6f5a22c7
TRIVY_CACHE ?= $(HOME)/.cache/trivy
TRIVY_ARGS := fs --quiet --scanners vuln,secret --severity HIGH,CRITICAL --ignore-unfixed --exit-code 1 \
	--skip-dirs src/frontend/node_modules --skip-dirs .tmp --skip-dirs .backups .
ci-trivy:
	@if command -v trivy > /dev/null && trivy --version 2> /dev/null | grep -qx 'Version: $(TRIVY_VERSION)'; then \
		echo "ci-trivy: trivy $(TRIVY_VERSION) from PATH"; \
		trivy $(TRIVY_ARGS); \
	elif docker info > /dev/null 2>&1; then \
		echo "ci-trivy: $(TRIVY_IMAGE)"; \
		mkdir -p "$(TRIVY_CACHE)" && \
		docker run --rm -v "$(CURDIR):/src:ro" -w /src -v "$(TRIVY_CACHE):/root/.cache/trivy" $(TRIVY_IMAGE) $(TRIVY_ARGS); \
	else \
		echo "ci-trivy: needs Docker running (the gate runs $(TRIVY_IMAGE)) or trivy $(TRIVY_VERSION) on PATH." >&2; \
		echo "ci-trivy: start Docker Desktop, or install exactly v$(TRIVY_VERSION) from https://github.com/aquasecurity/trivy/releases/tag/v$(TRIVY_VERSION)" >&2; \
		exit 1; \
	fi

# Every descriptor parses with only its required variables supplied, and not without them.
ci-compose:
	$(COMPOSE_ENV) docker compose -f docker-compose.yml config -q
	$(COMPOSE_ENV) TRAKT_CLIENT_ID=ci TRAKT_CLIENT_SECRET=ci TMDB_READ_ACCESS_TOKEN=ci BACKUP_HOST_DIR=/var/lib/reel-ci/backups docker compose -f docker-compose.prod.yml config -q
	@# And the production file refuses to start with nowhere for the dumps but a volume of the stack.
	@! env -u BACKUP_HOST_DIR $(COMPOSE_ENV) TRAKT_CLIENT_ID=ci TRAKT_CLIENT_SECRET=ci TMDB_READ_ACCESS_TOKEN=ci docker compose -f docker-compose.prod.yml config -q 2> /dev/null \
		|| { echo "ci-compose: docker-compose.prod.yml parsed without BACKUP_HOST_DIR" >&2; exit 1; }

# Build both images and prove the composed stack serves, under its own project name and ports.
ci-docker:
	$(MAKE) docker-ready COMPOSE_ENV="$(CI_COMPOSE_ENV)" GATEWAY_PORT=18090; status=$$?; \
		$(MAKE) docker-down COMPOSE_ENV="$(CI_COMPOSE_ENV)"; exit $$status
