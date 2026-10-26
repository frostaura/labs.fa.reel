# Surface baseline 99c0761, keyless — provenance

The reference for every **keyless** surface run on `reel-2026-10-23` (the merged tree has only ever been
measured keyless, because no eval key exists and the founder's key is refused). It replaces the keyed
`../99c0761/` for that comparison; that one stays the reference for a keyed run.

- **Build:** `99c0761245d7…` built outside any checkout by `scripts/eval/build-at.sh` (dll SHA-256 in
  `recording.json` → `buildInfo`).
- **Data:** `reel_baseline_ws7_99c0761`, a fresh `snapshot.sh clone` of `reel_eval_template`
  (snapshot `reel-2026-10-23`), sanitised; no migration was pending at 99c0761.
- **API:** `scripts/eval/run-api.sh --jobs` on :5187 from the dll's folder, every hosted service, boot
  repair and trust purge off, Trakt offline by the dead proxy (99c0761 predates `OfflineTraktClient`),
  and **no OpenRouter key**. So no model call ran: the why-lines are the templater's, and the lens copy and
  hooks are the ones stored in the snapshot. Ledger: $0, 0 Trakt calls.
- **Harness:** the 2026-10-25 harness (recording version 3): every copy field kept raw and scored as
  99c0761 rendered it (the hero shows the hook and the lens clause beneath it, detected from 99c0761's own
  `RecCardHero`), every surface fetched, chained jobs drained before the fetch (BuildFeed ×2, Train,
  PersonaliseTitles ×2, RefreshAvailability ×2, EnrichCatalog, BuildDigest, all Succeeded), and the
  artifact the Fresh build started from recorded (`artifact.input`, the comparability key).
- **Taken:** 2026-10-25 by WS-7 (R1-15). `metrics.json` and `report.md` are `--replay` output of the
  recording.
