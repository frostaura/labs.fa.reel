---
name: fa-reel-questions
description: "Every one of these is Dean's, and the deploy decision blocks the rest — until Reel is deployed the external-user kill criterion cannot be evaluated at all. Trakt commercial terms remain the one question that can end the product."
type: question
last_verified: 2026-09-11
---

# Open questions

**Owner: Dean Martin (founder), for every question on this page.** None of them is an agent's to answer: each one
either changes the spec, spends money, or touches a founder-locked rule. Where the 2026-09-11 Ask Reel programme had
to act before an answer existed, it took the conservative side and recorded the alternative — `decisions-chat.md`.

## Blocking everything else
- **Deploy date.** The deploy was decided 2026-07-24 and has never been executed, and it is what makes the "≥5
  external users" criterion evaluable at all. The external-user count is zero and the 60-day clock has passed on
  either anchor. Deploy and re-anchor at the first external serve, or apply the criterion as written and kill.
  **Nothing technical is left in the way:** CI is green on `main` on all five gates and publishes multi-arch images to
  Docker Hub; the redeploy step is skipped only because the repo carries no `DEPLOY_ENABLED` variable and no
  `PORTAINER_STACK_ID` secret (`state.md`). Setting those two is the deploy.

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
- **Ad-hoc lists from chat.** The spec defines one managed list per profile with an auto-removal contract; extra lists
  need that contract written, or a refusal.

## Ask Reel — cost and models
- **The pricing position of Ask Reel, and the real LLM ceiling.** The spec puts natural-language search behind the
  paywall; the endpoint has no tier check at all, so Free gets the whole pipeline. Proposed rungs exist as config
  only. The number needed is the per-account monthly budget — at the measured per-turn cost, a quarter-dollar buys
  roughly eighty turns.
- **Does the extraction-model lock extend to the search model?** Unstated. Rerank is most of a chat turn's tokens and
  is the only end of the pipe where a different model would matter.

## Product calls surfaced by the QA walks
- **Should a typed negation be airtight like a standing one?** By contract it is soft, so "thrillers, no horror" can
  still show horror under the honest "outside your request" caption when the clean set is thin.
- **The All/Movies/TV control keeps reading All after "only shows"**, because the words shape the intent rather than
  the hard filter. Correct by contract, surprising in use.

## Standing product and research calls
- **Spoiler tolerance for lens copy.** Assumed first-act only with a lexicon post-filter; the bar has never been set.
- **MovieLens licence request, or drop item-item collaborative filtering.** Retrieval supply is the binding
  constraint, which makes a public-corpus arm more interesting than it was — but it is a licensing decision.
- **Does `/rate` stay live ahead of an edge proof?** It was rebuilt around watched-but-unrated titles; whether it
  belongs in front of a user before the model beats a baseline is a product call.
