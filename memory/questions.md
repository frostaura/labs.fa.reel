---
name: fa-reel-questions
description: "All founder-owned. Ask Reel: four locks the programme kept conservatively (filter override, erasure carve-out, MCP identity, ad-hoc lists) plus pricing tier/LLM ceiling, the model lock on REEL_SEARCH_MODEL, deleting the 15 probe conversations (now via the endpoint), push. Standing: deploy date, spoiler tolerance, MovieLens, /rate, Trakt commercial terms, JustWatch" (filter override in chat, erasure vs append-only exposures, chat pricing tier + LLM ceiling, whether the model lock covers REEL_SEARCH_MODEL, MCP surface vs Trakt-only identity, ad-hoc lists, delete the probe conversations, push); plus deploy date with the kill clock past, spoiler tolerance, MovieLens vs dropping CF, whether /rate stays live; Trakt commercial terms existential, JustWatch unmade"
type: question
last_verified: 2026-09-11
---

# Open questions

All of these are founder-owned. The Ask Reel programme (2026-09-11) took the four lock-touching ones conservatively — see `decisions.md`; reversing any is a founder call.

## From the Ask Reel panel (2026-09-10), as they stand after the programme

- **Filter override in chat.** Keep exclusions airtight (spec §4, the settings PIN exists for kids' accounts), or allow a per-turn, explicitly tapped "just this once" that is unavailable while the PIN is set. A default-on override was refused by the panel. Rungs 1–2 (name the collision, show the conditions) need no decision.
- **Erasure vs append-only exposures.** Conversation delete now exists (`DELETE /api/search/conversations/{id}`); export and whole-account erasure do not. The programme's design is pseudonymisation (rewrite `AccountId` on exposures to a tombstone), which still touches the row — decide before the first external user.
- **Pricing position of Ask Reel and the real LLM ceiling.** The spec puts natural-language search behind the paywall; the endpoint has no tier check. Proposed: Free = heuristic rung (zero LLM), Paid = 40 calls / 60k tokens per day, Founder = 200 / 400k. Is ≤$0.25/account-month the real budget? At measured ≈$0.003/turn that is ~80 turns.
- **Does the `gpt-5.4-mini` lock extend to `REEL_SEARCH_MODEL`?** Unstated. Rerank is ≈74% of a turn's chat tokens and the only end of the pipe where a different model would matter.
- **An API/MCP surface for Gaia** collides with "Sign in with Trakt only". Decide before any demand test.
- **Ad-hoc lists from chat.** Spec §6: one list per profile with a defined auto-removal contract. Define it for extra lists or refuse.
- **The panel's test conversations.** 15 sit in the live DB (ids in `~/.claude/plans/ask-reel-panel-2026-09-10/probe/test-conversation-ids.txt`). The delete endpoint and `Recent ▾` now exist; delete them through the UI or the endpoint, never SQL.
- **Push.** The whole 2026-09 range — five migrations — exists on one Mac; `main` is ahead-only, a push is a fast-forward. Re-measure with `git rev-list --left-right --count @{u}...HEAD`; never quote a count from here.

## Standing

- **Deploy date.** The 2026-07-24 deploy decision has never been executed, and it is what makes the ≥5-external-users criterion evaluable at all. M4 is code-complete, the external-user count is zero, and the clock passed around 2026-08-11/18 on either anchor. Deploy and re-anchor at first external serve, or apply the criterion as written. _(The modelling half was **decided 2026-09-09** — "both, model first".)_ The panel added a blocker: the Portainer deploy file must be fixed first (see `gotchas.md`).
- **Spoiler tolerance for lens copy.** Assumed first-act only with a spoiler lexicon post-filter; Dean has not set the bar.
- **MovieLens licence request, or drop item-item CF.** Retrieval recall (25.5% ceiling) is the binding constraint, so a public-corpus arm is more interesting than it was — but it is a licensing decision.
- **Does `/rate` stay live ahead of an edge proof?** Rebuilt around watched-but-unrated titles in Phase 1; whether it belongs in front of a user before the model beats a baseline is a product call.
- **Trakt commercial terms** remain an open M5 gate and could kill the product outright. The **JustWatch deep-link decision** is still unmade.
