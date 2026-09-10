---
name: fa-reel-watch
description: "Calibrator collapse at rho~0 (fix in flight); feed is all TV shows under current supply; six local test failures quarantined (3 are shared-DB contamination, 3 real local failures — comma decimal separator is the lead); Tone/Era polluted pending ExtractorVersion re-extraction; no counter behind the 5-external-users gate"
type: watch
last_verified: 2026-09-09
---

# Watch list

_Cleared items are deleted here, not struck through — a watch list carrying resolved rows stops being read._

- **The model does not beat the popularity baseline — settled 2026-09-09 as a consistent negative** (iteration 12, −80%, 12/12 non-positive; served-title correlation −0.004). Founder chose model-first repair then product. The Phase 0 eval-instrument repair is what makes iteration 13 comparable; do not read iterations ≤12 as modelling attempts (the counter incremented on catalogue growth).
- **Calibrator collapse.** On 116 pairs at ρ≈0 the isotonic calibrator maps raw 6.5–9.5 → 6.25. A policy fix (bias-shift unless ρ ≥ 0.2, minimum-slope blend, `CALIBRATOR_MODE`) is in flight; until it merges, do not trigger a Fresh build on the live account.
- **The live feed is entirely TV shows** (hero + all rails) under the current supply; watch whether Phase 1 filter-aware discovery restores movies.
- **`Tone` and `Era` on the 2,312 pre-2026-08-07 `TitleAttributes` rows are polluted** — 215 distinct `Tone` values and 97 distinct `Era` values against closed vocabularies of 16 and 13, including incoherent entries like `period-victorian`, `1980s` and `fantasy` (not an era). Harmless today because `FeatureVectorBuilder` reads only the seven scalars plus `Themes`. **Re-extract those rows before surfacing either field in the UI or adding it to the feature schema** — the new strict JSON schema only constrains extractions from here on.
- **`PipelineStage` has no "not enough ratings" value.** An account below the 10-rating training floor is parked as `Degraded`, which is the only terminal stage the UI renders as stopped. The correct fix is a new enum member plus a frontend case.
- `docs/product-spec.md` §"FrostAura family look" cites `/docs/brand/assets/` as the canonical asset location; no such directory exists in this repo.
- Success metrics for the commercial half are instrumented nowhere — the "≥5 external users" gate has no counter behind it.
- **Six local test failures are now quarantined in code** (`[LocalFlakyFact]`): CandidateRetriever ×2 and EnrichCatalog are shared-database contamination (fix = per-class isolation); LiveSearchExpansion ×2 and LexicalSearch fail in isolation too and the local runner formats decimals with a comma separator — culture is the lead. CI (invariant culture, pgvector service) is authoritative.
