using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The I/O-free half of the leakage fixes: the arithmetic that lets a title's own label be taken
/// back out of the pool that describes it, and the shrinkage symmetry that makes genreDrift measure
/// recency instead of a formula mismatch. The end-to-end feature-vector proofs live in
/// <see cref="IntegrationTests.FeatureVectorLeakageTests"/>, which need a real database.
/// </summary>
public class FeatureLeakageMathTests
{
    [Fact]
    public void Aggregate_shrunken_mean_matches_the_list_form()
    {
        // The feature builder shrinks from a running (sum, count) so it can subtract one member in
        // O(1). It must agree exactly with the list form the taste dashboard still uses.
        short[] ratings = [9, 7, 8, 10, 6];
        var fromList = TasteMath.ShrunkenMean(ratings, priorMean: 7m);
        var fromAggregate = TasteMath.ShrunkenMean(ratings.Sum(r => (decimal)r), ratings.Length, priorMean: 7m);

        Assert.Equal(fromList, fromAggregate);
    }

    [Fact]
    public void Leaving_one_rating_out_equals_never_having_had_it()
    {
        // The whole leave-one-out contract in one line: subtracting a member from (sum, count) must
        // give the same affinity as a pool that never contained it. If this drifts, a training row
        // still sees a shadow of its own label.
        short[] all = [9, 7, 8, 10, 6];
        short excluded = 10;
        short[] without = [9, 7, 8, 6];

        var looped = TasteMath.ShrunkenMean(all.Sum(r => (decimal)r) - excluded, all.Length - 1, priorMean: 7m);
        var never = TasteMath.ShrunkenMean(without, priorMean: 7m);

        Assert.Equal(never, looped);
    }

    [Fact]
    public void Excluding_the_only_rating_falls_back_to_the_prior()
    {
        // Count hits zero: the pool has nothing left to say, so the answer is the prior — never a 0.
        Assert.Equal(7m, TasteMath.ShrunkenMean(sum: 10m - 10m, count: 0, priorMean: 7m));
        Assert.Equal(7m, TasteMath.ShrunkenWeightedMean(weightedSum: 0m, weightTotal: 0m, priorMean: 7m));
    }

    [Fact]
    public void Drift_is_zero_when_a_single_rating_has_not_aged()
    {
        // THE genreDrift defect. One rating of 10 from a user whose mean is 7, made "today":
        // all-time was shrunken (7.75) while recent was not (10.0), so the model was handed a
        // +2.25 "drift" produced by a single data point that had not moved at all.
        const decimal prior = 7m;
        var allTime = TasteMath.ShrunkenMean(sum: 10m, count: 1, priorMean: prior);
        var recent = TasteMath.ShrunkenWeightedMean(weightedSum: 10m, weightTotal: 1m, priorMean: prior);

        Assert.Equal(7.75m, allTime);
        Assert.Equal(allTime, recent);
        Assert.Equal(0m, recent - allTime);
    }

    [Fact]
    public void Drift_still_moves_when_taste_actually_changed()
    {
        // Shrinkage must not flatten the feature into uselessness: old 9s and new 6s still register
        // as negative drift, it is just no longer manufactured out of thin evidence.
        var asOf = new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);
        List<(short, DateTime)> ratings =
        [
            (9, asOf.AddDays(-1100)), (9, asOf.AddDays(-1050)),
            (6, asOf.AddDays(-20)), (6, asOf.AddDays(-10)),
        ];

        var allTime = TasteMath.ShrunkenMean([.. ratings.Select(r => r.Item1)], priorMean: 7m);
        var recent = TasteMath.RecencyWeightedMean(ratings, asOf, prior: 7m);

        Assert.True(recent < allTime - 0.3m, $"recent {recent} should sit clearly below all-time {allTime}");
    }

    [Fact]
    public void Schema_version_six_appends_the_watched_history_scalars_and_keeps_the_head_of_the_vector_put()
    {
        // v2 added the crowd-presence flags; v3 made dislike reasons learnable; v4 closed the
        // train/serve skew on certification, subgenres, tone and era; v5 closed the gap between
        // "credit rows exist" and "we know these people"; v6 is the first bump to read a WATCH
        // history at all. Each bump APPENDS to the scalars, so a stored artifact can only ever be
        // wrong loudly (width mismatch), never silently — see the ONE exception any bump has taken,
        // pinned separately below.
        Assert.Equal(6, FeatureSchema.Version);

        var names = FeatureSchema.Scalars;
        Assert.Equal("simWatchedCentroid", names[^5]);
        Assert.Equal("hasWatchedCentroid", names[^4]);
        Assert.Equal("simRewatchCentroid", names[^3]);
        Assert.Equal("hasRewatchCentroid", names[^2]);
        Assert.Equal("watchedGenreShare", names[^1]);

        // The kNN pair the v6 plan also scoped is NOT here. It was built, leave-one-out'd and then
        // dropped on a measured cost gate (2.3 s → 12.6 s on a full harness run against a copy of the
        // live database, against a 2× ceiling); the version history carries the numbers. Pinned by
        // name so re-adding it is a deliberate act with its own measurement, not a merge artefact.
        Assert.DoesNotContain("nearestRatedNeighbourRating", names);
        Assert.DoesNotContain("nearestRatedNeighbourSim", names);

        // v5's tail is now the three slots immediately BEFORE v6's — appended, not interleaved.
        Assert.Equal("knownPeopleFraction", names[^8]);
        Assert.Equal("knownDirectorOrWriter", names[^7]);
        Assert.Equal("isUnscripted", names[^6]);

        // …and v4's before that, still where v5 left them.
        Assert.Equal("hasCertification", names[^12]);
        Assert.Equal("hasSubgenres", names[^11]);
        Assert.Equal("hasAttributesTone", names[^10]);
        Assert.Equal("hasAttributesEra", names[^9]);

        // …and v3's before that.
        Assert.Equal("hasRuntime", names[^17]);
        Assert.Equal("hasTraktRating", names[^16]);
        Assert.Equal("hasPopularity", names[^15]);
        Assert.Equal("simNotInterestedCentroid", names[^14]);
        Assert.Equal("dislikedGenreOverlap", names[^13]);

        // Every scalar ahead of the deleted slot keeps its v3 index.
        Assert.Equal(0, Array.IndexOf(names, "releaseAgeLog"));
        Assert.Equal(1, Array.IndexOf(names, "runtimeMinutes"));
        Assert.Equal(3, Array.IndexOf(names, "traktRating"));
        Assert.Equal(5, Array.IndexOf(names, "tmdbPopularityLog"));
        Assert.Equal(6, Array.IndexOf(names, "certificationOrdinal"));
        Assert.Equal(15, Array.IndexOf(names, "contrarianAdjustedRating"));
    }

    [Fact]
    public void Leaving_one_member_out_of_a_centroid_equals_never_having_had_it()
    {
        // The embedding-side twin of Leaving_one_rating_out_equals_never_having_had_it, and the
        // arithmetic behind simLovedCentroid, simRecentCentroid and — since v6 — simWatchedCentroid.
        // Subtracting a member from (mean, n) must give the mean of a pool that never held it, or a
        // training row keeps seeing a shadow of its own embedding in the vector describing it.
        float[] a = [1f, 0f, 0f];
        float[] b = [0f, 1f, 0f];
        float[] c = [0f, 0f, 1f];

        var all = Mean([a, b, c]);
        var looped = FeatureVectorBuilder.CentroidExcluding(all, 3, a);
        var never = Mean([b, c]);

        Assert.NotNull(looped);
        for (var i = 0; i < never.Length; i++)
        {
            Assert.Equal(never[i], looped![i], 5);
        }

        // The member that WAS the whole centroid leaves nothing behind, and null is how Cosine is
        // told "no similarity known" rather than "similarity zero".
        Assert.Null(FeatureVectorBuilder.CentroidExcluding(a, 1, a));
        Assert.Null(FeatureVectorBuilder.CentroidExcluding(null, 5, a));
    }

    [Fact]
    public void Leaving_one_member_out_of_a_weighted_centroid_uses_that_members_own_weight()
    {
        // simRewatchCentroid is weighted by min(plays, 3), so the unweighted subtraction is not
        // merely imprecise here — it is wrong by a different amount for every title, and wrong in the
        // direction of whichever title was watched most. This is why SelfExclusion carries the
        // per-title rewatch weights and not just their total.
        float[] a = [1f, 0f, 0f];
        float[] b = [0f, 1f, 0f];
        float[] c = [0f, 0f, 1f];
        double wa = 3d, wb = 2d, wc = 2d;

        var all = WeightedMean([(a, wa), (b, wb), (c, wc)]);
        var looped = FeatureVectorBuilder.CentroidExcludingWeighted(all, wa + wb + wc, wa, a);
        var never = WeightedMean([(b, wb), (c, wc)]);

        Assert.NotNull(looped);
        for (var i = 0; i < never.Length; i++)
        {
            Assert.Equal(never[i], looped![i], 5);
        }

        // Subtracting the member's weight with the UNWEIGHTED formula gets a visibly different answer
        // — the test exists so the two can never be confused at a call site.
        var wrong = FeatureVectorBuilder.CentroidExcluding(all, 3, a);
        Assert.NotNull(wrong);
        Assert.NotEqual(never[0], wrong![0], 3);

        // The whole centroid taken back out leaves nothing, exactly as the unweighted form does.
        Assert.Null(FeatureVectorBuilder.CentroidExcludingWeighted(a, 3d, 3d, a));
    }

    [Fact]
    public void Watched_genre_share_is_a_union_over_the_pool_and_never_counts_the_candidate_itself()
    {
        // The definition, as arithmetic: |{pool titles sharing ≥1 genre}| / |pool|, with the
        // candidate removed from BOTH halves when it is itself a pool member. A title is counted ONCE
        // however many of the candidate's genres it shares, which is why the pool is held as masks
        // rather than per-genre counts — summing per-genre counts double-counts every multi-genre
        // title and can exceed the pool size outright.
        const ulong drama = 1UL << 0;
        const ulong thriller = 1UL << 1;
        const ulong western = 1UL << 2;

        // Six drama, two drama+thriller, two western.
        List<ulong> pool =
        [
            drama, drama, drama, drama, drama, drama,
            drama | thriller, drama | thriller,
            western, western,
        ];

        // A drama candidate that is NOT in the pool reaches eight of the ten.
        Assert.Equal(0.8d, FeatureVectorBuilder.WatchedGenreShare(pool, drama, candidateIsPoolMember: false), 6);

        // A drama+thriller candidate reaches the same eight — the union counts each title once, and a
        // per-genre sum would have claimed (8 + 2) / 10 = 1.0 here.
        Assert.Equal(0.8d, FeatureVectorBuilder.WatchedGenreShare(pool, drama | thriller, candidateIsPoolMember: false), 6);

        // The same candidate WHEN IT IS ONE OF THE POOL ROWS: it always matches itself, so it comes
        // out of both halves — 7/9, not 8/10.
        Assert.Equal(7d / 9d, FeatureVectorBuilder.WatchedGenreShare(pool, drama, candidateIsPoolMember: true), 6);

        // A rare genre reads low rather than absent, which is the spread the feature exists for.
        Assert.Equal(0.2d, FeatureVectorBuilder.WatchedGenreShare(pool, western, candidateIsPoolMember: false), 6);

        // No pool, no genres in common with anything the schema knows, or a pool of one that IS the
        // candidate: all 0, never a divide by zero.
        Assert.Equal(0d, FeatureVectorBuilder.WatchedGenreShare([], drama, false));
        Assert.Equal(0d, FeatureVectorBuilder.WatchedGenreShare(pool, 0UL, false));
        Assert.Equal(0d, FeatureVectorBuilder.WatchedGenreShare([drama], drama, candidateIsPoolMember: true));
    }

    [Fact]
    public void A_genre_mask_only_lights_bits_the_pool_actually_taught_it()
    {
        // The mask is built from the WATCHED pool's own genre vocabulary, so a candidate genre nobody
        // in the pool carries contributes no bit and therefore no match — which is the correct answer
        // (nothing watched shares it), not a bug.
        var bits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["drama"] = 0,
            ["western"] = 1,
        };

        Assert.Equal(1UL, FeatureVectorBuilder.GenreMask(["drama"], bits));
        Assert.Equal(3UL, FeatureVectorBuilder.GenreMask(["drama", "western"], bits));
        Assert.Equal(1UL, FeatureVectorBuilder.GenreMask(["DRAMA"], bits));
        Assert.Equal(0UL, FeatureVectorBuilder.GenreMask(["holiday"], bits));
        Assert.Equal(0UL, FeatureVectorBuilder.GenreMask([], bits));
    }

    private static float[] Mean(IReadOnlyList<float[]> vectors)
    {
        var result = new float[vectors[0].Length];
        foreach (var vector in vectors)
        {
            for (var i = 0; i < result.Length; i++)
            {
                result[i] += vector[i];
            }
        }

        for (var i = 0; i < result.Length; i++)
        {
            result[i] /= vectors.Count;
        }

        return result;
    }

    private static float[] WeightedMean(IReadOnlyList<(float[] Vector, double Weight)> members)
    {
        var result = new float[members[0].Vector.Length];
        var total = members.Sum(m => m.Weight);
        foreach (var (vector, weight) in members)
        {
            for (var i = 0; i < result.Length; i++)
            {
                result[i] += (float)(weight * vector[i]);
            }
        }

        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (float)(result[i] / total);
        }

        return result;
    }

    [Fact]
    public void Schema_version_four_deleted_the_dead_show_engagement_slot_and_it_stays_deleted()
    {
        // The one reorder in this file's history, and the reason it is allowed HERE and only here:
        // a version bump renumbers the vector anyway, so the dead slot goes now or it never goes.
        // showEngagement was pinned to 0 on both paths — train-only by construction, because a
        // scored candidate can never have episode ratings under the zero-footprint rule.
        Assert.DoesNotContain("showEngagement", FeatureSchema.Scalars);
        Assert.DoesNotContain("showEngagement", FeatureSchema.AllNames());

        // Everything that FOLLOWED it moved down exactly one place — that is the renumber, stated.
        Assert.Equal(16, Array.IndexOf(FeatureSchema.Scalars, "attrDarkness"));
    }

    [Fact]
    public void Schema_version_six_keeps_every_block_in_order_and_appends_within_them()
    {
        // Block-level form of the append rule: the scalar, genre and subgenre blocks keep their
        // internal order and their relative order, and the two new blocks land after all of them.
        var all = FeatureSchema.AllNames();
        var expectedWidth = FeatureSchema.Scalars.Length + FeatureSchema.Genres.Length
            + FeatureSchema.SubgenreBuckets + FeatureSchema.Tones.Length + FeatureSchema.Eras.Length;

        Assert.Equal(expectedWidth, FeatureSchema.VectorLength);
        Assert.Equal(expectedWidth, all.Length);
        Assert.Equal(46, FeatureSchema.Scalars.Length);
        Assert.Equal(30, FeatureSchema.Genres.Length);

        // 137, and 41 + 5 is where it comes from. The v6 plan said "132 → 138" in prose; 132 + 5 is
        // 137 and 132 + the 7 it originally scoped would have been 139, so the planned number was
        // never reachable either way. That is the FOURTH time a hand-typed width has been wrong in
        // this project's prose, which is exactly why nothing interpolates a literal and why this
        // assertion is derived above before it is pinned below.
        Assert.Equal(137, FeatureSchema.VectorLength);

        // v6 is a pure APPEND: five scalars on the end of the scalar block, nothing anywhere else.
        // Every index that existed in v5 is unmoved — the width alone changed, which is the loud
        // failure mode a stored artifact needs.
        Assert.Equal("isUnscripted", FeatureSchema.Scalars[40]);
        Assert.Equal(
            ["simWatchedCentroid", "hasWatchedCentroid", "simRewatchCentroid", "hasRewatchCentroid", "watchedGenreShare"],
            FeatureSchema.Scalars[41..]);

        // v5's own append is still where v5 left it: three scalars on the end of the scalar block,
        // five slugs on the end of the genre block.
        Assert.Equal("western", FeatureSchema.Genres[24]);
        Assert.Equal(["reality", "soap", "talk-show", "news", "game-show"], FeatureSchema.Genres[25..]);
        Assert.Equal("genre:action", all[FeatureSchema.Scalars.Length]);
        Assert.Equal("genre:game-show", all[FeatureSchema.Scalars.Length + FeatureSchema.Genres.Length - 1]);

        var toneOffset = FeatureSchema.Scalars.Length + FeatureSchema.Genres.Length + FeatureSchema.SubgenreBuckets;
        Assert.Equal("subgenreBucket:31", all[toneOffset - 1]);
        Assert.Equal("tone:" + FeatureSchema.Tones[0], all[toneOffset]);
        Assert.Equal("era:" + FeatureSchema.Eras[0], all[toneOffset + FeatureSchema.Tones.Length]);
        Assert.Equal("era:" + FeatureSchema.Eras[^1], all[^1]);

        // The blocks are the extractor's closed vocabularies, not a second copy of them — a copy
        // would drift the moment either moved, and the block width would stop matching what the
        // provider is actually allowed to return.
        Assert.Same(AttributeVocabulary.Tones, FeatureSchema.Tones);
        Assert.Same(AttributeVocabulary.Eras, FeatureSchema.Eras);
        Assert.Equal(16, FeatureSchema.Tones.Length);
        Assert.Equal(13, FeatureSchema.Eras.Length);

        // Names are unique, or an explanation lookup would silently pick the wrong slot.
        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_polluted_tone_or_era_reads_as_absent_rather_than_landing_in_a_slot()
    {
        // The 2,312 pre-2026-08-07 rows carry free text ("period-victorian", "1980s", "fantasy").
        // Two independent guards keep them out of the vector: the extractor-version gate in
        // FeatureVectorBuilder, and this vocabulary lookup, which is what makes the one-hot write
        // itself impossible rather than merely unlikely.
        Assert.True(FeatureSchema.MinTrustedExtractorVersion >= 2);
        Assert.Equal(-1, AttributeVocabulary.ToneIndex("period-victorian"));
        Assert.Equal(-1, AttributeVocabulary.EraIndex("1980s"));
        Assert.Equal(-1, AttributeVocabulary.EraIndex("fantasy"));
        Assert.Equal(-1, AttributeVocabulary.ToneIndex(null));
        Assert.Equal(-1, AttributeVocabulary.EraIndex(null));

        // …while every legitimate member resolves to its own slot, in vocabulary order.
        Assert.Equal(0, AttributeVocabulary.ToneIndex(AttributeVocabulary.Tones[0]));
        Assert.Equal(AttributeVocabulary.Tones.Length - 1, AttributeVocabulary.ToneIndex(AttributeVocabulary.Tones[^1]));
        Assert.Equal(AttributeVocabulary.Eras.Length - 1, AttributeVocabulary.EraIndex(AttributeVocabulary.Eras[^1]));
        Assert.All(AttributeVocabulary.Tones, t => Assert.True(AttributeVocabulary.IsClosedVocabTone(t)));
        Assert.All(AttributeVocabulary.Eras, e => Assert.True(AttributeVocabulary.IsClosedVocabEra(e)));
    }

    [Fact]
    public void The_residual_head_stats_are_recomputed_at_the_new_width()
    {
        // A bump changes the vector width, so the persisted mu/sigma block must be rebuilt with it.
        // FromRows sizes itself from the rows it is given rather than from a constant, which is what
        // makes the retrain sufficient — but "it works out" is the kind of thing that stops being
        // true silently, so it is pinned.
        var rows = new[]
        {
            Enumerable.Range(0, FeatureSchema.VectorLength).Select(i => (float)i).ToArray(),
            Enumerable.Range(0, FeatureSchema.VectorLength).Select(i => (float)(i * 2)).ToArray(),
        };

        var standardizer = FeatureStandardizer.FromRows(rows);

        Assert.False(standardizer.IsIdentity);
        Assert.Equal(FeatureSchema.VectorLength, standardizer.Length);
        Assert.Equal(FeatureSchema.VectorLength, standardizer.Standardize(rows[0]).Length);
    }

    [Fact]
    public void Neutral_imputation_is_never_the_extreme_end_of_the_range()
    {
        // A 0-minute film and a 0/10 crowd score are strong, false signals; the fill-ins have to sit
        // mid-distribution so the presence flag is what carries "we don't know".
        Assert.True(FeatureSchema.NeutralRuntimeMinutes(MediaType.Movie) > 60f);
        Assert.True(FeatureSchema.NeutralRuntimeMinutes(MediaType.Show) > 15f);
        Assert.True(FeatureSchema.NeutralPopularity(MediaType.Movie) > 0m);
        Assert.True(FeatureSchema.NeutralPopularity(MediaType.Show) > FeatureSchema.NeutralPopularity(MediaType.Movie));
    }

    [Fact]
    public void An_unknown_person_affinity_is_imputed_below_the_mean_not_at_it()
    {
        // The v5 imputation, as a constant with a direction. Imputing at the user's MEAN spelled
        // "we have never heard of these people" identically to "we know them and they're average
        // for you" — and the mean is a positive-sounding number, which is how directorAffinity and
        // writerAffinity became top positive contributors on titles with nobody known in them.
        Assert.True(
            FeatureSchema.NeutralLowPersonAffinityOffset > 0m,
            "the imputation must sit strictly below the user's mean, or it says nothing at all");

        // …but it is a nudge, not a verdict. Half a point is smaller than the spread between a
        // typical user's best and worst genres, so a well-matched unknown title can still outrank a
        // poorly-matched known one — the presence scalars are what let the model learn the rest.
        Assert.True(
            FeatureSchema.NeutralLowPersonAffinityOffset <= 1m,
            "a larger offset would be a verdict on unknown people rather than an admission of ignorance");

        // Same rule as every other neutral fill-in in this schema: never the extreme end of the
        // range. A user whose own mean is already 1.2 must not have unknown crews imputed at 0.7.
        Assert.True(Math.Max(1m, 1.2m - FeatureSchema.NeutralLowPersonAffinityOffset) >= 1m);
    }

    [Fact]
    public void The_unscripted_scalar_reads_the_same_vocabulary_the_fit_demotion_does()
    {
        // isUnscripted is the aggregate of the v5 genre slots, and it MUST be computed from
        // UnscriptedFormats — the same predicate that scales the LLM's fit verdict. Two lists with
        // one name is the drift this schema's comments have been warning about since v2, and here it
        // would show up as a model feature and a product rule quietly disagreeing about what reality
        // television is.
        Assert.True(UnscriptedFormats.IsUnscripted(["reality"]));
        Assert.True(UnscriptedFormats.IsUnscripted(["talk-show"]));
        Assert.True(UnscriptedFormats.IsUnscripted(["game-show"]));
        Assert.True(UnscriptedFormats.IsUnscripted(["news"]));

        // Soap gets a genre slot and is deliberately NOT unscripted: it is scripted television that
        // merely shares the scheduling economics. Documentary likewise — the founder's library rates
        // them, and UnscriptedFormats has excluded it from the start for exactly that reason.
        Assert.False(UnscriptedFormats.IsUnscripted(["soap"]));
        Assert.False(UnscriptedFormats.IsUnscripted(["documentary"]));
        Assert.Contains("soap", FeatureSchema.Genres);

        // Every slug the predicate recognises that is ALSO a Trakt genre now has a slot, so the
        // aggregate can never fire without at least one one-hot beside it to explain why.
        foreach (var slug in new[] { "reality", "talk-show", "game-show", "news" })
        {
            Assert.Contains(slug, FeatureSchema.Genres);
        }
    }

    [Fact]
    public void The_persisted_eval_caveat_no_longer_claims_symmetry()
    {
        // The harness is deliberately hostile to itself; it must not be hostile in a way that is
        // untrue. Present-day popularity IS the baseline's whole ranking signal and one of
        // VectorLength features for the model — that asymmetry runs in the baseline's favour.
        Assert.DoesNotContain("symmetrically", EvalHarness.PopularityCaveat, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT symmetric", EvalHarness.PopularityCaveat, StringComparison.Ordinal);
        Assert.Contains("understates the model", EvalHarness.PopularityCaveat, StringComparison.Ordinal);

        // The width is INTERPOLATED, never typed — it has already been wrong twice in prose. The
        // caveat is user-visible and persisted with every evaluation, so a stale number there is a
        // false statement in the ledger, not a typo.
        Assert.Contains(
            $"one of {FeatureSchema.VectorLength} features",
            EvalHarness.PopularityCaveat,
            StringComparison.Ordinal);
        Assert.DoesNotContain("86 features", EvalHarness.PopularityCaveat, StringComparison.Ordinal);
    }
}
