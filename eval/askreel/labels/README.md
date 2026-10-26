# Founder labels for the Ask Reel judge

`founder-100.csv` is the 100-item set the LLM judge is calibrated against (program §4.12, WS-7 task 5).
Until your labels exist and the judge agrees with them at Cohen's κ ≥ 0.6, the judge gates nothing and
only the SQL truth does (`eval/askreel/gates.json` → `judge`).

## How to label (about 30 minutes)

Each row is one card Ask Reel showed for one request. Read `request` (and `earlier_turns` when the
request refers back: "the second one", "not those"), look at the title, and put a number in `label`:

| Label | Meaning |
|---|---|
| 3 | What was asked for: it meets every explicit part of the request — the topic itself (a ninja film is about ninja, not merely a film with a fight), the genre, film or series, the era, the person, the language, the mood. |
| 2 | The core of the request with a minor miss: a weaker example of the topic, or one secondary part slightly off. |
| 1 | Related but not what was asked: shares a theme or a feel, or breaks an explicit part (a series when films were asked for; a samurai film for "ninja"). |
| 0 | Unrelated, or contradicts the request. A question or a greeting answered with cards is a 0 for every card. |

Judge the fit to the request only — not whether the title is good or whether you would like it. `note` is
optional. The `overview`, `genres` and `year` columns are the catalogue's own facts, shown so you can
judge a title you do not know; the system's own explanation is deliberately left out so it cannot steer
the label.

## Where it came from

`node scripts/eval/judge.mjs items --recording eval/askreel/baselines/99c0761/open/recording.json
--db <a reel-2026-10-23 copy> --out eval/askreel/labels/founder-100.csv --n 100 --seed 42`: one card
from the top eight of 100 different turns of the 99c0761 open baseline, drawn in a seeded order.
Regenerating with the same seed gives the same items.

**Still owed:** WS-3's 20 in-catalogue ninja films (WS-3 P1 task 11) are appended as yes/no confirm rows
with `--confirm <file of title ids>` once that list exists, so you confirm them in the same sitting.

## After labelling

`node scripts/eval/judge.mjs run …` judges a recording (paid, on the eval key), then
`node scripts/eval/judge.mjs kappa --labels eval/askreel/labels/founder-100.csv --judge <judge.json>`
reports κ (unweighted, the gate; quadratic-weighted and relevant/not-relevant beside it) and exits 0 only
when the judge may gate.
