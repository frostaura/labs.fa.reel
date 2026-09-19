---
name: fa-reel-gotchas-integrations
description: "TMDB's TV taxonomy has NO horror genre, Trakt paginated the watched endpoints and broke ingest with a 200 OK, and the API-usage ledger is day-grained with one purpose written by four callers — so per-turn cost cannot be divided out."
type: gotcha
last_verified: 2026-09-11
---

# Gotchas — external data and cost accounting

- **TMDB's TV genre taxonomy has NO horror genre.** American Horror Story arrives as drama/mystery/sci-fi. Anything
  matching genre terms against the title's genres alone is blind to TV horror — subgenres and keywords carry the truth
  — and the same blind spot applies to every genre-driven feature for TV.
- **Trakt paginated the watched endpoints and changed the `extended` defaults after 2026-06-30, and it broke ingest
  silently.** `sync/watched/movies|shows` returned **200 OK with only the first page and no seasons**, while ratings
  came through in full — which is what made it look like a display bug rather than an ingest failure. Fixed by walking
  the page-count header and requesting `extended=progress` on shows. Two residual traps: `extended=full` is now
  ignored, and `extended=progress` is capped at 100 per page while the other watched calls allow 250.
- **The API-usage ledger is day-grained and one purpose is written by four callers.** Rows are keyed by provider, day,
  account, model and purpose with no per-call detail, and the `rerank` purpose is written by two feed-build paths, the
  paginator and the live search expansion. Dividing a day's totals by a guessed turn count produced three different
  "tokens per turn" figures, all wrong. **Measure one turn in a window with the job queue empty**, and split the
  purpose before building any per-surface budget.
- **Another session may be driving the app while you probe.** During one panel a rating and several feed builds
  appeared mid-probe; the rating's source column and the sync job's trigger reason identified a real second client —
  the founder's own parallel session. **Attribute database writes by those columns, never by timing.**
- **Attribution is a temporal join because only about 2% of ratings are made in-app.** Most of the exposure surfaces
  cannot be joined back to what served them, which is why the append-only exposure log and its derived rollups exist
  at all (`decisions-model.md`).
