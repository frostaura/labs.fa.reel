---
name: fa-reel-questions
description: "Dean's, bar one. Deploy blocks the rest — undeployed, the external-user kill criterion cannot be evaluated. Confirm the committed, already-live gpt-6-luna swap is the founder lock. Trakt commercial terms can still end the product."
type: question
last_verified: 2026-10-22
---

# Open questions

**Owner: Dean Martin (founder), for every question here except the one marked unassigned** — each changes the spec,
spends money or touches a founder lock. Where the 2026-09-11 Ask Reel programme had to act first, it took the
conservative side and recorded the alternative — `decisions-chat.md`.

## Blocking everything else
- **Deploy date.** Decided 2026-07-24, never executed, and it is what makes the "≥5 external users" criterion
  evaluable at all. External users are zero and the 60-day clock has passed on either anchor: deploy and re-anchor at
  the first external serve, or apply the criterion as written and kill. **Nothing technical is in the way** — CI is
  green and publishing; the redeploy job skips because `DEPLOY_ENABLED` and `PORTAINER_STACK_ID` are still unset
  (re-checked 2026-10-22). Steps: the repo's `docs/deployment.md`.

## The M2 bar is written in a metric the harness no longer reads
- **The research question, milestone 2 and the modelling kill criterion all say "≥20% relative precision@10";
  the implemented gate reads NDCG@10 lift**, because precision@10 is quantised to 0.1 on a top-10 cut and its lift
  lattice moves with the baseline. Restate the three in NDCG lift, or record why precision@10 stays the bar.

## Existential, and not ours to decide
- **Trakt commercial terms** — an open milestone-5 gate that could end the product: identity, history and write-back
  all run through Trakt. **The JustWatch deep-link decision** is unmade; badges and provider-side search links are
  legal today, true deep links need a partnership, scraping is out.

## Ask Reel — the four locks the programme deliberately did not touch
- **Filter override in chat:** airtight, or a tapped "just this once" disabled under the settings PIN (never default-on).
- **Erasure versus the append-only exposure log:** conversation delete exists, export and erasure do not; the design
  (pseudonymisation) still rewrites an append-only row. **Decide before the first external user.**
- **An API or MCP surface for Gaia** collides with "Sign in with Trakt only" — decide before any demand test.
  **Ad-hoc lists from chat** need the one-list auto-removal contract extended, or a refusal.

## Models and cost
- **Is the extraction model now `openai/gpt-6-luna`? Confirm it.** The local stack runs and bills it (through
  2026-10-22), and on 2026-10-22 the dev clone committed the swap with the repo `CLAUDE.md` lock rewritten — under
  the git identity every agent session also uses, so not proof of a founder call. Published `main` still defaults to
  `gpt-5.4-mini`. The id keys re-extraction and the chat caches, so the catalogue may already be
  re-pricing (not measured). Confirm and the lock arrives here by merge; reverse it and the commit and compose `.env`
  both need undoing — `state.md`. **Does the lock extend to the search model?** Unstated; the swap changes both, and
  rerank is most of a chat turn's tokens.
- **The pricing position of Ask Reel, and the real LLM ceiling.** The spec puts natural-language search behind the
  paywall; the endpoint has no tier check, so Free gets the whole pipeline. The number needed is the per-account
  monthly budget — at the measured per-turn cost, a quarter-dollar buys roughly eighty turns.

## Deployed surfaces — Owner: unassigned
- **Should any `/api/dev` route be reachable on a deployed stack?** All of them (seed, lens audit, retrieval recall)
  map only under `Development` plus `QA_HELPERS_ENABLED`, so a `Production` deploy has no seed trigger and no
  lens-audit surface — where the 50-read lens review bar is meant to be met.

## Standing product and research calls
- **Should a typed negation be airtight like a standing one?** By contract it is soft. **And should "only shows"
  move the All/Movies/TV control?** It shapes the intent, not the hard filter — correct by contract, surprising.
- **Spoiler tolerance for lens copy** — assumed first-act only, never set. **MovieLens licence, or drop item-item CF?**
- **Does `/rate` stay live ahead of an edge proof?** Rebuilt around watched-but-unrated titles; a product call.
