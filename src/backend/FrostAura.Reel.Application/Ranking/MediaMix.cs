namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// The media-mix floor a row must carry — the presentation-side companion to
/// <see cref="MediaPlanner"/>, which fixes the same problem one layer earlier.
///
/// <para><b>Why both exist.</b> <see cref="MediaPlanner"/> makes the candidate POOL arrive in the
/// proportion the user actually rates. It cannot make ranking pick from that proportion, and on
/// 2026-09-10 it did not: a 90%-movies account got a five-card hero of five television shows,
/// because every movie in the shortlist scored 5.5–5.9 and every reality show scored 6.2–6.9. The
/// prior fixes most of that by demoting what the account has no history in; this fixes the residual
/// case, where a whole medium is squeezed out by a fraction of a point.</para>
///
/// <para><b>It is a floor on representation, never a quota.</b> The target is capped one seat below
/// the row so a mix rule can never make a row single-medium by itself — turning an all-TV hero into
/// an all-movie hero would be the same failure mirrored. And the floor is honoured only from
/// candidates that already clear the build's floor: a row never dips below what it was allowed to
/// show in order to hit a mix number, and a build that could not reach the target says so rather
/// than pretending it did.</para>
/// </summary>
public static class MediaMix
{
    /// <summary>
    /// How many of <paramref name="slots"/> should carry the account's majority medium, given its
    /// movie share. Zero when the library is mixed enough that neither medium needs protecting.
    ///
    /// <para>Capped at <c>slots - 1</c>: see the class remarks. A 100%-movies account still leaves
    /// one seat where a show can win on merit.</para>
    /// </summary>
    /// <param name="slots">Seats in the row.</param>
    /// <param name="movieRatedShare">The account's <c>TasteState.MovieRatedShare</c>, 0..1.</param>
    /// <returns>The medium to protect and how many seats it must hold; <c>Count</c> 0 for none.</returns>
    public static (bool WantMovies, int Count) TargetFor(int slots, decimal movieRatedShare)
    {
        if (slots <= 1)
        {
            return (movieRatedShare >= 0.5m, 0);
        }

        var wantMovies = movieRatedShare >= 0.5m;
        var share = wantMovies ? movieRatedShare : 1m - movieRatedShare;

        // Below the same lean threshold the media prior uses, a library is mixed and says nothing.
        if ((double)share <= TastePrior.MediaLeanThreshold)
        {
            return (wantMovies, 0);
        }

        var target = (int)Math.Round(slots * (double)share, MidpointRounding.ToEven);
        return (wantMovies, Math.Clamp(target, 0, slots - 1));
    }

    /// <summary>
    /// Re-composes <paramref name="selected"/> so at least <paramref name="target"/> members satisfy
    /// <paramref name="wanted"/>, drawing replacements from <paramref name="spares"/> — which the
    /// caller has already filtered to what the row is allowed to show and ordered best-first — and
    /// evicting the LOWEST-ranked members of the other medium. The list never changes length.
    ///
    /// <para>Returns the new list and whether the target was actually reached. A false there is a
    /// fact the build reports, not a failure it retries: it means the shortlist genuinely did not
    /// hold enough of that medium above the floor.</para>
    /// </summary>
    public static (List<T> Balanced, bool TargetMet) Balance<T>(
        IReadOnlyList<T> selected, IReadOnlyList<T> spares, Func<T, bool> wanted, int target)
    {
        var result = selected.ToList();
        if (target <= 0)
        {
            return (result, true);
        }

        var have = result.Count(wanted);
        if (have >= target)
        {
            return (result, true);
        }

        var replacements = new Queue<T>(spares.Where(wanted));
        for (var i = result.Count - 1; i >= 0 && have < target && replacements.Count > 0; i--)
        {
            if (wanted(result[i]))
            {
                continue;
            }

            result[i] = replacements.Dequeue();
            have++;
        }

        return (result, have >= target);
    }
}
