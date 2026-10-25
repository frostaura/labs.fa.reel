-- SQL truth for the Ask Reel golden eval. Independent of the pipeline: it reads only the catalogue.
-- Returns one column, "Id", of every catalogue title that satisfies: heists released 2010–2019.
-- TMDB keyword tags are noisy (Batman Begins carries "ninja"), so this set bounds precision from above
-- and is never a recall target (program §1, C*4).
SELECT t."Id"
  FROM "Titles" t
 WHERE coalesce(t."Year", extract(year FROM t."ReleasedAt")::int, extract(year FROM t."FirstAiredAt")::int) BETWEEN 2010 AND 2019 AND EXISTS (SELECT 1 FROM unnest(t."Keywords") k WHERE k ~ '\mheist|bank robbery|\mrobbery\M')
