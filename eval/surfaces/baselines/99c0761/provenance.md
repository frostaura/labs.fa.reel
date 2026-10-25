# Surface baseline 99c0761 — provenance not held in recording.json

This recording predates three harness fixes, so these facts were observed by hand at the time of the
run and are NOT replayable from `recording.json`. `report.md` and `metrics.json` are regenerated from the
recording alone (`node scripts/eval/surfaces.mjs --replay recording.json`).

- **Jobs that ran on `reel_ws7` during the harness** (read from `SyncJobs` before the database was
  re-cloned): BuildFeed Succeeded × 2 (the harness's Fresh build plus one chained after the retrain);
  Train × 1, PersonaliseTitles × 2, RefreshAvailability × 2, EnrichCatalog × 1, all Succeeded
  (enrichment ran with `EXTRACT_DAILY_TOKENS=0`, so no extraction spend).
- **Consequence:** the harness fetched while Train and the second BuildFeed were running, so the active
  artifact may have changed mid-fetch, and the artifact id was not recorded. The harness now drains every
  chained job before its first fetch and records the artifact at the start and end of the fetch
  (`jobsDrainedBeforeFetch`, `artifact.changedDuringFetch`); this baseline has neither.
- **Not fetched in this recording:** person pages and the Watch-tonight pin (added after). The digest was
  fetched and returned 0 cards because the weekly digest service is off on the eval stack; the harness
  now enqueues the BuildDigest job itself. The digest's 0 leaks are therefore not a measurement — the
  regenerated report lists it under "surfaces with no data".
- **Model key:** the run paid with the shared lane key, which is the founder's own OpenRouter key
  (SHA-256 match, compared without printing either value); see docs/ask-reel-eval.md §Spend.

A re-run with the current harness (one Fresh build, jobs drained, all surfaces, the eval stack's own key)
replaces this baseline; until then compare treats it as the reference only for the surfaces it measured.
