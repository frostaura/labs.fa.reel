---
name: fa-reel-watch
description: "dotnet format FAILS on FeedPaginator.cs so the next push fails CI; other Trakt calls still unpaginated; no counter behind the ≥5-external-users gate"
type: watch
last_verified: 2026-07-24
---

# Watch list

- **`dotnet format --verify-no-changes` currently FAILS on `FeedPaginator.cs`** (whitespace only, ~15 violations, pre-existing in the uncommitted working set as of 2026-07-24). CI gates on this, so the next push fails until it is run. Unrelated to the Trakt pagination fix.
- **Other Trakt collection calls remain unpaginated** and are fine only because the data is small today: `sync/ratings` (returned all 3,605, so uncapped) and `users/me/lists` (breaks only above 100 lists). If Trakt extends the pagination change to other sync endpoints, these truncate the same silent way. `search/tmdb/{id}` is a single-match lookup and is safe by construction.
- `docs/product-spec.md` §"FrostAura family look" cites `/docs/brand/assets/` as the canonical asset location; no such directory exists in this repo.
- Success metrics for the commercial half are instrumented nowhere — the "≥5 external users" gate has no counter behind it.
