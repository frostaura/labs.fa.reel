using FrostAura.Reel.Application.Ml;
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
    public void Schema_version_three_appends_the_dislike_signals()
    {
        // v2 added the crowd-presence flags; v3 makes dislike reasons learnable. Each bump APPENDS
        // — every pre-existing scalar keeps its position, so the version history stays honest and
        // a stored artifact can only ever be wrong loudly (width mismatch), never silently.
        Assert.Equal(3, FeatureSchema.Version);

        var names = FeatureSchema.Scalars;
        Assert.Equal("hasRuntime", names[^5]);
        Assert.Equal("hasTraktRating", names[^4]);
        Assert.Equal("hasPopularity", names[^3]);
        Assert.Equal("simNotInterestedCentroid", names[^2]);
        Assert.Equal("dislikedGenreOverlap", names[^1]);
        Assert.Equal(0, Array.IndexOf(names, "releaseAgeLog"));
        Assert.Equal(1, Array.IndexOf(names, "runtimeMinutes"));
        Assert.Equal(3, Array.IndexOf(names, "traktRating"));
        Assert.Equal(5, Array.IndexOf(names, "tmdbPopularityLog"));
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
    public void The_persisted_eval_caveat_no_longer_claims_symmetry()
    {
        // The harness is deliberately hostile to itself; it must not be hostile in a way that is
        // untrue. Present-day popularity IS the baseline's whole ranking signal and one of ~86
        // features for the model — that asymmetry runs in the baseline's favour.
        Assert.DoesNotContain("symmetrically", EvalHarness.PopularityCaveat, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT symmetric", EvalHarness.PopularityCaveat, StringComparison.Ordinal);
        Assert.Contains("understates the model", EvalHarness.PopularityCaveat, StringComparison.Ordinal);
    }
}
