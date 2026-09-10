using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>Taste DNA — the free hook: who you are, in data. Computed live from raw events.</summary>
public static class TasteEndpoints
{
    /// <summary>Default and maximum page sizes for the dislike history.</summary>
    private const int DislikePageSize = 20;

    private const int MaxDislikePageSize = 50;

    /// <summary>How many demoted genres / tones the one-line dislike summary names.</summary>
    private const int DislikeProfileTop = 3;

    public static void MapTasteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/taste").RequireAccount();

        // The strongest negative signal the product collects, made scrutable. 175 live
        // NotInterested / Dropped rows drive a recency-decayed, reason-weighted demotion on every
        // scoring surface, and until now the user could neither see the list nor audit what it had
        // taught the model — only revoke a title one at a time, if they could find it again.
        //
        // Two halves: the history (newest first, paginated, each row carrying the reason it was
        // given and the date) and the PROFILE — the genres and tones actually being demoted right
        // now. The profile is windowed to DislikeSignal.WindowDays and recency-decayed, so it
        // legitimately says less than the total; the payload reports both numbers rather than
        // letting one stand in for the other.
        group.MapGet("/dislikes", async (
                IReelDbContext db, IAccountContext accountContext, DislikeSignal dislikes,
                int? offset, int? limit, CancellationToken ct) =>
            Results.Ok(await BuildDislikesAsync(
                db, dislikes, accountContext.AccountId!.Value, offset, limit, ct)));

        group.MapGet("/", async (
            IReelDbContext db, IAccountContext accountContext, FeatureVectorBuilder featureBuilder, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var now = DateTime.UtcNow;
            var taste = await featureBuilder.BuildTasteStateAsync(accountId, now, ct);

            // Genres & eras by loved-ness.
            var topGenres = taste.GenreRatings
                .Select(kv => new
                {
                    genre = kv.Key,
                    affinity = Math.Round(TasteMath.ShrunkenMean(kv.Value.Select(r => r.Rating).ToList(), taste.UserMean), 2),
                    count = kv.Value.Count,
                })
                .Where(g => g.count >= 10)
                .OrderByDescending(g => (double)g.affinity * Math.Log(1 + g.count))
                .Take(8)
                .ToList();

            var eras = taste.DecadeRatings
                .Select(kv => new
                {
                    decade = kv.Key,
                    affinity = Math.Round(TasteMath.ShrunkenMean(kv.Value, taste.UserMean), 2),
                    count = kv.Value.Count,
                })
                .Where(e => e.count >= 5)
                .OrderBy(e => e.decade)
                .ToList();

            // Creator affinities with enough evidence.
            var personIds = taste.PersonRatings.Where(kv => kv.Value.Count >= 3).Select(kv => kv.Key).ToList();
            var personNames = await db.Persons
                .Where(p => personIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.KnownForDepartment, p.ProfilePath })
                .ToDictionaryAsync(p => p.Id, ct);
            var creators = personIds
                .Where(personNames.ContainsKey)
                .Select(id => new
                {
                    name = personNames[id].Name,
                    department = personNames[id].KnownForDepartment,
                    profilePath = personNames[id].ProfilePath,
                    affinity = Math.Round(TasteMath.ShrunkenMean(taste.PersonRatings[id], taste.UserMean), 2),
                    count = taste.PersonRatings[id].Count,
                })
                .OrderByDescending(c => c.affinity)
                .Take(12)
                .ToList();

            // Top rated actors — the user's EXPLICIT person ratings (distinct from derived creators).
            var topActors = await db.UserPersonRatings
                .Where(r => r.AccountId == accountId)
                .Join(db.Persons, r => r.PersonId, p => p.Id, (r, p) => new
                {
                    personId = p.Id,
                    name = p.Name,
                    profilePath = p.ProfilePath,
                    rating = r.Rating,
                    ratedAt = r.RatedAt,
                })
                .OrderByDescending(a => a.rating)
                .ThenByDescending(a => a.ratedAt)
                .Take(12)
                .ToListAsync(ct);

            // Ratings histogram + headline stats.
            var ratings = await db.UserRatings
                .Where(r => r.AccountId == accountId
                    && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
                .Select(r => new { r.Rating, r.RatedAt, r.TitleId })
                .ToListAsync(ct);
            var histogram = Enumerable.Range(1, 10)
                .Select(value => new { rating = value, count = ratings.Count(r => r.Rating == value) })
                .ToList();

            var watched = await db.WatchedTitles
                .Where(w => w.AccountId == accountId)
                .Join(db.Titles, w => w.TitleId, t => t.Id, (w, t) => new
                {
                    w.Plays,
                    w.IsFullyWatched,
                    t.RuntimeMinutes,
                    t.MediaType,
                    t.AiredEpisodes,
                })
                .ToListAsync(ct);

            var hoursWatched = watched.Sum(w => w.MediaType == MediaType.Movie
                ? (w.RuntimeMinutes ?? 100) * Math.Max(1, w.Plays) / 60d
                : (w.RuntimeMinutes ?? 45) * Math.Max(1, w.Plays) / 60d);
            var shows = watched.Where(w => w.MediaType == MediaType.Show).ToList();
            var completionRate = shows.Count > 0 ? (double)shows.Count(s => s.IsFullyWatched) / shows.Count : 0d;

            // Drift: per-year share of the user's top-5 genres among that year's loved ratings.
            var ratedTitleIds = ratings.Select(r => r.TitleId).Distinct().ToList();
            var genresByTitle = await db.Titles
                .Where(t => ratedTitleIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Genres })
                .ToDictionaryAsync(t => t.Id, t => t.Genres, ct);
            var topFive = topGenres.Take(5).Select(g => g.genre).ToList();
            var drift = ratings
                .Where(r => r.Rating >= 7)
                .GroupBy(r => r.RatedAt.Year)
                .Where(g => g.Count() >= 10)
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var yearGenres = g.SelectMany(r => genresByTitle.GetValueOrDefault(r.TitleId, [])).ToList();
                    var total = Math.Max(1, yearGenres.Count);
                    return new
                    {
                        year = g.Key,
                        shares = topFive.ToDictionary(
                            genre => genre,
                            genre => Math.Round((double)yearGenres.Count(x => x == genre) / total, 3)),
                    };
                })
                .ToList();

            return Results.Ok(new
            {
                userMean = Math.Round(taste.UserMean, 2),
                contrarianScore = Math.Round(taste.OverallContrarianOffset, 2),
                ratingsCount = ratings.Count,
                hoursWatched = Math.Round(hoursWatched),
                titlesWatched = watched.Count,
                showCompletionRate = Math.Round(completionRate, 3),
                topGenres,
                eras,
                creators,
                topActors,
                histogram,
                drift,
            });
        });
    }

    /// <summary>
    /// The dislike history plus the profile it currently drives. Extracted from the route so it can
    /// be exercised against real Postgres — the ordering, the live-reaction predicate and the
    /// pagination are all SQL.
    /// </summary>
    internal static async Task<DislikesResponse> BuildDislikesAsync(
        IReelDbContext db, DislikeSignal dislikes, Guid accountId, int? offset, int? limit, CancellationToken ct)
    {
        var skip = Math.Max(0, offset ?? 0);
        var take = Math.Clamp(limit ?? DislikePageSize, 1, MaxDislikePageSize);

        var live = db.UserTitleReactions
            .Where(r => r.AccountId == accountId && r.RevokedAt == null
                && (r.Kind == ReactionKind.NotInterested || r.Kind == ReactionKind.Dropped));

        var total = await live.CountAsync(ct);
        var items = await live
            .OrderByDescending(r => r.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Join(db.Titles, r => r.TitleId, t => t.Id, (r, t) => new DislikeEntry(
                t.Id,
                t.MediaType.ToString(),
                t.TmdbId,
                t.Name,
                t.Year,
                t.PosterPath,
                r.Kind.ToString(),
                r.Reason == null ? null : r.Reason.ToString(),
                r.CreatedAt))
            .ToListAsync(ct);

        var profile = await dislikes.GetProfileAsync(accountId, ct);
        return new DislikesResponse(
            total,
            skip,
            take,
            skip + items.Count < total,
            items,
            new DislikeProfileSummary(
                // What the demotion is actually built from right now — a subset of `total`,
                // because the profile only looks back DislikeSignal.WindowDays.
                DislikeSignal.WindowDays,
                profile.Recent.Count,
                [.. profile.GenreWeights
                    .OrderByDescending(kv => kv.Value)
                    .Take(DislikeProfileTop)
                    .Select(kv => new DemotedGenre(kv.Key, Math.Round(kv.Value, 2)))],
                [.. profile.ToneWeights
                    .OrderByDescending(kv => kv.Value)
                    .Take(DislikeProfileTop)
                    .Select(kv => new DemotedTone(kv.Key, Math.Round(kv.Value, 2)))]));
    }

    /// <summary>One live dislike. <c>Reason</c> is null for a Dropped row (dropping has no reason)
    /// and for dismissals recorded before the card overlay offered a picker.</summary>
    internal sealed record DislikeEntry(
        Guid TitleId,
        string MediaType,
        long? TmdbId,
        string Name,
        int? Year,
        string? PosterPath,
        string Kind,
        string? Reason,
        DateTime CreatedAt);

    internal sealed record DemotedGenre(string Genre, double Weight);

    internal sealed record DemotedTone(string Tone, double Weight);

    /// <summary>What the demotion is built from right now — deliberately reported apart from the
    /// total, because the profile is windowed and recency-decayed and legitimately says less.</summary>
    internal sealed record DislikeProfileSummary(
        int WindowDays,
        int ActiveCount,
        IReadOnlyList<DemotedGenre> Genres,
        IReadOnlyList<DemotedTone> Tones);

    internal sealed record DislikesResponse(
        int Total,
        int Offset,
        int Limit,
        bool HasMore,
        IReadOnlyList<DislikeEntry> Items,
        DislikeProfileSummary Profile);
}
