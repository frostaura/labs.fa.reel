---
name: fa-reel-watch
description: "The whole session's work exists on one Mac — main is ahead-only and unpushed with five migrations in the range; the M2 gate is a settled negative (iteration 14, no lift) and the 25.5% retrieval-recall ceiling says supply is the constraint; the 50-read human bar for lens copy is unmet; Tone/Era re-extraction still draining; interleaving off and ~3 months per comparison; no counter behind the 5-external-users gate"
type: watch
last_verified: 2026-09-10
---

# Watch list

_Cleared items are deleted here, not struck through — a watch list carrying resolved rows stops being read._

- **Everything from 2026-09-09/10 exists on one Mac.** `main` in the dev clone is ahead-only over `origin/main` and unpushed, and the range carries **five migrations** and the whole Phase 0–4 body. The push is Dean's call and is the single cheapest risk reduction available here. Re-measure the range before acting on it; do not quote a count from this file.
- **The model does not beat the popularity baseline, and that is now settled rather than suspected.** Iterations 13 and 14 ran on the *repaired* instrument and both came back non-positive (NDCG 0.09 vs 0.19, then 0.12 vs 0.20, CIs spanning zero). Do not read iterations ≤12 as modelling attempts — that counter incremented on catalogue growth.
- **Supply is the binding constraint: union retrieval recall cannot exceed 25.5%.** Only 14 of 55 titles Dean rated ≥8 were in the catalogue the day before he rated them. Filter-aware discovery and the cost meter are the live levers; watch whether the eligible pool actually grows past ≈421 and whether hero-eligible count at the 7.5 floor rises above zero.
- **The 50-read human review bar for lens copy has not been met.** The plan requires 50 human-reviewed generations per prompt version before anyone but Dean sees the copy. `GET /api/dev/lens-audit` and `docs/lens-audit.md` exist for exactly this; the reads have not happened.
- **Feed composition after a retrain.** The first live Fresh build following the schema-v4 retrain was led by a reality show (Love Island USA, 7.2) — the popularity/trending arm plus a weak model. Media balance is enforced in discovery and Phase 3's profile-conditioned reranker is the intended fix; confirm it holds over a week of builds.
- **`Tone` and `Era` on the 2,312 pre-2026-08-07 `TitleAttributes` rows are polluted** — 215 distinct Tone values and 97 distinct Era values against closed vocabularies of 16 and 13. FeatureSchema v4 gates the tone/era one-hots on `MinTrustedExtractorVersion = 2`, so a polluted row contributes nothing rather than nonsense, and `ExtractorVersion` 2 plus the nightly re-queue is draining them. Watch `enrichment.staleExtractorVersion` on `GET /api/metrics/pipeline` reach zero before surfacing either field in the UI.
- **Interleaving is off and stays off until it is worth three months.** `INTERLEAVE_ENABLED=false`; the exact mSPRT needs roughly 3 months per comparison at this traffic. Turning it on is a deliberate decision, not a default.
- **`PersonAffinities` is now written, but the pool is role-agnostic** — an actor-turned-director carries one average across every credit. Fine today; revisit if person affinity ever becomes a headline feature.
- **`PipelineStage` has no "not enough ratings" value** (verified 2026-09-10 — the enum is Linked/Ingesting/Extracting/Training/Evaluated/FeedReady/Degraded). An account below the 10-rating training floor is parked as `Degraded`, the only terminal stage the UI renders as stopped. The fix is a new enum member plus a frontend case.
- Success metrics for the commercial half are instrumented nowhere — the "≥5 external users" gate still has no counter behind it. `docs/product-spec.md` §"FrostAura family look" cites `/docs/brand/assets/` as the canonical asset location; no such directory exists in this repo.
