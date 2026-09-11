---
name: fa-reel-decisions-engineering
description: "Invariant formatting is guarded at source because a comma decimal is a wrong answer TMDB silently accepts. The npm audit gate carries one expiring react-router waiver — reviewing it at expiry is a parent-level call, never a silent renewal."
type: decision
last_verified: 2026-09-11
---

# Live decisions — engineering and infrastructure

## Correctness guards
- **Every number the backend formats is invariant, and a source-level guard enforces it.** *Why:* nothing here is
  localised — prompts, outbound query strings, cache keys, stored filter values, SSE payloads and job messages are
  machine-read or compared as exact strings, so a comma decimal separator is a wrong answer rather than a different
  rendering, **and a silent one, because TMDB ignores a malformed parameter and returns a valid, unfiltered page**.
  Three defences, all load-bearing: the host-start culture pin, an explicit invariant contract at each formatting
  site, and a test that greps the sources for the shapes the analyzer cannot see. The rule is in
  [`../CLAUDE.md`](../CLAUDE.md). **One deliberate exception:** the `Convert.ToDecimal`/`ToInt32` calls in
  `EligibilityQueryBuilder` stay provider-less, because EF translates only the single-argument overloads and a
  provider makes the predicate untranslatable — dozens of integration tests go red.
- **`hnsw.iterative_scan = 'strict_order'` is set on every connection from `ReelDbContext.OnConfiguring`, not from
  DI.** *Why:* the flaw belongs to every filtered vector query in the codebase, not to one arm, so a per-call-site
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
  that had been deliberately deleted. The quarantine is pinned by name so adding a row stays a deliberate act, and it
  is currently empty.
- **Backtest tests seed per test instead of caching a static account.** *Why:* per-test embedding cleanup and a
  class-lifetime seed cannot both be true, and the static was already wrong in a quieter way — the class's scope genre
  is per-instance, so every test after the first ran against an account configured for a different scope.

## Security
- **The npm audit gate allows narrow, EXPIRING waivers, and one is active.** The gate is `npm audit
  --audit-level=high` plus named waivers that each carry a reason and a review date, and it **fails once that date
  passes** — verified non-vacuous against both an un-waived high and an expired waiver. *Why one exists:* react-router
  has no clean version; the advisories overlap the whole usable range, and Reel is a client-only SPA with no RSC entry
  point, so the RSC-mode CSRF path is unreachable while the open redirect is not. The active waiver is reviewable
  **2026-11-07**. **Waiving an advisory is a cybersecurity decision, which root policy places at parent level — this
  one needs founder review at expiry, never a silent renewal.**
- **Vulnerable transitives get an explicit pin, not a suppression**, each commented to be removed once upstream ships
  a patched transitive. The `security` CI job fails the build otherwise.

## Deploy
- **`proxy_pass` in the Portainer stack's nginx config must never carry a URI, and a parity test guards it.** *Why:*
  a URI on `proxy_pass` replaces the matched location prefix, so `/api/health` arrived as `/health` and the deployed
  stack would have 404'd every call. The deploy itself is still a founder action (`decisions-product.md`).
