# Surface baseline 224eea0, keyless — provenance

The reference for every **keyless** surface run on `reel-2026-10-23` from wave A on. It replaces
`../9f42c9d-keyless/` for `--compare`, because it is the first baseline whose digest is measured: the
harness now seeds digest arrivals before building the digest (IREV3S-02), and `--compare` refuses runs
whose arrivals were made differently.

- **Build:** `224eea004c68…` (the `build/trust-v1` tip when it was taken), built outside any checkout by
  `scripts/eval/build-at.sh` (dll SHA-256 in `recording.json` → `buildInfo`).
- **Data:** `reel_ws7_surf`, a fresh `snapshot.sh clone` of `reel_eval_template` (snapshot
  `reel-2026-10-23`), sanitised. The Fresh build started from artifact `0536acbf…`, the same input
  artifact as `9f42c9d-keyless` and `99c0761-keyless`.
- **API:** `scripts/eval/run-api.sh --jobs` on :5187, every hosted service, boot repair and trust purge
  off, Trakt offline in-process, **no OpenRouter key**. Ledger: $0, 0 Trakt calls.
- **Harness:** recording version 3; one Fresh build, every chained job drained, then
  `eval/surfaces/seed-arrivals.sql` (60 titles seeded), then the digest build, then the fetch.
  `metrics.json` and `report.md` are `--replay` output of the recording.
- **Reproduced:** a second run of the same build on another fresh clone (`reel_ws7_surf2`) compared
  against this baseline with every row passing, the digest's 10 cards included.
- **Taken:** 2026-10-26 by WS-7 (wave A).

## What the digest measures now

The digest announces offers that arrived on a streaming service in the trailing 7 days
(`TitleAvailabilities.FirstSeenAt`, stamped by `AvailabilitySnapshot` when a refresh first sees an
offer). The snapshot's column was back-filled empty, so before seeding every eval copy built a 0-item
digest and the coverage gate could not pass (int-r3: "digest: no cards").

`seed-arrivals.sql` writes what a provider-set change would have: on every streaming-now offer (Flatrate,
Free, Ads) in the account's region of the 60 most popular titles that have one, `FirstSeenAt` one day ago
and `FetchedAt` now. Watched, rated, saved and rule-excluded titles are seeded **on purpose**, so the
digest's own eligibility is what keeps them out, and the leak check would see it if it did not. On this
snapshot 61 titles carried an arrival when the digest was built (60 seeded, 1 real): 28 eligible by the
eval's independent truth, 33 not (2 of them watched or rated). The digest keeps its top 10 by prediction:
9 seeded titles, and Open Range, a real arrival the chained `RefreshAvailability` stamped from TMDB
during the Fresh build. That one slot can vary with TMDB between runs; the repeat run drew the same 10.

## The older keyless baselines

`9f42c9d-keyless` and `99c0761-keyless` are not re-recorded. Both builds predate the arrivals digest:
their `BuildDigest` reads `FetchedAt` (every re-fetched offer counted as news, IR2S-02), so seeding would
measure a different selection rule, not the same digest with data. They stay as references for what the
program changed; `--compare` refuses them against a seeded run.
