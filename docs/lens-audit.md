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
| `meanSpecificity` | Is every clause true of this title and no other, or is it filler? |
| `meanGrounding` | Does every plot fact trace to the overview, and every name to the credits? |
| `meanSpoilerFreeness` | First-act premise only? |
| `meanVoice` | Second person, honest, no marketing register, no numbers? |
| `rows[]` | The notes themselves, with per-note scores and the judge's one-line complaint. |

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
