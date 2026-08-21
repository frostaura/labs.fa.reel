---
name: fa-reel-watch
description: "Model does not beat the popularity baseline after SEVEN iterations against a 3-iteration kill criterion (founder call); Tone/Era taxonomy polluted pre-2026-08-07; no counter behind the ≥5-external-users gate"
type: watch
last_verified: 2026-08-07
---

# Watch list

_Cleared items are deleted here, not struck through — a watch list carrying resolved rows stops being read._

- **THE open question: the model does not beat the popularity baseline.** Re-measured clean on 2026-08-07 at 0.30 vs 0.30 (0% relative lift). **Seven modelling iterations have now run against a kill criterion that names three.** See `memory/state.md` for the full table and why the leak fix did not move precision@10. Founder call.
- **`Tone` and `Era` on the 2,312 pre-2026-08-07 `TitleAttributes` rows are polluted** — 215 distinct `Tone` values and 97 distinct `Era` values against closed vocabularies of 16 and 13, including incoherent entries like `period-victorian`, `1980s` and `fantasy` (not an era). Harmless today because `FeatureVectorBuilder` reads only the seven scalars plus `Themes`. **Re-extract those rows before surfacing either field in the UI or adding it to the feature schema** — the new strict JSON schema only constrains extractions from here on.
- **`PipelineStage` has no "not enough ratings" value.** An account below the 10-rating training floor is parked as `Degraded`, which is the only terminal stage the UI renders as stopped. The correct fix is a new enum member plus a frontend case.
- **A full software-defect audit on 2026-08-07 catalogued ~50 confirmed defects** across search, the recommender, the data pipeline, LLM handling and the UI. Remediation is in progress; the plan lives at `~/.claude/plans/fix-all-defects-immidiately-toasty-parrot.md`. The three that reframe the program: search exclusions are applied *after* the candidate pool is cut so negation cannot remove anything; the recommender has two training-time label leaks; and 50% of the catalogue is fed to the trainer with `TraktRating = 0` because one failing title permanently aborts its whole 25-title hydration batch.
- `docs/product-spec.md` §"FrostAura family look" cites `/docs/brand/assets/` as the canonical asset location; no such directory exists in this repo.
- Success metrics for the commercial half are instrumented nowhere — the "≥5 external users" gate has no counter behind it.
