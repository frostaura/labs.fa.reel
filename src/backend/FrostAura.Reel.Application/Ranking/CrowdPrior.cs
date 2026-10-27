using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// The crowd's term in the feed's ranking blend: a bounded multiplier read off the vote-shrunk,
/// Trakt-first crowd score (<see cref="CrowdRating"/>, the same number the badge prints) and applied
/// to the ORDERING key of every feed surface — the snapshot build's <c>RankKey</c>, the
/// <c>/api/feed/more</c> rank and curation blend, and the Home backfill. Like the taste prior it
/// reorders and never changes <c>PredictedRating</c>, the number the card shows.
///
/// <para><b>Why the feed needs it.</b> Before this the feed ordered by calibrated prediction × taste
/// prior × presentation nudges and nothing else; the crowd reached it only through FastTree's RAW
/// <c>traktRating</c> feature, which is exactly the unshrunk average WS-3 replaced for the badge
/// (10.0 on 2 votes). Measured on the founder's copy (<c>reel_ws5</c>, 2026-10-26): on the 998 movie
/// and show ratings he gave AFTER the title's stored score was computed, the stored prediction
/// correlates with his rating at Spearman −0.059, the shrunk crowd score alone at +0.370 and the raw
/// average alone at +0.231. Folding this term in at <see cref="MaxSwing"/> moves the prediction's
/// own correlation to −0.019 and the top decile's mean rating from 6.36 to 6.43. Selection caveat:
/// these are titles he chose to watch.</para>
///
/// <para><b>Why it is bounded, not a full crowd-led blend.</b> Serving a ranker the crowd LEADS is
/// the transparent challenger, and whether it replaces FastTree is FD-4's call (default: FastTree
/// keeps serving). This term keeps the prediction as the dominant multiplicand and caps the crowd at
/// the same ±10% the declared-preference nudge already has, so it decides near-ties — which, on a
/// shortlist the model scores inside a fraction of a point, is most of the order.</para>
///
/// <para><b>Shape.</b> Linear in the shrunk score around the catalogue prior
/// (<see cref="CrowdRating.Prior"/>, 6.5), reaching <see cref="MaxSwing"/> at 10.0 and clamped to
/// ±<see cref="MaxSwing"/> below. The shrinkage does the low-vote work by itself: 10.0 on 2 votes is
/// 6.63 shrunk (×1.004) and ranks below 7.0 on 5,000 (6.995, ×1.014), with no vote cutoff that would
/// bury a new release. No crowd score is exactly 1.0 — unknown is not evidence either way.</para>
/// </summary>
public static class CrowdPrior
{
    /// <summary>The most the crowd may move a title's ranking key, either way.</summary>
    public const double MaxSwing = 0.10d;

    /// <summary>The shrunk-score distance from the prior at which the full swing applies (10.0 − 6.5).</summary>
    public const double FullSwingDelta = 3.5d;

    /// <summary>
    /// The badge's shrunk score, unrounded, or null when there is none — the materialised twin of
    /// <c>CrowdVotes.ShrunkScore</c>, resolved through <see cref="CrowdRating.Source"/> so the source
    /// can never disagree with the badge.
    /// </summary>
    public static decimal? ShrunkScoreOf(Title title) =>
        CrowdRating.Source(title) switch
        {
            CrowdSource.Trakt => CrowdRating.Shrink(title.TraktRating!.Value, title.TraktVotes),
            CrowdSource.Tmdb => CrowdRating.Shrink(title.TmdbVoteAverage!.Value, title.TmdbVoteCount),
            _ => null,
        };

    /// <summary>The most the crowd's VOLUME may move a title's ranking key, either way.</summary>
    public const double VolumeSwing = 0.10d;

    /// <summary>The vote count at which the volume term is neutral: the snapshot median the program targets (task 9).</summary>
    public const double VolumePivotVotes = 1_500d;

    /// <summary>How many decades of votes from the pivot reach the full volume swing.</summary>
    public const double VolumeFullSwingDecades = 1d;

    /// <summary>
    /// The ranking multiplier for <paramref name="title"/>: the crowd's score term times its volume
    /// term (<see cref="VolumeFactorFor"/>).
    /// </summary>
    public static double FactorFor(Title title) => FactorForScore(ShrunkScoreOf(title)) * VolumeFactorFor(VotesOf(title));

    /// <summary>
    /// How many people stand behind a title (WS-5 task 9, D-4): log-linear around
    /// <see cref="VolumePivotVotes"/>, reaching ±<see cref="VolumeSwing"/> a decade either side (150
    /// votes ×0.90, 15,000 ×1.10). No votes on record is the least evidence there is, so it takes
    /// the floor, ×0.90 — unlike the score term, where an unknown score is no evidence either way.
    ///
    /// <para><b>Why it is a term of its own.</b> The shrinkage folds volume into the score only
    /// weakly: 7.0 on 400 votes and 7.0 on 40,000 shrink to nearly the same number. On the founder's
    /// copy (<c>reel_ws5</c>, 2026-10-26) the TMDB vote count of a film he rated rank-correlates
    /// with his rating at Spearman +0.44 over 1,942 films — his mean rating is 5.4 under 300 votes,
    /// 6.2 at 300–1,500, 7.1 at 1,500–10,000 and 7.8 above 10,000 — while the served prediction
    /// correlates at −0.06. The snapshot's films sat at a median of 292 votes. Selection caveat as
    /// for the score term: these are films he chose to watch.</para>
    ///
    /// <para>Bounded like the score term, and an ordering term only: the number on the card is the
    /// prediction, untouched.</para>
    /// </summary>
    public static double VolumeFactorFor(int votes)
    {
        if (votes <= 0)
        {
            return 1d - VolumeSwing;
        }

        var decades = (Math.Log10(votes) - Math.Log10(VolumePivotVotes)) / VolumeFullSwingDecades;
        return 1d + (VolumeSwing * Math.Clamp(decades, -1d, 1d));
    }

    /// <summary>The vote count the volume term reads: TMDB's where it has any, else Trakt's.</summary>
    public static int VotesOf(Title title) => title.TmdbVoteCount > 0 ? title.TmdbVoteCount : title.TraktVotes;

    /// <summary>The ranking multiplier for a shrunk crowd score; 1.0 for null.</summary>
    public static double FactorForScore(decimal? shrunk)
    {
        if (shrunk is not { } score)
        {
            return 1d;
        }

        var delta = (double)(score - CrowdRating.Prior) / FullSwingDelta;
        return 1d + (MaxSwing * Math.Clamp(delta, -1d, 1d));
    }
}
