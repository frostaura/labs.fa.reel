---
name: labs-fa-reel-decisions-ask-v2
description: "Ask v2 answer-quality rules from the golden-eval fix passes, each with its why: relaxation ladder, language and named-genre requirements, unengaged genres, scheduled judge resends, first batch of three, specials."
type: decision
last_verified: 2026-10-30
---

# Live decisions — Ask Reel v2 answer quality (build/trust-v1, golden-eval fix passes)

Every rule below fixes behaviour, never a scenario; each has a test that varies its fixture. Commits on
`build/trust-v1`: 04775d48 and 45b58ba4 (fix passes 1–2), 1bad7051 and 426c3859 (pass 3).

## Retrieval and reading the request
- **A request with nothing offerable as asked is searched one rung wider (`ConceptLadder`).** Keywords drop before
  genres, words the newest message did not say first; a genre drops only under a person or language anchor. *Why:*
  "documentaries about space" answered none-found while documentaries existed. The judge still reads the whole
  message, so a wider rung widens recall, never precision. **The request's own retrieval comes first** (at most half
  the judge's slice) and the wider rung fills the rest — *why:* searching only the wider rung lost five unscored
  treasure hunts to plain adventures. An empty answer is reported at the request as asked unless nothing on the
  anchor is eligible at all — *why:* "kidnapping thrillers, last five years" once counted 1,732 thrillers. The
  topic the conversation holds is dropped last, so "more heart" never drops the heist.
- **A language word is a requirement on `Titles.Language`** ("korean", "french films"); a phrase the catalogue tags
  ("korean war") stays a topic. *Why:* the language collision with "English only" must be counted and named.
- **A genre word the user said that the account excludes is a requirement the planner may not drop** ("animated
  movies for kids" → honest none-found naming the exclusion). A mood that merely suggests a genre ("funny") and a
  genre word inside a held topic ("stand-up comedy") add nothing.
- **Unengaged genres:** for a request naming no topic, a title all of whose genres the account never watched or rated
  on that medium (with ≥ 50 titles of history there) is not judged. *Why:* 0 of 252 watched shows are reality, yet
  the model put Jersey Shore at 6.8.
- **A Refine that says it replaces the topic is a NewRequest** (`PlanSanitizer.SwitchesTopic`: "…mind benders
  instead"). Walking away from a topic does not rule it out — only the user's own words are negations.
- **A "special" names no medium** (`PlanSanitizer.UserMedia`). *Why:* the planner read "stand-up comedy specials" as
  TV and counted 6 shows where the catalogue holds 151 stand-up titles, 145 of them films. A medium word beside it
  still decides.

## The judge and latency
- **A judge batch is resent on a schedule — at the hedge (4 s) and twice it (8 s) — and at once when every call so
  far failed** (`AskReelHedge` schedule overload, `RequestJudge`). *Why:* two golden turns lost a whole batch to the
  12 s deadline with both routes slow; those two degraded turns of 167 put the fallback rate at 0.012 against ≤ 0.01. A healthy
  batch never reaches a rung. A title skipped inside an answered batch is asked about once more.
- **The first judge batch is three titles** (batches 3/6/6/1, at most three in flight). *Why:* the first card waits
  for exactly that call and a batch's time is mostly its output; first-card p50 sat on the 4 s gate.
- **Speculation is the big first-card lever, not batch sizes:** turns whose keyless reading matched the plan (≈40%)
  showed the first card at p50 ≈ 2.9 s against ≈ 4.7 s for the rest (fix-golden-v2-2 stage timings).
