# Surface baseline 9f42c9d, keyless — provenance

The reference for every **keyless** surface run on `reel-2026-10-23` from the round-2 integration
(`build/trust-v1` at `9f42c9d`) on. It replaces `../99c0761-keyless/` for `--compare`; that one stays
in the tree as the pre-program reference the re-baseline is measured against (IR2S-04).

- **Build:** `9f42c9d799b3…` built outside any checkout by `scripts/eval/build-at.sh` (dll SHA-256 in
  `recording.json` → `buildInfo`).
- **Data:** `reel_ws7_surf`, a fresh `snapshot.sh clone` of `reel_eval_template` (snapshot
  `reel-2026-10-23`), sanitised. The Fresh build started from artifact `0536acbf…`, the same input
  artifact as `99c0761-keyless`, so the two are comparable.
- **API:** `scripts/eval/run-api.sh --jobs` on :5187, every hosted service, boot repair and trust purge
  off, Trakt offline in-process, **no OpenRouter key**. Ledger: $0, 0 Trakt calls.
- **Harness:** recording version 3; one Fresh build and every chained job drained before the fetch.
  `metrics.json` and `report.md` are `--replay` output of the recording.
- **Reproduced:** the integrator's own `int-r2` run of the same commit compares against this baseline
  with every row passing.
- **Taken:** 2026-10-26 by WS-7 (round 3).

## Why the volume drop is re-baselined, not handed back

Against `99c0761-keyless`, feed rows fell 40 → 16 cards, Similar 60 → 6 and anchored cards 126 → 12. That
is the anchored-row and Similar evidence gate (`AnchorRowGate`, WS-5 tasks 4 and 10) doing what the
program asks: a card is admitted only when its plot-embedding similarity to the anchor is at least the
anchor's 50th-nearest-neighbour similarity **and** it shares a distinctive person, keyword or theme.

Measured on the snapshot, applying that rule as written to every anchored card each run served
(exact cosine scan, the gate's meta and common keyword and theme lists):

| Run | Anchored cards | Clear the similarity floor | Share evidence | Pass the gate |
|---|---|---|---|---|
| `99c0761-keyless` (feed rows, their next pages, Similar) | 126 | 11 | 21 | **3** |
| `9f42c9d` (this baseline) | 12 | 12 | 12 | **12** |

No anchored-row card at `99c0761` cleared its anchor's similarity floor (0 of 66). So the 114 cards that went
are cards the program rules out, not cards lost to a defect, and the build serves four times as many cards
that pass as `99c0761` did. Whether a Home with one anchored row of 6 is the product the founder wants is a
question about the 50th-neighbour bar, which is WS-5's to raise; the harness's job is to stop calling the
spec'd effect a regression.

## Stub why-lines: owned, not waived

The stub rate went 0.0035 → 0.095 (16 of 169 suggestion cards): 12 bare `GenericFallback` lines, which
the paginator and the feed response allow once per served page (so 8 genre rails carry 8, plus
deep-cuts and three `/feed/more` pages), and 4 composed tails (`…, and Strong fit with your taste
profile.`, IR2S-01) that no once-per-page guard catches. Both kinds stay stubs (`stubKind` in
`scripts/eval/surfaces-score.mjs`): the program says a fallback card shows the templated anchored line or
nothing. Re-baselining moves the **regression** reference, so a later merge is not blamed for this one,
but the absolute stub gate (≤ 0) keeps failing until WS-5 ships an empty caption instead of the generic
sentence and drops the composed tail. That change is WS-5's, recorded as a change request in WS-7's
round-3 report.
