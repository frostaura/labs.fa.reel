---
name: fa-reel-state
description: "QA remediation shipped 2026-08-10; the whole remediation is unpushed on main (4095762..fe29c06), a clean ahead-only fast-forward, still awaiting founder; M2 still NOT beating baseline (iter 7: 0% lift); kill clock past due with zero external users"
type: state
last_verified: 2026-08-21
---

# Current state

_Git state re-inspected 2026-08-21: `main` at `fe29c06`, tree clean, `rev-list --left-right --count origin/main...HEAD` shows ahead-only, zero deletions in the unpushed range, no fetch performed. A stale `.git/index.lock` and `objects/maintenance.lock` are now present and will block a commit until cleared. Product behaviour below was verified 2026-08-10 by direct inspection and a live Playwright/API replay against the rebuilt stack; it was **not** re-run this pass._

**A full QA remediation landed 2026-08-08→10: 14 product commits on `main` (`4095762`..`7232884`) plus the session-close context commit `fe29c06` — the whole range is NOT yet pushed. The push is a verified clean ahead-only fast-forward, founder's call, unchanged since 2026-08-10.** Every finding from the 2026-08-07 user-perspective QA plus Dean's two mid-run additions (rich why-lines, dislike steering) is implemented and live-verified:

- **Trakt writeback is movie-only and reversible** (show rating created ~801 fake episode plays before; verified live: show rating → zero watch trace, movie un-rate → `RemoveFromHistory` dispatched and Trakt confirmed clean). The QA's own residue (801 Simpsons + 1 Family Stone plays) was surgically removed via the new Founder `POST /api/sync/history-cleanup` endpoint on 2026-08-07 and re-verified.
- **Stable-tonight contract**: feed is day-stable (Preserve mode + no-op suppression — verified: two background sync cycles produced byte-identical hero and unchanged `generatedAt`), fresh across days / on manual refresh / filter changes / retrain.
- **Negations are hard**: contradicting titles are dropped or quarantined under a labeled "Outside your request" channel with deterministic captions; TV-horror taxonomy gap (TMDB TV has no horror genre) closed via Subgenres/Keywords matching (`06016fc`). Live-verified: zero horror/ghost leakage in main results by API cross-check.
- **PIN**: DataProtection unlock tokens; raw PIN never cached client-side; destructive ops re-prompt. Live matrix verified (423/200/token/legacy).
- **New features live**: global TMDB typeahead + on-demand title hydration (Jeanne Dielman test passes, 705ms hydrate with prediction), weekly digest + Web Push (VAPID, injectManifest SW), Blend (two-person joint picks; self-preview verified, unknown code 404s), reference-title anchoring, runtime/mood intents, expandable why-not contribution details, card quick actions, rebuilt branded share card (full-height, console-clean, self-hosted Figtree), dislike-reason demotion across all scoring surfaces (schema v3 — forces per-account retrain on deploy).
- **Why-lines**: build-time LLM whys persisted on FeedItems, anchored and title-specific, anti-recommendation guard + hero fit-floor swap, sentence-cased templater fallback, generic sentence max once per response (`7232884`).

**M2 remains unproven**: iteration 7 (2026-08-07, post-remediation ratings) = 30% vs 30%, 0% lift; iterations 5–6 were −33%. The kill-criterion question (3 iterations named, 7 run) is still the open founder call. The remediation improved the *product surface*, not the model's edge.

**Dean actively used the app during 2026-08-08→10** (conditions now: 8 excluded genres, min ratings 6/6, min year 2000 — his own settings, not QA residue; feed-build burst on 08-10 pre-deploy). Weekend usage ran on the P1-fixed image, so no show-rating history corruption occurred.

Backend tests: 397 passing + 6 known-flaky local integration failures (CandidateRetriever ×2, old LiveSearchExpansion ×2, LexicalSearch, EnrichCatalog — pre-existing, identical on old HEADs, CI's pgvector service is authoritative). Frontend: 181 unit + 19 e2e green. All 4 new migrations applied on the running stack.

M4 multi-tenant remains code-complete with **zero external users**, and the app is not deployed. The ≥5-external-users kill clock (~2026-08-11/18) has now **passed** on either anchor. The 2026-07-24 deploy decision was never executed, so the criterion has been neither met nor honestly evaluated — see `questions.md`.
