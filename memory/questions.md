---
name: fa-reel-questions
description: "Kill-criterion question DECIDED 2026-09-09 (model-first, then product) — still open and all founder-owned: deploy date, LLM spend ceiling per account-month, spoiler tolerance for lens copy, MovieLens licence vs dropping item-item CF, whether /rate stays live ahead of an edge proof; Trakt commercial terms remain existential and the JustWatch deep-link call is unmade"
type: question
last_verified: 2026-09-10
---

# Open questions

All of these are founder-owned. None of them blocks engineering work.

- **Deploy date.** The 2026-07-24 deploy decision has never been executed, and it is what makes the ≥5-external-users criterion evaluable at all. M4 is code-complete, the external-user count is zero, and the clock passed around 2026-08-11/18 on either anchor. The choice is: deploy and re-anchor at first external serve, or apply the criterion as written. Leaving it unanswered is the option that costs the most. _(The modelling half of this question was **decided 2026-09-09** — "both, model first" — and the ledger surface now states `criterionExceeded: true` rather than pretending the criterion names 3 iterations that have not run.)_
- **LLM spend ceiling per account-month.** The programme assumes ≤ $0.25 at mini-tier prices, enforced by the token ledger and the per-account lens ceilings. Never confirmed as the real budget.
- **Spoiler tolerance for lens copy.** The programme assumes first-act only, with a spoiler lexicon post-filter and a "this spoiled it" report. Dean has not set the bar.
- **MovieLens licence request, or drop item-item CF.** Retrieval recall (25.5% ceiling) is the binding constraint, so a public-corpus arm is now more interesting than it was — but it is a licensing decision, not an engineering one.
- **Does `/rate` stay live ahead of an edge proof?** It was rebuilt around watched-but-unrated titles in Phase 1; whether it belongs in front of a user before the model beats a baseline is a product call.
- **Trakt commercial terms** remain an open M5 gate and could kill the product outright regardless of model quality. The **JustWatch deep-link decision** is still unmade.
