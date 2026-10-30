-- SQL truth for the Ask Reel golden eval. Independent of the pipeline: it reads only the catalogue.
-- Returns one column, "Id", of every catalogue title that is a heist as a person means "heist": named a
-- heist, or tagged one by TMDB ("heist", "bank heist", "jewelry heist", "heist gone wrong" …).
-- truth/heist.sql stays the TAG population the reply counts, which also reads the generic robbery tags
-- ("robbery", "armed robbery", "bank robbery") exactly as the app's heist synonym group does; those tags
-- mark any film with a robbery in it — in fix-golden-v2-2 the clearing robbery-only pool left after
-- the heists already shown was Blood Work (a serial-killer thriller), Criminal (2016, a memory-transplant
-- spy thriller), Animal Kingdom, Road to Perdition and Wonderland, none a heist film, and the judge
-- rejected them, correctly. This set is the scenarios' `meant`, which only missed supply reads. It errs
-- narrow: a heist film tagged only "bank robbery" is not counted as missed supply.
SELECT t."Id"
  FROM "Titles" t
 WHERE lower(t."Name") ~ '\mheists?\M'
    OR EXISTS (SELECT 1 FROM unnest(t."Keywords") k WHERE k ~ '\mheist')
