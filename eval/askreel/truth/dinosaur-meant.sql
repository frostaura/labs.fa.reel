-- SQL truth for the Ask Reel golden eval. Independent of the pipeline: it reads only the catalogue.
-- Returns one column, "Id", of every catalogue title that is a dinosaur film or show as a person means it:
-- truth/dinosaur.sql minus titles hand-labelled as carrying the tag for a passing sequence, each named
-- here by its TMDB id with the reason. truth/dinosaur.sql stays the TAG population the reply counts; this
-- set is the scenarios' `meant`, which only missed supply reads.
--   8967  The Tree of Life (2011) — a family drama; its dinosaurs are one sequence of the creation montage
--         (fix-golden-v2-2, chitchat-midstream T3: the judge rejected it for "more of those [dinosaur
--         movies]", correctly, and the eval called it missed supply).
-- A label is added only for a title no film-literate reader would call a dinosaur film, never to clear a
-- turn the judge got wrong.
SELECT d."Id"
  FROM (SELECT t."Id"
          FROM "Titles" t
         WHERE lower(t."Name") ~ 'dinosaur'
            OR EXISTS (SELECT 1 FROM unnest(t."Keywords") k WHERE k ~ 'dinosaur')) d
  JOIN "Titles" t ON t."Id" = d."Id"
 WHERE t."TmdbId" IS DISTINCT FROM 8967 OR t."MediaType" <> 'Movie'
