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
| Snapshot | `reel_eval_template` → `reel_eval` / lane slots | `scripts/eval/snapshot.sh` blanks every Trakt token (`''`: the app reads a non-empty token as a live connection, which armed the ratings back-fill on every clone), truncates the outbox, deletes pending jobs, push subscriptions, `AppSecrets` (the VAPID private key), `DataProtectionKeys` and `RefreshSessions`, archives the ledger of any database it is about to drop, and stamps the snapshot id (`COMMENT ON DATABASE`) |
| API | `127.0.0.1:5180` (compose project `fareel-eval`) or a slot via `scripts/eval/run-api.sh` | Every hosted service off; job runner off unless a surface run needs one Fresh build; Trakt blackholed (see below); eval-only Ask/rerank allowances (every Ask turn limit — 4 at once, 120 a minute, 5,000 an hour and a day on every tier — the same on both, set only by the `EVAL_ASK_TURNS_*` twins, never by a live `ASK_TURNS_*` in the env file or the caller's shell); refuses a dll with a `.env` in any parent folder, starts the API from an **empty environment** holding only the env file's variables and its own switches (never a key the caller's shell exports), exports every credential the env file leaves unset as empty, and refuses an env file with no `JWT_SECRET`. It boots the copy the way the merged code needs: from the dll's own folder (the content root, so `appsettings.json` loads), with `Database__AutoMigrateOnStartup=true` and no dump handshake, `Background__DataRepairs` and `Background__TrustPurge` off, Trakt tokens empty and `TRAKT_CLIENT_ID=eval-no-trakt`; it never runs on a `*_template` database (`--dry-run` lists the names the API would get, and the values of the switches it sets) |
| Model key | `EVAL_OPENROUTER_API_KEY` (its own $10/day cap) | **Not supplied yet.** The shared lane key in `~/src/labs.fa.reel-ws/lane.env` **is the founder's own OpenRouter key** (SHA-256 match with the live clone's `.env`, compared without printing either). Every paid call WS-7 has made — the three 99c0761 baselines, the surface baseline and the aborted attempts — went on it. `run-api.sh`, `askreel.mjs live` and `surfaces.mjs` now refuse that key unless the founder's sign-off is passed (`EVAL_FOUNDER_KEY_SIGNOFF` / `--founder-key-signoff`) and recorded in the recording |

**Trakt isolation, three layers.** (1) Inside the eval container Trakt's hosts resolve to `0.0.0.0`; for a
process slot, all outbound HTTPS goes through a dead loopback proxy except `openrouter.ai` and the TMDB
hosts. (2) No Trakt credentials are configured, and the dev-link session resolves the snapshot account by
slug, so nothing exchanges a token. (3) `TRAKT_CLIENT_ID=eval-no-trakt` replaces the Trakt adapter with
`OfflineTraktClient` (`Inf/Eval/`, wired by `DependencyInjection.Eval.cs`), which fails every call with the
`HttpRequestException` an unreachable Trakt produces before any socket opens, and logs each refusal as a
warning ("Trakt is offline on this stack"). It fails rather than answers because an empty watched list or a
silently accepted write would be read by ingestion and the outbox as truth. Layer 3 needs the one call line
in `AddReelInfrastructure` (WS-1's file), after `AddReelData()`; until that line lands, layers 1 and 2 are
the guarantee, and `IT/AskReelGolden/EvalTraktIsolationWiringTests` — which boots the whole app through
`Program` with the eval client id and drives a sign-in — fails, naming the missing line. The real
adapter records Trakt usage before the request is sent, so with layer 3 in place a Trakt row in a run's
`ExternalApiUsages` delta means a call got past the fake — an isolation failure the `traktCalls` gate fails.

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
scripts/eval/build-at.sh 99c0761 ~/src/labs.fa.reel-ws/ws7/.tmp/base        # prints the dll; writes BUILD_INFO.json
scripts/eval/run-api.sh --dll <dll> --db reel_eval --port 5180 --env-file <eval env: the eval key + TMDB>
#    or: docker compose -p fareel-eval -f docker-compose.eval.yml --env-file <eval env> up -d eval-api

# 3. Quality run (concurrency ≤ 4) and, separately, the latency run (concurrency ≤ 2, every build slot held).
node scripts/eval/askreel.mjs live --api http://127.0.0.1:5180 --db reel_eval --label <name> --engine v1|v2 \
     --api-env <eval env> --build-info <dir>/BUILD_INFO.json
node scripts/eval/askreel.mjs live ... --concurrency 2 --hold-build-slots --only-file eval/askreel/latency-set.txt --label <name>-latency

#    --engine is required and is ASKED FOR, not just recorded: every turn sends ?engine=v1|v2 (honoured for the
#    founder account dev/link mints), the recording's engine is the one each turn's stream names
#    (conversation.engine, done.engine), and the run stops at the first turn another engine — or no named
#    engine — answered, writes the recording marked INVALID and exits 1 (HREV2-ASK-05). Only a pre-§4.13 build
#    (99c0761), which names no engine and has no v2, takes --allow-unnamed-engine with --engine v1.
#    A turn the API's per-account Ask limiter refuses (409 turn_in_progress or 429 ask_rate_*, each with
#    retryAfterSeconds) never reached the pipeline: the run stops starting scenarios, writes NO recording and
#    exits 2 naming the turn (H3-PLAT-01); fix the slot's limits (run-api.sh or the compose stack set them)
#    and --resume, which re-asks every refused scenario.
# 4. Gate against the baseline: exit 1 on any regression, exit 2 when the runs are incomparable.
node scripts/eval/askreel.mjs compare .tmp/eval/<date>/<name>/metrics.json --baseline eval/askreel/baselines/99c0761/open/metrics.json

# Replay (CI, 0 paid calls) and scenario validation.
node --test 'scripts/eval/*.test.mjs'
node scripts/eval/askreel.mjs replay <recording.json>
node scripts/eval/askreel.mjs rematerialise <recording.json> --db <the db it ran on>   # adds only truth sets the
#    scenarios now need and the recording never froze; refuses when any frozen fact recomputes differently there.
#    [--replace key,key] recomputes ONLY the named keys, whose truth file was restated on purpose after the run
#    (every other frozen key must still match, which is what proves the db is in the recording's state)
node scripts/eval/askreel.mjs check [--db reel_eval]          # --db also executes every truth query used
node scripts/eval/askreel.mjs check --db reel_eval --api http://127.0.0.1:5180   # + the truth against the API's own eligibility (cost meter)

# Surface harness: one Fresh build (API started with --jobs), then every surface; compare against a baseline.
node scripts/eval/surfaces.mjs --api http://127.0.0.1:5180 --db reel_eval --label <name> --fresh --api-env <eval env> --build-info <...>
node scripts/eval/surfaces.mjs --replay <recording.json>
node scripts/eval/surfaces.mjs --compare <metrics.json> --baseline eval/surfaces/baselines/6968db5-keyless/metrics.json   # keyless run; the keyed 99c0761/ for a keyed one (gates.json → baselines)

# Zero-footprint audit of the exposure log (exit 1 on any exposure served after its lock started).
node scripts/eval/footprint-audit.mjs --db reel_eval [--since <ISO time>]

# Exposure-free perf (task 11): the built app (npm run build; VITE_API_TARGET=<api> npx vite preview --port 5207) on an
# eval-slot API. Stubs /feed/more, the digest, lens and Ask at the route layer and exits 1 if the account's exposure
# count moved. First hero card, LCP, CLS, load and long tasks, p50/p95 over cold runs.
node scripts/eval/perf.mjs --app http://127.0.0.1:5207 --api http://127.0.0.1:5187 --db <slot db> --runs 7

# Ledger cost check (§7): every eval and lane database together, and the provider's own figure for a key.
node scripts/eval/ledger.mjs --all
node scripts/eval/ledger.mjs --openrouter-key <env file>
```

Reports land in `.tmp/eval/<date>/<label>/` (`recording.json`, `metrics.json`, `report.md`, plus
`partial.jsonl` — a killed run resumes with `--resume`, re-running only scenarios that died on the
transport, and still reads the ledger from the day its first attempt started). A turn with no byte for
120 s or longer than 300 s is aborted and scored as failed (`client_timeout`), as a user giving up would.
Baselines that gate merges are promoted into `eval/askreel/baselines/` and `eval/surfaces/baselines/`; the
holdout's baseline lives with the holdout in `~/src/labs.fa.reel-eval-sealed/`.

**Replay has two halves.** The scorer replay (`node --test 'scripts/eval/*.test.mjs'`, `askreel.mjs
replay`) re-scores a frozen recording with no API, database or paid call, which proves the committed
metrics and every scoring rule; `make ci-replay` runs it. A recording whose answered turns name an engine other than the
one it asked for (or older recordings, whose header held the `--engine` flag) is **invalid**: its report
says so above the gates, every gate reads `INVALID`, replay exits 1 and `compare` refuses it (exit 2). The **engine replay** (§4.12) is
`IT/AskReelGolden/Replay/AskReelV2ReplayTests` (`[Trait("Suite", "Replay")]`): each fixture in
`eval/askreel/replay/v2/` is a small world (titles, standing conditions, footprint) and a conversation —
per turn the planner's plan, the judge's verdict per title and an item question's answer — driven through
`POST /api/search/ask` with `ASK_ENGINE=v2`, so the real executor, composer, eligibility, trays, state,
handles and persistence run deterministically with the model ports replaced by the recording. It checks
the golden eval's machine expectations: mode, exact or minimum picks, titles that must never appear (every
standing exclusion and footprint title in the world), no repeat of an earlier turn, the handle an answer
resolves, and a grounded, non-echoing reply. Four fixtures (facet + critique + item question, honest empty,
media switch, pagination past the 40-candidate window) run in about 10 s with 0 paid calls. The fixtures are
authored (`provenance` says so) until the eval key exists. It runs in the integration pass today;
`make ci-replay` running `--filter Suite=Replay` is a change request to WS-8.

## The scenario set

- `eval/askreel/scenarios/open.json` — the open set every workstream may read and run (120 scenarios,
  34 multi-turn, 167 turns).
- `~/src/labs.fa.reel-eval-sealed/askreel/holdout/holdout.json` — 30% held out (51 scenarios, 64 turns),
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
recorded), `rejectTurns` (critique), `noRepeatTurns` (pagination) and `years` — the year window the
turn's own words set, `{ "last": N }` (the last N years through the run's UTC year: "from the last ten
years" asked in 2026 is 2016–2026) or `{ "from", "to" }`. A turn with `years` is scored against its concept
INSIDE that window (truth key `<concept>@<from>-<to>`), for precision, honest-empty, missed supply and the
supply counts alike: the API counts a windowed request over the windowed population, and a standing rule
the window subsumes ("released 2000 or later" under 2016+) knocks out nothing there. An undated title
passes the window, as it passes the API's. Without `years` a "last ten years" request was held to every
year's heists (487 matching, MinYear 2000 "unlocks 36"), so a correct reply read as wrong supply counts
and missed supply (HREV3-EVAL-A, HREV3-RET-05). A recording made before a turn gained its window has no
windowed truth: the turn is reported as unverified (`truthMissing`), never scored against an empty set,
until `rematerialise` adds it from the database the recording ran on. A turn whose media scope the recording
never counted (a `media` expectation added after it ran) is unverified the same way (`truthMissing`
`<key>:<scope>`), never scored against another scope's supply.

`meant` (with its justification, `meantWhy`) names a narrower truth where the concept truth is the TAG
population a reply counts ("You have 1 unseen ninja show tagged that way, but none of the ones I checked is
really a match"). Supply counts stay held to the concept; whether the turn missed supply, or owed an honest
empty, is read from the meant truth. It exists because the app has two layers by design — retrieval counts
what the catalogue tags, the judge rules on what the request means — and one SQL truth cannot be both: held
to the tags, a judge that correctly rejects American Ninja Warrior for "ninja shows" reads as missed supply;
held to the meaning, a reply that correctly counts the tags reads as wrong counts. Precision is still scored
against the concept (an upper bound). Three exist, each with its SQL fixture test in `ConceptTruthParityTests`:
`ninja-meant` (ninja minus the Ninja Warrior competition format), `heist-meant` (tagged or named a heist,
never only a robbery; it errs narrow) and `dinosaur-meant` (dinosaur minus hand-labelled titles that carry the
tag for a passing sequence, each named in the file by TMDB id with its reason — today only The Tree of Life).
A hand label is added only for a title no film-literate reader would call the concept, never to clear a turn
the judge got wrong: crit-not-spy T2's residual in fix-golden-v2-2 (a tagged spy title the judge rejected) is
not unambiguous, so it has no label and stays missed supply.

A concept truth states the concept as the request's own words mean it, not as its nearest genre tag.
"Romantic comedies" (`conflict-romcom`) is `truth/romantic-comedy.sql`: romance AND comedy, each read the way
an exclusion of that genre reads it (the soap alias, romance's TV stand-in keywords and overview phrases,
generated from `GenreStandIns` like `_eligible.sql`'s lists) — the app's own reading, held to it by
`ConceptTruthParityTests`. Held to every romance title instead, the app's correct count (2,981 of 4,675 at
50 votes) failed the supply gate and was filed as an app defect (HREV4-RET-01).

## Truth restatements of fix-golden-v2-2 (2026-10-30)

Each is a correctness fix — what the request's words mean, or the app's reading where the app is right —
made after the second fix run, verified on the eval copy (`reel_int_fix2`) count for count against the
reply it had failed, and held by a parity or unit test. No bar moved.

| Scenario / truth | Change | Why |
|---|---|---|
| scorer | Missed supply subtracts clearing titles an earlier turn showed | The app may never repeat a title. Turns that had already shown every clearing title ("only series", "show me more", "half of these have no zombies in them") were called missed supply for answering honestly (`askreel-score.test.mjs`) |
| `media` (20 turns) | Added where the user's words name the medium ("heist movies", "french films", "crime series") and on follow-ups of such a turn | "Films"/"movies" mean movies; without it the turn was held to movies and shows together, and the app's correct movie counts read as wrong (crit-not-heists, crit-not-spy, crit-zombie-not, conflict-french-films, …) |
| `media` (6 turns) | Removed where the words name none ("westerns", "found footage horror", "kidnapping thrillers", "something like The Hurt Locker", "a tense heist thriller from the 2010s") | A media expectation comes from the user's words, the rule the app itself holds (`PlanSanitizer.UserMedia`); "kidnapping thrillers" counted 4 shows the eval wrongly excluded |
| refine-recent → `kidnapping-thriller.sql` | kidnapping AND thriller (alias suspense; TV stand-ins) | "Kidnapping thrillers" are thrillers about kidnapping; held to every kidnapping title the reply's 69 read as 90 (`ConceptTruthParityTests`) |
| conflict-animated-kids → `animated-for-kids.sql` | animation AND family (alias children), movies | "For kids" is the family genre; held to every animated title the reply's 1,247 read as 4,004 |
| space-isolation-chain T2/T3 | `years: {to: 1999}` | "Not that, older" after cards all from 2000 on (the account's standing floor; the oldest shown was 2000) means before 2000 — the app's reading, which honestly reports the collision with "released 2000 or later" |
| `space.sql` | The app's space synonym group from a word start, the name arm on "space" only | It left out the bare "space" label (219 titles) and "spacecraft" (104), so "space opera" was not space; the app's 110 / 27 read as 55 / 18 |
| `martial-arts.sql` | Name arm reads "martial arts" only, not "kung fu" | A synonym is a keyword claim (HREV3-RET-A, as `heist.sql`); four untagged "Kung Fu …" names made 523 read as 527 |
| fantasy-then-mind-benders T2 | `mustNot: genre-fantasy` removed | "Let's do mind benders instead" switches topic and rules nothing out; only the user's own words are negations (founder-locked) |
| ninja / heist / dinosaur turns | `meant: ninja-meant` / `heist-meant` / `dinosaur-meant` | See `meant` above |
| `genre-fantasy.sql`, `kidnapping.sql` | Moved to the sealed holdout's truth / deleted | No open scenario uses them any more (`check`) |

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

**Supply truth.** A none-found reply states counts ("Of the 70 ninja movies in the catalogue with at least
50 votes, you've seen 18 and your conditions hold out 52", "2 have no score from your model yet",
"turning off “English only” would unlock 4"). `eval/askreel/supply/supply.sql` is the eval's own count of
them over a concept's truth set: the same vote floor as the reply (the badge's source, Trakt first at 50+
votes, then TMDB, then Trakt; `CrowdVotes.MinRelaxVotes` = 50), the active artifact's scores, the For-you
bar at badge precision, and `supply/rules.sql` for which single rule holds each footprint-free title out
(a knockout). `check --db` fails when `rules.sql` and `_eligible.sql` disagree on any title;
`SupplyTruthParityTests` pins both files to `SupplyReporter` on fixtures that fill every bucket and trip
every relaxation from both sides, and pins the reply wording the eval parses (`NoneFoundCopy`).

**The ExcludeGenre rule includes the taxonomy gaps.** The API also excludes a title by an alias genre
(Trakt's `children` for family, `suspense` for thriller), by a composite of catalogue facts (`anime` is
animation whose original language is Japanese, on both media — WS-3's H-7; before it "no anime" removed
nothing), or by a stand-in keyword on a medium whose TMDB
taxonomy has no id for the genre (`GenreStandIns`: TV romance, horror, history, thriller and music; anime
and musical on both media). Both truth files carry that rule's DATA as generated `VALUES` lists between
`-- BEGIN/END GENERATED` markers; `StandInTruthSqlTests` fails when they drift from `GenreStandIns`, and
`REEL_REGENERATE_EVAL_SQL=1` rewrites them. The matching itself is hand-written and pinned by the parity
tests' stand-in fixtures. `check --db <db> --api <url of an API on that db>` then holds the truth to the
API's own eligibility on the real catalogue: the cost meter's eligible count and every saved rule's
marginal must equal the SQL's, ±0 (IREV3-SYS-02: without the stand-ins the truth counted 3,825 eligible
where the API counts 3,508, and nine marginals moved).

A truth set is written as a concept, deliberately, not as a copy of the pipeline's topic rule. Where the
two would differ on a real title the truth says why: `samurai.sql` matches "ronin" as a TMDB keyword (the
masterless-samurai tag) but never in a name, because "Ronin" (1998) is a crime thriller (IREV2-SYS-03).

## Metrics

Pre-registered 2026-10-24 before the 99c0761 baseline. Every change made after any baseline data existed
is listed with its reason in `eval/askreel/gates.json` → `amendments`.

| Metric | Definition |
|---|---|
| Constraint precision@8 | Over turns with a concept and eligible supply > 0: share of the first 8 picks in the truth set and in no `mustNot` set; mean over turns. A failed or unrun turn scores 0 |
| Honest-empty compliance | Over turns whose concept has **zero eligible supply**: the turn ran, reported `NoneFound` and shows zero picks (a near-miss tray is allowed). A 500, a timeout or a clarify is not honest |
| Reference accuracy | The referenced card (by recorded display order) is what the turn resolved: v2 plan handles; v1 `debug.intent.referenceTitles`. A reference to a card an honest empty turn never showed is void — and since it has no referent, the turn is then held to mode `Clarify` instead of its scripted act and mode (ninja-critique T3, HREV3-RET-05); a reference turn that failed or never ran fails |
| Critique compliance | No title from the rejected turn(s) re-shown, and the turn is on topic (precision ≥ 0.75, or honest-empty when supply is zero) |
| Act classification | Observed act equals expected. **No data for v1**, which emits no act (turn position is not behaviour); a failed v2 turn fails |
| Conflict detection | The turn ran and its conflict flag equals the expectation (true for requests that collide with a standing exclusion, false for controls) |
| Grounding violations | Stub-family why-lines on picks (invented explanations shown as model output) + catalogue titles a reply names that are not on screen and not in the user's words. A title is named only as a whole name with its own capitalisation, and only where no longer cited name covers the mention ("Blade of the 47 Ronin" names that film, not 47 Ronin); it is on screen when any card shown this turn or earlier has the same normalised name (the reply cannot say which of two equal names it meant); one name counts once |
| Echo | The reply quotes the user's message (“…”), or repeats a message of five or more words verbatim |
| Fallback rate | Turns that failed, carry a stub reply, a stub why-line, a typed fallback source (`Fallback`, `Stub`, or `Templater` on an in-request pick), or a `degraded` frame. `Generic` (the captionless filmography list) and `Templater` on an outside-request card (its deterministic exclusion caption) are by design |
| Missed supply | Turns with a concept that show no pick while the truth (its `meant` truth when it has one) holds eligible titles clearing the For-you bar that the conversation has NOT already shown (gated at 0). The app never repeats a title, so a clearing title an earlier turn showed — as a pick or in any tray — is not supply a later turn can offer; the recording freezes which shown titles clear (`clearingShown`), and an older recording infers it from earlier picks and unchecked cards only (both clear the bar by construction), so the inference can only under-subtract. Eligible titles with none left clearing the bar make the turn an honest-empty check instead |
| Supply count accuracy | Over none-found turns that state supply counts: every count in `done.supply` and every count the **reply's words** state (matching, seen, saved or dismissed, held out, under the bar, unscored, each named relaxation, and that no unnamed relaxation unlocks more than a named one) equals the supply truth at the stated vote floor, ±0. A reply whose report says a partition or an unscored count was due, but which states none the parser reads, fails. Gated at 100% |
| Exclusion leaks | Shown titles (picks and every tray: under the bar, near misses, unchecked) outside `_eligible.sql` |
| Ruled-out titles | Shown titles (picks and every tray) in a turn's `mustNot` truth (gated at 0) |
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
| Surfaces (`eval/surfaces/baselines/99c0761/`) | Partial, keyed (founder's key): digest returned no cards (no data), person pages and Watch tonight were not fetched, chained jobs ran mid-fetch, the artifact was not recorded (`provenance.md`), and it kept only `whyThis`, so the lens copy and hooks users read are unmeasured. Re-scored under the 2026-10-25 rules (stub 0.212). Re-run with the current harness on the eval key |
| Surfaces, keyless (`eval/surfaces/baselines/99c0761-keyless/`) | The pre-program keyless reference, kept for what the re-baseline was measured against. Current harness: raw copy fields, every surface, jobs drained, input artifact recorded. No model call ran, so its why-lines are the templater's and the stored lens copy (`provenance.md`) |
| Surfaces, keyless (`eval/surfaces/baselines/9f42c9d-keyless/`) | The keyless reference for round 3 (IR2S-04), superseded by `224eea0-keyless` because it seeded no digest arrivals. Same snapshot and input artifact. Its lower anchored and Similar volume is the evidence gate (3 of 99c0761's 126 anchored cards pass it; all 12 of these do). Its stub rate is owned by WS-5, not waived: the absolute stub gate still fails (`provenance.md`) |
| Surfaces, keyless (`eval/surfaces/baselines/224eea0-keyless/`) | The wave-A reference (IREV3S-02): the first baseline with seeded digest arrivals (`eval/surfaces/seed-arrivals.sql`), so the digest is measured (10 cards) instead of empty. The keyed `99c0761/` seeded nothing, so `--compare` refuses it against a seeded run until the keyed re-run on the eval key |
| Surfaces, keyless (`eval/surfaces/baselines/20429ff-keyless/`) | The wave-B reference: the wave-A merge (API sources = `20429ff`), the first recording (v4) with the uncitable why-line and false-copy-claim metrics. Superseded by `6968db5-keyless`: it measured no shared people and served 22 anchored cards, so `--compare` refuses it against a v5 run on the evidence rule (`provenance.md`) |
| Surfaces, keyless (`eval/surfaces/baselines/6968db5-keyless/`) | **The reference for keyless runs** since hardening round 9 (HREV8-RET-04): `build/trust-v1` @ `6968db54`, the first recording (v5) that counts a person credited on both anchor and card as evidence. 50 anchored cards, evidence share 0.98 (Hokum → Factotum, one keyword under the cosine bar, still fails); every absolute gate passes. Reproduced on a second fresh clone (`provenance.md`) |

## The LLM judge and κ

`scripts/eval/judge.mjs` (WS-7 task 5). Until the founder's labels exist and the judge agrees with them at
Cohen's κ ≥ 0.6, **only the SQL truth gates** — the judge gates nothing. That is the state today: the
labels are not in, and no judge call has been made (no eval key).

- **The judge.** `judge.mjs run --recording <r> --db <db> --judge-env <env file>` asks
  `openai/gpt-6-luna` at reasoning effort none, temperature 0, structured output: one call per card in each
  turn's top 8 ("does this title satisfy the request", 0–3, UMBRELA-style — one passage per call, so cards
  never anchor each other) and one per reply ("is the reply grounded in its cards"). The card is described
  by the catalogue's own facts, never by the system's why-line. It refuses the founder's key without his
  recorded sign-off, stops at the first 401/402/403 (§7 rule 4), and records usage and length stops.
- **The prompt is sealed** in `~/src/labs.fa.reel-eval-sealed/askreel/judge/prompt.md` (two sections,
  `## satisfies` and `## grounded`); the runner refuses a prompt inside a worktree and records only its
  SHA-256, so the engine under test cannot be tuned to the judge's wording.
- **The labels.** `eval/askreel/labels/founder-100.csv`: 100 cards from 100 different turns of the 99c0761
  open baseline, drawn by `judge.mjs items --seed 42`, with the scale and instructions in
  `eval/askreel/labels/README.md`, followed by WS-3's 20 ninja films as yes/no confirm rows (`--confirm`;
  κ reads only the 100 `satisfies` rows).
- **κ.** `judge.mjs kappa --labels … --judge …`: unweighted Cohen's κ on the 0–3 scale is the
  pre-registered gate (`gates.json` → `judge`, registered before any label or verdict existed), with the
  quadratic-weighted and relevant/not-relevant κ beside it. It exits 0 only at κ ≥ 0.6 with every labelled
  item judged; then `judge.mjs compare --judge <run> --baseline <baseline judge> --kappa <kappa.json>` holds `judge.relevantShare` (−0.02) and `judge.ungroundedReplies` (+0) against the baseline, and refuses runs judged with a different prompt or model.

The 45-minute and $1 full-run acceptance lines include the judge's ~2,000 calls, so neither is proven yet:
the 99c0761 runs (open 27.2 min + holdout 10.0 min, $0.47) measured the SQL-truth half only.

## The surface harness

`scripts/eval/surfaces.mjs` measures every surface after one Fresh build (plus the BuildDigest job the
disabled weekly service would enqueue), and only after every chained job has drained; it records the
active artifact at the start and end of the fetch. Per surface it reports stub rate, exclusion leaks (same
`_eligible.sql`), cross-rail repeats on the feed page, the share of anchored and Similar cards with evidence
(a person credited on both in the same capacity — director, writer or top-billed cast, as `AnchorRowGate`
reads credits — or ≥ 2 shared TMDB keywords with the anchor, or embedding cosine above the p90 of 2,000
seeded random eligible pairs on the same snapshot; recordings before version 5 measured no people, and
`--compare` refuses to compare runs scored on different evidence rules), hedging and non-Latin why-lines, card volume, and the watched-or-rated footprint
of Saved and the Watch-tonight pin (gated; typeahead and person pages show what you have seen by design
and are reported only). Every response's HTTP status is recorded: an error, or an empty suggestion
surface, is **no data** — listed at the top of the report, and the run exits 1 — never "0 leaks". Ask is
measured by the golden eval on the same truth. `eval/surfaces/gates.json` holds the task-8 targets and the
regression tolerances `--compare` applies (rates per suggestion card; a surface the baseline measured that
goes dark is a regression; each suggestion surface, and anchored cards, must keep at least half the
baseline's cards; different snapshots, or Fresh builds that started from different artifacts, are refused).
Since recording version 4 it also reports **uncitable why-lines** and **false copy claims** (both below);
recordings before it score `null` on them ("no data"), never zero.

**Uncitable why-lines (WS-5's IREV3S-01 rule).** `ExplanationTemplater.IsCitable` lets a clause cite only
one of the card's two largest positive pushes, or one of at least 0.15. The recording freezes each
suggestion card's stored contributions (`TitleScores.ContributionsJson` for the artifact serving the fetch),
and the scorer reads every rendered `whyThis` through the phrase catalogue in
`eval/surfaces/citable-clauses.json`: a clause none of whose features is citable is counted (lens clauses
and hooks are model copy and are not checked). `CitableClauseCatalogueTests` generates the catalogue's rule
constants, a rendered sample of every `Translate` phrasing (alone and behind each kind of anchor clause)
and a set of IsCitable vectors from the templater itself, and fails when a phrasing is read as the wrong
feature, as two, or as none; `surfaces-score.test.mjs` replays the same samples and vectors through the
JavaScript twin. Stored contributions are rounded to 3 decimals, so a push within 0.0005 of the floor is
given the benefit of the doubt.

**False copy claims (task 9).** See *The claims audit* below; the harness records the audit of the tree it
runs from and gates `falseCopyClaims` and `unregisteredCopyClaims` at 0.

**Copy is scored as rendered.** The recording keeps every copy field raw (`whyThis`, `lensClause`, `hook`,
the pin's `reason`) and the component that renders each block; the scorer derives the string on screen:
the hero's hook, else its `whyThis` (`RecCardHero` since `95aab1f`; at `99c0761` the hook was followed by
the lens clause, and the harness reads the measured build's own `RecCardHero` to tell which), a rail card's
`lensClause ?? whyThis` (`RecCard`: feed rows, rails, Similar, the digest), and the Watch-tonight reason.
Saved grid cards, person pages and typeahead carry no why-line. Scoring `whyThis` alone missed the lens
copy's hedges (8 of 21 feed cards carried a lens clause in the P0 review, 3 hedging). The stub family
includes `GenericFallback` ("Strong fit with your taste profile."), alone or after an anchor clause (the
report counts each kind: the bare line, which the feed allows once per served page, the composed tail, and
the old "aligned with your taste" template — none is exempt, because a fallback card shows the templated
anchored line or nothing), and
the hedge family includes comparisons against the account's own taste ("…slower … than the action you
often prefer") and departure framing ("detour", "change of pace"). Both families are shared with the
golden eval, and were calibrated on the snapshot's 316 lens copies and stored why-lines, each hit read by
hand.

**Digest arrivals are seeded (IREV3S-02).** The digest announces offers that arrived in the trailing
week (`TitleAvailabilities.FirstSeenAt`), and a snapshot carries none, so `--fresh` first runs
`eval/surfaces/seed-arrivals.sql` — the 60 most popular titles' streaming-now offers in the account's
region stamped as arrived yesterday, eligible or not, so the digest's own filter is what the leak check
measures — after the chained jobs drain and before the digest build. How arrivals were made is part of a
recording (`digestSeed`) and a comparability key: `--compare` refuses runs seeded differently.

## The claims audit (WS-7 task 9)

`node scripts/eval/claims.mjs check | report | unregistered`. `scripts/eval/claims-extract.mjs` sweeps every
string a user can read — the frontend through the TypeScript compiler (JSX text, string and template
literals; tests and fixtures excluded) and the backend's user-facing C# literals (logging, exceptions,
regexes, SQL and routing statements excluded) — and keeps the **claim-shaped** ones: prose that asserts
what the product does (absolutes, automatic behaviour, sync and privacy, money, timing such as "tonight",
the product's "for you" vocabulary). `eval/claims/claims.json` is the register:

- every extracted string is registered, either in a **claim family** or as `notClaims` with a reason
  (a heading, a button label, a job progress label);
- a family names its **owner** (the workstream that owns the behaviour), a **verdict** checked against the
  code — `true` (pinned by the tests it names), `false` (a defect: the family says the fix) or
  `unverified` (the sweep could not establish it; the owner owes the test) — its evidence, and tests that
  must exist (`path::test name`);
- `check` fails on unregistered copy, on registered text that left its file (an edited claim is
  re-audited), and on a missing owner, verdict, defect or test. It does not fail on a false or unverified
  claim: those are owned defects, ratcheted like the exclusion matrix's leaks and counted by the surface
  harness.

`claims.test.mjs` (in the replay suite) runs the check on the tree, so new claim-shaped copy is audited
before it merges. `IT/ClaimsAudit/ClaimsAuditTests` pins the claims no owner's test covered (the push
port's only consumer is the weekly digest; nothing can send an email; the scoring model never reads the
taste narrative; the retrain threshold the status reports is the one the nightly gate applies). A pin on a
false claim fails when the owner's fix lands, and is then deleted in favour of the owner's own test (the
top-bar lookup's maturity leak and Ask's "no standing conditions" answer went that way in R1-13). The first
audit (build/trust-v1 @ 20429ff): 268 claim-shaped strings; 194 claims in 36 families — 123 true, 15 false
(9 families), 56 unverified (6 families); 78 not claims. One false claim was found by the harness rather
than by reading: `askreel.mjs check --db --api` showed WS-3's cost meter counting 199 unrated titles as
clearing the crowd floor, so "These rules leave N of M titles" overstates N
(`EligibilityTruthParityTests` ledgered it until WS-3's R1-06 fix, and now holds the meter to the truth,
unrated titles included).

The hardening re-audit (build/trust-v1 @ 4394aad, 2026-10-27): 280 claim-shaped strings; 202 claims in 39
families — 133 true, 13 false (10 families), 56 unverified; 81 not claims. `exclusions-are-airtight` is now
true (the matrix holds on all 485 cells with `MatrixLedger` empty); three new false families came from
wave-B copy: `top-bar-search-hides-and-counts` (WS-3), `ask-reel-no-conditions-means-everything` (WS-2), and
Ask's "They leave N unseen titles" joining `cost-meter-counts` (WS-3).

The post-merge re-audit (R1-13, build/trust-v1 @ 4394aad with the hardening round's eleven lane branches
merged, 2026-10-27): 285 claim-shaped strings; 207 claims in 41 families — 155 true, 0 false, 52 unverified
(6 families); 81 not claims. All ten false families were fixed by their owners and re-audited from the new
copy: `top-bar-search-hides-and-counts` (WS-3 R1-07), `ask-reel-conditions-answer` (was
`ask-reel-no-conditions-means-everything`, WS-2 R1-12), `cost-meter-counts` (WS-3 R1-06), `free-tier-upsell`
and `movie-rating-logs-a-play` (WS-10A R1-40), `verdict-goes-into-the-next-retrain` (WS-10A R1-40),
`first-model-waits-for-the-floor`, `pick-reasons-when-worth-naming` and `narrative-read-by-the-written-passes`
(WS-10B R1-43); `ask-reel-permanently-excludes` is retired (WS-2 R1-29: the notice no longer says
"permanently"). The track record's "same numbers the engine is judged on internally" is gone (WS-10B R1-47):
its remaining copy splits into `track-record-numbers-are-raw-pairs` (true) and `track-record-evidence-base`
(unverified). Two new families: `day-turns-over-in-your-zone` and `known-people-are-from-your-ratings`.

## The exclusion matrix (WS-7 task 7)

`Tests/IntegrationTests/ExclusionMatrix` holds every standing exclusion — genre (and its TV stand-in),
keyword, language, crowd floor, the For-you bar, and five kinds of footprint (watched, rated, not
interested, dropped, saved) — for movies and TV against every surface: tonight's picks, feed rows and their
pages, genre rails, Similar, the digest, Ask, typeahead, person pages, Saved and the Watch-tonight pin;
each both **standing** (before the build) and **late** (added after it, no rebuild). It runs in the
ordinary integration pass (the `postgres` collection) in about two minutes.

- **A world of its own.** One small catalogue in a database the class creates and drops, because ranked
  surfaces on the shared test database rank every other class's leftovers. Accounts come in pairs — a
  guarded account carrying the exclusion and a control twin with the same history and none — and every
  surface is fetched through the real API host with offline TMDB and the stub model.
- **A cell counts only when it was exercised**: the control was shown the violator on that surface. An
  unexercised cell fails. Focus pairs (the pool narrowed by watching everything else, with decoys above it
  for the rows) exercise the capped surfaces; pin pairs (a queue of one title) exercise the pin; a film
  carrying a horror stand-in must stay visible (films have the horror id).
- **The contract** is in `MatrixCells`: suggestion surfaces hold out everything; lookups (typeahead,
  person pages) hold out every content rule but may show what you have seen; Saved holds out content rules
  and consumed saves and must show what you saved; the pin is never a ruled-out or consumed title, and
  under the bar its reason says so. The bar is held at badge precision.
- **Leaks are ratcheted** in `MatrixLedger` with their defect and owner. A ledgered cell that stops
  leaking fails, so the ledger can only shrink as owners land fixes. XM-01…07 are all closed (wave B), so
  the ledger is empty and every cell must hold. The matrix reads the typeahead's local leg only; the
  global TMDB leg is covered by `TypeaheadSearchTests` and the claims audit.

**The timestamp-aware audit.** `node scripts/eval/footprint-audit.mjs --db <eval db> [--since <ISO>]`
lists every exposure in the append-only log served after its title entered the footprint or a standing
exclusion, per surface and reason, with the share more than a day late and the share from FD-18 probe
conversations (`eval/surfaces/footprint-audit.sql`; `FootprintAuditSqlTests` pins it). It exits 1 on any;
with `--since <a harness run's start>` it gates that run's own exposures. Rules are judged as they are
defined today (stand-ins included), and the maturity ceiling and deleted rules are not audited, because
nothing records when they started.

## The synthetic week (R1-36)

WS-4, WS-5 and WS-6 each carry "over 7 days" acceptance rows that §5.2 says run "on the eval stack under
a replayed synthetic week". The week runs in about half an hour, with zero paid calls and zero network:

1. `scripts/eval/snapshot.sh clone reel_eval_template reel_ws7_week` — a clone is spent by one run.
2. `REEL_SYNTHETIC_WEEK_DB=reel_ws7_week dotnet test --filter FullyQualifiedName~SyntheticWeekRun` (with
   the usual `REEL_TEST_CONNECTION_STRING`, inside `build-slot` and `caffeinate -i`, nothing else
   building: the queue waits are wall-clock measurements). `REEL_SYNTHETIC_WEEK_TICKS=3` is a smoke run.
3. `node scripts/eval/week.mjs --db reel_ws7_week --run .tmp/eval/week/<run>` — the acceptance SQL,
   bounded to the week by the run's `since`; exits 1 on a failed gate. The same queries, with
   `since = now() − 7 days`, are the post-deploy monitoring set.

**How a week passes.** 28 six-hour ticks. Each tick the founder account does what that part of a day
holds (a morning refresh, Home and Saved loads; a save and a not-interested; a Trakt watch-and-rate of a saved movie, an
in-app rating and a Trakt watchlist add; two quiet days with no ratings at all), the delta poll's own
scheduling pass runs, and the real job runner and outbox dispatcher work the queue on wall time until it
is idle. Then `DatabaseClock` ages the whole database six hours: every stored timestamp of every base
table moves back by the same interval, so code that reads `DateTime.UtcNow` sees six hours gone. A unique
index over a timestamp (`ExposureEvents`' minute bucket) is shifted in two steps, because a one-step shift
moves a row onto a bucket another row has not left yet (`DatabaseClockTests` fails one-step). Generated
columns and views move with their sources; `TitleEmbeddings.CreatedAt` and timestamps inside JSON stay put.
The Friday tick runs the weekly digest's scheduling pass; boot repairs run once at the start, as a deploy
runs them.

**The fakes.** Every model adapter runs in stub mode (`ReelApiFactory`). `WeekTraktClient` serves the
week's own items behind a `last_activities` whose stamps move; every ingest a delta runs is an upsert, so
older history stays as the snapshot left it, and a show snapshot (whose ingest reconciles progress away)
is refused loudly. Reel's own writes bump the stamps they would bump on Trakt. A catalogue title with no
Trakt id gets `9,000,000,000 + tmdbId`. `WeekTmdbClient` answers catalogue reads empty and a
watch-provider lookup with the offers the clone already holds — empty would make `AvailabilitySnapshot`
delete every refreshed title's offers.

**Not in the week.** The nightly reconcile: its pass is inline in `NightlyReconcileService.ExecuteAsync`
behind a wall-clock wait for 03:00 UTC, with no seam to call, so no nightly retrain, forced delta or
enrichment sweep runs, and the rows that need them report "no data" rather than passing. That seam is
owed to WS-5.

**First full run (2026-10-27, ws/ws7 after build/trust-v1 @ 4394aad, snapshot reel-2026-10-23).** 28 ticks
in 11.5 minutes, 0 paid calls, every Trakt call in-process; 24 BuildFeed, 27 DeltaSync, 24
RefreshAvailability, 17 PersonaliseTitles, 7 CrossReference, 6 BuildTasteNarrative and 1 BuildDigest, none
failed. All WS-6 rows pass (DeltaSync wait p95 1.9 s, BuildFeed run p95 18.7 s, longest cross-reference
attempt 46 s, no attempt over 3, no orphaned Failed, no `@p` in an error). WS-4 passes every row but
availability freshness: 30 of 37 saves (0.81) have offers fetched this week and none are stale — the other
7 have no offer row at all, which `AvailabilitySnapshot` cannot tell from never looked at. WS-5: no Unknown
why-source, no zero base score, no banned or decade why-lines, shortlist p50 moves at most 0.02 between
builds with no rating between them; but it fell 7.18 → 5.22 across two rating days whose ratings were all
at or above the model's prediction, and from day 4 every build said nothing cleared the founder's 6.0 bar
(hero-eligible 9–10 → 0), so the anchor share has no sample. Retrains: no data (no nightly).

## Spend

§7's WS-7 row: per UTC day at most 2 full golden runs, 1 latency run, 6 smoke runs and 4 surface runs.
At the 99c0761 baseline a v1 turn cost about $0.002. The $5 / $8 lines are per key per day, and every lane
database shares one key today, so check the day with `ledger.mjs --all` (all eval and lane databases on
:5434, net of the snapshot template, plus the archives `snapshot.sh` writes before any DROP to
`~/src/labs.fa.reel-ws/ledger-archive/`) and `ledger.mjs --openrouter-key <env file>` (the provider's own
figure, which also covers the live stack). On 2026-10-24 the shared key reported no spending limit set.
Paid calls outside a recording are listed in the lane report with their cause; on 2026-10-24 a smoke run
meant to make none paid 19 `rerank-page` calls ($0.011) because the API inherited an `OPENROUTER_API_KEY`
from the shell profile — the reason `run-api.sh` now launches from an empty environment.
