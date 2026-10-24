using System.Text.Json;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Ml;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

public static class DigestEndpoints
{
    public static void MapDigestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/digest").RequireAccount();

        // The current ISO week's digest as feed-shaped cards; { items: [] } until the weekly
        // build has run — the Home rail hides itself on empty rather than erroring.
        group.MapGet("/current", async (IReelDbContext db, ExposureLog exposures, LensService lens, TasteFactsBuilder tasteFacts, HttpContext http, CancellationToken ct) =>
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

            // Read-only: the digest is rendered, never generated from. The job that built it
            // already asked for lens copy on its top pick.
            var notes = await lens.ResolveManyAsync(
                account.Id, ids, await tasteFacts.CurrentHashAsync(account.Id, nowUtc, ct), ct);

            var cards = new List<FeedEndpoints.FeedCard>();
            var whySources = new List<ExposureWhySource>();
            foreach (var item in digestItems)
            {
                if (!titles.TryGetValue(item.TitleId, out var title))
                {
                    continue;
                }

                var releaseDate = title.MediaType == MediaType.Movie ? title.ReleasedAt : title.FirstAiredAt;
                var isReleased = releaseDate is null || releaseDate <= nowUtc;
                var (crowd, crowdVotes) = CrowdRating.Resolve(title);
                var note = notes.GetValueOrDefault(title.Id)?.Note;
                // The lens's hook where one exists, and the reason copy where it does not. Both are
                // true statements about why the card is here; the hook is simply the one written
                // for this reader.
                var whyThis = note?.Hook ?? DigestReasons.CopyFor(item.Reason);
                whySources.Add(DigestWhySource(note?.Hook));
                cards.Add(new(
                    title.Id, title.MediaType.ToString(), title.TmdbId, title.Name, title.Year, title.RuntimeMinutes,
                    title.PosterPath, title.BackdropPath, title.Genres,
                    Math.Round(item.PredictedRating, 1), crowd, crowdVotes, whyThis, isReleased,
                    providers.GetValueOrDefault(title.Id, []),
                    Confidence: null, FloorUsed: null,
                    Hook: note?.Hook, LensClause: note?.FirstClause(), Mood: note?.Mood));
            }

            // The digest is the one surface whose picks were frozen days ago by a job that did not
            // record an artifact id, so the promise is attributed to whichever model is Active when
            // it is READ. That is a small inaccuracy and it is stated here rather than papered over;
            // with no Active artifact at all the digest is simply not logged, because an exposure
            // that cannot name the model that made the promise proves nothing.
            if (cards.Count > 0)
            {
                var artifactId = await db.ModelArtifacts
                    .Where(a => a.AccountId == account.Id && a.Status == ArtifactStatus.Active)
                    .OrderByDescending(a => a.Version)
                    .Select(a => (Guid?)a.Id)
                    .FirstOrDefaultAsync(ct);
                if (artifactId is { } activeArtifactId)
                {
                    await exposures.RecordAsync(
                        account.Id, ExposureSurface.Digest, feedSnapshotId: null, activeArtifactId, nowUtc,
                        cards.Select((c, i) => new ExposureCard(
                            c.TitleId, i, c.PredictedRating,
                            WhySentence: c.WhyThis, WhySource: whySources[i])).ToList(),
                        ct);
                }
            }

            return Results.Ok(new
            {
                weekStart = (DateOnly?)digest.WeekStart,
                generatedAt = (DateTime?)digest.GeneratedAt,
                items = cards,
            });
        });
    }

    /// <summary>
    /// How a digest card's why-line came to be, typed for the exposure log. The line is the lens
    /// hook when one is cached for this reader — model text, written earlier and served from the
    /// lens cache, so <see cref="ExposureWhySource.Cache"/> — and otherwise the digest's own fixed
    /// reason copy, <see cref="ExposureWhySource.Digest"/>. It used to log every card as Digest,
    /// which booked model-written hooks as deterministic copy and hid them from every accuracy query
    /// that separates model text from templates.
    /// </summary>
    public static ExposureWhySource DigestWhySource(string? hook) =>
        string.IsNullOrWhiteSpace(hook) ? ExposureWhySource.Digest : ExposureWhySource.Cache;
}
