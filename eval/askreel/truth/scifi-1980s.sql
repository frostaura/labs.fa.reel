-- SQL truth for the Ask Reel golden eval. Independent of the pipeline: it reads only the catalogue.
-- Returns one column, "Id", of every catalogue title that satisfies: science fiction from the 1980s.
-- TMDB keyword tags are noisy (Batman Begins carries "ninja"), so this set bounds precision from above
-- and is never a recall target (program §1, C*4).
SELECT t."Id"
  FROM "Titles" t
 WHERE 'science-fiction' = ANY (t."Genres") AND coalesce(t."Year", extract(year FROM t."ReleasedAt")::int, extract(year FROM t."FirstAiredAt")::int) BETWEEN 1980 AND 1989
