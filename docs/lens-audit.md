# Reading the lens audit

`GET /api/dev/lens-audit` (Development + `QA_HELPERS_ENABLED=true`, founder session only) scores
the account's most recent generated lens notes against a frozen rubric, using the same model family
that wrote them. It exists so that prompt versions can be **compared**, and for no other reason.

```
GET /api/dev/lens-audit?sample=50&score=true
```

- `sample` — how many recent notes to cover. Default 50, which is the human-review bar this feature
  is held to before anyone but the founder sees its output.
- `score=false` — return the same rows without paying for a judgement. This is the right call when
  you want to read fifty generations yourself, which is the check that actually matters.

Runs under its own ledger purpose, `lens-audit`, so an audit can never consume the `lens` budget it
is auditing.

## What comes back

| Field | Meaning |
| --- | --- |
| `promptVersion` | The generation the writer is currently stamping. |
| `scored` / `unscored` | How many of the sampled notes the judge actually returned a verdict for. |
| `rejectedByGuards` | How many of the sampled notes a **deterministic** guard refused. |
| `rejectedByReason[]` | Those refusals grouped by failure class, worst first, each with the distinct **tokens** behind it — `{"reason": "invented-anchor", "count": 9, "tokens": ["Frontera", "Lawless"]}`. |
| `meanSpecificity` | Is every clause true of this title and no other, or is it filler? |
| `meanGrounding` | Does every plot fact trace to the overview, and every name to the credits? |
| `meanSpoilerFreeness` | Does it say anything the official overview does not? The premise itself is **not** a spoiler. |
| `meanVoice` | Spoken **to** the viewer — hook included — plain, honest, no marketing adjectives, no numbers? |
| `rows[]` | Every sampled note — **including the refused ones** — with its prose, its stored anchors, its scores and the judge's one-line complaint. |

Each row in `rows[]` carries the refusal in three forms, because they answer different questions:

| Field | Meaning |
| --- | --- |
| `rejectionReason` | Exactly what is stored on the row: `invented-anchor:Frontera`. |
| `rejectionKind` | The class alone, for grouping: `invented-anchor`. |
| `rejectionToken` | The offending name alone: `Frontera`. Null when the guard names no token. |
| `anchors[]` | The anchor titles **as stored** — after any ungrounded entry was stripped. |
| `povSlips` | The judge's quoted POV slips, or null — see below. |

Refused rows are never scored by the judge. A note a deterministic guard already refused has a
deterministic verdict on it, and paying a model to agree is spend for nothing.

## How to read it

**Compare means between prompt versions. Never read a level.** The judge is the same model family as
the writer and shares its blind spots, so an absolute 0.87 says almost nothing while a drop from
0.87 to 0.71 across a prompt change says a great deal. Run the audit before and after a prompt edit,
on a comparable sample, and read the difference.

**`rejectedByGuards` is worth more than any of the means.** It counts notes that were actually
refused — by the spoiler lexicon, the voice guard, the number guard or the provenance check — rather
than opinions about notes that passed. It is the only number here that is not itself a model output.
A prompt change that lifts every mean while raising this count has made the copy more confident and
less admissible.

**Read `rejectedByReason` before anything else, and read its tokens.** A total is a number; a class
with its tokens is a diagnosis. The worked example is the outage this endpoint was extended for:
every on-demand note the founder's account generated was refused as `invented-anchor`, and the
tokens were `Frontera`, `Lawless`, `Amateur` and `The Girl with All the Gifts` — all four of them
titles from the account's *declined* line, which was the only list of titles anywhere in the lens
prompt. A count of two said "the guard is firing". The tokens said "the prompt is showing the model
titles the guard is built to refuse", which is a different sentence and the only one you can act on.

**One class of refusal moved out of the guard entirely.** An anchor entry that does not resolve to
the account's library is now **stripped from the note and logged**, and the note is kept — an anchor
is a structured field the surface can simply omit, and refusing four sentences of sound prose over
one droppable comparison is what took the on-demand path to a hundred percent rejection.
`invented-anchor` now means only that the **prose** — `lens`, `hook` or `caution` — named a film the
account does not have. Stripped anchors are not on this report; they are in the application log, as
`dropped N ungrounded anchor(s)`. If `invented-anchor` counts fall to zero after a prompt edit,
check that log before concluding the prompt got better.

**`povSlips` is a note, not a score, and it is not in any mean.** It quotes every place the copy
made the **viewer** a character in the plot — `"the city that failed you"`, of a city that failed
*her*. The slip is rare enough that folding it into `voice` would move the mean by less than the
judge's own noise, and specific enough that the phrase is worth more than a number. It is also the
one defect with **no deterministic guard behind it**: "the city that failed you" and "the pacing
that suited you" are the same shape and only the first is wrong, and separating them needs to know
that "the city" is a story entity. A regex tuned to catch the first refuses the second, and honest
second-person address is the entire product — so the rule is stated in the prompt and measured
here, and `rejectedByGuards` will never count it. Read this field directly; do not expect it in a
mean.

**A note on comparing v3 to v4.** Prompt v4 and the judge rubric changed in the same commit, on
purpose. v3's rubric said "first-act premise only" and the judge read the premise itself as the
spoiler, marking notes down for "revealing the childhood-memory premise" — a premise printed on the
poster. `meanSpoilerFreeness` was therefore measuring something the product never wanted. The rubric
now says the official overview's setup is fair game and only what comes after it is a spoiler, which
means **a v3 spoiler-freeness number and a v4 one are not the same measurement**. Everything else on
the report compares cleanly; that one dimension re-baselines at v4.

**Watch for specificity rising while grounding falls.** That combination is what "more confident and
less true" looks like, and it is exactly what a prompt tweak aimed at livelier copy tends to buy. If
you only track one relationship, track that one.

**The means are not a release gate.** Fifty human-read generations per prompt version are the gate.
This endpoint makes that reading comparable across versions; it does not replace it.

## What it costs

One judge call per scored note, at roughly a third of a lens's own token count (the note is short;
the title block is the bulk of the prompt). A 50-note audit is therefore on the order of a third of
a day's lens budget in tokens — charged to `lens-audit`, so it does not displace generation. Use
`score=false` freely; use `score=true` deliberately.
