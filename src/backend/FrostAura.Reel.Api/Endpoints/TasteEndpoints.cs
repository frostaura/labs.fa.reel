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

    /// <summary>How many genres the fingerprint (and, from its head, the drift chart) carries.</summary>
    internal const int FingerprintGenreCount = 8;

    /// <summary>Ratings a genre needs before its shrunken mean is worth a bar.</summary>
    internal const int FingerprintMinRatings = 10;

    /// <summary>One genre row of the DNA fingerprint.</summary>
    internal sealed record GenreFingerprintRow(string Genre, decimal Affinity, int Count);

    /// <summary>
    /// The genres the DNA page charts as "each genre you rate a lot": the
    /// <see cref="FingerprintGenreCount"/> genres with the MOST ratings (at least
    /// <see cref="FingerprintMinRatings"/> each), most-rated first, ties by name. Volume alone picks
    /// the rows; affinity is only what each bar shows.
    ///
    /// <para>The rows were once ranked by affinity × log(1 + count) — "loved-ness" — which can never
    /// admit a genre rated often and liked less than average: on the founder's account horror (363
    /// ratings, the widest gap at −0.5) and mystery (339) were both missing while fantasy (270) was
    /// shown, on a diverging chart whose whole job is to show the below-average side
    /// (HREV4-DESK-03). The drift chart reads the first five, so it follows the same order.</para>
    /// </summary>
    internal static List<GenreFingerprintRow> FingerprintGenres(
        IReadOnlyDictionary<string, List<(short Rating, DateTime RatedAt)>> genreRatings, decimal userMean) =>
        [.. genreRatings
            .Where(kv => kv.Value.Count >= FingerprintMinRatings)
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(FingerprintGenreCount)
            .Select(kv => new GenreFingerprintRow(
                kv.Key,
                Math.Round(TasteMath.ShrunkenMean(kv.Value.Select(r => r.Rating).ToList(), userMean), 2),
                kv.Value.Count))];

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
            IReelDbContext db,
            IAccountContext accountContext,
            FeatureVectorBuilder featureBuilder,
            TasteClusterProvider tasteClusters,
            CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var now = DateTime.UtcNow;
            var taste = await featureBuilder.BuildTasteStateAsync(accountId, now, ct);

            var topGenres = FingerprintGenres(taste.GenreRatings, taste.UserMean)
                .Select(g => new { genre = g.Genre, affinity = g.Affinity, count = g.Count })
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

            // The pockets of taste retrieval actually splits its budget across, made visible. This is
            // the only surface on which the multi-centroid work is legible to the person it is about:
            // genres and eras describe a library by its labels, and these describe it by what the
            // titles are LIKE — the corner no genre names. Share, not weight, because the absolute
            // number is an internal scale and a percentage is the only honest reading of it.
            var partition = await tasteClusters.GetAsync(accountId, now, ct);
            var clusterWeight = partition.Sum(c => c.Weight);
            var clusterExemplarIds = partition.SelectMany(c => c.ExemplarTitleIds).Distinct().ToList();
            var clusterTitles = clusterExemplarIds.Count == 0
                ? []
                : await db.Titles
                    .Where(t => clusterExemplarIds.Contains(t.Id))
                    .Select(t => new { t.Id, t.Name, t.Year, t.PosterPath })
                    .ToDictionaryAsync(t => t.Id, ct);
            var clusters = clusterWeight <= 0d
                ? []
                : partition.Select(c => new
                {
                    ordinal = c.Ordinal,
                    share = Math.Round(c.Weight / clusterWeight, 3),
                    titleCount = c.MemberCount,
                    exemplars = c.ExemplarTitleIds
                        .Where(clusterTitles.ContainsKey)
                        .Select(id => new
                        {
                            name = clusterTitles[id].Name,
                            year = clusterTitles[id].Year,
                            posterPath = clusterTitles[id].PosterPath,
                        })
                        .ToList(),
                })
                .Where(c => c.exemplars.Count > 0)
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
                clusters,
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
