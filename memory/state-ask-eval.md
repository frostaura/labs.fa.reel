---
name: fa-reel-state-ask-eval
description: "Ask Reel v2 golden eval after fix-golden-v2-3 (2026-10-30): 17 of 20 gates pass; missed supply 1, supply counts 0.933 and echo 1 still fail, each for a stated reason. The run's report lives outside the repo."
type: state
last_verified: 2026-10-30
---

# Where the Ask Reel v2 golden eval stands

**fix-golden-v2-3** — `build/trust-v1` at 9c98dca8, open set (120 scenarios, 167 turns), concurrency 2 with every
build slot held, $0.13 on the founder's key under his signed-off one-run flag. Report and recording:
`~/src/fa.reel-ws/reports/hardening/final/fix-golden-v2-3/` (outside the repo; not a baseline).

Passing: precision@8 0.919, honest-empty 1.0 (n 30), reference, critique, act 0.956, conflict, grounding 0,
fallback 0.006, leaks 0, media-filter 0, ruled-out 0, Trakt 0, turn p50 4.7 s / p95 9.1 s, first card p50 3.5 s /
p95 6.1 s, cost $0.001 a turn.

Still failing, each left failing on purpose rather than bent:
- **Missed supply 1 — crit-not-spy T2.** Nine spy films clear the bar, eight were shown; the judge rejected the last
  tagged one. Whether it is a spy film is arguable, so no hand label (`decisions-ask-eval.md`).
- **Supply counts 0.933 (2 of 30).** conflict-standup: this run the planner read "stand-up comedy specials" as keyword
  "stand-up" + genre comedy (earlier runs: one "stand-up comedy" keyword), nothing on stand-up was eligible, and the
  relaxation ladder reported the whole comedy shelf (12,592) — an app defect in the ladder's report rung when a genre
  word sits inside the topic, unfixed because no live run was left to prove a fix. crit-not-spy: the app counts 308
  spy films, the truth 310 — a two-title breadth gap not yet traced.
- **Echo 1 — lookup-three-billboards.** The user typed only a title and the answer opened by naming it; the echo rule
  counts a 5+-word message repeated verbatim. Likely a scorer false positive, but changing a rule after seeing the
  run that fails it is the pattern the eval forbids — a founder or reviewer call.

Not gated, still visible: act misses on ordinal/critique follow-ups (0.956 overall), fantasy-then-mind-benders T2
showing shows after a movie conversation (media carry), space-isolation T5 mode.
