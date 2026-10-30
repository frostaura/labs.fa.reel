---
name: fa-reel-decisions-ask-eval
description: "How the Ask Reel golden eval's truth may change: only as a correctness fix of what the request means, equal to the app's count where the app is right, with a parity test, no bar moved. Never-repeat supply, meant truths, media from words."
type: decision
last_verified: 2026-10-30
---

# Live decisions — the Ask Reel golden eval's truth and scorer

The harness, gates and per-scenario justifications live in `docs/ask-reel-eval.md` (see "Truth restatements of
fix-golden-v2-2") and `eval/askreel/gates.json` → `amendments`. These are the rules behind them.

- **A truth or scorer change is a correctness fix or it does not land.** Each states what the request's words mean,
  is verified count for count on the eval copy against the reply it failed, carries a parity or unit test, and goes
  in the amendments with the data seen. No bar ever moves; a change that is not clearly right leaves the gate failing
  and says so. *Why:* the founder asked for gates that pass honestly; an eval bent to the app measures nothing.
- **Where the app's reading is right, the truth uses the app's concept definition** (synonym group, word-start match,
  name arm on the user's own words only — HREV3-RET-A). *Why:* supply counts are held ±0, so two readings of one
  topic fail correct replies forever. Examples: `space.sql` (the bare "space" label was missing), `martial-arts.sql`,
  `romantic-comedy.sql`, `kidnapping-thriller.sql`, `animated-for-kids.sql`, and `genre-anime.sql` (fix pass 1: the
  tag, animation in Japanese, or the stand-in keyword, as `rules.sql` and the app read anime).
- **Missed supply subtracts what the conversation already showed.** *Why:* the app may never repeat a title, so
  "more of those" after every clearing title was shown is honestly none-found. Recordings freeze `clearingShown`;
  older ones infer it from earlier picks and unchecked cards only, so the inference can only under-subtract.
- **`meant` exists because the app has two layers by design.** Retrieval counts the tag population ("tagged that
  way"), the judge rules on meaning. Supply counts are held to the tag truth; missed supply to the meant truth
  (`ninja-meant`, `heist-meant`, `dinosaur-meant`). A hand label (by TMDB id, with its reason) is allowed only for a
  title no film-literate reader would call the concept — never to clear a turn the judge got wrong; an arguable one
  stays failing.
- **A media expectation comes only from the user's words** ("films" = movies; "westerns" names no medium), the rule
  the app holds in `PlanSanitizer.UserMedia`. A turn whose scope or meant truth a recording never froze is
  `truthMissing`, never scored against another scope.
- **Only the user's own words are negations — in scenarios too.** "Let's do mind benders instead" switches topic and
  rules nothing out, so no `mustNot` for the abandoned genre.
- **Re-scoring is free; a live run is not.** `askreel.mjs replay` re-scores frozen facts; `rematerialise` needs the
  database in the recording's post-run state (OnDemandScorer writes `TitleScores` during a run, so a fresh template
  clone drifts and is refused). Rescore the live run's own database before dropping it.
