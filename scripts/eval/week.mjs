#!/usr/bin/env node
// The synthetic week's acceptance (R1-36; program §5.2 WS-4, WS-5 and WS-6 "over 7 days" rows).
//
//   node scripts/eval/week.mjs --db <eval db> --run <.tmp/eval/week/<run> dir> [--out <report.md>]
//
// The week itself is driven by SyntheticWeekRun (Tests/IntegrationTests/SyntheticWeek; opt-in through
// REEL_SYNTHETIC_WEEK_DB) on a cloned eval database. Its run.json carries `since`, the earliest instant a
// job of the week can carry once the database has been aged, so every query below is bounded to the
// week's own rows and never the snapshot's history. The same queries, with `since` = now() − 7 days, are
// the post-deploy monitoring set for the hand-off.
//
// Exits 1 when a gated row fails, 0 otherwise; a row whose input the week did not produce (no BuildFeed,
// no nightly retrain) reports "no data" and gates nothing — a missing measurement is listed, never passed.
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";

const WEEK = `"EnqueuedAt" >= :'since'::timestamptz`;
const SNAPSHOTS = `select "Id" from "FeedSnapshots" where "AccountId" = :'account' and "GeneratedAt" >= :'since'::timestamptz`;

// Each check: the workstream whose acceptance row it is, the row as the program states it, one SQL that
// returns ONE number (null = no data), and the gate on that number.
export const CHECKS = [
  { id: "ws6-attempts-over-3", owner: "WS-6", label: "Jobs with AttemptCount > 3", op: "==", target: 0,
    sql: `select count(*) from "SyncJobs" where ${WEEK} and "AttemptCount" > 3` },
  { id: "ws6-failed-without-completion", owner: "WS-6", label: "Failed jobs with NULL CompletedAt", op: "==", target: 0,
    sql: `select count(*) from "SyncJobs" where ${WEEK} and "Status" = 'Failed' and "CompletedAt" is null` },
  { id: "ws6-parameter-names-in-errors", owner: "WS-6", label: "Job errors leaking a SQL parameter name (Error LIKE '%@p%')", op: "==", target: 0,
    sql: `select count(*) from "SyncJobs" where ${WEEK} and "Error" like '%@p%'` },
  { id: "ws6-delta-wait-p95", owner: "WS-6", label: "DeltaSync queue wait p95 (s)", op: "<", target: 30,
    sql: `select round(percentile_cont(0.95) within group (order by extract(epoch from "StartedAt" - "EnqueuedAt"))::numeric, 1) from "SyncJobs" where ${WEEK} and "Kind" = 'DeltaSync' and "StartedAt" is not null` },
  { id: "ws6-buildfeed-run-p95", owner: "WS-6", label: "BuildFeed run p95 (s)", op: "<", target: 120,
    sql: `select round(percentile_cont(0.95) within group (order by extract(epoch from "CompletedAt" - "StartedAt"))::numeric, 1) from "SyncJobs" where ${WEEK} and "Kind" = 'BuildFeed' and "Status" = 'Succeeded'` },
  { id: "ws6-crossref-attempt-max", owner: "WS-6", label: "Longest cross-reference attempt (s), inside its 12 min deadline plus the 2 min overrun grace", op: "<=", target: 840,
    sql: `select round(max(extract(epoch from "CompletedAt" - "StartedAt"))::numeric, 1) from "SyncJobs" where ${WEEK} and "Kind" = 'CrossReference' and "CompletedAt" is not null` },

  { id: "ws4-consumed-active-saves", owner: "WS-4", label: "Active saves that are fully watched or rated", op: "==", target: 0,
    sql: `select count(*) from "UserTitleReactions" r where r."AccountId" = :'account' and r."Kind" = 'SaveForLater' and r."RevokedAt" is null
            and (exists (select 1 from "WatchedTitles" w where w."AccountId" = r."AccountId" and w."TitleId" = r."TitleId" and w."IsFullyWatched")
              or exists (select 1 from "UserRatings" u where u."AccountId" = r."AccountId" and u."TitleId" = r."TitleId"))` },
  { id: "ws4-failed-attributes-with-raw", owner: "WS-4", label: "Failed attribute rows with non-null RawJson", op: "==", target: 0,
    sql: `select count(*) from "TitleAttributes" where "Status" = 'Failed' and "RawJson" is not null` },
  { id: "ws4-failed-attribute-share", owner: "WS-4", label: "Failed attribute rows as a share of Done", op: "<", target: 0.01,
    sql: `select case when count(*) filter (where "Status" = 'Done') = 0 then null
                 else round(count(*) filter (where "Status" = 'Failed')::numeric / count(*) filter (where "Status" = 'Done'), 4) end from "TitleAttributes"` },
  { id: "ws4-overlapping-enrich-runs", owner: "WS-4", label: "Overlapping EnrichCatalog runs", op: "==", target: 0,
    sql: `select count(*) from "SyncJobs" a join "SyncJobs" b on a."Id" < b."Id"
            where a."Kind" = 'EnrichCatalog' and b."Kind" = 'EnrichCatalog' and a."EnqueuedAt" >= :'since'::timestamptz and b."EnqueuedAt" >= :'since'::timestamptz
              and a."StartedAt" is not null and b."StartedAt" is not null
              and a."StartedAt" < coalesce(b."CompletedAt", now()) and b."StartedAt" < coalesce(a."CompletedAt", now())` },
  { id: "ws4-saves-with-fresh-availability", owner: "WS-4", label: "Active saves with availability data under 7 days old (share)", op: ">=", target: 0.9,
    sql: `select case when count(*) = 0 then null else round(avg(case when exists (
              select 1 from "TitleAvailabilities" v join "Accounts" a on a."Id" = r."AccountId"
              where v."TitleId" = r."TitleId" and v."Region" = a."Region" and v."FetchedAt" >= now() - interval '7 days') then 1.0 else 0.0 end), 3) end
            from "UserTitleReactions" r where r."AccountId" = :'account' and r."Kind" = 'SaveForLater' and r."RevokedAt" is null` },
  { id: "ws4-pending-list-adds", owner: "WS-4", label: "Pending ListAdd outbox rows at the end of the week (each must show as 'syncing…')", op: "info",
    sql: `select count(*) from "TraktOutbox" where "AccountId" = :'account' and "Kind" = 'ListAdd' and "Status" = 'Pending'` },

  { id: "ws5-unknown-why-source", owner: "WS-5", label: "Exposures with WhySource = Unknown", op: "==", target: 0,
    sql: `select count(*) from "ExposureEvents" where "AccountId" = :'account' and "OccurredAt" >= :'since'::timestamptz and "WhySource" = 'Unknown'` },
  { id: "ws5-zero-base-score", owner: "WS-5", label: "Exposures with BaseScore = 0 and a non-zero prediction", op: "==", target: 0,
    sql: `select count(*) from "ExposureEvents" where "AccountId" = :'account' and "OccurredAt" >= :'since'::timestamptz and "BaseScore" = 0 and "PredictedRating" <> 0` },
  { id: "ws5-banned-why-shapes", owner: "WS-5", label: "Why-lines saying 'aligned with your taste' or starting 'A <vowel>'", op: "==", target: 0,
    sql: `select count(*) from "FeedItems" where "FeedSnapshotId" in (${SNAPSHOTS}) and ("WhyThisSentence" ilike '%aligned with your taste%' or "WhyThisSentence" ~ '^A [aeiou] ')` },
  { id: "ws5-decade-why-share", owner: "WS-5", label: "Why-lines naming a decade or 'your era' (share)", op: "<", target: 0.1,
    sql: `select case when count(*) = 0 then null else round(avg(case when "WhyThisSentence" ~* '(decade|your era)' then 1.0 else 0.0 end), 3) end
            from "FeedItems" where "FeedSnapshotId" in (${SNAPSHOTS}) and "WhyThisSentence" is not null` },
  { id: "ws5-anchors-from-9-10", owner: "WS-5", label: "Anchored-row anchors the account rated 9–10 (share)", op: ">=", target: 0.8,
    sql: `select case when count(*) = 0 then null else round(avg(case when u."Rating" >= 9 then 1.0 else 0.0 end), 3) end
            from (select distinct "FeedSnapshotId", "AnchorTitleId" from "FeedItems" where "FeedSnapshotId" in (${SNAPSHOTS}) and "AnchorTitleId" is not null) i
            left join "UserRatings" u on u."AccountId" = :'account' and u."TitleId" = i."AnchorTitleId" and u."SubjectType" in ('Movie', 'Show')` },
  // §5.2 WS-5: "with no new ratings, shortlist p50 varies by under 0.3 across builds of one artifact". So
  // only consecutive builds of one artifact with no rating event between them are compared; the move
  // across a rating is reported beside it, because a rating is allowed to move the shortlist.
  { id: "ws5-anchor-count", owner: "WS-5", label: "Distinct anchors on the week's anchored rows (the 9–10 share's sample)", op: "info",
    sql: `select count(distinct "AnchorTitleId") from "FeedItems" where "FeedSnapshotId" in (${SNAPSHOTS}) and "AnchorTitleId" is not null` },
  { id: "ws4-saves-without-any-offer-row", owner: "WS-4", label: "Active saves with no offer row at all (a lookup that found nothing leaves no trace, so their freshness is unmeasurable)", op: "info",
    sql: `select count(*) from "UserTitleReactions" r join "Accounts" a on a."Id" = r."AccountId" where r."AccountId" = :'account' and r."Kind" = 'SaveForLater' and r."RevokedAt" is null
            and not exists (select 1 from "TitleAvailabilities" v where v."TitleId" = r."TitleId" and v."Region" = a."Region")` },
  { id: "ws5-shortlist-p50-spread", owner: "WS-5", label: "Largest shortlist p50 move between builds of one artifact with no rating between them", op: "<", target: 0.3,
    sql: `with b as (select "BuiltAt", "ShortlistP50" as p50, lag("BuiltAt") over w as prev_at, lag("ShortlistP50") over w as prev_p50
                       from "FeedBuildStats" where "AccountId" = :'account' and "BuiltAt" >= :'since'::timestamptz and "ShortlistP50" is not null
                       window w as (partition by "ModelArtifactId" order by "BuiltAt"))
          select max(abs(p50 - prev_p50)) from b where prev_at is not null
            and not exists (select 1 from "UserRatingEvents" e where e."AccountId" = :'account' and e."ObservedAt" > b.prev_at and e."ObservedAt" <= b."BuiltAt")` },
  { id: "ws5-shortlist-p50-move-across-ratings", owner: "WS-5", label: "Largest shortlist p50 move between builds of one artifact with a rating between them", op: "info",
    sql: `with b as (select "BuiltAt", "ShortlistP50" as p50, lag("BuiltAt") over w as prev_at, lag("ShortlistP50") over w as prev_p50
                       from "FeedBuildStats" where "AccountId" = :'account' and "BuiltAt" >= :'since'::timestamptz and "ShortlistP50" is not null
                       window w as (partition by "ModelArtifactId" order by "BuiltAt"))
          select max(abs(p50 - prev_p50)) from b where prev_at is not null
            and exists (select 1 from "UserRatingEvents" e where e."AccountId" = :'account' and e."ObservedAt" > b.prev_at and e."ObservedAt" <= b."BuiltAt")` },
  { id: "ws5-buildfeed-wall-vs-stats", owner: "WS-5", label: "BuildFeed job wall time p95 over FeedBuildStats.DurationMs p95 (ratio)", op: "<=", target: 2,
    sql: `select case when s.p95 is null or s.p95 = 0 or j.p95 is null then null else round((j.p95 / s.p95)::numeric, 2) end
            from (select percentile_cont(0.95) within group (order by extract(epoch from "CompletedAt" - "StartedAt") * 1000) as p95
                  from "SyncJobs" where ${WEEK} and "Kind" = 'BuildFeed' and "Status" = 'Succeeded' and "AccountId" = :'account') j,
                 (select percentile_cont(0.95) within group (order by "DurationMs") as p95 from "FeedBuildStats" where "AccountId" = :'account' and "BuiltAt" >= :'since'::timestamptz) s` },
  { id: "ws5-retrains-on-days-without-ratings", owner: "WS-5", label: "Most Train jobs on one day with no new rating", op: "<=", target: 1,
    sql: `with days as (select generate_series(0, 6) as d),
            trains as (select floor(extract(epoch from "EnqueuedAt" - :'since'::timestamptz) / 86400)::int as d, count(*) as n from "SyncJobs"
                       where ${WEEK} and "Kind" = 'Train' and "AccountId" = :'account' group by 1),
            rated as (select distinct floor(extract(epoch from "ObservedAt" - :'since'::timestamptz) / 86400)::int as d from "UserRatingEvents"
                      where "AccountId" = :'account' and "ObservedAt" >= :'since'::timestamptz)
          select case when (select count(*) from trains) = 0 then null else coalesce(max(t.n), 0) end
            from days left join trains t on t.d = days.d where days.d not in (select d from rated)` },
];

/** Does a measured value pass its gate? null (no data) passes nothing and fails nothing. */
export function judge(check, value) {
  if (check.op === "info" || value === null || value === undefined || Number.isNaN(value)) return null;
  switch (check.op) {
    case "==": return value === check.target;
    case "<": return value < check.target;
    case "<=": return value <= check.target;
    case ">=": return value >= check.target;
    default: throw new Error(`unknown op ${check.op}`);
  }
}

/** psql's -At text for one number: "" is SQL NULL. */
export function parseNumber(text) {
  const t = String(text ?? "").trim();
  return t === "" ? null : Number(t);
}

export function render(run, results) {
  const failed = results.filter((r) => r.pass === false);
  const noData = results.filter((r) => r.pass === null && r.check.op !== "info");
  const L = [
    `# Synthetic week — ${run.database}`, "",
    `- Week: ${run.ticks} six-hour ticks from ${run.startedAt} (aged ${run.agedHours} h; since = ${run.since})${run.stopped ? `; **stopped early:** ${run.stopped}` : ""}`,
    `- Trakt: ${Object.entries(run.traktCalls ?? {}).map(([k, v]) => `${k} ${v}`).join(", ") || "no calls"} (all in-process, WeekTraktClient)`,
    `- Result: ${failed.length} failed, ${noData.length} without data, ${results.length - failed.length - noData.length} passed or informational`, "",
    "| Owner | Check | Value | Gate | Result |", "|---|---|---|---|---|",
    ...results.map(({ check, value, pass }) => `| ${check.owner} | ${check.label} | ${value ?? "no data"} | ${check.op === "info" ? "—" : `${check.op} ${check.target}`} | ${pass === null ? (check.op === "info" ? "info" : "no data") : pass ? "pass" : "**FAIL**"} |`),
  ];
  return L.join("\n");
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = {};
  const argv = process.argv.slice(2);
  for (let i = 0; i < argv.length; i++) if (argv[i].startsWith("--")) { args[argv[i].slice(2)] = argv[i + 1]; i++; }
  const die = (m) => { console.error(`week: ${m}`); process.exit(2); };
  const PG = { host: process.env.EVAL_PGHOST ?? "127.0.0.1", port: process.env.EVAL_PGPORT ?? "5434", user: process.env.EVAL_PGUSER ?? "reel", password: process.env.EVAL_PGPASSWORD ?? "reel_eval" };
  const db = args.db ?? die("--db is required");
  if (String(PG.port) === "5433") die("refusing port 5433 — the founder's LIVE Postgres");
  if (db === "reel" || !/^(reel_eval_.+|reel_ws.+)$/.test(db) || db.endsWith("_template")) die(`refusing database '${db}' — the week runs on a clone (reel_ws*, reel_eval_*), never a template`);
  const runFile = join(args.run ?? die("--run is required"), "run.json");
  if (!existsSync(runFile)) die(`${runFile} not found — run SyntheticWeekRun first`);
  const run = JSON.parse(readFileSync(runFile, "utf8"));
  if (run.database !== db) die(`${runFile} is a run on ${run.database}, not ${db}`);
  const one = (query) => execFileSync("psql", ["-X", "-q", "-v", "ON_ERROR_STOP=1", "-At", "-h", PG.host, "-p", PG.port, "-U", PG.user, "-d", db,
    "-v", `since=${run.since}`, "-v", `account=${run.accountId}`, "-f", "-"],
  { input: query, env: { ...process.env, PGPASSWORD: PG.password, PGTZ: "UTC" } }).toString();
  const results = CHECKS.map((check) => {
    const value = parseNumber(one(check.sql));
    return { check, value, pass: judge(check, value) };
  });
  const report = render(run, results);
  writeFileSync(args.out ?? join(args.run, "report.md"), `${report}\n`);
  console.log(report);
  process.exit(results.some((r) => r.pass === false) ? 1 : 0);
}
