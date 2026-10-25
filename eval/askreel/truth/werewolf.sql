-- SQL truth for the Ask Reel golden eval. Independent of the pipeline: it reads only the catalogue.
-- Returns one column, "Id", of every catalogue title that satisfies: werewolves.
-- TMDB keyword tags are noisy (Batman Begins carries "ninja"), so this set bounds precision from above
-- and is never a recall target (program §1, C*4).
SELECT t."Id"
  FROM "Titles" t
 WHERE lower(t."Name") ~ 'werewol'
    OR EXISTS (SELECT 1 FROM unnest(t."Keywords") k WHERE k ~ 'werewol')
