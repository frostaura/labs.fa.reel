-- SQL truth for the Ask Reel golden eval. Independent of the pipeline: it reads only the catalogue.
-- Returns one column, "Id", of every catalogue title that is about ninjas as a person means "ninja":
-- truth/ninja.sql minus the Ninja Warrior obstacle-course competition format, whose "ninja" names the
-- athletes, not the warrior (American Ninja Warrior is the catalogue's only such title, a reality show
-- matched by its name alone). truth/ninja.sql stays the TAG population the reply counts ("You have 1
-- unseen ninja show tagged that way, but none of the ones I checked is really a match"); this set is
-- the scenarios' `meant`, which only missed supply reads (fix-golden-v2-2, ninja-tv: the judge
-- rejected American Ninja Warrior for "ninja shows", correctly, and the eval called it missed supply).
SELECT n."Id"
  FROM (SELECT t."Id"
          FROM "Titles" t
         WHERE lower(t."Name") ~ '\mninjas?\M|shinobi|kunoichi'
            OR EXISTS (SELECT 1 FROM unnest(t."Keywords") k WHERE k ~ '\mninjas?\M|shinobi|kunoichi')) n
  JOIN "Titles" t ON t."Id" = n."Id"
 WHERE t."Name" !~* '\mninja warriors?\M'
