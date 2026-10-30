# Surface baseline 6968db5, keyless — provenance

The reference for every **keyless** surface run on `reel-2026-10-23` from hardening round 9 on, replacing
`../20429ff-keyless/` for `--compare`. It is the first recording (version 5) that measures people credited on
both an anchor and its card (HREV8-RET-04), so its evidence share is scored on the rule the product admits
anchored cards on: a shared credited person in the same capacity, or two shared TMDB keywords, or a cosine
at or above the snapshot's random-pair p90 (`summary.evidenceRule` = `people+keywords+cosine`).
`--compare` refuses a run scored on the old `keywords+cosine` rule against it, and the reverse.

- **Why re-baseline.** `20429ff-keyless` was taken at 22 anchored cards; the build now serves 50, so its
  evidence share of 1 stood on less than half the volume it gates. Re-scored by the old rule, this run's
  50 anchored cards give 0.96 against 20429ff's 1 (allowed 0.97, REGRESSION) — the round-8 finding,
  reproduced exactly. Two cards failed it: House → The Strain (a shared director, Deran Sarafian, which the
  old rule did not count) and Hokum → Factotum (one shared keyword; cosine 0.3855 against p90 0.3868).
  With people counted the first passes and the second still fails: **0.98, 49 of 50**. The 0.03 tolerance
  is unchanged.
- **Build:** the API built by `scripts/eval/build-at.sh 6968db54` (recorded in `recording.json` →
  `buildInfo`; dll SHA-256 `0d01f03a…`, 0 warnings). `6968db54` is the `build/trust-v1` tip at the time;
  `ws/ws7`'s backend and frontend sources are identical to it (the round-9 change touches the harness,
  its tests and eval data only). The claims audit was read from the `ws/ws7` tree at `cc4d3c97`.
- **Data:** `reel_ws7`, a fresh `snapshot.sh clone` of `reel_eval_template` (snapshot `reel-2026-10-23`),
  sanitised with blank Trakt tokens. The Fresh build started from, and served, artifact `0536acbf…` (the
  same input artifact as every earlier keyless baseline); keyless, it trained nothing.
- **API:** `scripts/eval/run-api.sh --jobs` on :5187, Trakt offline in-process, **no OpenRouter key** (the
  lane env file with the key blanked). Ledger: $0, 0 Trakt calls.
- **Harness:** recording version 5; one Fresh build, every chained job drained, 60 digest arrivals seeded,
  the digest built, then the fetch. `metrics.json` and `report.md` are `--replay` output of the recording.
- **Result:** every absolute gate passes. 195 suggestion cards (hero 5, feed rows 22, more 28, genre rails
  96, Similar 34, digest 10), 50 anchored; leaks, stubs, hedges, repeats, footprint and HTTP errors 0;
  uncitable why-lines 0 of 67 checked (0 unscored); false copy claims 0 of 215 registered, 0 unregistered.
- **Reproduced:** two runs of the same build, each on a fresh clone, served the same cards on every block
  and pass every compare row against each other in both directions. The committed recording is the second.
- **Taken:** 2026-10-30 by WS-7 (hardening round 9).
