# Ask Reel golden eval and the surface harness

This is the measurement every chat, retrieval, model, prompt and surface change has to pass before it
merges (program §4.12, §5.0 rule 6, WS-7). It runs **only** on the isolated eval stack, **only** by hand
(no schedule, no cron), and it never touches the founder's live Postgres (`:5433`), the `reel` database or
his Trakt profile. Every script under `scripts/eval/` refuses `:5433` and `reel` before it connects; the
one exception is `ledger.mjs --allow-live-read`, a read-only transaction kept for the founder and the
integrator, which no build lane passes.

## The isolated stack

| Piece | Where | What isolates it |
|---|---|---|
| Postgres | `fareel-eval-postgres`, `127.0.0.1:5434`, volume `fareel_eval_pgdata` | A separate container and volume; adopted (not recreated) by `docker-compose.eval.yml` behind the `db` profile |
| Snapshot | `reel_eval_template` → `reel_eval` / lane slots | `scripts/eval/snapshot.sh` placeholders every Trakt token, truncates the outbox, deletes pending jobs, push subscriptions, `AppSecrets` (the VAPID private key), `DataProtectionKeys` and `RefreshSessions`, archives the ledger of any database it is about to drop, and stamps the snapshot id (`COMMENT ON DATABASE`) |
| API | `127.0.0.1:5180` (compose project `fareel-eval`) or a slot via `scripts/eval/run-api.sh` | Every hosted service off; job runner off unless a surface run needs one Fresh build; Trakt blackholed (see below); eval-only Ask/rerank allowances; refuses a dll with a `.env` in any parent folder, starts the API from an **empty environment** holding only the env file's variables and its own switches (never a key the caller's shell exports), and exports every credential the env file leaves unset as empty (`--dry-run` lists the names the API would get) |
| Model key | `EVAL_OPENROUTER_API_KEY` (its own $10/day cap) | **Not supplied yet.** The shared lane key in `~/src/fa.reel-ws/lane.env` **is the founder's own OpenRouter key** (SHA-256 match with the live clone's `.env`, compared without printing either). Every paid call WS-7 has made — the three 99c0761 baselines, the surface baseline and the aborted attempts — went on it. `run-api.sh`, `askreel.mjs live` and `surfaces.mjs` now refuse that key unless the founder's sign-off is passed (`EVAL_FOUNDER_KEY_SIGNOFF` / `--founder-key-signoff`) and recorded in the recording |

**Trakt isolation, two layers.** (1) Inside the eval container Trakt's hosts resolve to `0.0.0.0`; for a
process slot, all outbound HTTPS goes through a dead loopback proxy except `openrouter.ai` and the TMDB
hosts. (2) No Trakt credentials are configured, and the dev-link session resolves the snapshot account by
slug, so nothing exchanges a token. The proof is the ledger: Trakt usage is recorded before the request
is sent, so a run's `ExternalApiUsages` delta counts attempts, and a Trakt row fails the `traktCalls`
gate. A code-level fake `ITraktClient` for the API host is a change request to WS-4/WS-8; until it lands,
the network layer is the guarantee.

**Test databases** for every workstream live on the eval Postgres as `reel_test_<ws>`.
`PostgresFixtureGate.LiveTargetRefusal` makes the fixture and `ReelApiFactory` refuse, before connecting,
any connection string whose effective database is `reel` (named, or defaulted from the username when no
`Database` is given) on any host, or that reaches port 5433 on this machine — every host of a multi-host
string, with its own port, where "this machine" is the loopback range, `0.0.0.0`/`::`, `*.localhost`,
Docker's host aliases, a unix socket, the machine's own names and its interface addresses.

## Running it

```sh
# 1. A fresh, sanitised eval database from the snapshot (or `restore <dump> reel_eval` for a new snapshot).
scripts/eval/snapshot.sh clone reel_eval_template reel_eval

# 2. An API on it, built at the commit under test OUTSIDE any checkout (DotEnv would load a parent .env).
scripts/eval/build-at.sh 99c0761 ~/src/fa.reel-ws/ws7/.tmp/base        # prints the dll; writes BUILD_INFO.json
scripts/eval/run-api.sh --dll <dll> --db reel_eval --port 5180 --env-file <eval env: the eval key + TMDB>
#    or: docker compose -p fareel-eval -f docker-compose.eval.yml --env-file <eval env> up -d eval-api

# 3. Quality run (concurrency ≤ 4) and, separately, the latency run (concurrency ≤ 2, every build slot held).
node scripts/eval/askreel.mjs live --api http://127.0.0.1:5180 --db reel_eval --label <name> --engine v1|v2 \
     --api-env <eval env> --build-info <dir>/BUILD_INFO.json
node scripts/eval/askreel.mjs live ... --concurrency 2 --hold-build-slots --only-file eval/askreel/latency-set.txt --label <name>-latency

# 4. Gate against the baseline: exit 1 on any regression, exit 2 when the runs are incomparable.
node scripts/eval/askreel.mjs compare .tmp/eval/<date>/<name>/metrics.json --baseline eval/askreel/baselines/99c0761/open/metrics.json

# Replay (CI, 0 paid calls) and scenario validation.
node --test 'scripts/eval/*.test.mjs'
node scripts/eval/askreel.mjs replay <recording.json>
node scripts/eval/askreel.mjs check [--db reel_eval]          # --db also executes every truth query used

# Surface harness: one Fresh build (API started with --jobs), then every surface; compare against a baseline.
node scripts/eval/surfaces.mjs --api http://127.0.0.1:5180 --db reel_eval --label <name> --fresh --api-env <eval env> --build-info <...>
node scripts/eval/surfaces.mjs --replay <recording.json>
node scripts/eval/surfaces.mjs --compare <metrics.json> --baseline eval/surfaces/baselines/99c0761/metrics.json

# Ledger cost check (§7): every eval and lane database together, and the provider's own figure for a key.
node scripts/eval/ledger.mjs --all
node scripts/eval/ledger.mjs --openrouter-key <env file>
```

Reports land in `.tmp/eval/<date>/<label>/` (`recording.json`, `metrics.json`, `report.md`, plus
`partial.jsonl` — a killed run resumes with `--resume`, re-running only scenarios that died on the
transport, and still reads the ledger from the day its first attempt started). A turn with no byte for
120 s or longer than 300 s is aborted and scored as failed (`client_timeout`), as a user giving up would.
Baselines that gate merges are promoted into `eval/askreel/baselines/` and `eval/surfaces/baselines/`; the
holdout's baseline lives with the holdout in `~/src/fa.reel-eval-sealed/`.

**Replay is a scorer replay.** It re-scores a frozen recording with no API, database or paid call, which
proves the committed metrics and every scoring rule. It does not yet drive an executor or composer from
recorded plans (§4.12's replay), because the v2 executor does not exist; that half lands with WS-2's
engine. It is not wired into CI or `make ci` yet (change request to WS-8).

## The scenario set

- `eval/askreel/scenarios/open.json` — the open set every workstream may read and run (120 scenarios,
  34 multi-turn, 167 turns).
- `~/src/fa.reel-eval-sealed/askreel/holdout/holdout.json` — 30% held out (51 scenarios, 64 turns),
  outside every worktree, for the integrator only until the release gate (G2). Its own truth queries live
  in `…/holdout/truth/` and are passed with `--truth-dir`; no holdout turn repeats an open query, and
  `check` fails on any open truth file no open scenario uses, so the open tree cannot name a holdout topic.
- Seeds: 134 of the 171 scenarios are authored from the §4.12 coverage list (`seed.source = "program"`),
  24 come from the founder's real conversations in snapshot `reel-2026-10-23` (`live-turn`) and 13 from
  the assessment probes of lanes A, B, C and G (`assessment-probe`; ids in
  `eval/askreel/fd18-exclusions.json` for FD-18 and live deletion). Every scenario names its seed.
- Expectations are versioned by **snapshot id and model artifact id** (stamped in the scenario file and in
  every recording). Supply and the For-you bar are materialised from the eval database at run time, so a
  re-snapshot or a retrain re-baselines them instead of silently breaking them.

A turn's `expect` block may carry: `act`, `mode` (`Picks`, `Answer`, `Clarify`, `NoneFound`, `Any`),
`concept` (a truth key), `mustNot` (truth keys), `media` (`Movie`/`Show`, from the user's words),
`conflict` (bool), `references` (`[{turn, rank}]`, resolved against the display order the run actually
recorded), `rejectTurns` (critique) and `noRepeatTurns` (pagination).

## SQL truth

`eval/askreel/truth/<key>.sql` returns the catalogue titles satisfying one concept (TMDB keyword phrases
and the title name, a genre, a credit, a year window or a language). They read only the catalogue, never
the pipeline. `_eligible.sql` is an independent statement of the founder-locked eligibility rule plus the
account's maturity ceiling; `EligibilityTruthParityTests` pins it to `EligibilityQueryBuilder` (both its
persisted and its `FilterRuleSet` overload, each through `WithinMaturity`) on movies and shows, and
executes every truth file against the migrated schema. The For-you bar is not eligibility — the scoring
surfaces apply it — so it is measured per surface by the exclusion matrix (task 7), not here.

TMDB keyword tags are noisy (Batman Begins carries "ninja"), so a truth set bounds precision from above and
is **never a recall target** — keyword-defined recall is circular (program §1, C*4).

## Metrics

Pre-registered 2026-10-24 before the 99c0761 baseline. Every change made after any baseline data existed
is listed with its reason in `eval/askreel/gates.json` → `amendments`.

| Metric | Definition |
|---|---|
| Constraint precision@8 | Over turns with a concept and eligible supply > 0: share of the first 8 picks in the truth set and in no `mustNot` set; mean over turns. A failed or unrun turn scores 0 |
| Honest-empty compliance | Over turns whose concept has **zero eligible supply**: the turn ran, reported `NoneFound` and shows zero picks (a near-miss tray is allowed). A 500, a timeout or a clarify is not honest |
| Reference accuracy | The referenced card (by recorded display order) is what the turn resolved: v2 plan handles; v1 `debug.intent.referenceTitles`. A reference to a card an honest empty turn never showed is void; a reference turn that failed or never ran fails |
| Critique compliance | No title from the rejected turn(s) re-shown, and the turn is on topic (precision ≥ 0.75, or honest-empty when supply is zero) |
| Act classification | Observed act equals expected. **No data for v1**, which emits no act (turn position is not behaviour); a failed v2 turn fails |
| Conflict detection | The turn ran and its conflict flag equals the expectation (true for requests that collide with a standing exclusion, false for controls) |
| Grounding violations | Stub-family why-lines on picks (invented explanations shown as model output) + catalogue titles a reply names that are not on screen and not in the user's words |
| Echo | The reply quotes the user's message (“…”), or repeats a message of five or more words verbatim |
| Fallback rate | Turns that failed, carry a stub reply, a stub why-line, a typed fallback source (`Fallback`, `Stub`, or `Templater` on an in-request pick), or a `degraded` frame. `Generic` (the captionless filmography list) and `Templater` on an outside-request card (its deterministic exclusion caption) are by design |
| Exclusion leaks | Shown titles (picks and trays) outside `_eligible.sql` |
| Media filter violations | Picks against the request's own `mediaTypes` filter (gated). Picks against the user's words alone ("only movies") are reported as media mismatches, ungated |
| Hedging why-lines (ungated) | Why-lines that argue against their own pick, including the concession family ("though/but … less / rather than / a weaker match") |
| Non-Latin script (ungated) | English why-lines carrying another script ("film-first偏好") |
| Unrun turns | Turns a scenario never reached because an earlier turn was refused or died; scored as failed turns with every expected check failed |
| Latency | Client-measured turn time and first card (v1: first `candidate` frame; v2: first `picks` frame) and server `LatencyMs` (v2 adds `StageTimingsJson`). A failed or unrun turn is charged the 300 s cap. **Gated only from a run at concurrency ≤ 2 that held every build slot (`--hold-build-slots`)** |
| Cost per turn | The run's `ExternalApiUsages` delta priced by `eval/askreel/prices.json`, over turns attempted |

The absolute gates are §4.12's, in `eval/askreel/gates.json`.

## Gate policy (WS-7 task 10)

A change merges into `build/trust-v1` only with an eval report attached whose `compare` against the
current baseline shows **no regression**. `compare` first **refuses** (exit 2) two runs that are not the
same measurement: different scenario fingerprints (ids and queries), planned turn counts, snapshot or model
artifact, or a recording scored against definitions edited after it ran. Re-baseline instead.

| Metric | Tolerance before it is a regression |
|---|---|
| Honest-empty | none |
| Grounding violations, echo, exclusion leaks, media filter violations | none — counted **paired**, over the turns that ran in both runs, so a crash cannot hide one |
| Newly failed turns (ran in the baseline, failed now) | 1% of planned turns |
| Trakt calls | none |
| Constraint precision@8, reference accuracy | −0.02 |
| Critique, act, conflict | −0.03 |
| Fallback rate | +0.01 |
| Turn latency p50 / p95, first-card p95 (latency-valid runs only) | +15% / +20% / +20% |
| Cost per turn | +20% |

A metric the baseline measured and the run did not (n = 0) is a **regression**, never "no data"; a
metric the baseline never measured (v1's act) is reported and not gated. Tolerances exist because a
single live run still varies by a turn or two on the open set; they change only by a reviewed commit. The
absolute §4.12 gates are the G1 (open set) and G2 (holdout) bar, not a per-merge bar: v1 fails most of
them, which is the point of the baseline.

## The 99c0761 baselines — what each is good for

| Baseline | Status |
|---|---|
| Open set (`eval/askreel/baselines/99c0761/open/`) | **Valid quality baseline.** Re-scored by replay under the amended rules (act now no data; hedging 670). Paid with the founder's key |
| Latency (`…/99c0761/latency/`) | **Not a latency baseline.** It ran 18:51–19:12 UTC while other lanes were building (§5.0 rule 1, §7 rule 5) and without held build slots, so its latency gates now read "not gated". Re-run with `--hold-build-slots` on the eval key |
| Holdout (sealed) | Valid for the holdout **as it was** (scored against `scenarios-at-recording/`); the holdout has since been revised, so a fresh holdout baseline (one run, 64 turns) is owed before G2 |
| Surfaces (`eval/surfaces/baselines/99c0761/`) | Partial: digest returned no cards (no data), person pages and Watch tonight were not fetched, chained jobs ran mid-fetch and the artifact was not recorded (`provenance.md`). Re-run with the current harness on the eval key |

## The LLM judge and κ

The 0–3 "satisfies the request" judge and the 100-item founder labelling set are WS-7 task 5. Until the
founder's labels exist and Cohen's κ ≥ 0.6, **only the SQL truth gates** — the judge gates nothing.
That is the state today. The 45-minute and $1 full-run acceptance lines include the judge's ~2,000 calls,
so neither is proven yet: the 99c0761 runs (open 27.2 min + holdout 10.0 min, $0.47) measured the SQL-truth
half only.

## The surface harness

`scripts/eval/surfaces.mjs` measures every surface after one Fresh build (plus the BuildDigest job the
disabled weekly service would enqueue), and only after every chained job has drained; it records the
active artifact at the start and end of the fetch. Per surface it reports stub rate, exclusion leaks (same
`_eligible.sql`), cross-rail repeats on the feed page, the share of anchored and Similar cards with evidence
(≥ 2 shared TMDB keywords with the anchor, or embedding cosine above the p90 of 2,000 seeded random
eligible pairs on the same snapshot), hedging and non-Latin why-lines, and the watched-or-rated footprint
of Saved and the Watch-tonight pin (gated; typeahead and person pages show what you have seen by design
and are reported only). Every response's HTTP status is recorded: an error, or an empty suggestion
surface, is **no data** — listed at the top of the report, and the run exits 1 — never "0 leaks". Ask is
measured by the golden eval on the same truth. `eval/surfaces/gates.json` holds the task-8 targets and the
regression tolerances `--compare` applies (rates per suggestion card; a surface the baseline measured that
goes dark is a regression; different snapshots are refused). False copy claims wait for the claims audit
(task 9).

## Spend

§7's WS-7 row: per UTC day at most 2 full golden runs, 1 latency run, 6 smoke runs and 4 surface runs.
At the 99c0761 baseline a v1 turn cost about $0.002. The $5 / $8 lines are per key per day, and every lane
database shares one key today, so check the day with `ledger.mjs --all` (all eval and lane databases on
:5434, net of the snapshot template, plus the archives `snapshot.sh` writes before any DROP to
`~/src/fa.reel-ws/ledger-archive/`) and `ledger.mjs --openrouter-key <env file>` (the provider's own
figure, which also covers the live stack). On 2026-10-24 the shared key reported no spending limit set.
Paid calls outside a recording are listed in the lane report with their cause; on 2026-10-24 a smoke run
meant to make none paid 19 `rerank-page` calls ($0.011) because the API inherited an `OPENROUTER_API_KEY`
from the shell profile — the reason `run-api.sh` now launches from an empty environment.
