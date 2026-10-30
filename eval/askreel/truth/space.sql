-- SQL truth for the Ask Reel golden eval. Independent of the pipeline: it reads only the catalogue.
-- Returns one column, "Id", of every catalogue title that satisfies: space (set in or about space travel).
-- TMDB keyword tags are noisy (Batman Begins carries "ninja"), so this set bounds precision from above
-- and is never a recall target (program §1, C*4).
-- The keyword arm is the app's space synonym group (ConceptSynonyms) matched from a word start to a word
-- end or an inflection, as HardConstraintFilter.KeywordPattern reads it; the name arm reads only the
-- request's own word, as the app's does (HREV3-RET-A). Before fix-golden-v2-2 the arm left out the bare
-- "space" label (219 titles) and "spacecraft" (104) — "space opera", "space colony", "deep space" were not
-- space here — so the app's correct counts for space-isolation-chain T2/T3 (110 films and 27 shows before
-- 2000, at 50 votes) read as wrong supply counts and missed supply.
SELECT t."Id"
  FROM "Titles" t
 WHERE t."Name" ~* '\mspaces?\M'
    OR EXISTS (SELECT 1 FROM unnest(t."Keywords") k
                WHERE k ~ '\m(space|outer space|astronaut|space station|space travel|spaceship|spacecraft|space mission)(e?s|e?rs?|ings?|e?d)?\M')
