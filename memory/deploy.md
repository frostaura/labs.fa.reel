---
name: labs-fa-reel-deploy
description: "Deploy chain is built but OFF: CI publishes backend, frontend and (from branch ws/deploy-chain) gateway images; the Portainer stack, its tunnel token and PORTAINER_STACK_ID do not exist yet — creating them is the founder's step."
type: state
last_verified: 2026-10-30
---

# Deploy chain — state

**Production shape (branch `ws/deploy-chain`, cut from `build/trust-v1` at 3a864fdf, committed 2026-10-30, not
merged, not pushed).** Matches technologies.crusaders.squash-control-system: three published images and a Portainer stack with the Cloudflare
tunnel inside it. `docker-compose.prod.yml` runs `labs.fa.reel-backend`, `labs.fa.reel-frontend` and `labs.fa.reel-gateway`, all at
`${REEL_TAG:-latest}`; `cloudflared` (`cloudflare/cloudflared:2026.10.0`, `tunnel --no-autoupdate run`, token from
the required stack key `CLOUDFLARE_TUNNEL_TOKEN`) reaches `http://gateway:80` by service name. **No prod service
publishes a host port.** The bridge is pinned to `10.83.30.0/24` and the API trusts `X-Forwarded-*` from exactly
that /24, walking the chain to the first untrusted address (ForwardLimit lifted) so it sees the visitor, not the
tunnel container. Founder runbook and env key names: `docs/deployment.md#standing-up-the-portainer-stack`.

**Why the gateway became an image.** The deploy job re-submits the stack file Portainer already stores, so the old
inline `gateway_nginx` config could only change by a hand re-paste — a gateway fix would have shipped a new backend
behind the old nginx and reported success (Crusaders hit exactly this, round-3 finding 18). The deploy job now also
refuses a stored stack file that does not run `labs.fa.reel-gateway:${REEL_TAG…}`. The backup script stays inline on
purpose: inline configs work in Portainer and nothing redeploys it differently.

**CI, as inspected 2026-10-30 (not re-run on GitHub from this branch).** On `main`, push-only publish jobs pushed
`labs.fa.reel-backend`/`labs.fa.reel-frontend` multi-arch on run 35436020820. The branch adds `build-gateway-{amd64,arm64}` +
`merge-gateway-manifests` (`${DOCKERHUB_USERNAME}/labs.fa.reel-gateway`, `sha-<short>` + `latest`, same needs and gates), and
`Docker Build & Stack Smoke` now also runs `make ci-gateway` (probe-gateway.sh against the built image) and
`make ci-docker-prod` (the prod descriptor itself, built from the checkout, minus cloudflared). The gateway jobs have
never run on GitHub — that needs the branch merged and pushed to `main`.

**Repo configuration (names only, per the brief, not re-read here).** Secrets set: `DOCKERHUB_USERNAME`,
`DOCKERHUB_TOKEN`, `PORTAINER_URL`, `PORTAINER_TOKEN`, `PORTAINER_ENDPOINT_ID`. **Not set:** `PORTAINER_STACK_ID`
secret and `DEPLOY_ENABLED` variable — correctly, because the stack does not exist; `deploy-portainer` is skipped,
not failed.

**What is left, all founder steps, in order:** create the tunnel (public hostname `reel.frostaura.net` →
`http://gateway:80`); create the `reel` stack from `docker-compose.prod.yml` with its required keys; deploy once and
check `/health`; set `PORTAINER_STACK_ID`; set `DEPLOY_ENABLED=true`. Until then nothing is deployed and the
external-user kill criterion stays unevaluable (`questions.md`).

**Unverified until a real push:** the gateway publish jobs and manifest; the deploy job's new stack-file guard; the
tunnel's ingress to `gateway:80`; `10.83.30.0/24` being free on the production host.
