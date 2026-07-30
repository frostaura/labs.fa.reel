---
name: fa-reel-decisions
description: "Deploy (2026-07-24); gpt-5.4-mini extraction; collaborative filtering shipped; Stripe scaffolded flag-off; remote 1536d embeddings; pin-not-suppress vulnerable transitives"
type: decision
last_verified: 2026-07-24
---

# Live decisions

- **Deploy (founder, 2026-07-24).** The external-user kill criterion is evaluated against a *deployed* product, so the trip-by-timer-for-logistical-reasons scenario is void. Remaining founder steps: push, create the Portainer stack from `deploy/portainer-stack.yml`, set `DOCKERHUB_TOKEN` / `PORTAINER_*` secrets and the `DEPLOY_ENABLED` variable, point Cloudflared at `reel.frostaura.net`.
- Extraction model `openai/gpt-5.4-mini`, no manual pipelines, eligibility = zero-interaction-footprint, identity = Sign in with Trakt only — all founder-locked 2026-06-12 and carried into `CLAUDE.md`.
- **Collaborative filtering shipped** 2026-06-18 (item-based via TMDB recommendations, in `CandidateGenerator.cs`). The old "deferred to ~200+ tenants" framing is dead.
- **Stripe is scaffolded and deploy-ready**, flag-gated off with blank keys.
- **Embeddings are remote, not local** — `EMBEDDINGS_PROVIDER=openai`, `text-embedding-3-small`, **1536 dims** via OpenRouter. Local ONNX MiniLM at 384d is no longer the default.
- **Vulnerable transitives get an explicit pin, not a suppression.** Two now: `Microsoft.OpenApi` 2.7.5 (GHSA-v5pm-xwqc-g5wc via AspNetCore.OpenApi 10.0.9) and `System.Security.Cryptography.Xml` 10.0.10 (GHSA-8q5v-6pqq-x66h et al. via DataProtection.EntityFrameworkCore 10.0.9). Both carry a comment saying to remove them once upstream ships a patched transitive. The `security` CI job fails the build otherwise.
