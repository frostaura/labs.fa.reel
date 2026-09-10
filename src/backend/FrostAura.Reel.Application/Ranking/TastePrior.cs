using System.Globalization;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// A multiplicative RANKING adjustment layered on top of the model's calibrated prediction — and,
/// deliberately, on nothing else. It reorders; it never changes <c>PredictedRating</c>, which stays
/// the model's own honest claim wherever it is shown.
///
/// <para><b>Why it exists.</b> On 2026-09-10 the founder's Fresh build reported
/// <c>ShortlistP50 5.54, ShortlistP90 5.85, HeroEligibleCount 0</c> — 220 shortlisted titles inside
/// a 0.3-point band, under his own <c>MinPredictedRating</c> of 6.0. A near-constant score under a
/// floor is pathological: the only titles clearing 6.0 were fourteen reality shows, so the hero
/// became American Ninja Warrior, two Below Decks and Teen Mom 2 for a man who has rated
/// <b>three</b> unscripted titles in 2,045 ratings and whose library is 90% movies. Ranking, not
/// filtering, produced that page: every scripted movie he would plausibly watch sat at 5.5–5.9 and
/// every reality show sat at 6.2–6.9, because the model's three most personal features
/// (director/cast/writer affinity) are imputed at the user's own mean whenever a title's credited
/// people are people he has never rated — which is every reality show and no thriller.</para>
///
/// <para><b>What it is not.</b> It is not a hard rule, and it names no genre. A no-history genre is
/// demoted because the account has no evidence about it, so the demotion <i>lifts by itself</i> the
/// moment the account starts rating that genre — an account that rates ten reality shows has a
/// reality prior of exactly 1.0 and gets its reality hero. Nothing here is a permanent statement
/// about what anyone is allowed to be shown.</para>
///
/// <para><b>It is a product rule layered on a weak model, and it is meant to shrink.</b> Every term
/// here compensates for a specific way the current FastTree model is uninformative: an imputed
/// affinity that reads as an endorsement, a genre one-hot the model never learned to use because
/// the training set contains no examples, a media-type flag that carries no weight. As the model
/// earns its discrimination — more labels, a better-behaved calibrator, a fix to the imputation
/// itself — every one of these multipliers should move toward 1.0 and this class should get
/// smaller, not larger. The build logs the average prior magnitude per run precisely so that
/// shrinkage is measurable rather than aspirational; a prior that is still swinging the page a year
/// from now is a finding about the model, not a success of this file.</para>
///
/// <para>Pure and I/O-free by construction: everything it needs comes off a prebuilt
/// <see cref="FeatureVectorBuilder.TasteState"/>, so its arithmetic is unit-tested against a
/// synthetic distribution with no database in the room.</para>
/// </summary>
public sealed class TastePrior
{
    /// <summary>
    /// The multiplier applied once per genre of the title the account has effectively no history
    /// in. 0.6 is a demotion, not an exclusion: a no-history title predicted a full point above the
    /// field still outranks a well-evidenced one predicted at the field's median.
    /// </summary>
    public const double NoHistoryFactor = 0.6d;

    /// <summary>
    /// The floor on the stacked no-history term. Two unknown genres are not meaningfully more
    /// damning than one — they are usually the same fact twice ("reality" and "game-show") — and
    /// compounding 0.6 unbounded would push a title below anything the ordering could recover from.
    /// </summary>
    public const double NoHistoryFloor = 0.4d;

    /// <summary>Absolute floor on what counts as history in a genre, for a small library.</summary>
    public const int MinGenreRatingsFloor = 10;

    /// <summary>
    /// The library size below which the no-history term does not fire AT ALL.
    ///
    /// <para>The demotion is a statement of CONTRAST — "this account has established what it
    /// watches, and this is not it". An account with a handful of ratings has established nothing,
    /// so there is no contrast to draw and every genre would read as unknown: the multiplier would
    /// be a uniform 0.6 that reorders nothing while a card cheerfully explained that the user has
    /// "rated 0 thriller titles ever". Cold start must assert nothing, and this is the line where
    /// it stops.</para>
    /// </summary>
    public const int MinRatingsForNoHistory = 50;

    /// <summary>
    /// Relative floor: half a percent of the account's ratings. A 2,000-rating library that has
    /// touched a genre ten times has still not said anything about it, so the bar scales.
    /// </summary>
    public const double MinGenreRatingsShare = 0.005d;

    /// <summary>The most the volume-weighted affinity term may move a title, either way.</summary>
    public const double MaxAffinitySwing = 0.08d;

    /// <summary>
    /// The affinity gap, in rating points, that earns the full <see cref="MaxAffinitySwing"/>. A
    /// genre the account rates a whole point above its own mean is as strong a preference as this
    /// term is ever allowed to express.
    /// </summary>
    public const decimal FullSwingDelta = 1.0m;

    /// <summary>
    /// The media share at which the media prior starts to bite. Below it — anything from a 20/80
    /// to an 80/20 library — both media are left alone, because a mixed library is a statement that
    /// the account watches both.
    /// </summary>
    public const double MediaLeanThreshold = 0.8d;

    /// <summary>The strongest the media prior may demote the medium the account does not rate.</summary>
    public const double MediaPriorFloor = 0.7d;

    private readonly IReadOnlyDictionary<string, GenreHistory> genres;
    private readonly decimal userMean;
    private readonly decimal movieRatedShare;
    private readonly double genreThreshold;
    private readonly int genreObservations;
    private readonly int ratingsCount;

    private TastePrior(
        IReadOnlyDictionary<string, GenreHistory> genres,
        decimal userMean,
        decimal movieRatedShare,
        double genreThreshold,
        int genreObservations,
        int ratingsCount)
    {
        this.genres = genres;
        this.userMean = userMean;
        this.movieRatedShare = movieRatedShare;
        this.genreThreshold = genreThreshold;
        this.genreObservations = genreObservations;
        this.ratingsCount = ratingsCount;
    }

    /// <summary>One genre's evidence: how many ratings the account has in it, and its shrunken mean.</summary>
    public readonly record struct GenreHistory(int Count, decimal Affinity);

    /// <summary>
    /// The prior's three factors, kept separate rather than pre-multiplied so the card can explain
    /// itself. <see cref="Multiplier"/> is the product and is what ranking uses.
    /// </summary>
    /// <param name="NoHistory">The stacked no-history demotion; 1.0 when every genre is evidenced.</param>
    /// <param name="GenreAffinity">The volume-weighted affinity term, within ±<see cref="MaxAffinitySwing"/>.</param>
    /// <param name="Media">The movie/show prior; 1.0 for a mixed library and for the favoured medium.</param>
    /// <param name="NoHistoryGenres">The genres that triggered the demotion, with their rating counts.</param>
    /// <param name="AffinityDelta">The share-weighted (affinity − userMean) behind <paramref name="GenreAffinity"/>.</param>
    public sealed record Components(
        double NoHistory,
        double GenreAffinity,
        double Media,
        IReadOnlyList<GenreEvidence> NoHistoryGenres,
        decimal AffinityDelta)
    {
        /// <summary>A title the prior had nothing to say about.</summary>
        public static Components Neutral { get; } = new(1d, 1d, 1d, [], 0m);

        /// <summary>The ranking multiplier: the product of the three factors.</summary>
        public double Multiplier => NoHistory * GenreAffinity * Media;

        /// <summary>True when this prior does not move the title at all.</summary>
        public bool IsNeutral => Math.Abs(Multiplier - 1d) < 1e-9d;
    }

    /// <summary>A no-history genre and how thin the account's evidence in it actually is.</summary>
    public readonly record struct GenreEvidence(string Genre, int RatedCount);

    /// <summary>
    /// A prior that moves nothing — the correct answer for an account with no ratings at all, and
    /// the safe default for any surface that has not built a taste state.
    /// </summary>
    public static TastePrior Neutral { get; } = new(
        new Dictionary<string, GenreHistory>(StringComparer.OrdinalIgnoreCase),
        NeutralMean,
        0.5m,
        MinGenreRatingsFloor,
        0,
        0);

    /// <summary>Matches <c>FeatureVectorBuilder</c>'s cold-start mean, so an empty account is neutral everywhere.</summary>
    private const decimal NeutralMean = 7m;

    /// <summary>The evidence bar a genre must clear, for an account with this many ratings.</summary>
    public static double GenreEvidenceBar(int ratingsCount) =>
        Math.Max(MinGenreRatingsFloor, MinGenreRatingsShare * ratingsCount);

    /// <summary>
    /// Reads the prior off a prebuilt taste state. Every input is already there — genre rating
    /// lists, the user mean, the media share — so this costs no query and no round trip.
    /// </summary>
    public static TastePrior From(FeatureVectorBuilder.TasteState taste)
    {
        var history = new Dictionary<string, GenreHistory>(taste.GenreRatings.Count, StringComparer.OrdinalIgnoreCase);
        var observations = 0;
        foreach (var (genre, ratings) in taste.GenreRatings)
        {
            // The same empirical-Bayes shrinkage the model's own genre feature uses, against the
            // same prior — so "this account rates crime above its mean" means one thing in the
            // feature vector and the same thing here.
            history[genre] = new GenreHistory(
                ratings.Count,
                TasteMath.ShrunkenMean(ratings.Select(r => r.Rating).ToList(), taste.UserMean));
            observations += ratings.Count;
        }

        return new TastePrior(
            history,
            taste.UserMean,
            taste.MovieRatedShare,
            GenreEvidenceBar(taste.Self.RatingCount),
            observations,
            taste.Self.RatingCount);
    }

    /// <summary>Directly constructed prior, for unit tests and for callers holding raw aggregates.</summary>
    public static TastePrior FromAggregates(
        IReadOnlyDictionary<string, GenreHistory> genreHistory,
        decimal userMean,
        decimal movieRatedShare,
        int ratingsCount) =>
        new(
            new Dictionary<string, GenreHistory>(genreHistory, StringComparer.OrdinalIgnoreCase),
            userMean,
            movieRatedShare,
            GenreEvidenceBar(ratingsCount),
            genreHistory.Values.Sum(g => g.Count),
            ratingsCount);

    /// <summary>The ranking multiplier for a title. Convenience over <see cref="ComponentsFor"/>.</summary>
    public double MultiplierFor(Title title) => ComponentsFor(title.Genres, title.MediaType).Multiplier;

    /// <summary>
    /// The shared ordering key: the calibrated prediction the card SHOWS, times this account's
    /// prior. One expression, used by the feed build, the paginator and the live refill in
    /// <c>FeedEndpoints</c>, because three copies of "predicted × prior" is how a rail and a
    /// backfill start disagreeing about what a good pick is — which is exactly what the refill did
    /// before it had a prior at all: it ordered by <c>PredictedRating</c> alone and handed the hero
    /// back the titles the build had just ranked down.
    ///
    /// <para><paramref name="predicted"/> is not modified and is not what this returns; the number
    /// on the card is the model's, untouched.</para>
    /// </summary>
    public decimal Rank(decimal predicted, Title title) => predicted * DecimalMultiplierFor(title);

    /// <inheritdoc cref="MultiplierFor(Title)"/>
    public decimal DecimalMultiplierFor(Title title) => (decimal)MultiplierFor(title);

    /// <summary>The prior's factors for a title, ready to explain.</summary>
    public Components ComponentsFor(Title title) => ComponentsFor(title.Genres, title.MediaType);

    /// <summary>
    /// The prior's factors for a genre list and a medium.
    ///
    /// <para><b>An empty genre list is neutral on the genre terms, not penalised.</b> Missing
    /// metadata is a fact about the catalogue, not about the title, and demoting it would quietly
    /// bury every freshly discovered candidate before enrichment has run. The media prior still
    /// applies, because a title's medium is known even when nothing else is.</para>
    /// </summary>
    public Components ComponentsFor(IReadOnlyList<string> titleGenres, MediaType mediaType)
    {
        var media = MediaFactor(mediaType);
        if (titleGenres.Count == 0)
        {
            return new Components(1d, 1d, media, [], 0m);
        }

        var noHistory = 1d;
        var unknown = new List<GenreEvidence>();
        decimal weightedDelta = 0m, weightTotal = 0m;
        var contrastAvailable = ratingsCount >= MinRatingsForNoHistory;

        foreach (var genre in titleGenres)
        {
            var known = genres.TryGetValue(genre, out var stat);
            var count = known ? stat.Count : 0;
            if (contrastAvailable && count < genreThreshold)
            {
                noHistory *= NoHistoryFactor;
                unknown.Add(new GenreEvidence(genre, count));
                // An unevidenced genre contributes no affinity opinion either: its shrunken mean IS
                // the user mean, so folding it into the weighted average below would only dilute the
                // genres that do carry information.
                continue;
            }

            if (count == 0)
            {
                // Below MinRatingsForNoHistory and unrated: nothing to demote and nothing to weigh.
                continue;
            }

            // Volume weight: the share of the account's genre-rating observations that sit in this
            // genre. A thriller opinion earned over 850 ratings outweighs a western opinion earned
            // over 25, which is exactly the asymmetry a flat average throws away.
            var weight = genreObservations > 0 ? (decimal)count / genreObservations : 0m;
            weightedDelta += weight * (stat.Affinity - userMean);
            weightTotal += weight;
        }

        var affinity = 1d;
        var delta = 0m;
        if (weightTotal > 0m)
        {
            delta = weightedDelta / weightTotal;
            var scaled = Math.Clamp((double)(delta / FullSwingDelta), -1d, 1d);
            affinity = 1d + (scaled * MaxAffinitySwing);
        }

        return new Components(Math.Max(NoHistoryFloor, noHistory), affinity, media, unknown, delta);
    }

    /// <summary>
    /// The media prior. Symmetric by construction: whichever medium the account has effectively
    /// stopped rating is the one demoted, and a library inside 20/80 either way is left alone.
    /// Linear from 1.0 at the lean threshold to <see cref="MediaPriorFloor"/> at a pure library, so
    /// the demotion is proportional to how one-sided the evidence actually is rather than a cliff.
    /// </summary>
    public double MediaFactor(MediaType mediaType)
    {
        var share = mediaType == MediaType.Movie
            ? (double)(1m - movieRatedShare)
            : (double)movieRatedShare;

        // `share` is now the share of the OTHER medium — how thoroughly this title's medium is the
        // one the account does not rate.
        if (share <= MediaLeanThreshold)
        {
            return 1d;
        }

        var slope = (1d - MediaPriorFloor) / (1d - MediaLeanThreshold);
        return Math.Clamp(1d - (slope * (share - MediaLeanThreshold)), MediaPriorFloor, 1d);
    }

    /// <summary>
    /// The prior's factors as synthetic contribution entries, for <c>ExplanationJson</c> and the
    /// title page's contribution bars. Written as DELTAS from 1.0 (a ×0.6 reads as −0.400) so they
    /// render on the same signed-bar axis as the model's own contributions without anyone having to
    /// know that one axis is additive and the other multiplicative. Only non-neutral factors are
    /// emitted — a bar at zero is noise on a card.
    /// </summary>
    public static IReadOnlyList<(string Feature, double Value)> ContributionEntries(Components components)
    {
        var entries = new List<(string, double)>(3);
        if (components.NoHistory < 1d)
        {
            entries.Add((NoHistoryFeature, components.NoHistory - 1d));
        }

        if (Math.Abs(components.GenreAffinity - 1d) > 1e-9d)
        {
            entries.Add((GenreAffinityFeature, components.GenreAffinity - 1d));
        }

        if (components.Media < 1d)
        {
            entries.Add((MediaFeature, components.Media - 1d));
        }

        return entries;
    }

    /// <summary>The synthetic feature name for the no-history-genre demotion.</summary>
    public const string NoHistoryFeature = "tastePriorNoHistoryGenre";

    /// <summary>The synthetic feature name for the volume-weighted genre-affinity term.</summary>
    public const string GenreAffinityFeature = "tastePriorGenreAffinity";

    /// <summary>The synthetic feature name for the movie/show media prior.</summary>
    public const string MediaFeature = "tastePriorMedia";

    /// <summary>
    /// The sentence behind the no-history bar — the founder's own "why not": <i>you have rated 3
    /// reality titles ever</i>. Names the count rather than a threshold, because the count is the
    /// fact and the threshold is our arithmetic. Null when nothing was demoted.
    /// </summary>
    public static string? NoHistoryNote(Components components)
    {
        if (components.NoHistoryGenres.Count == 0)
        {
            return null;
        }

        var parts = components.NoHistoryGenres
            .OrderBy(g => g.Genre, StringComparer.Ordinal)
            .Select(g => string.Create(
                CultureInfo.InvariantCulture,
                $"{g.RatedCount} {g.Genre} title{(g.RatedCount == 1 ? string.Empty : "s")}"));
        return $"you have rated {string.Join(" and ", parts)} ever";
    }

    /// <summary>The note behind the media bar, or null when the media prior is neutral.</summary>
    public string? MediaNote(MediaType mediaType)
    {
        if (MediaFactor(mediaType) >= 1d)
        {
            return null;
        }

        var favoured = movieRatedShare >= 0.5m ? "movies" : "shows";
        var share = Math.Round(movieRatedShare >= 0.5m ? movieRatedShare : 1m - movieRatedShare, 2) * 100m;
        return string.Create(CultureInfo.InvariantCulture, $"{share:0}% of what you rate is {favoured}");
    }
}
