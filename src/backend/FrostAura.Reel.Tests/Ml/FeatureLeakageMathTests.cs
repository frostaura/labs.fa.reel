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
    public void Schema_version_five_appends_the_presence_flags_and_keeps_the_head_of_the_vector_put()
    {
        // v2 added the crowd-presence flags; v3 made dislike reasons learnable; v4 closed the
        // train/serve skew on certification, subgenres, tone and era; v5 closes the last one — the
        // gap between "credit rows exist" and "we know these people". Each bump APPENDS to the
        // scalars, so a stored artifact can only ever be wrong loudly (width mismatch), never
        // silently — see the ONE exception any bump has taken, pinned separately below.
        Assert.Equal(5, FeatureSchema.Version);

        var names = FeatureSchema.Scalars;
        Assert.Equal("knownPeopleFraction", names[^3]);
        Assert.Equal("knownDirectorOrWriter", names[^2]);
        Assert.Equal("isUnscripted", names[^1]);

        // v4's tail is now the four slots immediately BEFORE v5's — appended, not interleaved.
        Assert.Equal("hasCertification", names[^7]);
        Assert.Equal("hasSubgenres", names[^6]);
        Assert.Equal("hasAttributesTone", names[^5]);
        Assert.Equal("hasAttributesEra", names[^4]);

        // …and v3's before that, still where v4 left them.
        Assert.Equal("hasRuntime", names[^12]);
        Assert.Equal("hasTraktRating", names[^11]);
        Assert.Equal("hasPopularity", names[^10]);
        Assert.Equal("simNotInterestedCentroid", names[^9]);
        Assert.Equal("dislikedGenreOverlap", names[^8]);

        // Every scalar ahead of the deleted slot keeps its v3 index.
        Assert.Equal(0, Array.IndexOf(names, "releaseAgeLog"));
        Assert.Equal(1, Array.IndexOf(names, "runtimeMinutes"));
        Assert.Equal(3, Array.IndexOf(names, "traktRating"));
        Assert.Equal(5, Array.IndexOf(names, "tmdbPopularityLog"));
        Assert.Equal(6, Array.IndexOf(names, "certificationOrdinal"));
        Assert.Equal(15, Array.IndexOf(names, "contrarianAdjustedRating"));
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
    public void Schema_version_five_keeps_every_block_in_order_and_appends_within_them()
    {
        // Block-level form of the append rule: the scalar, genre and subgenre blocks keep their
        // internal order and their relative order, and the two new blocks land after all of them.
        var all = FeatureSchema.AllNames();
        var expectedWidth = FeatureSchema.Scalars.Length + FeatureSchema.Genres.Length
            + FeatureSchema.SubgenreBuckets + FeatureSchema.Tones.Length + FeatureSchema.Eras.Length;

        Assert.Equal(expectedWidth, FeatureSchema.VectorLength);
        Assert.Equal(expectedWidth, all.Length);
        Assert.Equal(41, FeatureSchema.Scalars.Length);
        Assert.Equal(30, FeatureSchema.Genres.Length);
        Assert.Equal(132, FeatureSchema.VectorLength);

        // v5 is a pure APPEND at both levels: three scalars on the end of the scalar block, five
        // slugs on the end of the genre block. Every index that existed in v4 is unmoved — the
        // width alone changed, which is the loud failure mode a stored artifact needs.
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
