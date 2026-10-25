-- SQL truth for the Ask Reel golden eval. Independent of the pipeline: it reads only the catalogue.
-- Returns one column, "Id", of every catalogue title that satisfies: Jason Statham credited as Actor.
-- TMDB keyword tags are noisy (Batman Begins carries "ninja"), so this set bounds precision from above
-- and is never a recall target (program §1, C*4).
SELECT DISTINCT c."TitleId" AS "Id"
  FROM "TitleCredits" c
  JOIN "Persons" p ON p."Id" = c."PersonId"
 WHERE p."Name" = 'Jason Statham' AND c."Role" = 'Actor'
