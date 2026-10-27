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
        -- ExcludeGenre: one row per excluded genre the title carries — by its genre tags, an alias genre, a
        -- composite (anime: animation in Japanese) or a stand-in keyword or overview phrase on a medium that cannot tag the genre (the arm of `_eligible.sql`, which explains
        -- the matching; the VALUES lists are generated from GenreStandIns by StandInTruthSqlTests).
        SELECT 'ExcludeGenre' AS "Kind", lower(btrim(f."Value")) AS "Value"
          FROM "ContentFilters" f
         WHERE f."AccountId" = :'account' AND f."Kind" = 'ExcludeGenre'
           AND (f."Value" = ANY (t."Genres")
                OR EXISTS (SELECT 1 FROM (
                           -- BEGIN GENERATED genre-aliases
                           VALUES ('family', ARRAY['children']::text[]),
                                  ('romance', ARRAY['soap']::text[]),
                                  ('thriller', ARRAY['suspense']::text[])
                           -- END GENERATED genre-aliases
                           ) a (slug, genres)
                            WHERE a.slug = f."Value" AND a.genres && t."Genres")
                OR EXISTS (SELECT 1 FROM (
                           -- BEGIN GENERATED genre-composites
                           VALUES ('anime', 'animation', 'ja')
                           -- END GENERATED genre-composites
                           ) c (slug, genre, language)
                            WHERE c.slug = f."Value" AND c.genre = ANY (t."Genres") AND lower(t."Language") = c.language)
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
                                               WHERE strpos(' ' || k || ' ', ' ' || w || ' ') > 0)))
                OR EXISTS (SELECT 1 FROM (
                           -- BEGIN GENERATED genre-overview-phrases
                           VALUES ('romance', ARRAY['Show']::text[], ARRAY['romance', 'romantic', 'love story', 'in love', 'crush']::text[])
                           -- END GENERATED genre-overview-phrases
                           ) o (slug, media, phrases)
                            WHERE o.slug = f."Value" AND t."MediaType" = ANY (o.media)
                              AND t."Overview" ~* ('(^|[^a-z])(' || array_to_string(o.phrases, '|') || ')([^a-z]|$)')))
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
