# Surface baseline 20429ff, keyless — provenance

The reference for every **keyless** surface run on `reel-2026-10-23` from wave B on, replacing
`../224eea0-keyless/` for `--compare`. It is the first baseline that records the two metrics added in
wave B — templated why-lines a stored contribution makes uncitable (WS-5's IREV3S-01 rule) and the
claims audit's false and unregistered copy claims (task 9) — so those metrics have baseline data instead
of "no baseline data".

- **Build:** the API built by `scripts/eval/build-at.sh` at `d86e65a` on `ws/ws7` (recorded in
  `recording.json` → `buildInfo`). Its backend and frontend sources are identical to `build/trust-v1`
  at `20429ff` (`git diff 20429ff d86e65a -- src/backend ':!src/backend/FrostAura.Reel.Tests' src/frontend`
  is empty: the branch adds tests, eval data, scripts and docs only), hence the name: `d86e65a` stops
  existing when the branch is squash-merged, `20429ff` does not.
- **Data:** `reel_ws7`, a fresh `snapshot.sh clone` of `reel_eval_template` (snapshot `reel-2026-10-23`),
  sanitised with blank Trakt tokens. The Fresh build started from, and served, artifact `0536acbf…` (the
  same input artifact as every earlier keyless baseline); keyless, it trained nothing.
- **API:** `scripts/eval/run-api.sh --jobs` on :5187, every hosted service, boot repair and trust purge
  off, Trakt offline in-process, **no OpenRouter key** (the lane env file with the key blanked). Ledger:
  $0, 0 Trakt calls.
- **Harness:** recording version 4; one Fresh build, every chained job drained, 60 digest arrivals seeded,
  the digest built, then the fetch. `metrics.json` and `report.md` are `--replay` output of the recording.
- **Result:** against `224eea0-keyless` every compare row passes (Similar 6 → 8 cards, anchored 12 → 14,
  every rate unchanged at 0, evidence share 1). Uncitable why-lines 0 of 59 whyThis lines checked (0
  unscored). False copy claims 15 of 194 registered, 0 unregistered — the claims register's false
  families, each owned (`eval/claims/claims.json`).
- **Reproduced:** three runs of the same build, each on a fresh clone, agree on every row. The committed
  recording is the third: the claims register had gained one false family between the second and third
  (WS-3's cost meter, found by `askreel.mjs check --db --api`), so the first two recorded 14 false copy
  claims and this one 15 — a register finding, not a change in the code under test.
- **Taken:** 2026-10-26 by WS-7 (wave B).
