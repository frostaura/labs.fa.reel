using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Trakt;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Recommendations;

/// <summary>What the "Trakt recommends" rail can honestly say about itself. Serialised by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TraktRecommendationsState>))]
public enum TraktRecommendationsState
{
    /// <summary>Trakt answered (now or within the cache window); the items are its list, filtered.</summary>
    Ok,

    /// <summary>Trakt did not answer just now; the items are the last list it did give, from <c>FetchedAt</c>.</summary>
    Stale,

    /// <summary>The account has no Trakt link at all, so there is no list to ask for.</summary>
    NotLinked,

    /// <summary>The link exists but only a re-link can produce a usable token.</summary>
    RelinkRequired,

    /// <summary>Trakt did not answer and there is no earlier list to fall back on.</summary>
    Unavailable,
}

/// <summary>
/// The rail's list after Reel's rules: <paramref name="Titles"/> in Trakt's order, every one of them
/// eligible right now. The counts say what was taken out and why, so an empty rail can tell "Trakt
/// sent nothing" apart from "you have seen everything Trakt sent".
/// </summary>
/// <param name="ReturnedByTrakt">Movies plus shows in Trakt's answer.</param>
/// <param name="Unresolved">Entries Reel cannot link to a title page (no TMDB id).</param>
/// <param name="HiddenAsSeen">Watched (even partly) or rated.</param>
/// <param name="HiddenAsSetAside">Saved, "Not for me" or dropped.</param>
/// <param name="HiddenByConditions">Ruled out by a standing condition or the maturity ceiling.</param>
/// <param name="Ranks">Each kept title's 1-based position in Trakt's list for its media type.</param>
public sealed record TraktRecommendationsResult(
    TraktRecommendationsState State,
    DateTime? FetchedAt,
    int ReturnedByTrakt,
    int Unresolved,
    int HiddenAsSeen,
    int HiddenAsSetAside,
    int HiddenByConditions,
    IReadOnlyList<Title> Titles,
    IReadOnlyDictionary<Guid, int> Ranks);

/// <summary>
/// Trakt's own personalised recommendations for the signed-in account — both media types, at the
/// most Trakt will give (100 each) — mapped onto the catalogue and filtered through Reel's
/// eligibility rule on every read.
///
/// <para><b>What is cached, and what never is.</b> Trakt's answer is cached per account (ordered
/// title ids, <see cref="FreshFor"/>), because it moves slowly and every call spends shared Trakt
/// rate budget. The FILTER is never cached: the list is pushed through
/// <see cref="EligibilityQueryBuilder.EligibleTitles(Guid, bool, IReadOnlySet{Domain.Filters.FilterKind})"/>
/// and the maturity gate on every read, so a title rated, saved, declined or ruled out by a new
/// condition a second ago is already gone — the founder-locked "never show things I've already
/// seen" and "exclusions are airtight" rules hold on this surface exactly as on Reel's own.</para>
///
/// <para><b>Refresh on sync.</b> The cached answer is also keyed on a digest of the connection's
/// stored <c>/sync/last_activities</c> snapshot, which the delta sync rewrites only when Trakt
/// reports activity. A watch, rating or hide made on Trakt therefore re-asks Trakt on the next read
/// after the sync that saw it, without a second poll of its own.</para>
///
/// <para><b>Degrading honestly.</b> No link, a dead link and an unreachable Trakt are three
/// different states, never an empty list. An unreachable Trakt falls back to the last list it gave
/// (<see cref="TraktRecommendationsState.Stale"/>, kept for <see cref="KeepStaleFor"/>), and a
/// failure is remembered for <see cref="FailureCooldown"/> so a Home that is opened repeatedly while
/// Trakt is down does not spend the shared rate budget asking again each time.</para>
/// </summary>
public sealed class TraktRecommendationsService(
    IReelDbContext db,
    ITraktClient trakt,
    TraktTokenStore tokens,
    TraktLibraryIngestor ingestor,
    TitleHydrator hydrator,
    CatalogHydrationBudget hydrationBudget,
    EligibilityQueryBuilder eligibility,
    IMemoryCache cache,
    ILogger<TraktRecommendationsService> logger)
{
    /// <summary>How long Trakt's answer is served before Trakt is asked again (absent a sync change).</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(3);

    /// <summary>How long the last good answer is kept to fall back on while Trakt is unreachable.</summary>
    public static readonly TimeSpan KeepStaleFor = TimeSpan.FromHours(24);

    /// <summary>After a failed ask, how long before Trakt is asked again (unless the user retries).</summary>
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(2);

    /// <summary>Trakt's own ceiling per media type.</summary>
    public const int TraktLimit = 100;

    /// <summary>
    /// New titles hydrated from TMDB inline, per fetch. A recommendation Reel has never seen arrives
    /// with Trakt's metadata but no poster; this many get TMDB artwork and credits before the answer
    /// is returned (inside the system-wide <see cref="CatalogHydrationBudget"/> as well), and the rest
    /// are left to the catalogue hydration sweep. A latency ceiling on one GET, not a quality claim.
    /// </summary>
    public const int InlineHydrationCap = 20;

    // One fetch per account at a time: two Home tabs opening together must not ask Trakt twice.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> FetchLocks = new();

    private sealed record Snapshot(
        DateTime FetchedAt, string SyncMarker, IReadOnlyList<Guid> TitleIds, IReadOnlyDictionary<Guid, int> Ranks, int Returned, int Unresolved);

    private sealed record Failure(DateTime At, TraktRecommendationsState State);

    private static string SnapshotKey(Guid accountId) => $"trakt-recs:snapshot:{accountId:N}";

    private static string FailureKey(Guid accountId) => $"trakt-recs:failure:{accountId:N}";

    /// <summary>Drops the cached answer for one account, so the next read asks Trakt.</summary>
    public void Invalidate(Guid accountId)
    {
        cache.Remove(SnapshotKey(accountId));
        cache.Remove(FailureKey(accountId));
    }

    /// <param name="retry">The user pressed Retry: ignore a remembered failure and ask Trakt now.</param>
    public async Task<TraktRecommendationsResult> GetAsync(Account account, bool retry, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        var connection = await db.TraktConnections.FirstOrDefaultAsync(c => c.AccountId == account.Id, ct);
        if (connection is null)
        {
            return Empty(TraktRecommendationsState.NotLinked);
        }

        if (connection.Status == ConnectionStatus.Revoked)
        {
            return Empty(TraktRecommendationsState.RelinkRequired);
        }

        var marker = SyncMarker(connection.LastActivitiesJson);
        var snapshot = cache.Get<Snapshot>(SnapshotKey(account.Id));
        if (IsFresh(snapshot, marker, nowUtc))
        {
            return await FilterAsync(account, TraktRecommendationsState.Ok, snapshot!, ct);
        }

        if (!retry && cache.Get<Failure>(FailureKey(account.Id)) is { } recent && nowUtc - recent.At < FailureCooldown)
        {
            return await FallBackAsync(account, recent.State, snapshot, ct);
        }

        var gate = FetchLocks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // A sibling request may have fetched while this one queued.
            snapshot = cache.Get<Snapshot>(SnapshotKey(account.Id));
            if (IsFresh(snapshot, marker, nowUtc))
            {
                return await FilterAsync(account, TraktRecommendationsState.Ok, snapshot!, ct);
            }

            var (fetched, failure) = await FetchAsync(account, connection, marker, nowUtc, ct);
            if (fetched is null)
            {
                cache.Set(FailureKey(account.Id), new Failure(nowUtc, failure), FailureCooldown);
                return await FallBackAsync(account, failure, snapshot, ct);
            }

            cache.Remove(FailureKey(account.Id));
            cache.Set(SnapshotKey(account.Id), fetched, KeepStaleFor);
            return await FilterAsync(account, TraktRecommendationsState.Ok, fetched, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsFresh(Snapshot? snapshot, string marker, DateTime nowUtc) =>
        snapshot is not null && nowUtc - snapshot.FetchedAt < FreshFor && snapshot.SyncMarker == marker;

    /// <summary>
    /// A dead link is said as such even when an older list exists: the user's next step is to
    /// re-link, and a stale rail would bury that. An unreachable Trakt serves the last list it gave.
    /// </summary>
    private async Task<TraktRecommendationsResult> FallBackAsync(
        Account account, TraktRecommendationsState failure, Snapshot? snapshot, CancellationToken ct) =>
        failure == TraktRecommendationsState.Unavailable && snapshot is not null
            ? await FilterAsync(account, TraktRecommendationsState.Stale, snapshot, ct)
            : Empty(failure);

    private static TraktRecommendationsResult Empty(TraktRecommendationsState state) =>
        new(state, null, 0, 0, 0, 0, 0, [], new Dictionary<Guid, int>());

    /// <summary>A digest of the stored last-activities snapshot; "none" until the first sync stored one.</summary>
    internal static string SyncMarker(string? lastActivitiesJson) =>
        string.IsNullOrEmpty(lastActivitiesJson)
            ? "none"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lastActivitiesJson)));

    private async Task<(Snapshot? Snapshot, TraktRecommendationsState Failure)> FetchAsync(
        Account account, TraktConnection connection, string marker, DateTime nowUtc, CancellationToken ct)
    {
        IReadOnlyList<TraktRecommendation> movies;
        IReadOnlyList<TraktRecommendation> shows;
        try
        {
            var token = await tokens.GetValidAccessTokenAsync(connection, ct);
            try
            {
                movies = await trakt.GetRecommendationsAsync(token, movies: true, TraktLimit, RatePriority.Interactive, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The token died before its tracked expiry: one reactive refresh, the same self-heal
                // DeltaSync uses, then the call again. A second 401 propagates.
                token = await tokens.ForceRefreshAsync(connection, token, ct);
                movies = await trakt.GetRecommendationsAsync(token, movies: true, TraktLimit, RatePriority.Interactive, ct);
            }

            shows = await trakt.GetRecommendationsAsync(token, movies: false, TraktLimit, RatePriority.Interactive, ct);
        }
        catch (TraktRelinkRequiredException)
        {
            return (null, TraktRecommendationsState.RelinkRequired);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            logger.LogWarning(ex, "Trakt refused the recommendations read for {AccountId} after a refresh; the link needs re-authorising.", account.Id);
            return (null, TraktRecommendationsState.RelinkRequired);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Deliberately broad, like hydration: a timeout surfaces as TaskCanceledException with the
            // token un-cancelled, a refused refresh as InvalidOperationException, a 5xx or 429 that
            // outlived the adapter's retries as HttpRequestException. All of them mean "Trakt did not
            // answer", which is a state the rail states — never a 500 and never an empty list.
            logger.LogWarning(ex, "Trakt recommendations could not be read for {AccountId}.", account.Id);
            return (null, TraktRecommendationsState.Unavailable);
        }

        var ordered = Interleave(movies, shows);
        var titles = await ingestor.ResolveRecommendedTitlesAsync(ordered, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A sync inserting the same title in the same instant: nothing is lost by asking again
            // shortly, and nothing half-written survives this request's context.
            logger.LogWarning(ex, "Trakt recommendations for {AccountId} could not be stored this time.", account.Id);
            return (null, TraktRecommendationsState.Unavailable);
        }

        await HydrateNewTitlesAsync(account, titles, nowUtc, ct);

        var linkable = titles.Where(t => t.TmdbId is not null).Select(t => t.Id).ToList();
        return (new Snapshot(nowUtc, marker, linkable, RanksOf(ordered, titles), movies.Count + shows.Count, titles.Count - linkable.Count),
            TraktRecommendationsState.Ok);
    }

    /// <summary>
    /// Trakt's two lists, alternated movie, show, movie, show… so both media are on the first screen
    /// of the rail. Within each media type Trakt's own order is kept exactly; when one list runs out
    /// the other continues in its order.
    /// </summary>
    internal static IReadOnlyList<TraktRecommendation> Interleave(
        IReadOnlyList<TraktRecommendation> movies, IReadOnlyList<TraktRecommendation> shows)
    {
        var byRank = (IReadOnlyList<TraktRecommendation> list) => list.OrderBy(r => r.Rank).ToList();
        var m = byRank(movies);
        var s = byRank(shows);
        var merged = new List<TraktRecommendation>(m.Count + s.Count);
        for (var i = 0; i < Math.Max(m.Count, s.Count); i++)
        {
            if (i < m.Count)
            {
                merged.Add(m[i]);
            }

            if (i < s.Count)
            {
                merged.Add(s[i]);
            }
        }

        return merged;
    }

    /// <summary>
    /// Each title's Trakt rank, matched by the ids Trakt sent rather than by list position — the
    /// resolver drops duplicates, so positions in the two lists need not line up.
    /// </summary>
    private static Dictionary<Guid, int> RanksOf(IReadOnlyList<TraktRecommendation> ordered, IReadOnlyList<Title> titles)
    {
        var ranks = new Dictionary<Guid, int>();
        foreach (var title in titles)
        {
            var match = ordered.FirstOrDefault(r => title.MediaType == MediaType.Movie
                ? r.Movie is { } m && (m.Ids.Trakt == title.TraktId || (m.Ids.Tmdb is not null && m.Ids.Tmdb == title.TmdbId))
                : r.Show is { } s && (s.Ids.Trakt == title.TraktId || (s.Ids.Tmdb is not null && s.Ids.Tmdb == title.TmdbId)));
            if (match is not null)
            {
                ranks.TryAdd(title.Id, match.Rank);
            }
        }

        return ranks;
    }

    private async Task HydrateNewTitlesAsync(Account account, IReadOnlyList<Title> titles, DateTime nowUtc, CancellationToken ct)
    {
        var unhydrated = titles.Where(t => t.LastMetadataRefreshAt is null && t.TmdbId is not null).ToList();
        if (unhydrated.Count == 0)
        {
            return;
        }

        var allowance = await hydrationBudget.AllowanceAsync(nowUtc, ct);
        var take = (int)Math.Min(InlineHydrationCap, allowance.Remaining);
        if (take <= 0)
        {
            return;
        }

        try
        {
            await hydrator.HydrateBatchResilientAsync(unhydrated.Take(take).ToList(), ct, account.Region);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Artwork is an improvement, not a precondition: the list is already stored and the
            // sweep will hydrate what this could not.
            logger.LogWarning(ex, "Inline hydration of new Trakt recommendations failed for {AccountId}.", account.Id);
        }
    }

    /// <summary>
    /// The cached list through Reel's rules, read now. Precedence when a title trips more than one:
    /// seen, then set aside, then conditions — the reason a user most recognises.
    /// </summary>
    private async Task<TraktRecommendationsResult> FilterAsync(
        Account account, TraktRecommendationsState state, Snapshot snapshot, CancellationToken ct)
    {
        var ids = snapshot.TitleIds;
        if (ids.Count == 0)
        {
            return new(state, snapshot.FetchedAt, snapshot.Returned, snapshot.Unresolved, 0, 0, 0, [], snapshot.Ranks);
        }

        var accountId = account.Id;
        var eligible = (await eligibility.EligibleTitles(accountId)
            .Where(t => ids.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(ct)).ToHashSet();
        var allowed = (await eligibility.ContentFilteredTitles(accountId)
            .Where(t => ids.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(ct)).ToHashSet();
        var seen = (await db.Titles
            .Where(eligibility.Seen(accountId))
            .Where(t => ids.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(ct)).ToHashSet();
        var titles = await db.Titles.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var maturityCeiling = account.Settings.MaturityCeiling;

        int hiddenSeen = 0, hiddenSetAside = 0, hiddenConditions = 0, unresolved = snapshot.Unresolved;
        var kept = new List<Title>(ids.Count);
        foreach (var id in ids)
        {
            if (!titles.TryGetValue(id, out var title))
            {
                unresolved++;
            }
            else if (seen.Contains(id))
            {
                hiddenSeen++;
            }
            else if (!allowed.Contains(id) || !EligibilityQueryBuilder.PassesMaturity(title, maturityCeiling))
            {
                hiddenConditions++;
            }
            else if (!eligible.Contains(id))
            {
                hiddenSetAside++;
            }
            else
            {
                kept.Add(title);
            }
        }

        return new(state, snapshot.FetchedAt, snapshot.Returned, unresolved, hiddenSeen, hiddenSetAside, hiddenConditions, kept, snapshot.Ranks);
    }
}
