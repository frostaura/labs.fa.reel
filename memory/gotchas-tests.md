---
name: fa-reel-gotchas-tests
description: "A bare WebApplicationFactory boots with live credentials and bills real money — ReelApiFactory exists for that. The integration fixture DROPS the database it is pointed at. One order-dependent flake is left, and it is upstream."
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
- **The `LiveSearchExpansionTests` semantic-arm pair is an ORDER flake**, roughly one full run in four, always green
  alone and in a class-only run. The cause is the HNSW index shared across collections and it is upstream. **Classify
  by re-running the class alone before touching anything** — this one has been misdiagnosed as a product regression
  more than once.
- **Embedding contamination lives inside the shared HNSW index, where eligibility isolation cannot reach it.** A
  fixture's leftover vectors change which rows an unrelated account's search ever gets to filter, so a synthetic
  genre, a reserved dimension block or a non-English language will not save you. Inherit the embedded-catalogue base
  class; a source guard fails the build otherwise.
- **A day-granular fixture built from `DateTime.UtcNow` plus an offset fails deterministically inside the 23:00 UTC
  hour** — local 01:00–02:00 — and looks exactly like a timezone bug in the product. The product
  keys every day-granular thing on UTC correctly; pin fixture instants to fixed hours of one UTC date.
- **`TenancyModelTests` hard-codes the model's table count.** Any new entity bumps it in the same commit as its
  migration, and two branches that each add a table will both bump it — the integrator sums them rather than taking
  either side.
- **The local-flaky quarantine is currently empty**, and pinned by name so that adding a row is a deliberate decision
  rather than a way to make a red test go away. The skip flag belongs in the local command only; CI's pgvector service
  is authoritative and runs all of them.
- **Verify a failure by observable behaviour — logs, HTTP codes — never by config inspection.** An "embeddings are
  broken" diagnosis read one environment variable, missed the key fallback chain, and steered a whole QA pass at a
  non-bug.
