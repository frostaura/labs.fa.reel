using System.Collections.Concurrent;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Feed;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// One Home page's "already on screen" set, shared by every rail request that page makes, so no
/// title is served on two rails of the same page.
///
/// <para><b>Why the server holds it.</b> Home fires every rail's first <c>POST /feed/more</c>
/// within ~165 ms of the feed landing. A page-wide exclude list sent by the client cannot help
/// those requests — none of them has seen another's answer yet — and the result was 23% of the
/// titles on a 1440 px Home appearing in two or more sections (NCIS in five). The client therefore
/// sends one opaque token per page view; each request CLAIMS its cards against the token's set
/// under a lock, and a card another rail already claimed is skipped for the next-best one. The set
/// is seeded with the Active snapshot's titles, so a genre rail also never repeats tonight's picks
/// or a because-you-loved rail above it.</para>
///
/// <para><b>Claims belong to a row.</b> Each claimed title remembers the row key that claimed it,
/// and a title is free for the row that owns it. An RTK refetch or a tag invalidation re-requests a
/// rail with the same token; without ownership that request found every one of its own cards
/// already taken and served a different, shorter page (10 cards, then 8 with 0 overlap).</para>
///
/// <para><b>One set per page, created atomically.</b> The set is built inside a
/// <see cref="Lazy{T}"/> stored with <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/>,
/// so concurrent first requests for one token all await the same seeding and claim into the same
/// set. The earlier check-then-create, with an <c>await</c> between them, let two first requests
/// each build a private set and serve the same titles (the lane's own concurrency test failed 1 run
/// in 8 on it).</para>
///
/// <para><b>Bounded.</b> Tokens are client-chosen, so the store caps what one account can hold
/// (<see cref="MaxPagesPerAccount"/>, oldest evicted) and what the process holds
/// (<see cref="MaxPages"/>), and drops a page idle for <see cref="IdleTtl"/>. In-process and
/// short-lived on purpose: a page view is minutes long, and the worst a lost set costs (a restart
/// or an eviction mid-page) is the pre-claims behaviour for the rest of that page.</para>
/// </summary>
public static class FeedPageClaims
{
    /// <summary>How long an idle page's set is kept. Sliding: every claim keeps it alive.</summary>
    public static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(30);

    /// <summary>Longest token accepted — a client nonce, not a payload.</summary>
    public const int MaxTokenLength = 64;

    /// <summary>Live page views one account may hold; the least recently used is dropped beyond it.</summary>
    public const int MaxPagesPerAccount = 8;

    /// <summary>Live page views the process holds across all accounts.</summary>
    public const int MaxPages = 4096;

    private static readonly ConcurrentDictionary<(Guid AccountId, string Token), Lazy<Task<Page>>> Pages = new();

    /// <summary>The owner recorded for a title seeded from tonight's snapshot: no rail may claim it.</summary>
    private const string SnapshotOwner = "\u0000snapshot";

    /// <summary>
    /// The titles of <paramref name="orderedTitleIds"/> that another row of this page already holds —
    /// a read, not a claim. Lets a rail drop them BEFORE it spends a curation pass on its slice,
    /// so a deep lane is not cut to the few survivors of a slice its neighbours already took.
    /// Empty when there is no token.
    /// </summary>
    public static async Task<IReadOnlySet<Guid>> HeldElsewhereAsync(
        IReelDbContext db, Guid accountId, string? pageToken, string rowKey,
        IReadOnlyCollection<Guid> orderedTitleIds, DateTime now, CancellationToken ct)
    {
        if (await PageForAsync(db, accountId, pageToken, now, ct) is not { } page)
        {
            return new HashSet<Guid>();
        }

        lock (page.Gate)
        {
            return orderedTitleIds
                .Where(id => page.Owners.TryGetValue(id, out var owner) && owner != rowKey)
                .ToHashSet();
        }
    }

    /// <summary>
    /// Claims, in order, as many of <paramref name="orderedTitleIds"/> as are free for
    /// <paramref name="rowKey"/> on this page, up to <paramref name="limit"/>, and returns them in
    /// the same order. A title is free when nobody holds it or this row already does. With no
    /// token every title is free (the behaviour before page claims existed).
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ClaimAsync(
        IReelDbContext db, Guid accountId, string? pageToken, string rowKey,
        IReadOnlyList<Guid> orderedTitleIds, int limit, DateTime now, CancellationToken ct)
    {
        if (await PageForAsync(db, accountId, pageToken, now, ct) is not { } page)
        {
            return [.. orderedTitleIds.Distinct().Take(limit)];
        }

        var taken = new List<Guid>(Math.Min(limit, orderedTitleIds.Count));
        lock (page.Gate)
        {
            foreach (var titleId in orderedTitleIds)
            {
                if (taken.Count >= limit)
                {
                    break;
                }

                if (page.Owners.TryGetValue(titleId, out var owner))
                {
                    if (owner == rowKey && !taken.Contains(titleId))
                    {
                        taken.Add(titleId);
                    }

                    continue;
                }

                page.Owners[titleId] = rowKey;
                taken.Add(titleId);
            }
        }

        return taken;
    }

    /// <summary>How many page views are currently held, for tests and diagnostics.</summary>
    public static int HeldPages(Guid accountId) => Pages.Keys.Count(k => k.AccountId == accountId);

    private static async Task<Page?> PageForAsync(
        IReelDbContext db, Guid accountId, string? pageToken, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pageToken) || pageToken.Length > MaxTokenLength)
        {
            return null;
        }

        var key = (accountId, pageToken);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var created = false;
            var lazy = Pages.GetOrAdd(key, _ =>
            {
                created = true;
                return new Lazy<Task<Page>>(() => SeedAsync(db, accountId, now, ct), LazyThreadSafetyMode.ExecutionAndPublication);
            });

            if (created)
            {
                Evict(accountId, key, now);
            }

            try
            {
                var page = await lazy.Value;
                page.Touch(now);
                return page;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The request that created the set failed or was cancelled while seeding it. Its
                // task is faulted for everyone awaiting it, so it is removed (only if it is still
                // the stored one) and this request seeds afresh with its own context.
                Pages.TryRemove(new KeyValuePair<(Guid, string), Lazy<Task<Page>>>(key, lazy));
            }
        }

        return null;
    }

    private static async Task<Page> SeedAsync(IReelDbContext db, Guid accountId, DateTime now, CancellationToken ct)
    {
        // Seeded with tonight's snapshot, read once per page view.
        var seed = await db.FeedItems
            .Where(i => i.AccountId == accountId
                && db.FeedSnapshots.Any(s => s.Id == i.FeedSnapshotId && s.Status == SnapshotStatus.Active))
            .Select(i => i.TitleId)
            .ToListAsync(ct);
        var page = new Page(now);
        foreach (var titleId in seed)
        {
            page.Owners[titleId] = SnapshotOwner;
        }

        return page;
    }

    /// <summary>Drops idle pages, then the account's and the process's least recently used beyond the caps.</summary>
    private static void Evict(Guid accountId, (Guid AccountId, string Token) keep, DateTime now)
    {
        foreach (var (key, lazy) in Pages)
        {
            if (!key.Equals(keep) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully
                && now - lazy.Value.Result.LastTouched > IdleTtl)
            {
                Pages.TryRemove(new KeyValuePair<(Guid, string), Lazy<Task<Page>>>(key, lazy));
            }
        }

        TrimToCap(Pages.Where(p => p.Key.AccountId == accountId).ToList(), MaxPagesPerAccount, keep);
        if (Pages.Count > MaxPages)
        {
            TrimToCap(Pages.ToList(), MaxPages, keep);
        }
    }

    private static void TrimToCap(
        List<KeyValuePair<(Guid AccountId, string Token), Lazy<Task<Page>>>> held, int cap,
        (Guid AccountId, string Token) keep)
    {
        if (held.Count <= cap)
        {
            return;
        }

        // A page still seeding sorts as the newest — it is being used right now.
        foreach (var stale in held
                     .Where(p => !p.Key.Equals(keep))
                     .OrderBy(p => p.Value.IsValueCreated && p.Value.Value.IsCompletedSuccessfully
                         ? p.Value.Value.Result.LastTouched
                         : DateTime.MaxValue)
                     .Take(held.Count - cap))
        {
            Pages.TryRemove(stale);
        }
    }

    private sealed class Page(DateTime createdAt)
    {
        private long lastTouchedTicks = createdAt.Ticks;

        public object Gate { get; } = new();

        /// <summary>Title → the row key holding it (<see cref="SnapshotOwner"/> for tonight's picks).</summary>
        public Dictionary<Guid, string> Owners { get; } = [];

        public DateTime LastTouched => new(Interlocked.Read(ref lastTouchedTicks), DateTimeKind.Utc);

        public void Touch(DateTime now) => Interlocked.Exchange(ref lastTouchedTicks, now.Ticks);
    }
}
