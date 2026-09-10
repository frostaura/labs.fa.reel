# Reading the retrieval-recall test

`GET /api/dev/retrieval-recall` (Development + `QA_HELPERS_ENABLED=true`, founder-only) answers the
one question every modelling iteration so far has been structurally unable to see past:

> Of the titles this account went on to **love**, how many would the first-pass pool have even
> **contained** on the day before they were rated — and which arm would have put them there?

It is read-only, persists nothing, and makes no TMDB calls. It is a measurement, not a job.

```
GET /api/dev/retrieval-recall?window=180
GET /api/dev/retrieval-recall?window=365&popCount=2000&annCount=2000
```

| Parameter | Default | Meaning |
| --- | --- | --- |
| `window` | 180 | Trailing days of ratings to measure, by `UserRating.FirstRatedAt`. |
| `popCount` | 700 | Popularity-arm budget. Mirrors the feed's `FirstPassPopPool`. |
| `annCount` | 500 | ANN-arm budget (loved + drift combined). Mirrors `FirstPassAnnPool`. |
| `seconds` | 60 | Runtime cap. Clamped to 60 and **not** raisable — see *Cost*. |
| `slug` | `TRAKT_DEV_PROFILE` | Which account, when more than one is linked. |

## Why it exists

Every measurement Reel has taken to date — precision@10, NDCG, the eval ledger, the retro loop — is
a measurement of **ranking**: given a pool of candidates, does the model order it better than
popularity does. None of them can see a title the pool never contained. A title that is never
retrieved cannot be ranked, cannot be explained, and cannot be recovered by any amount of model
work; it is simply invisible, and it is invisible to the instruments too.

This measures the ceiling **above** the ranker. If recall is low, no ranking change matters until
supply is fixed. If recall is high, then the pool already contains the good titles and the model's
failure to surface them is a genuine ranking failure worth another iteration.

## How to read it — in this order

A loved title can be missed for two completely different reasons that want opposite responses.
Reading only the headline recall conflates them and points at the wrong fix.

**1. `loved.rates.inCatalogue` — the supply ceiling.**
The share of loved titles that existed in the catalogue at all on the as-of day. No retrieval arm
can return a row that does not exist, so **every other rate sits under this one**. A low number here
means discovery is not finding these titles before the user does, and widening a retrieval budget
cannot help. On the founder's account the expected pattern is a *low* number: he watches something
Reel has never heard of, and the `Title` row is created by the very sync that carries the rating in.

**2. `loved.recallGivenReachable` — the retrieval number.**
Of the titles that existed **and** were eligible that day, what share the arms actually returned.
**This is the number the decision rule reads.** It is `null`, not `0`, when the denominator is empty
— "0% of nothing" is exactly the figure that gets misquoted into a licensing decision later.

**3. `loved.rates.popularity` / `annLoved` / `annRecent` — which arm to widen.**
These do **not** sum to `union`, and are not meant to. Attribution in the combined pool is
first-claim, matching the retriever's cross-arm dedup, and popularity runs first — so it shadows
every title ANN would also have found. The two ANN figures therefore come from a second,
popularity-free pass, and answer "could this arm have reached it", not "did it get the credit".
`popularity` needs no such pass: nothing runs ahead of it.

**4. `perCluster` — did multi-centroid retrieval actually do anything?**

This block exists to answer one question and it is not "did recall go up". Every loved title is
assigned to the taste cluster whose centroid it is nearest, so the `targetsNear` counts **partition**
the loved set and the per-ordinal recalls are directly comparable.

| Field | Meaning |
| --- | --- |
| `ordinal` | The cluster's canonical rank — 0 is the heaviest pocket. |
| `weight` | Mean share of the account's taste weight over the days evaluated. |
| `memberCount` | Mean number of titles that voted for the pocket. |
| `targetsNear` | Loved titles in the window whose embedding is nearest this centroid. |
| `hits` | How many of those the pool actually contained. |
| `recall` | `hits / targetsNear`, or `null` when nothing was near this pocket. |

**The pass condition is that recall rises in the LOW-weight clusters.** The single all-time centroid
already serves the heaviest pocket well — that is what a mean vector does. The claim B1 makes is
about the pockets it points *away* from, and that claim is only supported if the small ordinals'
recall moves while the big ones' stays roughly where it was.

**A uniform rise across every ordinal is a negative result, not a positive one.** It means the extra
slots bought the recall and the partition contributed nothing, and the honest response is to raise
`FirstPassAnnPool`, delete the clusterer, and keep the simpler system.

**Read the ordinal as a rank, not an identity.** The partition is recomputed for each as-of day, so
ordinal 2 in March and ordinal 2 in September both mean "the third-heaviest pocket" and need not hold
the same titles. `weight` and `memberCount` are means over the evaluated days for the same reason. An
account whose taste genuinely reorganised inside the window will blur this table rather than lie
about it — and an account below `TasteClusterProvider.MinClusterInputs` has no partition at all, so
the block is empty and `annLoved` carries the whole ANN figure.

**`annCluster` in `hits` / `rates`.** These come from the popularity-free pass like the other two ANN
figures, and they are the multi-centroid arm's total reach across all pockets. On an account with a
partition, `annLoved` should be **zero** — the single-centroid arm only fires when there are no
clusters, so a non-zero `annLoved` beside a populated `perCluster` means something is wrong.

**5. `disliked` — the false-positive side.**
The same computation over titles rated ≤ 4. A **high** union here is not a supply success; it means
the arms are efficiently surfacing titles this user goes on to reject, which is a **ranking**
problem. Read it against the loved union: the gap between the two is the arms' discrimination.

**6. `notReachable[]` — the misses, by name, with the reason each was missed.**

| `reason` | What it means | What would fix it |
| --- | --- | --- |
| not in the catalogue that day | The row did not exist yet | Discovery, not retrieval |
| in the catalogue but not eligible that day | A content rule or an interaction excluded it | Revisit the filter stack |
| reachable by ANN alone, but crowded out | ANN found it; the combined budget did not keep it | Rebalance `popCount` / `annCount` |
| eligible and reachable, and no arm returned it | A genuine retrieval miss | A bigger pool, or a new arm |

**7. `budget.exhausted`.**
`true` means the rates describe `daysEvaluated` of `daysTotal` days and nothing more. Days are
processed newest-first, so a truncated run has dropped the **oldest** evidence. Read the rates as a
sample, not as the window.

## The decision rule this feeds

From the Phase 4 plan, and it is the only reason the tool was built:

> **Only if the union is recall-limited does a public-corpus collaborative arm get considered.**

Bringing in an external collaborative corpus (MovieLens or similar) is a licensing decision with a
real cost and a real delay. It is worth taking **only** if the arms Reel already has are
demonstrably leaving loved titles behind — that is, if `loved.recallGivenReachable` is low while
`loved.rates.inCatalogue` is high. The opposite shape (high `recallGivenReachable`, low
`inCatalogue`) says the problem is discovery supply, and a CF arm would be an expensive answer to a
question nobody asked.

Run it, read `recallGivenReachable` first, and let the number decide.

## What it cannot answer

**TMDB's recommendations / similar / person / keyword discover arms are not replayable offline.**
They are live calls, and their answers on a past day are gone. What survives is their *deposit*: a
title one of them found is a row in the catalogue with a `CreatedAt`. Catalogue membership as-of is
therefore used as the reachability proxy for all four discovery arms **at once**. It is an upper
bound on them and it cannot separate them from each other, which is why it is reported as
`inCatalogue` rather than as an arm alongside popularity and ANN — it is not an arm, it is the
precondition all of them share.

**Content rules are read as they stand today, not as-of.** `ContentFilter` rows are mutated and
deleted in place, so there is no history to rewind to. The figure therefore answers "under today's
rules, would the arms have reached this title", which is the useful question — today's rules are the
ones the next build will use.

**Two dating calls bound the answer.** A watch counts as-of only when `LastWatchedAt` is known and
falls on or before the as-of instant; an undated watch row reads as not-yet-watched, matching the
year filter's "absence of a date is not evidence of the wrong date". And a rating counts by
`FirstRatedAt`, never `RatedAt`, for the same reason `EvalHarness` splits on it: `RatedAt` is
overwritten by every Trakt ratings walk, so a re-rate would drag a title backwards across its own
as-of line.

## The as-of rewind, and why the tool is worthless without it

Every target is **by construction** a title the account has since rated. Under the live eligibility
predicate — "never show things I've already seen" — every one of them is therefore ineligible, and a
probe that forgot to rewind would report exactly 0% recall, for every account, for ever. That number
looks like a devastating finding and is in fact a bug.

`EligibilityQueryBuilder.EligibleTitlesAsOf` is the rewind, and it is deliberately a **separate
method** rather than an optional parameter on the serving overload: a nullable date threaded through
the live predicate would change the SQL every surface in the product emits, in order to serve a
diagnostic that runs by hand. The serving path is byte-for-byte what it was.
`RetrievalRecallProbeTests.A_target_is_eligible_on_its_own_as_of_day_even_though_it_is_rated_today`
is the test that holds this.

## Cost

The probe rebuilds a **full taste state per distinct rating day** in the window — and, since B1, a
**taste partition** per day as well (cached day-granular, so the two retrievals below share it) — then
runs two retrievals against it. That is the expensive part, and it is why the 60-second cap is clamped rather
than merely defaulted: an unbounded run against the live database is a self-inflicted outage. Days
are grouped, so a window with 40 rated days costs 40 taste states, not one per rating.
