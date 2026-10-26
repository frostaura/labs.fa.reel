using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Saved;

/// <summary>
/// Keeps the streaming facts behind the Saved shelf current.
///
/// <para><b>Why it exists (G-2, G+1).</b> Availability was fetched only for the feed's top titles
/// (the availability job walked the active feed snapshot and nothing else) and for a title page
/// someone opened. Saved titles are excluded from the feed by design, so a save was priced once —
/// if ever — and then read as "streaming now" or "not streaming" off a snapshot that aged out of
/// <see cref="Providers.StreamingNowPolicy.FreshFor"/> and silently dropped the card off the Tonight
/// order. The shelf whose whole job is "what can I put on tonight" was the one surface with no
/// refresh path.</para>
///
/// <para><b>Two routes, one selection.</b> The availability job refreshes up to
/// <see cref="MaxSaves"/> live saves on every run, stalest first; opening Saved queues that job when
/// a live save is stale, at most once per <see cref="Debounce"/>. Both read
/// <see cref="StaleSavesAsync"/>, so "which saves need a refetch" has one answer.</para>
/// </summary>
public static class SavedAvailabilityRefresh
{
    /// <summary>At most this many saves are refetched per run — 40 TMDB calls on the Backfill lane.</summary>
    public const int MaxSaves = 40;

    /// <summary>A snapshot younger than this is not refetched — the same TTL the where-to-watch ladder caches for.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    /// <summary>Opening Saved queues a refresh at most once in this window.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromHours(24);

    /// <summary>The <see cref="SyncJob.TriggerReason"/> a Saved-opened refresh carries; the debounce keys on it.</summary>
    public const string TriggerReason = "saved-opened";

    /// <summary>
    /// The account's live, unconsumed saves whose availability in <paramref name="region"/> was never
    /// fetched or is older than <see cref="Ttl"/>, never-fetched first, then oldest first. A snapshot
    /// is replaced wholesale (<see cref="Providers.AvailabilitySnapshot"/>), so the newest
    /// <c>FetchedAt</c> of a title's rows is when it was last looked at.
    /// </summary>
    public static async Task<IReadOnlyList<Title>> StaleSavesAsync(
        IReelDbContext db, Guid accountId, string region, DateTime nowUtc, int take, CancellationToken ct)
    {
        var cutoff = nowUtc - Ttl;
        var rows = await SaveConsumption.LiveSaves(db, accountId)
            .Unconsumed(db)
            .Join(db.Titles.Where(t => t.TmdbId != null), r => r.TitleId, t => t.Id, (r, t) => t)
            .Select(t => new
            {
                Title = t,
                LastFetched = db.TitleAvailabilities
                    .Where(a => a.TitleId == t.Id && a.Region == region)
                    .Max(a => (DateTime?)a.FetchedAt),
            })
            .Where(x => x.LastFetched == null || x.LastFetched <= cutoff)
            // false sorts first: never-fetched saves lead, then the oldest snapshot.
            .OrderBy(x => x.LastFetched != null)
            .ThenBy(x => x.LastFetched)
            .ThenBy(x => x.Title.Id)
            .Take(take)
            .ToListAsync(ct);

        return rows.Select(x => x.Title).DistinctBy(t => t.Id).ToList();
    }

    /// <summary>
    /// Queues a <see cref="JobKind.RefreshAvailability"/> for the account when Saved is opened, if a
    /// live save is stale, no availability job is in flight, and no Saved-opened refresh was queued
    /// inside <see cref="Debounce"/>. <b>Saves</b> when it queued. Returns whether it did.
    /// <para>The GET that calls this never waits on TMDB: the refreshed facts reach the next open,
    /// which is the honest trade for a page load that must not stall on 40 network calls.</para>
    /// </summary>
    public static async Task<bool> RequestAsync(IReelDbContext db, Account account, DateTime nowUtc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(account.Region))
        {
            return false;
        }

        var debounceStart = nowUtc - Debounce;
        var recent = await db.SyncJobs.AnyAsync(
            j => j.AccountId == account.Id
                && j.Kind == JobKind.RefreshAvailability
                && (j.Status == JobStatus.Pending
                    || j.Status == JobStatus.Running
                    || (j.TriggerReason == TriggerReason && j.EnqueuedAt > debounceStart)),
            ct);
        if (recent)
        {
            return false;
        }

        if ((await StaleSavesAsync(db, account.Id, account.Region, nowUtc, 1, ct)).Count == 0)
        {
            return false;
        }

        db.SyncJobs.Add(new SyncJob
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = JobKind.RefreshAvailability,
            Priority = 3,
            EnqueuedAt = nowUtc,
            TriggerReason = TriggerReason,
        });

        // A feed build can enqueue the same kind between the check and the insert; the partial
        // unique index then rejects this row, and the winner's run refreshes saves too.
        await db.SaveAbsorbingRebuildRaceAsync(ct);
        return true;
    }
}
