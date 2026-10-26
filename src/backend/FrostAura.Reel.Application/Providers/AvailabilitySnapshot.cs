using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Providers;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Providers;

/// <summary>
/// The one writer of a title's watch-provider snapshot in a region. The availability job and the
/// where-to-watch endpoint both replace the snapshot wholesale on refetch; they used to carry two
/// copies of the same delete-and-insert, and the insert stamped only <c>FetchedAt</c> — so a cache
/// refresh was indistinguishable from an arrival, and the digest announced 20-year-old shows as
/// "Just landed on your services" (IR2S-02). Replacing goes through here so
/// <see cref="TitleAvailability.FirstSeenAt"/> is carried, or stamped, the same way everywhere.
/// </summary>
public static class AvailabilitySnapshot
{
    /// <summary>
    /// The <see cref="TitleAvailability.FirstSeenAt"/> a freshly fetched offer of
    /// <paramref name="providerId"/>/<paramref name="kind"/> gets, given the snapshot it replaces.
    /// <list type="bullet">
    /// <item>No previous snapshot: null. Reel is looking for the first time, so everything it sees
    /// was already there as far as it can tell — calling it new is the very lie this fixes.</item>
    /// <item>The offer was in the previous snapshot: its previous value, null included. A refetch
    /// that finds the same offer changes nothing about when it arrived.</item>
    /// <item>The offer was not: <paramref name="nowUtc"/>. The provider set changed, and this is the
    /// change.</item>
    /// </list>
    /// Kind is part of the identity: a title that moves from Rent to subscription on the same
    /// provider has newly become streamable there, which is the arrival a reader cares about.
    /// <para>
    /// Known limit, stated rather than hidden: a lookup that found no offers writes no rows, so
    /// "looked and saw nothing" leaves no trace and a title's first offers after it are treated as a
    /// first look. In practice a title that is about to stream already carries Rent/Buy rows, so
    /// its subscription arrival is still observed.
    /// </para>
    /// </summary>
    public static DateTime? FirstSeenAt(
        IReadOnlyCollection<TitleAvailability> previous, Guid providerId, AvailabilityKind kind, DateTime nowUtc)
    {
        if (previous.Count == 0)
        {
            return null;
        }

        var match = previous.FirstOrDefault(p => p.ProviderId == providerId && p.Kind == kind);
        return match is null ? nowUtc : match.FirstSeenAt;
    }

    /// <summary>
    /// Replaces the snapshot for <paramref name="titleId"/> in <paramref name="region"/> with
    /// <paramref name="fresh"/>, registering any provider Reel has not met before, and saves.
    /// </summary>
    public static async Task ReplaceAsync(
        IReelDbContext db, Guid titleId, string region, IReadOnlyList<TmdbWatchProvider> fresh,
        DateTime nowUtc, CancellationToken ct)
    {
        var tmdbProviderIds = fresh.Select(f => f.ProviderId).Distinct().ToList();
        var known = await db.StreamingProviders
            .Where(p => tmdbProviderIds.Contains(p.TmdbProviderId))
            .ToDictionaryAsync(p => p.TmdbProviderId, ct);

        var previous = await db.TitleAvailabilities
            .AsNoTracking()
            .Where(a => a.TitleId == titleId && a.Region == region)
            .ToListAsync(ct);

        await db.TitleAvailabilities
            .Where(a => a.TitleId == titleId && a.Region == region)
            .ExecuteDeleteAsync(ct);

        // (provider, kind) is unique per title and region; a payload that repeats one must not
        // trip the unique index on the way in.
        foreach (var item in fresh.DistinctBy(f => (f.ProviderId, f.Kind)))
        {
            if (!known.TryGetValue(item.ProviderId, out var provider))
            {
                provider = new StreamingProvider
                {
                    Id = Guid.NewGuid(),
                    TmdbProviderId = item.ProviderId,
                    Name = item.Name,
                    LogoPath = item.LogoPath,
                    DisplayPriority = item.DisplayPriority,
                };
                known[item.ProviderId] = provider;
                db.StreamingProviders.Add(provider);
            }

            db.TitleAvailabilities.Add(new TitleAvailability
            {
                Id = Guid.NewGuid(),
                TitleId = titleId,
                Region = region,
                ProviderId = provider.Id,
                Kind = item.Kind,
                FetchedAt = nowUtc,
                FirstSeenAt = FirstSeenAt(previous, provider.Id, item.Kind, nowUtc),
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
