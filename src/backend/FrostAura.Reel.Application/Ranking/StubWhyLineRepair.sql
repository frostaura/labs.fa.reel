-- CR-9 stub why-line repair (WS-5, trust-v1). Optional, one-off, run by hand on the live database
-- AFTER the pre-migration dump and AFTER migrations M5/M1/M3/M4 have applied:
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f StubWhyLineRepair.sql
--
-- What it does. Blanks the deterministic stub sentence a failed rerank batch wrote — "A {genre}
-- pick aligned with your taste." — and the templater's retired generic sentence ("Strong fit with
-- your taste profile.", cut back to its anchor clause where it was spliced after one) on the cards
-- of each account's ACTIVE snapshot, so the stored
-- row says what Home already serves: the serve-time guard (FeedWhyGuard.IsServable) has shown
-- these lines as no line at all since this image, and an empty WhyThisSentence renders as a card
-- with no why-line (no placeholder, no generic sentence).
--
-- What it never touches.
--   * The exposure log ("ExposureEvents"). It is append-only; stub rows there are excluded at read
--     time by the FD-18 filter (eval/askreel/fd18-exclusions.json, stubSentenceRegex.why), whose
--     pattern matches every row this one does (3,712 of 3,712 on the 2026-10-23 eval snapshot, and
--     no row either pattern matches that the other misses).
--   * Superseded snapshots. They are never served; retention prunes them.
--   * "WhySource". A blanked card keeps the provenance it was written with — the failure it records
--     is still true.
--
-- Interaction with the deploy purge (UntrustedSnapshotSweep). The purge counts a refused line, not
-- an empty one, so after this repair a snapshot is rebuilt only for its other findings (rows with
-- no stored why-source, unexplained anchored cards, captioned fallbacks). Running the repair first
-- therefore never adds a paid build; it can only remove one.
--
-- Idempotent: a second run matches nothing. Reviewed against FeedWhyGuard's stub regex and
-- exercised on real Postgres by StubWhyLineRepairTests.

BEGIN;

UPDATE "FeedItems" i
SET "WhyThisSentence" = ''
FROM "FeedSnapshots" s
WHERE s."Id" = i."FeedSnapshotId"
  AND s."Status" = 'Active'
  AND i."WhyThisSentence" ~ '^A .+ pick aligned with your taste\.$';

-- The templater's retired generic sentence (ExplanationTemplater.GenericFallback, 2026-10-26).
-- Spliced after an anchor clause it is cut back to the anchor clause alone — exactly the line the
-- templater now writes for that card — and standing alone it is blanked, like the stub above.
UPDATE "FeedItems" i
SET "WhyThisSentence" = regexp_replace(i."WhyThisSentence", ', and strong fit with your taste profile\.?$', '.', 'i')
FROM "FeedSnapshots" s
WHERE s."Id" = i."FeedSnapshotId"
  AND s."Status" = 'Active'
  AND i."WhyThisSentence" ~* '^.+, and strong fit with your taste profile\.?$';

UPDATE "FeedItems" i
SET "WhyThisSentence" = ''
FROM "FeedSnapshots" s
WHERE s."Id" = i."FeedSnapshotId"
  AND s."Status" = 'Active'
  AND i."WhyThisSentence" ~* '^strong fit with your taste profile\.?$';

COMMIT;
