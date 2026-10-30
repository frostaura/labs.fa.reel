-- SQL truth for the Ask Reel golden eval. Independent of the pipeline: it reads only the catalogue.
-- Returns one column, "Id", of every catalogue title that satisfies: martial arts.
-- TMDB keyword tags are noisy (Batman Begins carries "ninja"), so this set bounds precision from above
-- and is never a recall target (program §1, C*4).
-- The name arm reads only the request's own words ("martial arts"), as the app's does and as heist.sql's
-- does (HREV3-RET-A): a synonym is a keyword claim. Read in names too, "kung fu" counted 4 more martial-arts
-- films at 50 votes than the reply, all named "Kung Fu …" with no martial-arts tag (the Kung Fu Panda
-- shorts, Kung Fu Soccer, Kung Fu League, … — fix-golden-v2-2, pagination-more T2: 527 vs 523).
SELECT t."Id"
  FROM "Titles" t
 WHERE lower(t."Name") ~ 'martial arts'
    OR EXISTS (SELECT 1 FROM unnest(t."Keywords") k WHERE k ~ 'martial arts|kung fu')
