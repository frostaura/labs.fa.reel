using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Providers;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>
/// Where-to-watch: TMDB availability (24h cached per title+region) resolved through the
/// link ladder — a maintained provider-side search URL ("▶ direct") or the title's TMDB watch
/// page ("↗ via TMDB"). True one-click deep links are the M5 JustWatch gate. No scraping.
/// </summary>
public static class ProviderEndpoints
{
    /// <summary>v1 static pattern registry: provider-side search URLs (licence-safe).</summary>
    private static readonly Dictionary<int, (string Name, string UrlTemplate)> DirectSearchPatterns = new()
    {
        [8] = ("Netflix", "https://www.netflix.com/search?q={q}"),
        [9] = ("Prime Video", "https://www.primevideo.com/search?phrase={q}"),
        [119] = ("Prime Video", "https://www.primevideo.com/search?phrase={q}"),
        [337] = ("Disney+", "https://www.disneyplus.com/search?q={q}"),
        [350] = ("Apple TV+", "https://tv.apple.com/search?term={q}"),
        [1899] = ("Max", "https://play.max.com/search?q={q}"),
        [15] = ("Hulu", "https://www.hulu.com/search?q={q}"),
        [531] = ("Paramount+", "https://www.paramountplus.com/search/?query={q}"),
        [386] = ("Peacock", "https://www.peacocktv.com/search?q={q}"),
        [192] = ("YouTube", "https://www.youtube.com/results?search_query={q}"),
        [3] = ("Google Play", "https://play.google.com/store/search?q={q}&c=movies"),
        [55] = ("Showmax", "https://www.showmax.com/eng/search?q={q}"),
    };

    /// <summary>
    /// How long the ladder serves a cached snapshot before it refetches from TMDB. A surface that
    /// wants to name a provider the ladder will show must read rows inside this window: older rows
    /// are replaced by a refetch the moment the ladder renders, and may no longer hold that provider.
    /// </summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// Whether the where-to-watch ladder gives this provider a chip of its own. The ladder
    /// (<c>WhereToWatch.tsx</c>) renders a named chip only for a provider with a maintained
    /// provider-side link (<c>linkKind == "direct"</c>); every other provider collapses into one
    /// "More options via TMDB" link that names nobody. A sentence elsewhere on the card that names a
    /// provider must name one this returns true for, or it names something the card does not show
    /// (review R1-18: a Watch-tonight reason said "on Philo" over a ladder of Google Play and YouTube).
    /// </summary>
    public static bool RendersAsChip(int tmdbProviderId) => DirectSearchPatterns.ContainsKey(tmdbProviderId);

    public static void MapProviderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/titles/{mediaType}/{tmdbId:long}").RequireAccount().MapGet("/providers", async (
            string mediaType, long tmdbId,
            IReelDbContext db, ITmdbClient tmdb, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            var title = await TitleEndpoints.ResolveTitleAsync(db, mediaType, tmdbId, ct);
            if (account is null || title is null)
            {
                return Results.NotFound();
            }

            var region = account.Region;
            var cutoff = DateTime.UtcNow - CacheTtl;
            var cached = await db.TitleAvailabilities
                .Where(a => a.TitleId == title.Id && a.Region == region && a.FetchedAt > cutoff)
                .ToListAsync(ct);

            if (cached.Count == 0)
            {
                var fresh = await tmdb.GetWatchProvidersAsync(tmdbId, title.MediaType == MediaType.Movie, region, ct);

                // The same writer the availability job uses: opening a title page refetches, and a
                // refetch must carry each offer's FirstSeenAt, not restamp the whole set as new.
                await AvailabilitySnapshot.ReplaceAsync(db, title.Id, region, fresh, DateTime.UtcNow, ct);
                cached = await db.TitleAvailabilities
                    .Where(a => a.TitleId == title.Id && a.Region == region)
                    .ToListAsync(ct);
            }

            var providers = await db.StreamingProviders.ToDictionaryAsync(p => p.Id, ct);
            var tmdbWatchPage = $"https://www.themoviedb.org/{(title.MediaType == MediaType.Movie ? "movie" : "tv")}/{tmdbId}/watch?locale={region}";

            var results = cached
                .Where(a => providers.ContainsKey(a.ProviderId))
                .GroupBy(a => a.ProviderId)
                .Select(g =>
                {
                    var provider = providers[g.Key];
                    var direct = DirectSearchPatterns.TryGetValue(provider.TmdbProviderId, out var pattern);
                    return new
                    {
                        provider = provider.Name,
                        logoPath = provider.LogoPath,
                        kinds = g.Select(a => a.Kind.ToString()).Distinct(),
                        linkKind = direct ? "direct" : "tmdb",
                        url = direct
                            ? pattern.UrlTemplate.Replace("{q}", Uri.EscapeDataString(title.Name))
                            : tmdbWatchPage,
                        displayPriority = provider.DisplayPriority,
                    };
                })
                .OrderBy(r => r.displayPriority)
                .ToList();

            return Results.Ok(new
            {
                region,
                attribution = "Watch-provider data powered by JustWatch via TMDB.",
                tmdbWatchPage,
                providers = results,
            });
        });
    }
}
