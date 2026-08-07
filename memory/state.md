---
name: fa-reel-state
description: "M2 edge proof RE-RUN clean 2026-08-07: model does NOT beat the popularity baseline (0.30 vs 0.30) after FOUR iterations — kill-criterion territory, founder call; leaks fixed; fully pushed to a now-private repo"
type: state
last_verified: 2026-08-07
---

# Current state

_Git facts below re-verified 2026-08-07 by direct inspection (`git rev-parse`, `git rev-list --left-right --count`, `git status --porcelain`). Everything else last verified 2026-07-24 by the Labs tree-audit pass (`ci.yml`, `deploy/portainer-stack.yml`, test counts and package manifests inspected directly)._

**Technically UNPROVEN, commercially unproven — but, as of 2026-08-07, no longer only on one machine.** Branch `main` at `f0f3184`, **fully pushed** to `origin/main` with a clean working tree (`git rev-list --left-right --count` → `0 0`). The repo was flipped **private** immediately before that push. The ~2-month single-machine durability gap described by the 2026-07-24 audit is **closed**.

**The M2 edge proof was re-run on clean code and the model does NOT beat the popularity baseline.** Measured 2026-08-07 on the founder's real account, after the two training-time label leaks were removed:

| | iteration 3 (leaky) | iteration 4 (fixed) |
| --- | --- | --- |
| model precision@10 | 0.30 | 0.30 |
| popularity baseline | 0.40 | 0.30 |
| relative lift | −25% | **0%** |
| RMSE | 2.0007 | 1.9539 |
| Spearman ρ | 0.1609 | 0.1714 |

The 2026-06-12 record of "70% vs 50%, a 40% relative lift on a leakage-clean time split" does not reproduce. The split was clean; **the featurizer was not**. `FeatureVectorBuilder` compared a loved title's embedding against a set containing that title (`Cosine(a,a)` = exactly 1.0, a perfect label proxy) and fed a title's own rating into the genre/decade/cast/director statistics used to predict it. Neither can happen at serving time, because a scored candidate is by definition unrated.

**The fix is confirmed by feature importance, not just by the metric.** Before: `lovedCastCount` 1.00 (top feature), `castAffinity` 0.71, `simTopLovedMax` present. After: `simTopLovedMax` and `castAffinity` are gone from the top 15 entirely and `lovedCastCount` collapsed 1.00 → 0.05. The features that were quietly re-reading the label lost essentially all their gain, and the serialized artifact shrank (156,360 → 155,205 bytes) despite a *wider* feature vector. Removing the leak did not deflate the headline number — it made the model honest.

**This is a founder decision point, and it should not be softened.** The program's own kill criterion is "the model cannot beat the popularity baseline by a meaningful margin after **3** modelling iterations". Four have now run and none has passed the ≥20% gate. Two things are worth weighing before concluding: n is still 1 (the founder's account, ~10–30 holdout titles, so 0.30 vs 0.30 is 3 hits versus 3), and the holdout only partially proxies real serving quality — the leak corrupted *training*, so the gain from fixing it should show up in what users actually see rather than in this metric. Neither point rescues the number as it stands.

The committed set as of the last audit: hybrid retrieval, the online residual model, feed pagination + refresh coordinator, search intent parsing, the managed-list reconciler, the `Microsoft.OpenApi` security override, `deploy/portainer-stack.yml` and the QA screenshots (now under `docs/screenshots/`, previously loose at repo root).

**No `.git/*.lock` debris was present on 2026-08-07** — the stale-lock gotcha did not reproduce this session.

M4 (multi-tenant) is code-complete with **zero external users**.

CI is real and among the strongest in the portfolio: `dotnet format` gate, **187 backend tests** with a 10% coverage ratchet, frontend lint/build/tests with a 6% ratchet, a Playwright e2e job now running **all 8 specs** (was 1), an **expiring-allowlist npm audit gate** (`npm run audit:gate`) plus a `dotnet list package --vulnerable` gate, an integration job against a real `pgvector/pgvector:pg16` service, native `ubuntu-24.04-arm` image builds and a multi-arch manifest merge. The Portainer redeploy job exists and is skipped (not failed) while the repo variable `DEPLOY_ENABLED` is unset.
