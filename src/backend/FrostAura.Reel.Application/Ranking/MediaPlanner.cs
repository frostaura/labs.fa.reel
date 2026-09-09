namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// Decides, arm by arm, whether a discovery call asks TMDB for movies or for shows, so the candidate
/// pool arrives in the proportion the user actually rates.
///
/// <para>
/// Why it exists: the build fetched one movie arm and one TV arm per genre — a fixed 1:1 — and every
/// other arm took whatever medium the endpoint happened to be. Downstream, the live feed came out
/// entirely TV shows (2026-09-09: hero = five network procedurals), because a fixed intake ratio
/// says nothing about which medium survives ranking. Supply cannot fix ranking, but it can stop
/// under-supplying the medium the user demonstrably prefers.
/// </para>
///
/// <para>
/// The rule is the standard online largest-remainder walk: take a movie whenever the movies already
/// taken are below the target share of the sequence including this slot. That makes the realised mix
/// track the target from the very first slot rather than only in the limit, and it is a pure
/// function of the call order — the same taste state plans the same build every time.
/// </para>
/// </summary>
public sealed class MediaPlanner
{
    /// <summary>
    /// Neither medium is ever planned below this share, whatever the ratings say. A user whose
    /// history is 95% one medium would otherwise get a plan that fetches the other one never — and a
    /// pool that can never contain it cannot produce the evidence that would change the ratio. The
    /// floor is a supply hedge, not a taste claim, and it is small enough that the plan still reads
    /// as the user's mix.
    /// </summary>
    public const double MinShare = 0.15;

    /// <summary>Used when the account has rated nothing yet — an even split makes no claim.</summary>
    public const double NeutralShare = 0.5;

    private readonly double target;
    private int movies;
    private int shows;

    public MediaPlanner(double movieShare) =>
        target = double.IsNaN(movieShare) ? NeutralShare : Math.Clamp(movieShare, MinShare, 1 - MinShare);

    /// <summary>The clamped share this planner is steering toward.</summary>
    public double Target => target;

    /// <summary>Movie arms handed out so far.</summary>
    public int Movies => movies;

    /// <summary>TV arms handed out so far.</summary>
    public int Shows => shows;

    /// <summary>
    /// The medium for the next arm: true = movies, false = shows, null when the caller says neither
    /// medium is available (a genre slug TMDB maps in neither id space) and the arm must be skipped.
    /// An arm constrained to one medium still counts toward the realised mix — it is supply that
    /// arrived, and pretending otherwise would over-correct the following arms.
    /// </summary>
    public bool? NextIsMovie(bool movieAvailable = true, bool tvAvailable = true)
    {
        if (!movieAvailable && !tvAvailable)
        {
            return null;
        }

        var pick = !tvAvailable || (movieAvailable && movies < target * (movies + shows + 1));
        if (pick)
        {
            movies++;
        }
        else
        {
            shows++;
        }

        return pick;
    }
}
