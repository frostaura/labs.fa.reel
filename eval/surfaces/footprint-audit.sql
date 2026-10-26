-- The timestamp-aware zero-footprint audit (program WS-7 task 7): every exposure the account was served
-- AFTER the moment the title entered its footprint or a standing exclusion. The exposure log is
-- append-only and written only where cards are actually served, so a row here is a card the user was
-- shown that the locks say they must never see. Parameters: :'account', :'since' (an ISO timestamp; only
-- exposures at or after it are audited), :'probes' (a text[] literal of FD-18 probe conversation ids).
-- Expects the view eval_rules_all: supply/rules.sql WITHOUT its footprint filter (scripts/eval/
-- footprint-audit.mjs and FootprintAuditSqlTests build it the same way), i.e. every (title, rule it breaks).
--
-- When each lock started binding, as the system could know it:
--   watched          WatchedTitles.FirstSyncedAt — any watch row, partial included (the eligibility rule);
--   rated            the earliest FirstRatedAt — the rating's own moment (a Trakt rating Reel synced later
--                    is counted from when it was made; the report's "after a day" column is the margin);
--   not interested,  UserTitleReactions.CreatedAt, until RevokedAt — reversible exclusions;
--   saved, dropped
--   a content rule   the rule's CreatedAt: per value for ExcludeGenre and ExcludeKeyword, and for the
--                    list rules (Language, IncludeGenre) and the single-valued ones (crowd floor, year
--                    window) the newest row of that kind — the rule as it stands has bound since then. With
--                    no Language row at all, English-only has bound since the account was created.
-- Not audited, because nothing records when they started: the maturity ceiling (an account setting) and
-- rules deleted since (only rules that still exist are known). The For-you bar is a ranking statement,
-- not eligibility, and is the matrix's to hold.
WITH x AS (
    SELECT e."Id", e."TitleId", e."Surface", e."OccurredAt", e."SourceTurnId"
      FROM "ExposureEvents" e
     WHERE e."AccountId" = :'account' AND e."OccurredAt" >= :'since'::timestamptz
), rule_since AS (
    SELECT r."Id" AS "TitleId", r."Kind", r."Value",
           CASE WHEN r."Kind" IN ('ExcludeGenre', 'ExcludeKeyword')
                THEN (SELECT min(f."CreatedAt") FROM "ContentFilters" f
                       WHERE f."AccountId" = :'account' AND f."Kind" = r."Kind" AND lower(btrim(f."Value")) = r."Value")
                ELSE coalesce((SELECT max(f."CreatedAt") FROM "ContentFilters" f
                                WHERE f."AccountId" = :'account' AND f."Kind" = r."Kind"),
                              CASE WHEN r."Kind" = 'Language'
                                   THEN (SELECT a."CreatedAt" FROM "Accounts" a WHERE a."Id" = :'account') END)
           END AS since
      FROM eval_rules_all r
     WHERE r."Kind" <> 'Maturity'
), leaks AS (
    SELECT x.*, 'watched' AS reason, w."FirstSyncedAt" AS since
      FROM x JOIN "WatchedTitles" w ON w."AccountId" = :'account' AND w."TitleId" = x."TitleId"
     WHERE x."OccurredAt" > w."FirstSyncedAt"
    UNION ALL
    SELECT x.*, 'rated', r.since
      FROM x JOIN (SELECT "TitleId", min("FirstRatedAt") AS since FROM "UserRatings"
                    WHERE "AccountId" = :'account' GROUP BY "TitleId") r ON r."TitleId" = x."TitleId"
     WHERE x."OccurredAt" > r.since
    UNION ALL
    SELECT x.*, CASE u."Kind" WHEN 'NotInterested' THEN 'not-interested' WHEN 'SaveForLater' THEN 'saved' ELSE 'dropped' END,
           u."CreatedAt"
      FROM x JOIN "UserTitleReactions" u ON u."AccountId" = :'account' AND u."TitleId" = x."TitleId"
     WHERE u."Kind" IN ('NotInterested', 'SaveForLater', 'Dropped')
       AND x."OccurredAt" > u."CreatedAt" AND (u."RevokedAt" IS NULL OR x."OccurredAt" < u."RevokedAt")
    UNION ALL
    SELECT x.*, 'rule ' || r."Kind" || ' ' || r."Value", r.since
      FROM x JOIN rule_since r ON r."TitleId" = x."TitleId"
     WHERE r.since IS NOT NULL AND x."OccurredAt" > r.since
)
-- One row per (surface, reason): exposures, distinct titles, how many came more than a day after the lock
-- started (a sync lag cannot explain those), how many belong to FD-18 probe conversations, the first and
-- last, and up to three example "title name @ time" strings.
SELECT l."Surface", l.reason, count(*)::int, count(DISTINCT l."TitleId")::int,
       count(*) FILTER (WHERE l."OccurredAt" > l.since + interval '1 day')::int,
       count(*) FILTER (WHERE s."ConversationId"::text = ANY (:'probes'::text[]))::int,
       to_char(min(l."OccurredAt") AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
       to_char(max(l."OccurredAt") AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
       (SELECT string_agg(e, ' | ') FROM (
            SELECT t."Name" || ' @ ' || to_char(l2."OccurredAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') AS e
              FROM leaks l2 JOIN "Titles" t ON t."Id" = l2."TitleId"
             WHERE l2."Surface" = l."Surface" AND l2.reason = l.reason
             ORDER BY l2."OccurredAt" DESC LIMIT 3) ex)
  FROM leaks l
  LEFT JOIN "SearchTurns" s ON s."Id" = l."SourceTurnId"
 GROUP BY l."Surface", l.reason
 ORDER BY 3 DESC, 1, 2;
