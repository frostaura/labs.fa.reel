---
name: labs-fa-reel-decisions-engineering
description: "Invariant formatting is guarded at source because TMDB silently accepts a comma decimal. The npm-audit waiver list is empty since react-router 8.3; a stack-smoke job gates every image publish; CI has no cron, by founder rule."
type: decision
last_verified: 2026-11-20
---

# Live decisions — engineering and infrastructure

## Correctness guards
- **Every number the backend formats is invariant, guarded at source rather than by convention.** *Why:* a comma
  decimal is a silent wrong answer — TMDB ignores a malformed parameter and returns a valid, unfiltered page — so
  review cannot be trusted to catch it. The rule and its three defences are in [`../CLAUDE.md`](../CLAUDE.md).
  **One deliberate exception:** the `Convert.ToDecimal`/`ToInt32` calls in
  `EligibilityQueryBuilder` stay provider-less, because EF translates only the single-argument overloads and a
  provider makes the predicate untranslatable — dozens of integration tests go red.
- **`hnsw.iterative_scan = 'strict_order'` is issued on every connection by `HnswFilteredSearchInterceptor`,
  registered in `ReelDbContext.OnConfiguring` — not from DI.** *Why:* the flaw belongs to every filtered vector query in the codebase, not to one arm, so a per-call-site
  `SET LOCAL` would need its own transaction and would be forgotten by the next arm added. Wiring it in
  `OnConfiguring` means a context built by hand — which is exactly what the integration fixture does — cannot have a
  different retrieval contract from production, so the suite guards the thing that actually ships. `strict_order` over
  `relaxed_order` because both call sites treat the result as distance-ordered. The statement is swallowed on failure:
  a retrieval setting must never be why a connection cannot open.
- **Embedding test isolation is a shared base class, not a fourth per-fixture patch.** *Why:* three fixtures had each
  independently diagnosed and fixed their own half of the contamination, and the victim broke again the moment a
  fourth embedded fixture landed. Per-fixture care does not compose over a shared index. The base snapshots on init
  and removes the difference on dispose — never a truncate, so one class cannot destroy rows another still needs — and
  a source guard fails the build on a class that seeds embeddings without it.
- **"It only fails locally" is a hypothesis, not a diagnosis.** *Why:* every test ever put in the local-flaky
  quarantine turned out to be a real defect — a fake client returning a null language, an assertion for a contract
  that had been deliberately deleted. The quarantine is pinned by name so adding a row stays a deliberate act; it held
  no tests at the 2026-11-20 inspection of `main`.
- **Backtest tests seed per test instead of caching a static account.** *Why:* per-test embedding cleanup and a
  class-lifetime seed cannot both be true, and the static was already wrong in a quieter way — the class's scope genre
  is per-instance, so every test after the first ran against an account configured for a different scope.

## Security
- **The npm audit gate allows narrow, EXPIRING waivers — and the allowlist is empty, which is the goal state.** The
  gate is `npm audit --audit-level=high` plus named waivers that each carry a reason and a review date and **fail once
  that date passes**. The one waiver that existed (react-router's RSC-mode CSRF advisory, which had no clean version)
  carried its own exit condition and was removed on 2026-09-19 when react-router 8.3 shipped outside the affected
  range. *Why the mechanism stays:* without it the only answer to an advisory with nothing to move to is deleting
  the gate. **Adding a waiver is a cybersecurity decision, which root policy places at parent level.**
- **Vulnerable transitives get an explicit pin, or a whole-set version move — never a suppression.** *Why:* NU1903
  under `TreatWarningsAsErrors` fails `dotnet restore` outright, so an advisory published while the repo sits idle
  makes it unbuildable; that is exactly how `main` stopped building between 2026-06-18 and 2026-08-01. Each pin is
  commented to be dropped once the framework package carries the patched transitive.

## CI and deploy
- **A `Docker Build & Stack Smoke` job gates every publish job and runs on PRs** (PR #1, 2026-09-19). *Why:* the
  Dockerfiles and compose wiring were only exercised by the push-to-main publish jobs, so a broken image or gateway
  route passed the PR and was found only after a bad `:latest` shipped. It also parses `docker-compose.prod.yml`,
  the literal Portainer stack file, which nothing else in CI read.
- **No scheduled CI run.** A weekly "rot check" was proposed and dropped before merge under the founder's no-cron
  rule. The idle-repo blind spot is accepted: the first push after a quiet month may fail on a new advisory.
- **`proxy_pass` in the Portainer stack's nginx config must never carry a URI, and a parity test guards it.** *Why:*
  a URI on `proxy_pass` replaces the matched location prefix, so `/api/health` arrived as `/health` and the deployed
  stack would have 404'd every call. The deploy itself is a founder action (`decisions-product.md`).
