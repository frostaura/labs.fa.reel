-- Digest arrivals for the surface harness (IREV3S-02). Run by `surfaces.mjs --fresh` on a disposable eval
-- copy, after the Fresh build has drained and before the digest is built.
--
-- Why: the digest announces offers that ARRIVED on a streaming service in the trailing 7 days — a provider
-- set that changed, which `AvailabilitySnapshot` stamps as `FirstSeenAt` when a refresh first sees the
-- offer. A snapshot carries no arrivals (the column was back-filled empty), so without this the digest is
-- empty on every eval copy and the harness cannot measure it at all. This simulates exactly what the
-- refresh would write had the provider set changed yesterday: `FirstSeenAt` one day ago and `FetchedAt` now
-- (a change is only ever seen by a fetch), on every streaming-now offer (Flatrate, Free, Ads) in the
-- account's region of the :n most popular titles that have one.
--
-- It deliberately does NOT pre-filter by eligibility: watched, rated, saved and rule-excluded titles are
-- seeded too, so the digest's own eligibility is what keeps them out — and the harness's leak check sees it
-- when it does not. Deterministic: popularity, then id. Returns the seeded title ids, one per row.
WITH region AS (
    SELECT "Region" FROM "Accounts" WHERE "Id" = :'account'
), picked AS (
    SELECT t."Id"
      FROM "Titles" t
     WHERE t."PosterPath" IS NOT NULL AND t."TmdbId" IS NOT NULL
       AND EXISTS (SELECT 1 FROM "TitleAvailabilities" a
                    WHERE a."TitleId" = t."Id" AND a."Region" = (SELECT "Region" FROM region)
                      AND a."Kind" IN ('Flatrate', 'Free', 'Ads'))
     ORDER BY t."TmdbPopularity" DESC NULLS LAST, t."Id"
     LIMIT :n
), seeded AS (
    UPDATE "TitleAvailabilities" a
       SET "FirstSeenAt" = now() - interval '1 day', "FetchedAt" = now()
     WHERE a."TitleId" IN (SELECT "Id" FROM picked)
       AND a."Region" = (SELECT "Region" FROM region)
       AND a."Kind" IN ('Flatrate', 'Free', 'Ads')
    RETURNING a."TitleId"
)
SELECT DISTINCT "TitleId" FROM seeded ORDER BY 1;
