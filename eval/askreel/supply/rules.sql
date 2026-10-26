-- Which standing rule each footprint-free title breaks: the eval's independent statement of the
-- relaxations ("knockouts") an honest none-found reply may offer. Parameter: :'account'.
-- Returns "Id", "Kind", "Value": one row per (title, rule it fails), over titles with NO interaction
-- footprint (never watched, rated, saved, marked not interested or dropped) — "already seen" is a bucket,
-- never a relaxation. A footprint-free title with no row here is exactly a title `_eligible.sql` admits;
-- `askreel.mjs check --db` and SupplyTruthParityTests hold the two files to that.
--
-- One rule per value for the genre and keyword lists, one for the language allow-list as a whole, one
-- each for the crowd floor and the year window ends — the drop-one unit FilterRuleSet.Rules() uses. Two
-- kinds block without ever being a relaxation, and the runner never reports them as one:
--   Maturity        the account's certification ceiling (not a content filter);
--   IncludeGenre *  an allow-list of two or more genres, where dropping one value only narrows the list.
-- Each predicate is the negation of its clause in `_eligible.sql`, written from the same rule wording.
SELECT DISTINCT t."Id", x."Kind", x."Value"
  FROM "Titles" t
  CROSS JOIN LATERAL (
        -- ExcludeGenre: one row per excluded genre the title carries.
        SELECT 'ExcludeGenre' AS "Kind", lower(btrim(f."Value")) AS "Value"
          FROM "ContentFilters" f
         WHERE f."AccountId" = :'account' AND f."Kind" = 'ExcludeGenre' AND f."Value" = ANY (t."Genres")
        UNION ALL
        -- ExcludeKeyword: in the name, the overview or any TMDB keyword phrase.
        SELECT 'ExcludeKeyword', lower(btrim(f."Value"))
          FROM "ContentFilters" f
         WHERE f."AccountId" = :'account' AND f."Kind" = 'ExcludeKeyword'
           AND (strpos(lower(t."Name"), lower(f."Value")) > 0
                OR strpos(lower(coalesce(t."Overview", '')), lower(f."Value")) > 0
                OR EXISTS (SELECT 1 FROM unnest(t."Keywords") k WHERE strpos(k, lower(f."Value")) > 0))
        UNION ALL
        -- IncludeGenre: the title carries none of the allowed genres. Named by its value only when the
        -- list has exactly one (dropping it removes the allow-list); otherwise '*'.
        SELECT 'IncludeGenre',
               CASE WHEN (SELECT count(DISTINCT lower(btrim(g."Value"))) FROM "ContentFilters" g
                           WHERE g."AccountId" = :'account' AND g."Kind" = 'IncludeGenre') = 1
                    THEN (SELECT min(lower(btrim(g."Value"))) FROM "ContentFilters" g
                           WHERE g."AccountId" = :'account' AND g."Kind" = 'IncludeGenre')
                    ELSE '*' END
         WHERE EXISTS (SELECT 1 FROM "ContentFilters" f WHERE f."AccountId" = :'account' AND f."Kind" = 'IncludeGenre')
           AND NOT EXISTS (SELECT 1 FROM "ContentFilters" f
                            WHERE f."AccountId" = :'account' AND f."Kind" = 'IncludeGenre' AND f."Value" = ANY (t."Genres"))
        UNION ALL
        -- Language: outside the allow-list ("*" admits everything; no rows means English only; an
        -- unknown language fails). Valued as the sorted list it would drop.
        SELECT 'Language',
               coalesce((SELECT string_agg(DISTINCT lower(btrim(g."Value")), ',' ORDER BY lower(btrim(g."Value")))
                           FROM "ContentFilters" g WHERE g."AccountId" = :'account' AND g."Kind" = 'Language'), 'en')
         WHERE NOT (EXISTS (SELECT 1 FROM "ContentFilters" f WHERE f."AccountId" = :'account' AND f."Kind" = 'Language' AND f."Value" = '*')
                    OR (t."Language" IS NOT NULL AND EXISTS (SELECT 1 FROM "ContentFilters" f
                            WHERE f."AccountId" = :'account' AND f."Kind" = 'Language' AND f."Value" = lower(t."Language")))
                    OR (NOT EXISTS (SELECT 1 FROM "ContentFilters" f WHERE f."AccountId" = :'account' AND f."Kind" = 'Language')
                        AND t."Language" = 'en'))
        UNION ALL
        -- MinCrowdRating: the raw score, TMDB first then Trakt, is under the floor or missing.
        SELECT 'MinCrowdRating', to_char(f."Value"::numeric, 'FM990.0')
          FROM (SELECT f."Value" FROM "ContentFilters" f
                 WHERE f."AccountId" = :'account' AND f."Kind" = 'MinCrowdRating' LIMIT 1) f
         WHERE NOT coalesce(coalesce(nullif(t."TmdbVoteAverage", 0), nullif(t."TraktRating", 0)) >= f."Value"::numeric, false)
        UNION ALL
        -- MinYear / MaxYear on Year → ReleasedAt → FirstAiredAt; a title with no date fails neither.
        SELECT 'MinYear', (f."Value"::int)::text
          FROM (SELECT f."Value" FROM "ContentFilters" f
                 WHERE f."AccountId" = :'account' AND f."Kind" = 'MinYear' LIMIT 1) f
         WHERE coalesce(t."Year", extract(year FROM t."ReleasedAt" AT TIME ZONE 'UTC')::int, extract(year FROM t."FirstAiredAt" AT TIME ZONE 'UTC')::int) < f."Value"::int
        UNION ALL
        SELECT 'MaxYear', (f."Value"::int)::text
          FROM (SELECT f."Value" FROM "ContentFilters" f
                 WHERE f."AccountId" = :'account' AND f."Kind" = 'MaxYear' LIMIT 1) f
         WHERE coalesce(t."Year", extract(year FROM t."ReleasedAt" AT TIME ZONE 'UTC')::int, extract(year FROM t."FirstAiredAt" AT TIME ZONE 'UTC')::int) > f."Value"::int
        UNION ALL
        -- Maturity: over the account's ceiling (ordinal scale as in `_eligible.sql`). Blocks, never relaxes.
        SELECT 'Maturity', ''
         WHERE NOT (SELECT CASE WHEN m.ceiling = 0 THEN true
                                WHEN m.rating = 0 THEN m.ceiling >= 4
                                ELSE m.rating <= m.ceiling END
                      FROM (SELECT coalesce((SELECT CASE upper(btrim(a."Settings"->>'MaturityCeiling'))
                             WHEN 'G' THEN 1 WHEN 'TV-G' THEN 1 WHEN 'TV-Y' THEN 1 WHEN 'TV-Y7' THEN 1 WHEN 'U' THEN 1
                             WHEN 'PG' THEN 2 WHEN 'TV-PG' THEN 2
                             WHEN 'PG-13' THEN 3 WHEN 'TV-14' THEN 3 WHEN '12' THEN 3 WHEN '12A' THEN 3
                             WHEN 'R' THEN 4 WHEN 'TV-MA' THEN 4 WHEN '15' THEN 4 WHEN '16' THEN 4
                             WHEN 'NC-17' THEN 5 WHEN '18' THEN 5 WHEN 'X' THEN 5
                             ELSE 0 END
                                               FROM "Accounts" a WHERE a."Id" = :'account'), 0) AS ceiling,
                                   CASE upper(btrim(t."Certification"))
                             WHEN 'G' THEN 1 WHEN 'TV-G' THEN 1 WHEN 'TV-Y' THEN 1 WHEN 'TV-Y7' THEN 1 WHEN 'U' THEN 1
                             WHEN 'PG' THEN 2 WHEN 'TV-PG' THEN 2
                             WHEN 'PG-13' THEN 3 WHEN 'TV-14' THEN 3 WHEN '12' THEN 3 WHEN '12A' THEN 3
                             WHEN 'R' THEN 4 WHEN 'TV-MA' THEN 4 WHEN '15' THEN 4 WHEN '16' THEN 4
                             WHEN 'NC-17' THEN 5 WHEN '18' THEN 5 WHEN 'X' THEN 5
                             ELSE 0 END AS rating) m)
       ) x
 WHERE NOT EXISTS (SELECT 1 FROM "WatchedTitles" w WHERE w."AccountId" = :'account' AND w."TitleId" = t."Id")
   AND NOT EXISTS (SELECT 1 FROM "UserRatings" r WHERE r."AccountId" = :'account' AND r."TitleId" = t."Id")
   AND NOT EXISTS (SELECT 1 FROM "UserTitleReactions" ux
                    WHERE ux."AccountId" = :'account' AND ux."TitleId" = t."Id" AND ux."RevokedAt" IS NULL
                      AND ux."Kind" IN ('NotInterested', 'SaveForLater', 'Dropped'))
