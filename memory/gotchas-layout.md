---
name: labs-fa-reel-gotchas-layout
description: "A results grid scores CLS three ways and only one shows on a fast machine — a placeholder keyed by its position shuffles, animating left/top bills every frame, and a transient block above the grid cannot just unmount."
type: gotcha
last_verified: 2026-09-11
---

# Gotchas — layout shift

- **Three ways a results grid scores CLS, and only one of them shows on a fast machine** (found on `ci/cls-flake`,
  2026-09-11). **(1) A placeholder keyed by its position among the placeholders shuffles** — eight skeletons keyed
  `slot-0…slot-7` leave seven keyed `slot-0…slot-6` when a card arrives at the head, so React keeps the same nodes and
  slides every one forward by a cell. A fast machine coalesces the batch into one layout pass and scores nothing; a
  loaded runner paints each card and scores a lot, which is what made it read as a CI flake. **Key the CELL**
  (`slot-${visible.length + i}`); any reserve rendered as trailing siblings of the real items has this bug unless its
  keys are absolute. **(2) Animating `left`/`top` bills CLS every frame, and bills MORE as the machine slows** —
  `transform` is composited, scores nothing, looks identical. **(3) A transient block above the grid cannot just
  unmount** — the progress banner leaving flow pulled the grid up and scored on every turn; it now sits in a
  fixed-height slot that hands its box to the answer's provenance line. Reserving a box and leaving it empty trades a
  jump for a hole: the box has to be given to whatever the next state has to say. The turn's CLS guard is 0.05.
- **A 44 px tap target measured through a scale transform reads 43.99997.** The mobile spec's floor tolerates 0.01 px;
  an exact `>= 44` assertion on a transformed element is a flake, not a finding.

_Other front-end and retrieval traps: [`gotchas-runtime.md`](gotchas-runtime.md). Test-fixture traps: [`gotchas-tests.md`](gotchas-tests.md)._
