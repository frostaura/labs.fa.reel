using System.Globalization;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Ingestion;

/// <summary>
/// When a hydrated catalogue row is fetched from TMDB again (E-11, FD-9).
///
/// <para><b>Why it exists.</b> Every hydration caller selected <c>LastMetadataRefreshAt == null</c>,
/// so a row was fetched once and then frozen: 17,980 titles were over 30 days stale, 303 had been
/// fetched within 90 days of release while their vote counts were still moving, and 31 before
/// release. The crowd floor and the popularity baseline read those frozen numbers. TMDB's terms also
/// limit caching to six months without an agreement, so a row that is never refreshed is a row Reel
/// may not keep.</para>
///
/// <para><b>Two tiers.</b> A title released (or first aired) in the last <see cref="RecentWindow"/>
/// — or not yet released — is refreshed every <see cref="RecentInterval"/>: its vote count and
/// average are still settling. Everything else is refreshed on a rolling <see cref="RollingInterval"/>,
/// which leaves a month of slack under TMDB's limit for a day the ceiling runs short.</para>
///
/// <para><b>Inside the same daily ceiling.</b> A refresh is one TMDB call, exactly like a first
/// hydration, and <c>CatalogHydrationBudget</c> already counts both. The global sweep spends the day's
/// allowance in this order: rows past <see cref="RollingInterval"/> (oldest first — the ones at risk
/// of the cache limit), then recent releases due their weekly refresh, then rows never hydrated.
/// <see cref="Project"/> says whether the configured ceiling can sustain that.</para>
/// </summary>
public static class MetadataRefreshPolicy
{
    /// <summary>A title released inside this window, or not yet released, is "recent".</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(180);

    /// <summary>How often a recent title is refetched.</summary>
    public static readonly TimeSpan RecentInterval = TimeSpan.FromDays(7);

    /// <summary>How often every other hydrated title is refetched — 30 days inside the limit below.</summary>
    public static readonly TimeSpan RollingInterval = TimeSpan.FromDays(150);

    /// <summary>TMDB's caching limit without an agreement: six months, taken as 182 days.</summary>
    public static readonly TimeSpan CacheLimit = TimeSpan.FromDays(182);

    /// <summary>Hydrated rows past <see cref="RollingInterval"/> — the tier that guards the cache limit.</summary>
    public static IQueryable<Title> Overdue(IReelDbContext db, DateTime nowUtc)
    {
        var rollingCutoff = nowUtc - RollingInterval;
        return db.Titles.Where(t => t.TmdbId != null
            && t.LastMetadataRefreshAt != null
            && t.LastMetadataRefreshAt < rollingCutoff);
    }

    /// <summary>Recent titles whose weekly refresh is due, and that are not already <see cref="Overdue"/>.</summary>
    public static IQueryable<Title> RecentDue(IReelDbContext db, DateTime nowUtc)
    {
        var recentSince = nowUtc - RecentWindow;
        var recentCutoff = nowUtc - RecentInterval;
        var rollingCutoff = nowUtc - RollingInterval;
        return db.Titles.Where(t => t.TmdbId != null
            && t.LastMetadataRefreshAt != null
            && t.LastMetadataRefreshAt < recentCutoff
            && t.LastMetadataRefreshAt >= rollingCutoff
            && (t.ReleasedAt ?? t.FirstAiredAt) >= recentSince);
    }

    /// <summary>Every hydrated row due a refetch now, either tier.</summary>
    public static IQueryable<Title> Due(IReelDbContext db, DateTime nowUtc)
    {
        var recentSince = nowUtc - RecentWindow;
        var recentCutoff = nowUtc - RecentInterval;
        var rollingCutoff = nowUtc - RollingInterval;
        return db.Titles.Where(t => t.TmdbId != null
            && t.LastMetadataRefreshAt != null
            && (t.LastMetadataRefreshAt < rollingCutoff
                || (t.LastMetadataRefreshAt < recentCutoff && (t.ReleasedAt ?? t.FirstAiredAt) >= recentSince)));
    }

    /// <summary>Recent titles (either refreshed or not) — the weekly tier's steady-state size.</summary>
    public static IQueryable<Title> Recent(IReelDbContext db, DateTime nowUtc)
    {
        var recentSince = nowUtc - RecentWindow;
        return db.Titles.Where(t => t.TmdbId != null && (t.ReleasedAt ?? t.FirstAiredAt) >= recentSince);
    }

    /// <summary>
    /// What the configured ceiling can sustain for a catalogue of <paramref name="catalogue"/>
    /// TMDB-backed titles, <paramref name="recent"/> of them recent.
    /// <list type="bullet">
    /// <item>Steady-state refresh load per day: <c>recent / 7 + (catalogue − recent) / 150</c>.</item>
    /// <item>Cycle: how many days a full pass over the non-recent catalogue takes when every call
    /// the weekly tier does not need goes to it — never shorter than <see cref="RollingInterval"/>,
    /// because the sweep waits until a row is due.</item>
    /// </list>
    /// The cycle is inside TMDB's limit when it is at most <see cref="CacheLimit"/>.
    /// </summary>
    public static MetadataRefreshProjection Project(long catalogue, long recent, long dailyCap)
    {
        var older = Math.Max(0, catalogue - recent);
        var recentLoad = recent / RecentInterval.TotalDays;
        var steadyLoad = recentLoad + (older / RollingInterval.TotalDays);
        var spare = dailyCap - recentLoad;
        var cycleDays = spare <= 0
            ? double.PositiveInfinity
            : Math.Max(RollingInterval.TotalDays, older / spare);
        return new MetadataRefreshProjection(
            catalogue, recent, dailyCap, Math.Ceiling(steadyLoad), cycleDays, cycleDays <= CacheLimit.TotalDays);
    }
}

/// <summary>What <see cref="MetadataRefreshPolicy.Project"/> found. <paramref name="CycleDays"/> is infinite when the weekly tier alone exceeds the cap.</summary>
public sealed record MetadataRefreshProjection(
    long Catalogue, long Recent, long DailyCap, double SteadyRefreshesPerDay, double CycleDays, bool InsideCacheLimit)
{
    /// <summary>"38,262 titles cycle in 150 days at 1,500/day (≈ 501 refreshes/day)" — invariant, for logs.</summary>
    public string Describe() => string.Format(
        CultureInfo.InvariantCulture,
        "{0:N0} titles ({1:N0} recent) cycle in {2} at {3:N0}/day (about {4:N0} refreshes/day){5}",
        Catalogue,
        Recent,
        double.IsInfinity(CycleDays) ? "never" : string.Format(CultureInfo.InvariantCulture, "{0:N0} days", Math.Ceiling(CycleDays)),
        DailyCap,
        SteadyRefreshesPerDay,
        InsideCacheLimit ? string.Empty : " — OUTSIDE TMDB's 6-month cache limit; raise HYDRATE_MAX_TITLES_PER_DAY");
}
