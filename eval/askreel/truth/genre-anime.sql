-- SQL truth for the Ask Reel golden eval. Independent of the pipeline: it reads only the catalogue.
-- Returns one column, "Id", of every catalogue title that satisfies: the anime genre, read as the account's
-- own exclusion of it reads (supply/rules.sql, _eligible.sql): Trakt genre 'anime', OR animation whose
-- original language is Japanese (the composite — TMDB has no anime genre on either medium), OR the 'anime'
-- stand-in keyword. Restated 2026-10-30 (final-golden-v2): the tag alone held 2 titles while the
-- exclusion the founder's "anime" request collides with holds out 1,700, so a correct count of what that
-- rule hides read as a 1,700-title mismatch. ConceptTruthParityTests holds this file to the app's reading.
-- TMDB keyword tags are noisy (Batman Begins carries "ninja"), so this set bounds precision from above
-- and is never a recall target (program §1, C*4).
SELECT t."Id"
  FROM "Titles" t
 WHERE 'anime' = ANY (t."Genres")
    OR ('animation' = ANY (t."Genres") AND lower(t."Language") = 'ja')
    OR 'anime' = ANY (t."Keywords")
