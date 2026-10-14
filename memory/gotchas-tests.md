---
name: fa-reel-gotchas-tests
description: "A bare WebApplicationFactory boots with live credentials and bills real money — ReelApiFactory exists for that. The integration fixture DROPS the database it is pointed at. A vector flake that looks order-dependent is a VACUUM problem."
type: gotcha
last_verified: 2026-09-11
---

# Gotchas — tests and fixtures

- **A bare `WebApplicationFactory<Program>` boots this app with the real `.env` and spends real money.**
  `IntegrationTests/ReelApiFactory.cs` is the HTTP harness (the rule is in [`../CLAUDE.md`](../CLAUDE.md)); what it
  actually handles, and therefore what you lose by going around it: it blanks every provider key in a static
  constructor *before* the env loader runs — which never overrides an already-set variable — forces stub model mode,
  swaps the embedding provider for the shared fake, removes every hosted service, points DI at the Postgres fixture,
  and **refuses to boot if a live credential is still in configuration**. Ledger assertions read
  `IPendingApiUsage.PendingToday`, because the recorder's flush is `internal`.
- **The integration fixture drops and re-migrates the database it is given.** Throwaway `reel_test_*` databases live
  on port 5433 for parallel worktrees; pointing the variable at `reel` destroys the founder's live data. Absent the
  variable the DB tests skip in silence, which is why CI sets the require-integration flag — a suite reporting green
  having run nothing is worse than a red one. Both rules are in [`../CLAUDE.md`](../CLAUDE.md); what the throwaway
  copies cost is a watch item.
- **CI's integration job filters on the `IntegrationTests` namespace.** A DB-backed test written anywhere else
  silently never runs in CI and reports green from the unit job.
- **A DELETE leaves the element in pgvector's HNSW graph until `VACUUM`, and `hnsw.iterative_scan` cannot reach past
  it.** This is what the `LiveSearchExpansionTests` "semantic-arm order flake" actually was: dead entries left by
  `EmbeddedCatalogTestBase.DisposeAsync` crowded the candidate window for the next class, so a test was green alone and
  red in a full run. Fixed by `RepairSharedVectorIndexAsync` — `VACUUM "TitleEmbeddings"` — on every teardown;
  `EnrichCatalogJobHandlerTests` and `CatalogFollowThroughTests` inherit the base for it, and `FilteredAnnRecallTests`
  carries the regression test. **A vector-index flake that looks order-dependent is a VACUUM problem before it is a
  product regression.**
- **Never seed `TitleEmbeddings` without `EmbeddedCatalogTestBase`.** A fixture's leftover vectors change which rows an
  unrelated account's search ever gets to filter, so a synthetic genre, a reserved dimension block or a non-English
  language will not save you; eligibility isolation cannot reach inside the shared index. The source guard now matches
  any mention of the table, so going around the base class fails the build.
- **A day-granular fixture built from `DateTime.UtcNow` plus an offset fails deterministically inside the 23:00 UTC
  hour** — local 01:00–02:00 — and looks exactly like a timezone bug in the product. The product
  keys every day-granular thing on UTC correctly; pin fixture instants to fixed hours of one UTC date.
- **`TenancyModelTests` hard-codes the model's table count.** Any new entity bumps it in the same commit as its
  migration, and two branches that each add a table will both bump it — the integrator sums them rather than taking
  either side.
- **Verify a failure by observable behaviour — logs, HTTP codes — never by config inspection.** An "embeddings are
  broken" diagnosis read one environment variable, missed the key fallback chain, and steered a whole QA pass at a
  non-bug.
