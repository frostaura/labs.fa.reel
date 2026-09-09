using System.Text.Json;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

public static class DigestEndpoints
{
    public static void MapDigestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/digest").RequireAccount();

        // The current ISO week's digest as feed-shaped cards; { items: [] } until the weekly
        // build has run — the Home rail hides itself on empty rather than erroring.
        group.MapGet("/current", async (IReelDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            var nowUtc = DateTime.UtcNow;
            var weekStart = DigestWeek.MondayOf(nowUtc);
            var digest = await db.AccountDigests
                .FirstOrDefaultAsync(d => d.AccountId == account.Id && d.WeekStart == weekStart, ct);
            if (digest is null)
            {
                return Results.Ok(new
                {
                    weekStart = (DateOnly?)null,
                    generatedAt = (DateTime?)null,
                    items = Array.Empty<object>(),
                });
            }

            var digestItems = JsonSerializer.Deserialize<List<DigestItem>>(digest.ItemsJson) ?? [];
            var ids = digestItems.Select(i => i.TitleId).ToList();
            var titles = ids.Count == 0
                ? []
                : await db.Titles.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
            var providers = await FeedEndpoints.ProvidersForAsync(db, account.Region, ids, nowUtc, ct);

            var cards = new List<FeedEndpoints.FeedCard>();
            foreach (var item in digestItems)
            {
                if (!titles.TryGetValue(item.TitleId, out var title))
                {
                    continue;
                }

                var releaseDate = title.MediaType == MediaType.Movie ? title.ReleasedAt : title.FirstAiredAt;
                var isReleased = releaseDate is null || releaseDate <= nowUtc;
                var (crowd, crowdVotes) = CrowdRating.Resolve(title);
                var whyThis = DigestReasons.CopyFor(item.Reason);
                cards.Add(new(
                    title.Id, title.MediaType.ToString(), title.TmdbId, title.Name, title.Year, title.RuntimeMinutes,
                    title.PosterPath, title.BackdropPath, title.Genres,
                    Math.Round(item.PredictedRating, 1), crowd, crowdVotes, whyThis, isReleased,
                    providers.GetValueOrDefault(title.Id, [])));
            }

            return Results.Ok(new
            {
                weekStart = (DateOnly?)digest.WeekStart,
                generatedAt = (DateTime?)digest.GeneratedAt,
                items = cards,
            });
        });
    }
}
