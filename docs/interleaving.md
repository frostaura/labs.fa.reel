# Interleaving — the online instrument

Reel has one user. Population A/B is not available and never will be at this scale, so the
question "is ranker X better than ranker Y for Dean" has, until now, had no honest answer at all —
the offline ledger compares a model against a popularity baseline on titles he has already rated,
which is a different question from the one the product is asking.

**Team-draft interleaving is the answer, and it is slow.** Both facts belong in the same sentence.
Netflix reports needing on the order of 100× fewer subjects than population A/B for the same power,
and that sensitivity is real — but "subjects" is the axis it saves on, and here there is one. What
supplies power at N=1 is decided *slates*, and slates are decided by outcomes, which are sparse.

## What it does

On a **Fresh** feed build, each row is drafted alternately between two rankers:

- the hero (5 cards) and every rail (10 cards) is one **slate**;
- a seeded coin flip decides who picks first on that slate, then the teams strictly alternate;
- on its turn a team takes its own highest-ranked card that nobody has taken yet;
- each card's exposure row records **which ranker put it there** (`PolicyId`) and **which slate**
  (`InterleaveGroup`).

Contributions are therefore balanced to within one seat by construction. That matters: without it a
ranker could win simply by supplying more of the page.

A **Preserve** rebuild never re-drafts — tonight's seats do not move, that is the whole contract —
but it *carries the attribution forward*, so a card that is still on screen still counts for the
ranker that earned it. The live serve paths (the feed refill, `/feed/more`, genre rails, the digest,
Ask Reel) write no attribution at all, because they are not slates.

## Scoring

A card earns its ranker one **credit** if, after being served, the user **saved** it, **watched** it,
or **rated it 8 or higher** — the three signals the `ExposureOutcomes` view already computes, with
the causal time guards already in it. A rating below 8 is deliberately not a credit: the product's
claim is "you will love this", so a tolerated film is not a win.

A slate is **decided** when one team out-credits the other, and **tied** otherwise. Most slates tie
at zero credits apiece, and that is normal — it is what makes the cadence what it is.

## The test

An **mSPRT** (mixture sequential probability ratio test) over the stream of decided slates, testing
p = ½. Its statistic is a nonnegative supermartingale — exactly, not approximately: the ±½
increments are ½-sub-Gaussian by Hoeffding's lemma at precisely the Bernoulli(½) variance — so
Ville's inequality bounds the type-I error under **any** stopping rule.

**You may look at the number as often as you like. Peeking costs nothing.** That is the single
practical property the whole construction buys, and the reason a fixed-sample test would have been
the wrong instrument for a founder watching his own product.

| parameter | default | meaning |
| --- | --- | --- |
| `INTERLEAVE_MSPRT_TAU` | `0.15` | mixing sd on the preference rate — aims the test at a 65/35 effect |
| `INTERLEAVE_ALPHA` | `0.05` | the always-valid type-I ceiling |

Simulated, not merely asserted (`MsprtSimulationTests`): 10,000 null runs peeking after every single
slate cross α **2.6%** of the time against a stated 5%, and **0.6%** against a stated 1%. Power at a
true 65/35 preference is >99% within four times the projection.

A plain **sign test** is reported beside it as a sanity companion. It always reads lower, because it
assumes you committed in advance to looking exactly once. **It is never the decision.**

## The cadence — read this before planning around it

A 65/35 preference takes about **103 decided slates** on the expected path (median 82 in
simulation). At roughly one decided slate a day, that is **two to three months per comparison** —
and comparisons serialise, because two experiments running at once on one user's feed would confound
each other.

This directly caps how many product ideas can ever be validated online, and it argues for **fewer,
larger changes**. It was optimistic in the original research write-up ("weeks — not months") and the
adversarial review corrected it; the corrected number is the one the panel renders, and a test pins
it so it cannot drift back toward optimism without someone deciding to change it.

## Turning it on

It is **off by default**, and with it off the feed is exactly what it was — same picks, same order,
no `PolicyId` written anywhere. An integration test pins that a disabled build and an unconfigured
build produce an identical feed.

```env
INTERLEAVE_ENABLED=true
INTERLEAVE_POLICY_A=CalibratedModel
INTERLEAVE_POLICY_B=PopularityBaseline
```

Then force a Fresh build (a manual refresh, a filter change, or the next new UTC day).

**A = CalibratedModel, B = PopularityBaseline is the honest first comparison**, and it should be run
before anything else. It is the same question the eval ledger has asked offline twelve times and
answered non-positively every time — but asked online, on the titles Reel actually serves, with the
outcomes it actually produces. If the served ranker cannot beat crowd popularity on Dean's own feed,
that is the most important fact about this product, and no amount of further modelling should
proceed without knowing it.

### The policies

| id | what it orders by |
| --- | --- |
| `CalibratedModel` | calibrated prediction × freshness × preference boost × the slate's own affinity term — today's served order, reproduced exactly |
| `PopularityBaseline` | present-day TMDB popularity within the eligible pool — the identical comparator `EvalHarness` uses |
| `ProfileRerank` | LLM lens fit where one exists, calibrated order otherwise |

`ProfileRerank` is **a stub today and configuring it would compare a ranker against itself.** Fit
verdicts are produced by the why/re-rank pass, which runs *after* assembly, so at draft time every
candidate's fit is null and the policy is byte-for-byte the calibrated one. It becomes a real arm
only once lens fit is computed before assembly. The panel reports the configured policy ids so this
is visible rather than silent.

## Reading the panel

`/lab` → **Interleaving**, founder-only (`GET /api/metrics/interleave`).

- **rows drafted** vs **decided rows** — a large gap is the instrument working, not failing.
- **always-valid p** — the decision. Significant when ≤ α.
- **win rate + range** — an always-valid confidence sequence, valid at every moment simultaneously.
- **cadence line** — the observed rate and what remains at that rate.
- **standings** — per-ranker cards, credits and rows won, including any ranker no longer configured
  as an arm. Reconfiguring the arms restarts the comparison rather than mixing two experiments into
  one p-value.

## Known limits, stated rather than buried

1. **A swapped hero loses its attribution.** When the why pass swaps a low-fit hero card, the
   replacement was drafted by neither ranker, so the seat is credited to nobody. Crediting the team
   whose seat it took would feed the test an outcome neither ranker earned. The slate loses that
   seat instead.
2. **Interleaving measures preference between rankers, not absolute quality.** Both arms can be bad.
   It cannot replace the calibration and track-record work, and it must not be allowed to.
3. **Only slates drafted between the two currently configured arms enter the test.** Everything else
   is reported in the standings and excluded from the p-value.
4. **Ties carry no information and are not observations.** They are counted and shown because their
   volume is what sets the cadence.
