-- SQL truth for the Ask Reel golden eval. Independent of the pipeline: it reads only the catalogue.
-- Returns one column, "Id", of every catalogue title that satisfies: heists and robberies.
-- TMDB keyword tags are noisy (Batman Begins carries "ninja"), so this set bounds precision from above
-- and is never a recall target (program §1, C*4).
-- The name arm reads only the scenario's own word ("heist"), never a synonym (HREV3-RET-A: a title named
-- "... Robbery" is not a heist by name; the keyword arm still counts robbery tags).
SELECT t."Id"
  FROM "Titles" t
 WHERE lower(t."Name") ~ '\mheists?\M'
    OR EXISTS (SELECT 1 FROM unnest(t."Keywords") k WHERE k ~ '\mheist|bank robbery|\mrobbery\M')
