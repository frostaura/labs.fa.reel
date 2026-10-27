/**
 * Populated /lab payloads for the full-axe spec (PH-02), shaped like the eval snapshot: a trained
 * model with runs and five split points, conversions with worst misses, the counterfactual windows,
 * cross-reference backtests and outcomes, and a running interleave. The numbers are the snapshot's
 * aggregate statistics; every title name and id is synthetic. A mocked /lab with empty runs and a
 * never-run interleave drew none of the nested stat cards or the ledger scroller axe failed on.
 */
export const LAB_MODEL_METRICS = {
 "gate": {
  "threshold": 0.2,
  "passed": false,
  "latestLift": -0.75,
  "ndcgModel": 0.056719,
  "ndcgBaseline": 0.207585,
  "ndcgLiftCiLow": -0.420902,
  "ndcgLiftCiHigh": 0.028924,
  "ciExcludesZero": false,
  "legacyThreshold": 0.2,
  "legacyPassed": false,
  "iterationsUsed": 20,
  "killCriterionAt": 3,
  "criterionExceeded": true
 },
 "efficiency": {
  "computedAt": "2026-10-27T06:00:00Z",
  "conversions": {
   "count": 117,
   "ratedCount": 117,
   "scoredCount": 117,
   "accuracy": {
    "count": 117,
    "meanPredicted": 6.75,
    "meanActual": 6.06,
    "meanAbsoluteError": 1.7,
    "bias": 0.69,
    "withinOnePointRate": 0.3248,
    "pearsonCorrelation": 0.0364
   },
   "ratingDistribution": [
    {
     "rating": 3,
     "count": 5
    },
    {
     "rating": 4,
     "count": 16
    },
    {
     "rating": 5,
     "count": 16
    },
    {
     "rating": 6,
     "count": 33
    },
    {
     "rating": 7,
     "count": 27
    },
    {
     "rating": 8,
     "count": 17
    },
    {
     "rating": 9,
     "count": 3
    }
   ],
   "worstMisses": [
    {
     "titleId": "00000000-0000-0000-0000-000000000101",
     "name": "Missed title 1",
     "year": 2024,
     "predicted": 9.78,
     "actual": 3,
     "gap": 6.78
    },
    {
     "titleId": "00000000-0000-0000-0000-000000000102",
     "name": "Missed title 2",
     "year": 2017,
     "predicted": 9.36,
     "actual": 4,
     "gap": 5.36
    },
    {
     "titleId": "00000000-0000-0000-0000-000000000103",
     "name": "Missed title 3",
     "year": 2018,
     "predicted": 8.88,
     "actual": 4,
     "gap": 4.88
    },
    {
     "titleId": "00000000-0000-0000-0000-000000000104",
     "name": "Missed title 4",
     "year": 2019,
     "predicted": 7.71,
     "actual": 3,
     "gap": 4.71
    }
   ]
  },
  "predictedVsActual": {
   "pairs": 198,
   "accuracy": {
    "count": 198,
    "meanPredicted": 6.58,
    "meanActual": 6.44,
    "meanAbsoluteError": 1.65,
    "bias": 0.13,
    "withinOnePointRate": 0.3636,
    "pearsonCorrelation": 0.0102
   },
   "worstMisses": [
    {
     "titleId": "00000000-0000-0000-0000-000000000111",
     "name": "Missed title 11",
     "year": 2024,
     "predicted": 9.78,
     "actual": 3,
     "gap": 6.78
    },
    {
     "titleId": "00000000-0000-0000-0000-000000000112",
     "name": "Missed title 12",
     "year": 2017,
     "predicted": 9.36,
     "actual": 4,
     "gap": 5.36
    },
    {
     "titleId": "00000000-0000-0000-0000-000000000113",
     "name": "Missed title 13",
     "year": 2018,
     "predicted": 8.88,
     "actual": 4,
     "gap": 4.88
    },
    {
     "titleId": "00000000-0000-0000-0000-000000000114",
     "name": "Missed title 14",
     "year": 2019,
     "predicted": 7.71,
     "actual": 3,
     "gap": 4.71
    }
   ]
  },
  "counterfactual": [
   {
    "window": "last365Days",
    "days": 365,
    "viaReelCount": 120,
    "viaReelMeanRating": 6.07,
    "otherCount": 442,
    "otherMeanRating": 6.1,
    "delta": -0.03
   },
   {
    "window": "allTime",
    "days": null,
    "viaReelCount": 120,
    "viaReelMeanRating": 6.07,
    "otherCount": 2022,
    "otherMeanRating": 6.77,
    "delta": -0.71
   }
  ]
 },
 "activeArtifact": {
  "version": 101,
  "algo": "FastTree",
  "trainedAt": "2026-10-22T19:57:14Z"
 },
 "runs": [
  {
   "id": "00000000-0000-0000-0000-000000000030",
   "iteration": 20,
   "configHash": "54BC13471907B077",
   "splitAt": "2026-04-06T18:29:00Z",
   "trainRowCount": 1714,
   "holdoutRowCount": 251,
   "positiveThreshold": 8,
   "status": "Succeeded",
   "startedAt": "2026-10-22T19:57:15Z",
   "hyperparams": {
    "coverage": {
     "titles": 1965,
     "attrCoverage": 0.89,
     "schemaVersion": 5,
     "embeddingCoverage": 1
    },
    "hyperparams": {
     "Trees": 200,
     "Leaves": 20,
     "LearningRate": 0.2,
     "MinExamplesPerLeaf": 10
    },
    "frozenThreshold": 8,
    "lowSampleFallback": false
   },
   "eval": {
    "modelPrecisionAt10": 0.1,
    "baselinePrecisionAt10": 0.4,
    "relativeImprovement": -0.75,
    "rmse": 1.4929,
    "mae": 1.136,
    "spearmanRho": 0.1826,
    "spearmanSe": 0.0635,
    "ndcgAt10Model": 0.056719,
    "ndcgAt10Baseline": 0.207585,
    "ndcgLiftCiLow": -0.420902,
    "ndcgLiftCiHigh": 0.028924,
    "calibrationBias": 0.2174,
    "ece": 0.259562,
    "eligibleHoldoutCount": 251,
    "unfilteredHoldoutCount": 426,
    "holdoutPositiveCount": 57,
    "lowSample": false,
    "passedGate": false,
    "passedLegacyGate": false,
    "origins": [
     {
      "fraction": 0.6,
      "splitAt": "2024-09-24T19:50:37Z",
      "trainRows": 1286,
      "unfilteredHoldout": 835,
      "eligibleHoldout": 414,
      "threshold": 8,
      "lowSample": false,
      "ndcgModel": 0.120381,
      "ndcgBaseline": 0.217611,
      "ndcgRelativeLift": -0.4468064573941575,
      "precisionModel": 0.3,
      "precisionBaseline": 0.5
     },
     {
      "fraction": 0.65,
      "splitAt": "2025-04-08T19:36:37Z",
      "trainRows": 1393,
      "unfilteredHoldout": 731,
      "eligibleHoldout": 371,
      "threshold": 8,
      "lowSample": false,
      "ndcgModel": 0.281191,
      "ndcgBaseline": 0.231778,
      "ndcgRelativeLift": 0.21319107076599159,
      "precisionModel": 0.7,
      "precisionBaseline": 0.5
     },
     {
      "fraction": 0.7,
      "splitAt": "2025-09-07T08:22:06Z",
      "trainRows": 1500,
      "unfilteredHoldout": 625,
      "eligibleHoldout": 324,
      "threshold": 8,
      "lowSample": false,
      "ndcgModel": 0.204321,
      "ndcgBaseline": 0.231778,
      "ndcgRelativeLift": -0.11846249428332284,
      "precisionModel": 0.6,
      "precisionBaseline": 0.5
     },
     {
      "fraction": 0.75,
      "splitAt": "2025-11-21T21:53:34Z",
      "trainRows": 1607,
      "unfilteredHoldout": 531,
      "eligibleHoldout": 291,
      "threshold": 8,
      "lowSample": false,
      "ndcgModel": 0.017074,
      "ndcgBaseline": 0.231778,
      "ndcgRelativeLift": -0.9263346823253286,
      "precisionModel": 0.1,
      "precisionBaseline": 0.5
     },
     {
      "fraction": 0.8,
      "splitAt": "2026-04-06T18:29:00Z",
      "trainRows": 1714,
      "unfilteredHoldout": 426,
      "eligibleHoldout": 251,
      "threshold": 8,
      "lowSample": false,
      "ndcgModel": 0.056719,
      "ndcgBaseline": 0.207585,
      "ndcgRelativeLift": -0.726767348315148,
      "precisionModel": 0.1,
      "precisionBaseline": 0.4
     }
    ],
    "detail": {
     "ndcg": {
      "seed": 20260909,
      "ciLow": -0.420902,
      "model": 0.056719,
      "ciHigh": 0.028924,
      "baseline": 0.207585,
      "relevance": "graded: max(0, rating \u2212 threshold + 1), gain 2^rel \u2212 1 \u2014 the threshold itself is the first rung, so a title rated exactly at the 'loved' line still earns gain",
      "resamples": 1000,
      "pairedLift": -0.150866
     },
     "gates": {
      "ndcgCiExcludesZero": false,
      "legacyGateThreshold": 0.2,
      "legacyPrecisionLift": -0.75,
      "meanRelativeNdcgLift": -0.40103598231039306,
      "ndcgLiftGateThreshold": 0.2
     },
     "caveats": [
      "popularity baseline uses present-day TMDB popularity (no historical snapshots exist). This is NOT symmetric: present-day popularity is 100% of the baseline's ranking signal, while for the model it is one of 132 features. Post-split popularity therefore flatters the baseline, so the measured lift understates the model rather than overstating it.",
      "precision@10 is quantised to 0.1 (a top-10 cut), so the achievable relative-lift lattice depends on the baseline: +33% is the smallest passing step at a baseline of 0.3, +20% at 0.5. It is retained for ledger continuity and DISPLAY ONLY \u2014 the gate reads NDCG@10 with a bootstrap CI on the paired model-minus-baseline difference."
     ],
     "origins": [
      {
       "splitAt": "2024-09-24T19:50:37Z",
       "fraction": 0.6,
       "lowSample": false,
       "ndcgModel": 0.120381,
       "threshold": 8,
       "trainRows": 1286,
       "ndcgBaseline": 0.217611,
       "precisionModel": 0.3,
       "eligibleHoldout": 414,
       "ndcgRelativeLift": -0.4468064573941575,
       "precisionBaseline": 0.5,
       "unfilteredHoldout": 835
      },
      {
       "splitAt": "2025-04-08T19:36:37Z",
       "fraction": 0.65,
       "lowSample": false,
       "ndcgModel": 0.281191,
       "threshold": 8,
       "trainRows": 1393,
       "ndcgBaseline": 0.231778,
       "precisionModel": 0.7,
       "eligibleHoldout": 371,
       "ndcgRelativeLift": 0.21319107076599159,
       "precisionBaseline": 0.5,
       "unfilteredHoldout": 731
      },
      {
       "splitAt": "2025-09-07T08:22:06Z",
       "fraction": 0.7,
       "lowSample": false,
       "ndcgModel": 0.204321,
       "threshold": 8,
       "trainRows": 1500,
       "ndcgBaseline": 0.231778,
       "precisionModel": 0.6,
       "eligibleHoldout": 324,
       "ndcgRelativeLift": -0.11846249428332284,
       "precisionBaseline": 0.5,
       "unfilteredHoldout": 625
      },
      {
       "splitAt": "2025-11-21T21:53:34Z",
       "fraction": 0.75,
       "lowSample": false,
       "ndcgModel": 0.017074,
       "threshold": 8,
       "trainRows": 1607,
       "ndcgBaseline": 0.231778,
       "precisionModel": 0.1,
       "eligibleHoldout": 291,
       "ndcgRelativeLift": -0.9263346823253286,
       "precisionBaseline": 0.5,
       "unfilteredHoldout": 531
      },
      {
       "splitAt": "2026-04-06T18:29:00Z",
       "fraction": 0.8,
       "lowSample": false,
       "ndcgModel": 0.056719,
       "threshold": 8,
       "trainRows": 1714,
       "ndcgBaseline": 0.207585,
       "precisionModel": 0.1,
       "eligibleHoldout": 251,
       "ndcgRelativeLift": -0.726767348315148,
       "precisionBaseline": 0.4,
       "unfilteredHoldout": 426
      }
     ],
     "spearman": {
      "se": 0.06350006350009525,
      "rho": 0.1826
     },
     "threshold": {
      "frozen": 8,
      "effective": 8,
      "lowSampleFallback": false
     },
     "topRanked": [
      {
       "hit": true,
       "rating": 8,
       "titleId": "8d136647-636f-4647-a0d8-9b4df72b7ca6",
       "predicted": 7.29
      },
      {
       "hit": false,
       "rating": 5,
       "titleId": "35bc6674-7bb2-427e-96f4-fa6b56647a87",
       "predicted": 7.28
      },
      {
       "hit": false,
       "rating": 4,
       "titleId": "83ed169e-8ea2-4992-b55a-d0f1a4d47543",
       "predicted": 7.25
      },
      {
       "hit": false,
       "rating": 7,
       "titleId": "d8867576-aa35-4d81-8711-cfe4c9d43737",
       "predicted": 7.22
      },
      {
       "hit": false,
       "rating": 7,
       "titleId": "27afc87a-40ab-44da-86d9-dcef039755d6",
       "predicted": 7.22
      },
      {
       "hit": false,
       "rating": 7,
       "titleId": "7a293302-1d3d-4b2b-b090-3d3a468f9cb8",
       "predicted": 7.17
      },
      {
       "hit": false,
       "rating": 7,
       "titleId": "88c0cb6e-b2d0-43db-9162-28461e06d9f1",
       "predicted": 7.17
      },
      {
       "hit": false,
       "rating": 6,
       "titleId": "4395cb23-588b-4d2e-bd17-37f777ef52c1",
       "predicted": 7.17
      },
      {
       "hit": false,
       "rating": 5,
       "titleId": "9d17832a-e7d5-4c29-a41b-7a8f51bc0fd9",
       "predicted": 7.17
      },
      {
       "hit": false,
       "rating": 6,
       "titleId": "c3d95fdd-4925-4658-b770-8f13c18e8013",
       "predicted": 7.16
      }
     ],
     "calibration": {
      "ece": 0.259562,
      "mae": 1.136,
      "bias": 0.2174,
      "buckets": 10,
      "measuredOn": "raw base scores vs actual ratings \u2014 no residual head, no calibrator, no demotion"
     },
     "eligibility": {
      "rule": "EligibilityQueryBuilder.ContentFilteredTitles + PassesMaturity \u2014 content rules only; the interaction-footprint exclusion is deliberately NOT applied, since every holdout title is by definition rated",
      "dropped": 175,
      "eligibleHoldout": 251,
      "unfilteredHoldout": 426
     },
     "featureImportance": {
      "traktRating": 0.08,
      "attrOptimism": 0.06,
      "castAffinity": 0.05,
      "releaseAgeLog": 0.45,
      "traktVotesLog": 0.05,
      "decadeAffinity": 1,
      "runtimeMinutes": 0.06,
      "writerAffinity": 0.07,
      "simTopLovedMean": 0.06,
      "directorAffinity": 0.34,
      "simRecentCentroid": 0.08,
      "tmdbPopularityLog": 0.14,
      "userGenreAffinity": 0.08,
      "knownPeopleFraction": 0.18,
      "contrarianAdjustedRating": 0.46
     }
    }
   }
  },
  {
   "id": "00000000-0000-0000-0000-000000000031",
   "iteration": 20,
   "configHash": "54BC13471907B077",
   "splitAt": "2026-04-06T18:29:00Z",
   "trainRowCount": 1714,
   "holdoutRowCount": 251,
   "positiveThreshold": 8,
   "status": "Succeeded",
   "startedAt": "2026-10-21T19:57:15Z",
   "hyperparams": {
    "coverage": {
     "titles": 1965,
     "attrCoverage": 0.85,
     "schemaVersion": 5,
     "embeddingCoverage": 1
    },
    "hyperparams": {
     "Trees": 200,
     "Leaves": 20,
     "LearningRate": 0.2,
     "MinExamplesPerLeaf": 10
    },
    "frozenThreshold": 8,
    "lowSampleFallback": false
   },
   "eval": {
    "modelPrecisionAt10": 0,
    "baselinePrecisionAt10": 0.4,
    "relativeImprovement": -1,
    "rmse": 1.489,
    "mae": 1.1384,
    "spearmanRho": 0.1358,
    "spearmanSe": 0.0635,
    "ndcgAt10Model": 0,
    "ndcgAt10Baseline": 0.207585,
    "ndcgLiftCiLow": -0.447298,
    "ndcgLiftCiHigh": -0.057003,
    "calibrationBias": 0.209,
    "ece": 0.218576,
    "eligibleHoldoutCount": 251,
    "unfilteredHoldoutCount": 426,
    "holdoutPositiveCount": 57,
    "lowSample": false,
    "passedGate": false,
    "passedLegacyGate": false,
    "origins": [
     {
      "fraction": 0.6,
      "splitAt": "2024-09-24T19:50:37Z",
      "trainRows": 1286,
      "unfilteredHoldout": 835,
      "eligibleHoldout": 414,
      "threshold": 8,
      "lowSample": false,
      "ndcgModel": 0.160432,
      "ndcgBaseline": 0.217611,
      "ndcgRelativeLift": -0.26275785690980696,
      "precisionModel": 0.4,
      "precisionBaseline": 0.5
     },
     {
      "fraction": 0.65,
      "splitAt": "2025-04-08T19:36:37Z",
      "trainRows": 1393,
      "unfilteredHoldout": 731,
      "eligibleHoldout": 371,
      "threshold": 8,
      "lowSample": false,
      "ndcgModel": 0.291424,
      "ndcgBaseline": 0.231778,
      "ndcgRelativeLift": 0.2573410763748069,
      "precisionModel": 0.6,
      "precisionBaseline": 0.5
     },
     {
      "fraction": 0.7,
      "splitAt": "2025-09-07T08:22:06Z",
      "trainRows": 1500,
      "unfilteredHoldout": 625,
      "eligibleHoldout": 324,
      "threshold": 8,
      "lowSample": false,
      "ndcgModel": 0.02836,
      "ndcgBaseline": 0.231778,
      "ndcgRelativeLift": -0.8776415362976642,
      "precisionModel": 0.1,
      "precisionBaseline": 0.5
     },
     {
      "fraction": 0.75,
      "splitAt": "2025-11-21T21:53:34Z",
      "trainRows": 1607,
      "unfilteredHoldout": 531,
      "eligibleHoldout": 291,
      "threshold": 8,
      "lowSample": false,
      "ndcgModel": 0.047266,
      "ndcgBaseline": 0.231778,
      "ndcgRelativeLift": -0.7960721034783284,
      "precisionModel": 0.2,
      "precisionBaseline": 0.5
     },
     {
      "fraction": 0.8,
      "splitAt": "2026-04-06T18:29:00Z",
      "trainRows": 1714,
      "unfilteredHoldout": 426,
      "eligibleHoldout": 251,
      "threshold": 8,
      "lowSample": false,
      "ndcgModel": 0,
      "ndcgBaseline": 0.207585,
      "ndcgRelativeLift": -1,
      "precisionModel": 0,
      "precisionBaseline": 0.4
     }
    ],
    "detail": {
     "ndcg": {
      "seed": 20260909,
      "ciLow": -0.447298,
      "model": 0,
      "ciHigh": -0.057003,
      "baseline": 0.207585,
      "relevance": "graded: max(0, rating \u2212 threshold + 1), gain 2^rel \u2212 1 \u2014 the threshold itself is the first rung, so a title rated exactly at the 'loved' line still earns gain",
      "resamples": 1000,
      "pairedLift": -0.207585
     },
     "gates": {
      "ndcgCiExcludesZero": false,
      "legacyGateThreshold": 0.2,
      "legacyPrecisionLift": -1,
      "meanRelativeNdcgLift": -0.5358260840621986,
      "ndcgLiftGateThreshold": 0.2
     },
     "caveats": [
      "popularity baseline uses present-day TMDB popularity (no historical snapshots exist). This is NOT symmetric: present-day popularity is 100% of the baseline's ranking signal, while for the model it is one of 132 features. Post-split popularity therefore flatters the baseline, so the measured lift understates the model rather than overstating it.",
      "precision@10 is quantised to 0.1 (a top-10 cut), so the achievable relative-lift lattice depends on the baseline: +33% is the smallest passing step at a baseline of 0.3, +20% at 0.5. It is retained for ledger continuity and DISPLAY ONLY \u2014 the gate reads NDCG@10 with a bootstrap CI on the paired model-minus-baseline difference."
     ],
     "origins": [
      {
       "splitAt": "2024-09-24T19:50:37Z",
       "fraction": 0.6,
       "lowSample": false,
       "ndcgModel": 0.160432,
       "threshold": 8,
       "trainRows": 1286,
       "ndcgBaseline": 0.217611,
       "precisionModel": 0.4,
       "eligibleHoldout": 414,
       "ndcgRelativeLift": -0.26275785690980696,
       "precisionBaseline": 0.5,
       "unfilteredHoldout": 835
      },
      {
       "splitAt": "2025-04-08T19:36:37Z",
       "fraction": 0.65,
       "lowSample": false,
       "ndcgModel": 0.291424,
       "threshold": 8,
       "trainRows": 1393,
       "ndcgBaseline": 0.231778,
       "precisionModel": 0.6,
       "eligibleHoldout": 371,
       "ndcgRelativeLift": 0.2573410763748069,
       "precisionBaseline": 0.5,
       "unfilteredHoldout": 731
      },
      {
       "splitAt": "2025-09-07T08:22:06Z",
       "fraction": 0.7,
       "lowSample": false,
       "ndcgModel": 0.02836,
       "threshold": 8,
       "trainRows": 1500,
       "ndcgBaseline": 0.231778,
       "precisionModel": 0.1,
       "eligibleHoldout": 324,
       "ndcgRelativeLift": -0.8776415362976642,
       "precisionBaseline": 0.5,
       "unfilteredHoldout": 625
      },
      {
       "splitAt": "2025-11-21T21:53:34Z",
       "fraction": 0.75,
       "lowSample": false,
       "ndcgModel": 0.047266,
       "threshold": 8,
       "trainRows": 1607,
       "ndcgBaseline": 0.231778,
       "precisionModel": 0.2,
       "eligibleHoldout": 291,
       "ndcgRelativeLift": -0.7960721034783284,
       "precisionBaseline": 0.5,
       "unfilteredHoldout": 531
      },
      {
       "splitAt": "2026-04-06T18:29:00Z",
       "fraction": 0.8,
       "lowSample": false,
       "ndcgModel": 0,
       "threshold": 8,
       "trainRows": 1714,
       "ndcgBaseline": 0.207585,
       "precisionModel": 0,
       "eligibleHoldout": 251,
       "ndcgRelativeLift": -1,
       "precisionBaseline": 0.4,
       "unfilteredHoldout": 426
      }
     ],
     "spearman": {
      "se": 0.06350006350009525,
      "rho": 0.1358
     },
     "threshold": {
      "frozen": 8,
      "effective": 8,
      "lowSampleFallback": false
     },
     "topRanked": [
      {
       "hit": false,
       "rating": 6,
       "titleId": "6692a288-aad0-415c-b2e1-4ddb750ff150",
       "predicted": 7.26
      },
      {
       "hit": false,
       "rating": 5,
       "titleId": "9d17832a-e7d5-4c29-a41b-7a8f51bc0fd9",
       "predicted": 7.24
      },
      {
       "hit": false,
       "rating": 7,
       "titleId": "88c0cb6e-b2d0-43db-9162-28461e06d9f1",
       "predicted": 7.2
      },
      {
       "hit": false,
       "rating": 7,
       "titleId": "ca63e4d9-cce0-479c-9c69-15ad427e3f60",
       "predicted": 7.18
      },
      {
       "hit": false,
       "rating": 6,
       "titleId": "70c738ec-2b77-4871-a7d5-2cf267d99b43",
       "predicted": 7.17
      },
      {
       "hit": false,
       "rating": 7,
       "titleId": "7a293302-1d3d-4b2b-b090-3d3a468f9cb8",
       "predicted": 7.17
      },
      {
       "hit": false,
       "rating": 5,
       "titleId": "35bc6674-7bb2-427e-96f4-fa6b56647a87",
       "predicted": 7.16
      },
      {
       "hit": false,
       "rating": 6,
       "titleId": "b09974c4-42f6-4222-9dd9-d14dc36af962",
       "predicted": 7.16
      },
      {
       "hit": false,
       "rating": 7,
       "titleId": "d8867576-aa35-4d81-8711-cfe4c9d43737",
       "predicted": 7.16
      },
      {
       "hit": false,
       "rating": 7,
       "titleId": "707b727e-ce3f-44ba-a161-43d667634912",
       "predicted": 7.15
      }
     ],
     "calibration": {
      "ece": 0.218576,
      "mae": 1.1384,
      "bias": 0.209,
      "buckets": 10,
      "measuredOn": "raw base scores vs actual ratings \u2014 no residual head, no calibrator, no demotion"
     },
     "eligibility": {
      "rule": "EligibilityQueryBuilder.ContentFilteredTitles + PassesMaturity \u2014 content rules only; the interaction-footprint exclusion is deliberately NOT applied, since every holdout title is by definition rated",
      "dropped": 175,
      "eligibleHoldout": 251,
      "unfilteredHoldout": 426
     },
     "featureImportance": {
      "genreDrift": 0.06,
      "traktRating": 0.09,
      "attrOptimism": 0.09,
      "releaseAgeLog": 0.46,
      "traktVotesLog": 0.05,
      "decadeAffinity": 1,
      "runtimeMinutes": 0.06,
      "writerAffinity": 0.08,
      "simTopLovedMean": 0.06,
      "directorAffinity": 0.34,
      "simRecentCentroid": 0.08,
      "tmdbPopularityLog": 0.15,
      "userGenreAffinity": 0.09,
      "knownPeopleFraction": 0.18,
      "contrarianAdjustedRating": 0.47
     }
    }
   }
  }
 ]
};

export const LAB_CROSS_REFERENCE = {
 "window": "all",
 "population": {
  "name": "Titles served under a cross-reference standing that you have since rated",
  "selectionNote": "Only about 2% of ratings are made in the app, so this joins by time rather than cause, and it holds only titles you chose to watch. It corroborates the offline backtest; it never gates anything."
 },
 "crossReference": {
  "minimumOutcomes": 20,
  "online": [
   {
    "verdict": "ConfirmedKeep",
    "n": 4,
    "meanLaterRating": 7.75,
    "lovedRate": 0.5,
    "dislikedRate": 0,
    "answerable": false
   },
   {
    "verdict": "Out",
    "n": 7,
    "meanLaterRating": 7,
    "lovedRate": 0.2857,
    "dislikedRate": 0,
    "answerable": false
   },
   {
    "verdict": "ProvisionalKeep",
    "n": 28,
    "meanLaterRating": 6.68,
    "lovedRate": 0.3214,
    "dislikedRate": 0.0714,
    "answerable": true
   },
   {
    "verdict": "Unsure",
    "n": 5,
    "meanLaterRating": 7,
    "lovedRate": 0.4,
    "dislikedRate": 0,
    "answerable": false
   }
  ],
  "backtests": [
   {
    "promptVersion": 2,
    "evaluatedAt": "2026-09-10T22:26:28Z",
    "judged": 1194,
    "auc": 0.655,
    "meanLaterRatingKeep": 6.83,
    "meanLaterRatingOut": 6.16,
    "keepLovedRate": 0.3315,
    "keepDislikedRate": 0.0809,
    "falseRuleOutRate": 0.1619,
    "falseRuleOutLow": 0.1232,
    "falseRuleOutHigh": 0.2097,
    "trueRuleOutRate": 0.2278,
    "unsureRate": 0.4585,
    "gating": "demote-only",
    "caveat": "Measured on 278 holdout title(s) this account went on to rate 8 or higher and 180 it rated 4 or lower, out of 1194 judged. The false-rule-out rate is 0.162 with a 95% Wilson interval of [0.123, 0.210] \u2014 the gate reads the UPPER bound, because at this sample size the point estimate alone cannot distinguish a good judge from a lucky one. 20 eligible holdout title(s) were skipped for having no plot embedding and no extracted attributes, and are not in any denominator here.",
    "current": true
   },
   {
    "promptVersion": 1,
    "evaluatedAt": "2026-09-10T22:26:28Z",
    "judged": 1307,
    "auc": 0.611,
    "meanLaterRatingKeep": 6.53,
    "meanLaterRatingOut": 6.21,
    "keepLovedRate": 0.2837,
    "keepDislikedRate": 0.13,
    "falseRuleOutRate": 0.0906,
    "falseRuleOutLow": 0.0638,
    "falseRuleOutHigh": 0.1271,
    "trueRuleOutRate": 0.1152,
    "unsureRate": 0.3569,
    "gating": "demote-only",
    "caveat": "Measured on 320 holdout title(s) this account went on to rate 8 or higher and 191 it rated 4 or lower, out of 1307 judged. The false-rule-out rate is 0.091 with a 95% Wilson interval of [0.064, 0.127] \u2014 the gate reads the UPPER bound, because at this sample size the point estimate alone cannot distinguish a good judge from a lucky one. 20 eligible holdout title(s) were skipped for having no plot embedding and no extracted attributes, and are not in any denominator here. False-rule-out and true-rule-out are within 5 points of each other, which means the out verdict is not distinguishing loved from disliked on this history: it is hiding titles this account went on to love at very nearly the rate it hides ones they went on to dislike. The rank AUC of 0.611 says the same thing without reference to the out threshold \u2014 0.500 is chance.",
    "current": false
   }
  ]
 }
};
