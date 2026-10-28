-- SQL truth for the Ask Reel golden eval. Independent of the pipeline: it reads only the catalogue.
-- Returns one column, "Id", of every catalogue title that is BOTH a romance and a comedy — "romantic
-- comedies" read as the two genres the request names, each matched the way an exclusion of that genre
-- reads it (truth/_eligible.sql's ExcludeGenre arm, restated here with the genre as the slug): by its own
-- genre tag; by an alias genre (soap for romance); by a composite; or, on a medium whose TMDB taxonomy has
-- no id for the genre (TV for romance), by a stand-in keyword label or a stand-in overview phrase. Genre
-- tags alone would hold the scenario to every romance title, so an honest count of romantic comedies
-- would read as wrong (HREV4-RET-01). The VALUES lists are generated from GenreStandIns by
-- StandInTruthSqlTests (REEL_REGENERATE_EVAL_SQL=1); the matching is written here and held to
-- HardConstraintFilter by ConceptTruthParityTests.
SELECT t."Id"
  FROM "Titles" t
 WHERE (SELECT count(*)
          FROM (VALUES ('romance'), ('comedy')) g (slug)
         WHERE g.slug = ANY (t."Genres")
            OR EXISTS (SELECT 1 FROM (
                       -- BEGIN GENERATED genre-aliases
                       VALUES ('family', ARRAY['children']::text[]),
                              ('romance', ARRAY['soap']::text[]),
                              ('thriller', ARRAY['suspense']::text[])
                       -- END GENERATED genre-aliases
                       ) a (slug, genres)
                        WHERE a.slug = g.slug AND a.genres && t."Genres")
            OR EXISTS (SELECT 1 FROM (
                       -- BEGIN GENERATED genre-composites
                       VALUES ('anime', 'animation', 'ja')
                       -- END GENERATED genre-composites
                       ) c (slug, genre, language)
                        WHERE c.slug = g.slug AND c.genre = ANY (t."Genres") AND lower(t."Language") = c.language)
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
                        WHERE s.slug = g.slug AND t."MediaType" = ANY (s.media)
                          AND (s.exact && t."Keywords"
                               OR EXISTS (SELECT 1 FROM unnest(t."Keywords") k, unnest(s.words) w
                                           WHERE strpos(' ' || k || ' ', ' ' || w || ' ') > 0)))
            -- An overview phrase (romance on TV): the phrase as a whole word in the overview, any case,
            -- bounded by a non-letter or the text's edge ("in love", never "in lovely").
            OR EXISTS (SELECT 1 FROM (
                       -- BEGIN GENERATED genre-overview-phrases
                       VALUES ('romance', ARRAY['Show']::text[], ARRAY['romance', 'romantic', 'love story', 'in love', 'crush']::text[])
                       -- END GENERATED genre-overview-phrases
                       ) o (slug, media, phrases)
                        WHERE o.slug = g.slug AND t."MediaType" = ANY (o.media)
                          AND t."Overview" ~* ('(^|[^a-z])(' || array_to_string(o.phrases, '|') || ')([^a-z]|$)'))) = 2
