-- Independent SQL statement of the founder-locked eligibility rule (CLAUDE.md: "never show things I've
-- already seen" + every standing ContentFilter), written from the rule's wording rather than generated
-- from EligibilityQueryBuilder, so the eval can catch the builder drifting. Parameter: :'account'.
-- Returns one column, "Id", of every title the account may be shown on a suggestion surface.
-- It covers what EligibilityQueryBuilder.EligibleTitles + WithinMaturity apply. The For-you bar
-- (MinPredictedRating) is NOT eligibility: the scoring surfaces apply it with the active model, so it is
-- measured per surface by the exclusion matrix (WS-7 task 7), never folded in here.
SELECT t."Id"
  FROM "Titles" t
 WHERE NOT EXISTS (SELECT 1 FROM "WatchedTitles" w WHERE w."AccountId" = :'account' AND w."TitleId" = t."Id")
   AND NOT EXISTS (SELECT 1 FROM "UserRatings" r WHERE r."AccountId" = :'account' AND r."TitleId" = t."Id")
   AND NOT EXISTS (SELECT 1 FROM "UserTitleReactions" x
                    WHERE x."AccountId" = :'account' AND x."TitleId" = t."Id" AND x."RevokedAt" IS NULL
                      AND x."Kind" IN ('NotInterested', 'SaveForLater', 'Dropped'))
   -- ExcludeGenre: no excluded genre on the title.
   AND NOT EXISTS (SELECT 1 FROM "ContentFilters" f
                    WHERE f."AccountId" = :'account' AND f."Kind" = 'ExcludeGenre' AND f."Value" = ANY (t."Genres"))
   -- ExcludeKeyword: not in the name, the overview or any TMDB keyword phrase.
   AND NOT EXISTS (SELECT 1 FROM "ContentFilters" f
                    WHERE f."AccountId" = :'account' AND f."Kind" = 'ExcludeKeyword'
                      AND (strpos(lower(t."Name"), lower(f."Value")) > 0
                           OR strpos(lower(coalesce(t."Overview", '')), lower(f."Value")) > 0
                           OR EXISTS (SELECT 1 FROM unnest(t."Keywords") k WHERE strpos(k, lower(f."Value")) > 0)))
   -- IncludeGenre: an allow-list when present.
   AND (NOT EXISTS (SELECT 1 FROM "ContentFilters" f WHERE f."AccountId" = :'account' AND f."Kind" = 'IncludeGenre')
        OR EXISTS (SELECT 1 FROM "ContentFilters" f
                    WHERE f."AccountId" = :'account' AND f."Kind" = 'IncludeGenre' AND f."Value" = ANY (t."Genres")))
   -- Language: allow-list; "*" opts everything in; no rows means English only; unknown language fails.
   AND (EXISTS (SELECT 1 FROM "ContentFilters" f WHERE f."AccountId" = :'account' AND f."Kind" = 'Language' AND f."Value" = '*')
        OR (t."Language" IS NOT NULL AND EXISTS (SELECT 1 FROM "ContentFilters" f
                    WHERE f."AccountId" = :'account' AND f."Kind" = 'Language' AND f."Value" = lower(t."Language")))
        OR (NOT EXISTS (SELECT 1 FROM "ContentFilters" f WHERE f."AccountId" = :'account' AND f."Kind" = 'Language')
            AND t."Language" = 'en'))
   -- Crowd floor on the raw score, TMDB first then Trakt; no score fails a floor that exists (FD-7 default).
   AND (NOT EXISTS (SELECT 1 FROM "ContentFilters" f WHERE f."AccountId" = :'account' AND f."Kind" = 'MinCrowdRating')
        OR coalesce(nullif(t."TmdbVoteAverage", 0), nullif(t."TraktRating", 0))
           >= (SELECT f."Value"::numeric FROM "ContentFilters" f
                WHERE f."AccountId" = :'account' AND f."Kind" = 'MinCrowdRating' LIMIT 1))
   -- Inclusive year window on Year → ReleasedAt → FirstAiredAt; a title with no date passes.
   AND (coalesce(t."Year", extract(year FROM t."ReleasedAt" AT TIME ZONE 'UTC')::int, extract(year FROM t."FirstAiredAt" AT TIME ZONE 'UTC')::int) IS NULL
        OR (coalesce(t."Year", extract(year FROM t."ReleasedAt" AT TIME ZONE 'UTC')::int, extract(year FROM t."FirstAiredAt" AT TIME ZONE 'UTC')::int)
              >= coalesce((SELECT f."Value"::int FROM "ContentFilters" f
                            WHERE f."AccountId" = :'account' AND f."Kind" = 'MinYear' LIMIT 1), -2147483648)
            AND coalesce(t."Year", extract(year FROM t."ReleasedAt" AT TIME ZONE 'UTC')::int, extract(year FROM t."FirstAiredAt" AT TIME ZONE 'UTC')::int)
              <= coalesce((SELECT f."Value"::int FROM "ContentFilters" f
                            WHERE f."AccountId" = :'account' AND f."Kind" = 'MaxYear' LIMIT 1), 2147483647)))
   -- MaxMaturity: the account's certification ceiling (Settings.MaturityCeiling) on the ordinal scale
   -- G/U 1 · PG 2 · PG-13/TV-14/12 3 · R/TV-MA/15/16 4 · NC-17/18/X 5; no ceiling (or an unknown one)
   -- passes everything; an unknown certification passes only a ceiling that admits adult content (≥ 4).
   AND (SELECT CASE WHEN m.ceiling = 0 THEN true
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
