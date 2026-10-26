-- The supply truth behind one concept: the eval's independent count of what an honest none-found reply
-- may say ("you've seen N", "M have no prediction yet", "dropping <rule> would add K"), computed from the
-- catalogue and the account's own rows, never from the pipeline. It reads three temporary views the
-- caller creates first:
--   eval_concept   the concept's truth query (eval/askreel/truth/<key>.sql) — one column, "Id";
--   eval_eligible  eval/askreel/truth/_eligible.sql;
--   eval_rules     eval/askreel/supply/rules.sql.
-- Parameters: :'account', :'media' ('Any', 'Movie' or 'Show') and :minvotes (the vote floor).
--
-- The population and its partition follow the SupplyReport contract (WS-3, RetrievalContracts.cs): titles
-- in the concept, in the media scope, with at least :minvotes crowd votes (Trakt first at 50+ votes, else
-- TMDB, else Trakt — the badge's own source); each counted once, in the first bucket it reaches:
--   matching = alreadySeen + savedOrDismissed + excludedByConditions + eligible
--   eligible = unscored + belowFloor + clearingFloor
-- Scores are the ACTIVE artifact's (highest version); the For-you bar is MinPredictedRating clamped to
-- 0–10, where 0 or a malformed value means no bar, compared at badge precision (one decimal, half away
-- from zero). A knockout is a rule that is the ONLY rule a footprint-free title in the population fails.
-- Returns "Bucket", "Kind", "Value", "Count": one row per bucket, one per knockout.
WITH scoped AS (
    SELECT t."Id",
           EXISTS (SELECT 1 FROM "WatchedTitles" w WHERE w."AccountId" = :'account' AND w."TitleId" = t."Id")
             OR EXISTS (SELECT 1 FROM "UserRatings" r WHERE r."AccountId" = :'account' AND r."TitleId" = t."Id") AS seen,
           EXISTS (SELECT 1 FROM "UserTitleReactions" ux
                    WHERE ux."AccountId" = :'account' AND ux."TitleId" = t."Id" AND ux."RevokedAt" IS NULL
                      AND ux."Kind" IN ('NotInterested', 'SaveForLater', 'Dropped')) AS reacted,
           EXISTS (SELECT 1 FROM eval_eligible e WHERE e."Id" = t."Id") AS eligible,
           CASE WHEN t."TraktRating" > 0 AND t."TraktVotes" >= 50 THEN t."TraktVotes"
                WHEN t."TmdbVoteAverage" > 0 AND t."TmdbVoteCount" > 0 THEN t."TmdbVoteCount"
                WHEN t."TraktRating" > 0 AND t."TraktVotes" > 0 THEN t."TraktVotes"
                ELSE 0 END AS votes
      FROM (SELECT DISTINCT "Id" FROM eval_concept) c
      JOIN "Titles" t ON t."Id" = c."Id"
     WHERE :'media' = 'Any' OR t."MediaType" = :'media'
),
pop AS (SELECT * FROM scoped WHERE votes >= :minvotes),
artifact AS (
    SELECT a."Id" FROM "ModelArtifacts" a
     WHERE a."AccountId" = :'account' AND a."Status" = 'Active'
     ORDER BY a."Version" DESC LIMIT 1
),
bar AS (
    SELECT CASE WHEN v > 0 THEN least(v, 10) END AS v
      FROM (SELECT (SELECT CASE WHEN f."Value" ~ '^\s*[+-]?[0-9]+(\.[0-9]+)?\s*$' THEN btrim(f."Value")::numeric END
                      FROM "ContentFilters" f
                     WHERE f."AccountId" = :'account' AND f."Kind" = 'MinPredictedRating'
                     ORDER BY f."CreatedAt", f."Id" LIMIT 1) AS v) raw
),
scored AS (
    SELECT p."Id",
           (SELECT s."PredictedRating" FROM "TitleScores" s, artifact
             WHERE s."AccountId" = :'account' AND s."TitleId" = p."Id" AND s."ModelArtifactId" = artifact."Id"
             LIMIT 1) AS predicted
      FROM pop p
     WHERE p.eligible
),
failing AS (
    SELECT r."Id", count(*) AS n, min(r."Kind") AS kind, min(r."Value") AS value
      FROM eval_rules r
      JOIN pop p ON p."Id" = r."Id" AND NOT p.seen AND NOT p.reacted
     GROUP BY r."Id"
)
SELECT 'matching' AS "Bucket", '' AS "Kind", '' AS "Value", (SELECT count(*) FROM pop)::int AS "Count"
UNION ALL SELECT 'lowVoteMatches', '', '', ((SELECT count(*) FROM scoped) - (SELECT count(*) FROM pop))::int
UNION ALL SELECT 'alreadySeen', '', '', (SELECT count(*) FROM pop WHERE seen)::int
UNION ALL SELECT 'savedOrDismissed', '', '', (SELECT count(*) FROM pop WHERE reacted AND NOT seen)::int
UNION ALL SELECT 'excludedByConditions', '', '', (SELECT count(*) FROM pop WHERE NOT seen AND NOT reacted AND NOT eligible)::int
UNION ALL SELECT 'eligible', '', '', (SELECT count(*) FROM pop WHERE eligible)::int
UNION ALL SELECT 'unscored', '', '', (SELECT count(*) FROM scored WHERE predicted IS NULL)::int
UNION ALL SELECT 'belowFloor', '', '', (SELECT count(*) FROM scored, bar WHERE predicted IS NOT NULL AND bar.v IS NOT NULL AND round(predicted, 1) < bar.v)::int
UNION ALL SELECT 'clearingFloor', '', '', (SELECT count(*) FROM scored, bar WHERE predicted IS NOT NULL AND (bar.v IS NULL OR round(predicted, 1) >= bar.v))::int
UNION ALL SELECT 'knockout', kind, value, count(*)::int
            FROM failing
           WHERE n = 1 AND kind <> 'Maturity' AND value <> '*'
           GROUP BY kind, value
