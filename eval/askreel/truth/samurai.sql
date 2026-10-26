-- SQL truth for the Ask Reel golden eval. Independent of the pipeline: it reads only the catalogue.
-- Returns one column, "Id", of every catalogue title that satisfies: samurai / ronin films.
-- TMDB keyword tags are noisy (Batman Begins carries "ninja"), so this set bounds precision from above
-- and is never a recall target (program §1, C*4).
--
-- "ronin" is a samurai word only as a TMDB keyword (the masterless-samurai tag). In a NAME it is
-- deliberately not matched: "Ronin" (1998) is a crime thriller with no samurai tag, and counting it put
-- the truth one matching and one seen over every honest samurai reply (IREV2-SYS-03). The name arm is a
-- whole word with the plural allowed, the same reading HardConstraintFilter gives a name.
SELECT t."Id"
  FROM "Titles" t
 WHERE lower(t."Name") ~ '\msamurais?\M'
    OR EXISTS (SELECT 1 FROM unnest(t."Keywords") k WHERE k ~ '\msamurai|\mronin\M')
