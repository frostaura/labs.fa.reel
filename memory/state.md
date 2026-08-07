---
name: fa-reel-state
description: "Technically proven but the M2 edge proof is INVALIDATED by two training leaks; fully pushed to a now-private repo 2026-08-07; CI among the strongest in the portfolio"
type: state
last_verified: 2026-08-07
---

# Current state

_Git facts below re-verified 2026-08-07 by direct inspection (`git rev-parse`, `git rev-list --left-right --count`, `git status --porcelain`). Everything else last verified 2026-07-24 by the Labs tree-audit pass (`ci.yml`, `deploy/portainer-stack.yml`, test counts and package manifests inspected directly)._

**Technically proven, commercially unproven — and, as of 2026-08-07, no longer only on one machine.** Branch `main` at `625f375`, **fully pushed** to `origin/main` with a clean working tree (`git rev-list --left-right --count` → `0 0`). The repo was flipped **private** immediately before that push. The ~2-month single-machine durability gap described by the 2026-07-24 audit is **closed**.

**The M2 edge proof (precision@10 70% vs a 50% popularity baseline) is INVALIDATED and must be re-run.** A 2026-08-07 audit found two training-time label leaks in `FeatureVectorBuilder`: a loved title's embedding is compared against a set containing itself (`Cosine(a,a)` is exactly 1.0, a perfect label proxy that is structurally impossible at serving time), and a title's own rating feeds the genre/decade/cast/director affinity features used to predict it. Both vanish at serving time, so the model was scored under conditions it never sees in production. Separately, the harness's persisted claim that present-day popularity affects model and baseline "symmetrically" is false — the baseline is built 100% from that signal while the model dilutes it across 86 features. Treat 70%/50% as **unproven** until re-measured.

The committed set as of the last audit: hybrid retrieval, the online residual model, feed pagination + refresh coordinator, search intent parsing, the managed-list reconciler, the `Microsoft.OpenApi` security override, `deploy/portainer-stack.yml` and the QA screenshots (now under `docs/screenshots/`, previously loose at repo root).

**No `.git/*.lock` debris was present on 2026-08-07** — the stale-lock gotcha did not reproduce this session.

The M2 edge proof passed 2026-06-12 — precision@10 of 70% against a 50% popularity baseline, a 40% relative lift on a leakage-clean time split. **Caveat, stated plainly:** n = 1 (the founder's account) over roughly 10–30 holdout titles, so 70% vs 50% is 7 hits versus 5. The framework is sound; the sample is thin. "Iteration 1 of 3" is the right posture. M4 (multi-tenant) is code-complete with **zero external users**.

CI is real and among the strongest in the portfolio: `dotnet format` gate, 84 backend test methods with a 10% coverage ratchet, frontend lint/build/tests with a 6% ratchet, a Playwright e2e job, `npm audit --audit-level=high` plus a `dotnet list package --vulnerable` gate, an integration job against a real `pgvector/pgvector:pg16` service, native `ubuntu-24.04-arm` image builds and a multi-arch manifest merge. The Portainer redeploy job exists and is skipped (not failed) while the repo variable `DEPLOY_ENABLED` is unset.
