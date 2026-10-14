---
name: fa-reel-questions
description: "All Dean's. Deploy blocks the rest — undeployed, the external-user kill criterion cannot be evaluated. New: is the uncommitted gpt-6-luna model swap a founder decision? Trakt commercial terms can still end the product."
type: question
last_verified: 2026-10-14
---

# Open questions

**Owner: Dean Martin (founder), for every question on this page.** None of them is an agent's to answer: each one
either changes the spec, spends money, or touches a founder-locked rule. Where the 2026-09-11 Ask Reel programme had
to act before an answer existed, it took the conservative side and recorded the alternative — `decisions-chat.md`.

## Blocking everything else
- **Deploy date.** Decided 2026-07-24, never executed, and it is what makes the "≥5 external users" criterion
  evaluable at all. External users are zero and the 60-day clock has passed on either anchor: deploy and re-anchor at
  the first external serve, or apply the criterion as written and kill. **Nothing technical is in the way** — CI is
  green and publishing; the redeploy job skips because `DEPLOY_ENABLED` and `PORTAINER_STACK_ID` are still unset
  (re-checked 2026-10-14). Steps: the repo's `docs/deployment.md`.

## The M2 bar is written in a metric the harness no longer reads
- **The research question, milestone 2 and the modelling kill criterion all say "≥20% relative precision@10";
  the implemented gate reads NDCG@10 lift.** precision@10 was retired in the instrument repair — quantised to
  0.1 on a top-10 cut, so its achievable lift lattice moves with the baseline and the bar as written is
  near-unusable at the measured one. `CLAUDE.md`'s *description of the harness* is corrected; the founder-set
  wording is not. Restate the three in NDCG lift, or record why precision@10 stays the stated bar.

## Existential, and not ours to decide
- **Trakt commercial terms.** An open milestone-5 gate that could end the product outright — Reel's identity, history
  and write-back all run through Trakt.
- **The JustWatch deep-link decision** is still unmade. Provider badges and provider-side search links are legal
  today; true deep links need a partnership, and scraping is out.

## Ask Reel — the four locks the programme deliberately did not touch
- **Filter override in chat.** Keep standing exclusions airtight, or allow a per-turn, explicitly tapped "just this
  once" that is unavailable while the settings PIN is set. A default-on override was refused outright. Naming the
  collision and showing the conditions needs no decision.
- **Erasure versus the append-only exposure log.** Conversation delete exists; export and whole-account erasure do
  not. The designed approach is pseudonymisation, which still rewrites an append-only row. **Decide before the first
  external user**, not after.
- **An API or MCP surface for Gaia** collides with "Sign in with Trakt only". Decide before any demand test.
- **Ad-hoc lists from chat** need the spec's one-list auto-removal contract extended, or a refusal.

## Models and cost
- **Is the extraction model now `openai/gpt-6-luna`?** Uncommitted dev-clone edits swap every default away from the
  locked `gpt-5.4-mini` and call it "founder-locked 2026-10-14"; published `main` and this scope's `CLAUDE.md` do
  not. Those edits say the id keys re-extraction and the chat caches (unverified), so the swap may re-price the
  whole catalogue. Commit it with the rule change, or discard it — topology in `state.md`.
- **Does the extraction-model lock extend to the search model?** Unstated — the uncommitted swap changes both.
  Rerank is most of a chat turn's tokens and the only end of the pipe where a different model would matter.
- **The pricing position of Ask Reel, and the real LLM ceiling.** The spec puts natural-language search behind the
  paywall; the endpoint has no tier check, so Free gets the whole pipeline. The number needed is the per-account
  monthly budget — at the measured per-turn cost, a quarter-dollar buys roughly eighty turns.

## Standing product and research calls
- **Should a typed negation be airtight like a standing one?** By contract it is soft. **And should "only shows"
  move the All/Movies/TV control?** It shapes the intent, not the hard filter — correct by contract, surprising.
- **Spoiler tolerance for lens copy** — assumed first-act only; the bar has never been set.
- **MovieLens licence request, or drop item-item CF** — supply is the binding constraint; a licensing call.
- **Does `/rate` stay live ahead of an edge proof?** Rebuilt around watched-but-unrated titles; a product call.
