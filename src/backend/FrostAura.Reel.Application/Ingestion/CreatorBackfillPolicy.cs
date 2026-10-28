using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Ingestion;

/// <summary>
/// Which hydrated shows must be fetched from TMDB once more because they were hydrated by code that
/// dropped their creators (HREV2-D-02). Until then <c>created_by</c> was parsed and thrown away, so
/// every show in the catalogue — 6,716 on the evaluation copy of the live database — has no
/// <see cref="CreditRole.Creator"/> credit, and the data exists nowhere but at TMDB.
///
/// <para><b>The watermark, and why there is no marker column.</b> A creator credit can only be written
/// by <see cref="TitleHydrator"/>, and the same hydration stamps <c>LastMetadataRefreshAt</c>. So the
/// OLDEST refresh among shows that hold a creator credit is no earlier than the moment this database
/// was first hydrated by code that stores creators. A creator-less show refreshed before that moment
/// was read by code that dropped them; one refreshed after it was read by code that would have stored
/// them, so TMDB simply names nobody and fetching it again would buy nothing. A database that holds no
/// creator credit at all has never run that code, and its watermark is now: every hydrated show
/// qualifies. No schema change, and the selection is self-terminating — a fetched show is stamped past
/// the watermark whether or not it turned out to have creators.</para>
///
/// <para><b>What the watermark does over time.</b> It moves only when the show that defines it is
/// refreshed again, and then only forward, to the next-oldest credited show. A creator-less show
/// refreshed between the old and the new watermark qualifies once more: at most one extra fetch per
/// show per pass of the 150-day rolling refresh, which would have fetched it within that pass anyway.
/// It never spins — a fetch stamps the row at "now", which no credited show's refresh can pass.</para>
///
/// <para><b>Spent last.</b> The global hydration sweep gives the back-fill only what new supply and the
/// metadata refresh sweep leave of the day's TMDB ceiling, most-voted first, so the shows people
/// actually open are fixed first.</para>
/// </summary>
public static class CreatorBackfillPolicy
{
    /// <summary>
    /// Hydrated shows refreshed before the watermark (see the class remarks). A show that holds a
    /// creator credit is excluded by construction, not by a second clause: its refresh is one of the
    /// values the watermark is the minimum of, so it can never be strictly earlier than it.
    /// </summary>
    public static IQueryable<Title> Due(IReelDbContext db, DateTime nowUtc)
    {
        var creditedShows = db.Titles.Where(s => s.MediaType == MediaType.Show
            && s.LastMetadataRefreshAt != null
            && db.TitleCredits.Any(c => c.TitleId == s.Id && c.Role == CreditRole.Creator));

        return db.Titles.Where(t => t.MediaType == MediaType.Show
            && t.TmdbId != null
            && t.LastMetadataRefreshAt != null
            && t.LastMetadataRefreshAt < (creditedShows.Min(s => s.LastMetadataRefreshAt) ?? nowUtc));
    }
}
