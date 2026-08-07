---
name: fa-reel-watch
description: "~50 open software defects catalogued 2026-08-07 (search, recommender, data pipeline); sync/ratings still unpaginated; no counter behind the ≥5-external-users gate"
type: watch
last_verified: 2026-08-07
---

# Watch list

- **RESOLVED 2026-08-07:** the `dotnet format` failure on `FeedPaginator.cs` was fixed and pushed; the gate now exits 0 across all 204 files.
- **A full software-defect audit on 2026-08-07 catalogued ~50 confirmed defects** across search, the recommender, the data pipeline, LLM handling and the UI. Remediation is in progress; the plan lives at `~/.claude/plans/fix-all-defects-immidiately-toasty-parrot.md`. The three that reframe the program: search exclusions are applied *after* the candidate pool is cut so negation cannot remove anything; the recommender has two training-time label leaks; and 50% of the catalogue is fed to the trainer with `TraktRating = 0` because one failing title permanently aborts its whole 25-title hydration batch.
- **Other Trakt collection calls remain unpaginated** and are fine only because the data is small today: `sync/ratings` (returned all 3,605, so uncapped) and `users/me/lists` (breaks only above 100 lists). If Trakt extends the pagination change to other sync endpoints, these truncate the same silent way. `search/tmdb/{id}` is a single-match lookup and is safe by construction.
- `docs/product-spec.md` §"FrostAura family look" cites `/docs/brand/assets/` as the canonical asset location; no such directory exists in this repo.
- Success metrics for the commercial half are instrumented nowhere — the "≥5 external users" gate has no counter behind it.
