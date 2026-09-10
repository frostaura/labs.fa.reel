using System.Globalization;
using FrostAura.Reel.Application.Ml;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>
/// The account's genre affinities as one prompt line, and the base rates a calibrated confidence
/// is anchored on — both derived from a <see cref="FeatureVectorBuilder.TasteState"/> and nothing
/// else.
///
/// <para><b>Why the taste state and not <c>TasteFactsBuilder</c>.</b> The fact sheet is the richer
/// object and is the right source on a serving path, but it reads current, cached, recency-decayed
/// aggregates with no as-of mode. The taste state is built by
/// <c>FeatureVectorBuilder.BuildTasteStateAsync(accountId, asOf, …)</c> and is as-of by
/// construction, so a backtest that reads it cannot be handed a genre affinity computed partly
/// from the holdout it is about to be scored on. That is the whole reason this is a separate,
/// pure function over the state rather than a call into the builder.</para>
///
/// <para><b>The affinity is the shrunken mean, not the count.</b> The same empirical-Bayes estimate
/// <c>TasteSummary</c> orders by and the model itself scores on — ordering genres by how MANY
/// titles were rated in them describes what the account has watched, not what it likes, and a
/// prompt that opens on the first while claiming the second is simply wrong about the reader.</para>
///
/// <para><b>Genres are rendered with their means and their counts.</b> "thriller 8.4 over 41" and
/// "thriller 8.4 over 2" are different claims, and a judge asked to weigh genre evidence against
/// neighbour evidence needs to be able to tell them apart — the same reason the neighbour clause
/// carries the similarity and not just the rating.</para>
/// </summary>
public static class GenreAffinityLine
{
    /// <summary>Top genres named. Five is a hand of evidence, not a taxonomy dump.</summary>
    public const int TopGenres = 5;

    /// <summary>Bottom genres named. Fewer, because a negative is a stronger claim.</summary>
    public const int BottomGenres = 3;

    /// <summary>
    /// A genre needs at least this many rated titles to be named at all. Below it the shrunken mean
    /// is mostly the prior, so naming it tells the judge about the account's overall average
    /// wearing a genre's label.
    /// </summary>
    public const int MinRatingsPerGenre = 3;

    /// <summary>
    /// The line, or null when the account has nothing nameable. Null is omitted from the prompt
    /// entirely rather than rendered as an empty heading — an empty "genres you rate highest:" is a
    /// claim that someone looked.
    /// </summary>
    public static string? Build(FeatureVectorBuilder.TasteState taste)
    {
        var scored = taste.GenreRatings
            .Where(kv => kv.Value.Count >= MinRatingsPerGenre)
            .Select(kv => new Scored(
                kv.Key,
                TasteMath.ShrunkenMean(kv.Value.Select(r => r.Rating).ToList(), taste.UserMean),
                kv.Value.Count))
            // Count then name as tie-breakers: shrunken means collide readily on small pools, and a
            // line that reshuffles between runs churns the prompt for nothing and breaks a golden.
            .OrderByDescending(g => g.Affinity)
            .ThenByDescending(g => g.Count)
            .ThenBy(g => g.Genre, StringComparer.Ordinal)
            .ToList();

        if (scored.Count == 0)
        {
            return null;
        }

        static string Render(Scored g) => string.Create(
            CultureInfo.InvariantCulture, $"{g.Genre} {g.Affinity:0.0}/10 over {g.Count}");

        var top = scored.Take(TopGenres).Select(Render).ToList();
        // Taken from the END of the same ordering and re-read low-to-high, and only where they are
        // genuinely a different set from the top — an account with four nameable genres has no
        // "bottom three", it has the same four in reverse.
        var bottom = scored.Count > TopGenres
            ? scored.AsEnumerable().Reverse().Take(BottomGenres).Select(Render).ToList()
            : [];

        var line = "GENRES YOU RATE HIGHEST: " + string.Join("; ", top);
        return bottom.Count > 0 ? line + "\nGENRES YOU RATE LOWEST: " + string.Join("; ", bottom) : line;
    }

    /// <summary>One genre, its shrunken affinity and the pool that affinity rests on.</summary>
    private sealed record Scored(string Genre, decimal Affinity, int Count);

    /// <summary>
    /// The two base rates the v2 system prompt anchors confidence on: the share of rated titles
    /// scored ≥ 8 and the share scored ≤ 4, each 0–1.
    ///
    /// <para><b>Both are computed from the ratings the caller passes in, which must already be cut
    /// at the split.</b> Telling a judge "this viewer dislikes 14% of what they finish" using a
    /// figure that includes the holdout is a small leak, but it is the kind that makes a measured
    /// improvement unattributable — and this whole PR exists because an unattributable result cost
    /// a run.</para>
    ///
    /// <para>Null when there is nothing rated, so the sentence is omitted rather than claiming
    /// 0% of anything.</para>
    /// </summary>
    public static (decimal? LovedShare, decimal? DislikedShare) BaseRates(IReadOnlyList<short> ratings)
    {
        if (ratings.Count == 0)
        {
            return (null, null);
        }

        var loved = ratings.Count(r => r >= DiscriminationMetrics.LovedFloor) / (decimal)ratings.Count;
        var disliked = ratings.Count(r => r <= DiscriminationMetrics.DislikedCeiling) / (decimal)ratings.Count;
        return (Math.Round(loved, 4, MidpointRounding.AwayFromZero),
            Math.Round(disliked, 4, MidpointRounding.AwayFromZero));
    }
}
