.PHONY: up down build test lint lint-backend lint-frontend e2e docker-build docker-ready docker-down release-validation qa

# Compose needs these two before it will interpolate (both are `:?` required in the compose
# files). Real values live in .env locally / Portainer in prod; these defaults exist so
# `make docker-ready` runs unattended in CI without a secret. Never use them for anything real.
POSTGRES_PASSWORD ?= reel_local_pw
JWT_SECRET ?= reel_local_jwt_secret_at_least_32_chars
GATEWAY_PORT ?= 8090
COMPOSE_ENV = POSTGRES_PASSWORD=$(POSTGRES_PASSWORD) JWT_SECRET=$(JWT_SECRET) GATEWAY_PORT=$(GATEWAY_PORT)

up:
	$(COMPOSE_ENV) docker compose up -d --build

# COMPOSE_ENV is needed even to tear down: `docker compose down` still interpolates the compose
# file, and POSTGRES_PASSWORD/JWT_SECRET are `:?` required — a bare `down` errors out.
down:
	$(COMPOSE_ENV) docker compose down

lint: lint-backend lint-frontend

lint-backend:
	dotnet format labs.fa.reel.slnx --verify-no-changes --verbosity minimal

lint-frontend:
	cd src/frontend && npm run lint

build:
	dotnet build labs.fa.reel.slnx --nologo

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
