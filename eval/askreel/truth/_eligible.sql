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
   -- ExcludeGenre: no excluded genre on the title — by its own genre tags; by an alias genre a Trakt-synced
   -- title carries instead (children for family, suspense for thriller); or, on a medium whose TMDB taxonomy
   -- has no id for the genre (TV for romance, horror, history, thriller, music; both media for anime and
   -- musical), by a stand-in keyword label. An Exact label matches the whole label; a Word stem matches as
   -- a whole word or phrase inside a label ("teenage romance", never "bromance"). The two VALUES lists are
   -- the rule's DATA (which labels stand in, on which media) and are generated from GenreStandIns by
   -- StandInTruthSqlTests (REEL_REGENERATE_EVAL_SQL=1) so they cannot drift; the matching is written here.
   AND NOT EXISTS (SELECT 1 FROM "ContentFilters" f
                    WHERE f."AccountId" = :'account' AND f."Kind" = 'ExcludeGenre'
                      AND (f."Value" = ANY (t."Genres")
                           OR EXISTS (SELECT 1 FROM (
                                      -- BEGIN GENERATED genre-aliases
                                      VALUES ('family', ARRAY['children']::text[]),
                                             ('thriller', ARRAY['suspense']::text[])
                                      -- END GENERATED genre-aliases
                                      ) a (slug, genres)
                                       WHERE a.slug = f."Value" AND a.genres && t."Genres")
                           OR EXISTS (SELECT 1 FROM (
                                      -- BEGIN GENERATED genre-stand-ins
                                      VALUES ('anime', ARRAY['Movie', 'Show']::text[],
                                                ARRAY['anime']::text[],
                                                ARRAY[]::text[]),
                                             ('history', ARRAY['Show']::text[],
                                                ARRAY['history', 'british monarchy', 'joseon dynasty (1392–1910)', 'african american history', 'british history', 'american history', 'russian history', 'french history',
                                                  'italian history', 'spanish history', 'european history', 'soviet union history', 'world history', 'ancient history', 'period drama', 'period piece',
                                                  '19th century', '18th century', '17th century', '16th century', '15th century', '14th century', '20th century', 'medieval',
                                                  'middle ages (476-1453)', 'world war i', 'world war ii', 'post world war ii', 'holocaust (shoah)', 'ancient rome', 'roman empire', 'edo period',
                                                  'feudal japan', 'american civil war', 'usa history', 'royal family', 'japanese occupation of korea', 'historical event', 'palace intrigue', 'pacific war',
                                                  'auschwitz-birkenau concentration camp']::text[],
                                                ARRAY['historical']::text[]),
                                             ('horror', ARRAY['Show']::text[],
                                                ARRAY['gore', 'zombie', 'zombie apocalypse', 'vampire', 'vampire hunter (slayer)', 'werewolf', 'demon', 'demonic',
                                                  'demonic possession', 'possession', 'possessed', 'exorcism', 'exorcist', 'haunted house', 'haunted', 'haunting',
                                                  'ghost story', 'vengeful ghost', 'evil spirit', 'paranormal', 'paranormal phenomena', 'paranormal investigation', 'paranormal activity', 'occult',
                                                  'black magic', 'witchcraft', 'satanism', 'satanic cult', 'satanic ritual', 'séance', 'ouija board', 'cannibal',
                                                  'cannibalism', 'undead', 'dracula', 'frankenstein', 'creature feature', 'monster movie', 'found footage', 'found footage adjacent',
                                                  'evil doll', 'killer doll', 'possessed doll', 'killer clown', 'masked killer', 'urban legend', 'lovecraftian', 'cabin in the woods',
                                                  'axe murder', 'decapitation', 'chainsaw', 'evil child', 'ancient evil', 'reanimation', 'human sacrifice', 'monster',
                                                  'sea monster', 'satan', 'anti-christ', 'ritual murder', 'devil worship', 'ritual sacrifice', 'reanimated corpse', 'apparition',
                                                  'mutilation']::text[],
                                                ARRAY['horror', 'slasher']::text[]),
                                             ('music', ARRAY['Show']::text[],
                                                ARRAY['concert', 'concert film', 'concert footage', 'live performance', 'rock band', 'boy band', 'rock ''n'' roll', 'singer-songwriter',
                                                  'recording studio', 'pop star', 'pop singer', 'jazz', 'heavy metal', 'opera', 'record producer', 'singing',
                                                  'musician', 'singer', 'aspiring singer', 'rapper', 'hip-hop', 'composer', 'rock star', 'punk rock',
                                                  'idol', 'k-pop', 'singing competition', 'dj']::text[],
                                                ARRAY['music']::text[]),
                                             ('musical', ARRAY['Movie', 'Show']::text[],
                                                ARRAY['musical', 'jukebox musical', 'broadway musical', 'stage musical', 'rock musical', 'musical comedy', 'musical drama', 'musical theater',
                                                  'musical adaptation', 'musical biopic', 'dance musical', 'horror musical', 'romance musical', 'cabaret musical']::text[],
                                                ARRAY[]::text[]),
                                             ('romance', ARRAY['Show']::text[],
                                                ARRAY['love', 'love triangle', 'first love', 'falling in love', 'new love', 'love of one''s life', 'forbidden love', 'boys'' love (bl)',
                                                  'girls'' love (gl)', 'love at first sight', 'teenage love', 'young love', 'secret love', 'lovesickness', 'love story', 'lovers',
                                                  'lover', 'tragic love', 'love letter', 'true love', 'soulmates', 'opposites attract', 'love affair', 'romcom',
                                                  'hallmark', 'valentine''s day', 'marriage proposal', 'friends in love', 'pretend relationship', 'blind date', 'dating', 'unrequited love',
                                                  'heartbreak', 'crush', 'ex-lover', 'online dating', 'older woman younger man relationship', 'older man younger woman relationship', 'boyfriend girlfriend relationship', 'enemies to lovers',
                                                  'star crossed lovers', 'matchmaking', 'searching for love', 'long distance relationship', 'high school sweetheart', 'dating app']::text[],
                                                ARRAY['romance', 'romantic']::text[]),
                                             ('thriller', ARRAY['Show']::text[],
                                                ARRAY['suspenseful', 'tense', 'ominous', 'psychopath', 'sociopath', 'neo-noir', 'home invasion', 'hostage situation',
                                                  'cat and mouse', 'stalker', 'stalking', 'manhunt', 'race against time', 'sadistic psychopath', 'nordic noir', 'film noir']::text[],
                                                ARRAY['thriller']::text[])
                                      -- END GENERATED genre-stand-ins
                                      ) s (slug, media, exact, words)
                                       WHERE s.slug = f."Value" AND t."MediaType" = ANY (s.media)
                                         AND (s.exact && t."Keywords"
                                              OR EXISTS (SELECT 1 FROM unnest(t."Keywords") k, unnest(s.words) w
                                                          WHERE strpos(' ' || k || ' ', ' ' || w || ' ') > 0)))))
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
