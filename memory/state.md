---
name: fa-reel-state
description: "2026-08-10: FULL QA remediation shipped — 14 unpushed commits on main (4095762..7232884) fixing all 2026-08-07 QA findings + Dean's why-line/dislike additions; live-verified against the running stack; push is a clean fast-forward awaiting founder; M2 still NOT beating baseline (iter 7: 0% lift)"
type: state
last_verified: 2026-08-10
---

# Current state

_Git facts and live-stack behavior below verified 2026-08-10 by direct inspection (`git rev-list --left-right --count @{u}...HEAD` → `0 14`, clean tree, no lock debris, zero deletions in the unpushed range) and by a live Playwright/API replay against the rebuilt stack._

**A full QA remediation landed 2026-08-08→10: 14 commits on `main` (`4095762`..`7232884`), NOT yet pushed — push is a verified clean fast-forward (0 behind / 14 ahead), founder's call.** Every finding from the 2026-08-07 user-perspective QA plus Dean's two mid-run additions (rich why-lines, dislike steering) is implemented and live-verified:

- **Trakt writeback is movie-only and reversible** (show rating created ~801 fake episode plays before; verified live: show rating → zero watch trace, movie un-rate → `RemoveFromHistory` dispatched and Trakt confirmed clean). The QA's own residue (801 Simpsons + 1 Family Stone plays) was surgically removed via the new Founder `POST /api/sync/history-cleanup` endpoint on 2026-08-07 and re-verified.
- **Stable-tonight contract**: feed is day-stable (Preserve mode + no-op suppression — verified: two background sync cycles produced byte-identical hero and unchanged `generatedAt`), fresh across days / on manual refresh / filter changes / retrain.
- **Negations are hard**: contradicting titles are dropped or quarantined under a labeled "Outside your request" channel with deterministic captions; TV-horror taxonomy gap (TMDB TV has no horror genre) closed via Subgenres/Keywords matching (`06016fc`). Live-verified: zero horror/ghost leakage in main results by API cross-check.
- **PIN**: DataProtection unlock tokens; raw PIN never cached client-side; destructive ops re-prompt. Live matrix verified (423/200/token/legacy).
- **New features live**: global TMDB typeahead + on-demand title hydration (Jeanne Dielman test passes, 705ms hydrate with prediction), weekly digest + Web Push (VAPID, injectManifest SW), Blend (two-person joint picks; self-preview verified, unknown code 404s), reference-title anchoring, runtime/mood intents, expandable why-not contribution details, card quick actions, rebuilt branded share card (full-height, console-clean, self-hosted Figtree), dislike-reason demotion across all scoring surfaces (schema v3 — forces per-account retrain on deploy).
- **Why-lines**: build-time LLM whys persisted on FeedItems, anchored and title-specific, anti-recommendation guard + hero fit-floor swap, sentence-cased templater fallback, generic sentence max once per response (`7232884`).

**M2 remains unproven**: iteration 7 (2026-08-07, post-remediation ratings) = 30% vs 30%, 0% lift; iterations 5–6 were −33%. The kill-criterion question (3 iterations named, 7 run) is still the open founder call. The remediation improved the *product surface*, not the model's edge.

**Dean actively used the app during 2026-08-08→10** (conditions now: 8 excluded genres, min ratings 6/6, min year 2000 — his own settings, not QA residue; feed-build burst on 08-10 pre-deploy). Weekend usage ran on the P1-fixed image, so no show-rating history corruption occurred.

Backend tests: 397 passing + 6 known-flaky local integration failures (CandidateRetriever ×2, old LiveSearchExpansion ×2, LexicalSearch, EnrichCatalog — pre-existing, identical on old HEADs, CI's pgvector service is authoritative). Frontend: 181 unit + 19 e2e green. All 4 new migrations applied on the running stack.

M4 multi-tenant remains code-complete with **zero external users**; the ≥5-external-users kill clock (~2026-08-11/18) is now due.
