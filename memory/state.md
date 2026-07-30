---
name: fa-reel-state
description: "Technically proven (edge proof passed, n=1 caveat), deploy decided, 10 commits unpushed, still on one machine; CI among the strongest in the portfolio"
type: state
last_verified: 2026-07-24
---

# Current state

_Last verified: 2026-07-24 by the Labs tree-audit pass (git log, upstream tracking, working tree, `ci.yml`, `deploy/portainer-stack.yml`, test counts and package manifests inspected directly)._

**Technically proven, commercially unproven, engineering-ready to deploy — and still on one machine.** Branch `main` at `2767cf9` (2026-07-24), which landed the month-old working set: hybrid retrieval, the online residual model, feed pagination + refresh coordinator, search intent parsing, the managed-list reconciler, the `Microsoft.OpenApi` security override, `deploy/portainer-stack.yml` and the QA screenshots (now under `docs/screenshots/`, previously loose at repo root). **Ten commits are unpushed**; `origin/main` is still at `513bd10` (2026-06-18), so the public repo is over a month behind and every line since exists only on the founder's Mac plus iCloud.

The M2 edge proof passed 2026-06-12 — precision@10 of 70% against a 50% popularity baseline, a 40% relative lift on a leakage-clean time split. **Caveat, stated plainly:** n = 1 (the founder's account) over roughly 10–30 holdout titles, so 70% vs 50% is 7 hits versus 5. The framework is sound; the sample is thin. "Iteration 1 of 3" is the right posture. M4 (multi-tenant) is code-complete with **zero external users**.

CI is real and among the strongest in the portfolio: `dotnet format` gate, 84 backend test methods with a 10% coverage ratchet, frontend lint/build/tests with a 6% ratchet, a Playwright e2e job, `npm audit --audit-level=high` plus a `dotnet list package --vulnerable` gate, an integration job against a real `pgvector/pgvector:pg16` service, native `ubuntu-24.04-arm` image builds and a multi-arch manifest merge. The Portainer redeploy job exists and is skipped (not failed) while the repo variable `DEPLOY_ENABLED` is unset.
