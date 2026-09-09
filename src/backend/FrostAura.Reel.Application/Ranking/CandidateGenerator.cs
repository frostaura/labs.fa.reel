using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// Widens the catalog beyond the user's own history: TMDB discover for their top genres,
/// this week's trending, and fresh releases — upserted as TMDB-only titles (Trakt ids resolve
/// lazily at write-back). The pool is global catalog data; eligibility filtering happens at
/// feed-build time through the EligibilityQueryBuilder.
/// </summary>
public class CandidateGenerator(
    IReelDbContext db,
    ITmdbClient tmdb,
    ILogger<CandidateGenerator> logger)
{
    private const int TopGenreCount = 4;
    private const int RecommendationSeeds = 5;

    /// <summary>
    /// Widens the pool for one account and reports what it found: how many TMDB items came back
    /// across all arms, and how many of those the catalogue had never seen. The second number is the
    /// direct instrument for "the picks get weaker every round" — it was computed on every build and
    /// only ever logged.
    ///
    /// <paramref name="accountId"/> is load-bearing, not bookkeeping: the account's ExcludeGenre
    /// rules have to shape the discovery calls themselves. A banned genre can sit high in the taste
    /// map — Dean bans 8 of 25 including two of his highest-COUNT genres — so up to half the
    /// per-build discover budget was being spent fetching titles the eligibility layer is
    /// guaranteed to reject, every build, for ever.
    /// </summary>
    public async Task<PoolRefreshResult> RefreshPoolAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, string region, CancellationToken ct)
    {
        // Read the ban list first: a genre the user has excluded is not a "top genre" of theirs in
        // any sense the pool cares about, whatever their historical ratings say.
        var excludedGenres = (await db.ContentFilters
                .Where(f => f.AccountId == accountId && f.Kind == FilterKind.ExcludeGenre)
                .Select(f => f.Value)
                .ToListAsync(ct))
            .Select(v => v.Trim().ToLowerInvariant())
            .Where(v => v.Length > 0)
            .ToHashSet();

        var topGenres = taste.GenreRatings
            .Where(kv => !excludedGenres.Contains(kv.Key))
            .Select(kv => new
            {
                Genre = kv.Key,
                Score = (double)TasteMath.ShrunkenMean(kv.Value.Select(r => r.Rating).ToList(), taste.UserMean)
                    * Math.Log(1 + kv.Value.Count),
            })
            .OrderByDescending(g => g.Score)
            .Take(TopGenreCount)
            .Select(g => g.Genre)
            .ToList();

        var batches = new List<IReadOnlyList<TmdbListItem>>();

        foreach (var genre in topGenres)
        {
            if (!TmdbGenres.SlugToIds.TryGetValue(genre, out var ids))
            {
                // Not silent: an unmapped slug costs this build a whole discover arm, and the only
                // evidence used to be a pool that came back thinner than it should have. Phase 1
                // replaces the skip with a keyword/concept fallback; until then, say which slug.
                logger.LogInformation(
                    "Candidate pool: no TMDB genre id for slug '{Genre}' — that discovery arm is skipped this build.",
                    genre);
                continue;
            }

            if (ids.Movie is { } movieGenre)
            {
                batches.Add(await tmdb.DiscoverAsync(movies: true, movieGenre, region, releasedAfter: null, page: 1, ct));
            }

            if (ids.Tv is { } tvGenre)
            {
                batches.Add(await tmdb.DiscoverAsync(movies: false, tvGenre, region: null, releasedAfter: null, page: 1, ct));
            }
        }

        batches.Add(await tmdb.GetTrendingAsync(movies: true, ct));
        batches.Add(await tmdb.GetTrendingAsync(movies: false, ct));
        batches.Add(await tmdb.DiscoverAsync(movies: true, genreId: null, region, DateTime.UtcNow.AddDays(-90), page: 1, ct));

        // Collaborative signal: TMDB recommendations for the user's most-loved titles — "people
        // who liked your favourites also liked these". Real CF from day one, no cross-tenant data
        // needed (our own matrix-factorisation CF waits until we have ~200 tenants to be non-noise).
        // TopLovedTitleIds arrives ordered (rating desc, then RatedAt desc), and that order is the
        // whole point of a "most-loved" seed. The query had a Take with no ORDER BY, so Postgres was
        // free to hand back any five rows it liked — a different five between builds, and rarely the
        // actual top five. Re-impose the taste order client-side, then take.
        var lovedRank = taste.TopLovedTitleIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);
        var lovedTitles = (await db.Titles
                .Where(t => taste.TopLovedTitleIds.Contains(t.Id) && t.TmdbId != null)
                .Select(t => new { t.Id, t.MediaType, TmdbId = t.TmdbId!.Value })
                .ToListAsync(ct))
            .OrderBy(t => lovedRank[t.Id])
            .Take(RecommendationSeeds)
            .ToList();
        foreach (var loved in lovedTitles)
        {
            batches.Add(await tmdb.GetRecommendationsAsync(loved.MediaType == MediaType.Movie, loved.TmdbId, ct));
        }

        var items = batches.SelectMany(b => b)
            .GroupBy(i => (i.IsMovie, i.Id))
            .Select(g => g.First())
            .ToList();

        var upserted = await UpsertCandidatesAsync(items, ct);
        logger.LogInformation("Candidate pool refreshed: {Items} TMDB items ({Upserted} new) across genres [{Genres}].",
            items.Count, upserted, string.Join(", ", topGenres));
        return new PoolRefreshResult(items.Count, upserted);
    }

    /// <summary>What one pool refresh produced: items seen, and titles genuinely new to the catalogue.</summary>
    public readonly record struct PoolRefreshResult(int Discovered, int NewlyUpserted);

    /// <summary>
    /// Upserts TMDB list items as global, TMDB-only <see cref="Title"/> rows (dedup by
    /// (MediaType, TmdbId); TraktId resolves lazily). Reused by the feed pool and live Ask Reel
    /// expansion. Summary-only — credits/keywords/embeddings come later via hydrate/enrich.
    /// Returns the count of newly-created rows.
    /// </summary>
    public async Task<int> UpsertCandidatesAsync(IReadOnlyList<TmdbListItem> items, CancellationToken ct)
    {
        var movieIds = items.Where(i => i.IsMovie).Select(i => i.Id).ToList();
        var tvIds = items.Where(i => !i.IsMovie).Select(i => i.Id).ToList();

        var existing = await db.Titles
            .Where(t => t.TmdbId != null
                && ((t.MediaType == MediaType.Movie && movieIds.Contains(t.TmdbId.Value))
                    || (t.MediaType == MediaType.Show && tvIds.Contains(t.TmdbId.Value))))
            .ToDictionaryAsync(t => (t.MediaType, TmdbId: t.TmdbId!.Value), ct);

        var added = 0;
        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            var mediaType = item.IsMovie ? MediaType.Movie : MediaType.Show;
            if (!existing.TryGetValue((mediaType, item.Id), out var title))
            {
                title = new Title
                {
                    Id = Guid.NewGuid(),
                    MediaType = mediaType,
                    TmdbId = item.Id,
                    TraktSlug = string.Empty,
                    CreatedAt = now,
                };
                existing[(mediaType, item.Id)] = title;
                db.Titles.Add(title);
                added++;
            }

            title.Name = item.Name;
            title.PosterPath ??= item.PosterPath;
            title.BackdropPath ??= item.BackdropPath;
            title.Overview ??= item.Overview;
            // Carry the discovery language so an English candidate isn't dropped by the English-only
            // eligibility default before full hydration runs. ??= so a hydrated value is never clobbered.
            title.Language ??= string.IsNullOrWhiteSpace(item.OriginalLanguage) ? null : item.OriginalLanguage.ToLowerInvariant();
            title.TmdbPopularity = item.Popularity;
            title.TmdbVoteAverage = item.VoteAverage;
            title.TmdbVoteCount = item.VoteCount;
            if (item.ReleasedAt is { } released)
            {
                if (item.IsMovie)
                {
                    title.ReleasedAt ??= released;
                }
                else
                {
                    title.FirstAiredAt ??= released;
                }

                title.Year ??= released.Year;
            }

            if (title.Genres.Length == 0)
            {
                title.Genres = TmdbGenres.MapToSlugs(item.IsMovie, item.GenreIds);
            }
        }

        await db.SaveChangesAsync(ct);
        return added;
    }
}
