using System.Text.Json;
using FrostAura.Reel.Application.Abstractions;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

public static class TitleEndpoints
{
    public static async Task<Title?> ResolveTitleAsync(IReelDbContext db, string mediaType, long tmdbId, CancellationToken ct)
    {
        var type = mediaType.ToLowerInvariant() switch
        {
            "movie" => MediaType.Movie,
            "show" or "tv" => MediaType.Show,
            _ => (MediaType?)null,
        };
        if (type is null)
        {
            return null;
        }

        return await db.Titles.FirstOrDefaultAsync(t => t.MediaType == type && t.TmdbId == tmdbId, ct);
    }

    public static void MapTitleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/titles").RequireAccount();

        // Cold-start / taste-sharpening: popular catalogue titles the user hasn't rated yet — a
        // grid to quickly rate from memory, bootstrapping (or sharpening) the model.
        group.MapGet("/rate-suggestions", async (IReelDbContext db, IAccountContext accountContext, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var candidates = await db.Titles
                .Where(t => t.TmdbId != null && t.PosterPath != null && t.TmdbVoteCount > 200
                    && !db.UserRatings.Any(r => r.AccountId == accountId && r.TitleId == t.Id)
                    // "Rate more" must not re-offer what the user already pushed away: a live
                    // (unrevoked) NotInterested or Dropped reaction excludes the title here too.
                    && !db.UserTitleReactions.Any(r => r.AccountId == accountId && r.TitleId == t.Id
                        && r.RevokedAt == null
                        && (r.Kind == ReactionKind.NotInterested || r.Kind == ReactionKind.Dropped)))
                .OrderByDescending(t => t.TmdbPopularity)
                .Take(30)
                .ToListAsync(ct);
            var suggestions = candidates.Select(t => new
            {
                titleId = t.Id,
                mediaType = t.MediaType.ToString(),
                tmdbId = t.TmdbId,
                name = t.Name,
                year = t.Year,
                posterPath = t.PosterPath,
                crowdRating = CrowdRating.For(t),
                crowdVotes = CrowdRating.VotesFor(t),
            }).ToList();
            return Results.Ok(suggestions);
        });

        group.MapGet("/{mediaType}/{tmdbId:long}", async (
            string mediaType, long tmdbId,
            IReelDbContext db, IAccountContext accountContext,
            ITmdbClient tmdb, CandidateGenerator candidateGenerator, TitleHydrator hydrator,
            ICatalogWorkCoordinator coordinator, OnDemandScorer scorer, FeatureVectorBuilder featureBuilder,
            DislikeSignal dislikes, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var title = await ResolveTitleAsync(db, mediaType, tmdbId, ct);

            // Unknown locally ≠ nonexistent: the global typeahead can point at any TMDB title, and
            // a bare 404 here would dead-end that navigation. Hydrate on demand instead — only a
            // genuine TMDB 404 (or an unparseable media type) still falls through to NotFound.
            title ??= await HydrateOnDemandAsync(db, tmdb, candidateGenerator, hydrator, coordinator, mediaType, tmdbId, ct);
            if (title is null)
            {
                return Results.NotFound();
            }

            var cast = await db.TitleCredits
                .Where(c => c.TitleId == title.Id)
                .Join(db.Persons, c => c.PersonId, p => p.Id, (c, p) => new
                {
                    personId = p.Id,
                    name = p.Name,
                    role = c.Role.ToString(),
                    character = c.CharacterName,
                    castOrder = c.CastOrder,
                    profilePath = p.ProfilePath,
                    userRating = db.UserPersonRatings
                        .Where(pr => pr.AccountId == accountId && pr.PersonId == p.Id)
                        .Select(pr => (short?)pr.Rating)
                        .FirstOrDefault(),
                })
                .ToListAsync(ct);

            var score = await ResolveScoreAsync(db, scorer, accountId, title.Id, ct);

            // The tap-through behind the contribution bars — who/what is driving the person and
            // era features, from the same verified-person data the why-sentences cite. Only built
            // when a prediction exists: the bars it annotates render under the prediction block.
            IReadOnlyList<ContributionDetails.Detail> contributionDetails = score is null
                ? []
                : await ContributionDetails.BuildAsync(
                    db, await featureBuilder.BuildTasteStateAsync(accountId, DateTime.UtcNow, ct), title, ct);

            // The serving-time dislike demotion behind this score (the PredictedRating already
            // reflects it — OnDemandScorer subtracted it). When it is big enough to notice, the
            // breakdown gets an honest synthetic negative bar + note so the lowered number
            // explains itself instead of silently disagreeing with the crowd rating.
            List<ContributionEntry> contributions = score is null
                ? []
                : JsonSerializer.Deserialize<List<ContributionEntry>>(score.ContributionsJson) ?? [];
            if (score is not null)
            {
                var profile = await dislikes.GetProfileAsync(accountId, ct);
                var demotion = (await dislikes.DemotionsForAsync(profile, [title.Id], ct))
                    .GetValueOrDefault(title.Id);
                if (demotion >= ContributionDetails.DislikeNoteThreshold)
                {
                    contributions.Add(new ContributionEntry(
                        ContributionDetails.DislikedSimilarityFeature, -Math.Round((decimal)demotion, 1)));
                    contributionDetails = [.. contributionDetails, ContributionDetails.DislikedSimilarity()];
                }
            }

            var watched = await db.WatchedTitles.FirstOrDefaultAsync(w => w.AccountId == accountId && w.TitleId == title.Id, ct);
            var rating = await db.UserRatings.FirstOrDefaultAsync(
                r => r.AccountId == accountId && r.TitleId == title.Id
                    && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show), ct);
            var reactions = await db.UserTitleReactions
                .Where(r => r.AccountId == accountId && r.TitleId == title.Id && r.RevokedAt == null)
                .Select(r => r.Kind.ToString())
                .ToListAsync(ct);

            return Results.Ok(new
            {
                titleId = title.Id,
                mediaType = title.MediaType.ToString(),
                tmdbId = title.TmdbId,
                name = title.Name,
                year = title.Year,
                overview = title.Overview,
                tagline = title.Tagline,
                runtimeMinutes = title.RuntimeMinutes,
                certification = title.Certification,
                genres = title.Genres,
                network = title.Network,
                status = title.Status,
                traktRating = title.TraktRating,
                traktVotes = title.TraktVotes,
                // The card-consistent community number (TMDB, then Trakt) alongside the raw Trakt
                // pair the detail page already exposed — the modal renders the same badge as the card.
                crowdRating = CrowdRating.For(title),
                crowdVotes = CrowdRating.VotesFor(title),
                posterPath = title.PosterPath,
                backdropPath = title.BackdropPath,
                trailerUrl = title.TrailerUrl,
                cast = cast.Where(c => c.role == "Actor").OrderBy(c => c.castOrder).Take(10),
                directors = cast.Where(c => c.role == "Director").Select(c => new { c.personId, c.name, c.profilePath, c.userRating }),
                writers = cast.Where(c => c.role == "Writer").Select(c => new { c.personId, c.name, c.profilePath, c.userRating }).Take(4),
                prediction = score is null ? null : new
                {
                    // Served ratings are uniformly 1dp at the response edge (stored precision is 2dp).
                    predictedRating = Math.Round(score.PredictedRating, 1),
                    contributions,
                    scoredAt = score.ScoredAt,
                },
                contributionDetails,
                userState = new
                {
                    isFullyWatched = watched?.IsFullyWatched ?? false,
                    plays = watched?.Plays ?? 0,
                    userRating = rating?.Rating,
                    savedForLater = reactions.Contains(nameof(ReactionKind.SaveForLater)),
                    notInterested = reactions.Contains(nameof(ReactionKind.NotInterested)),
                    dropped = reactions.Contains(nameof(ReactionKind.Dropped)),
                },
            });
        });
    }

    /// <summary>
    /// Fetches, upserts and hydrates a title Reel has never seen, so a global-typeahead navigation
    /// lands on a real detail page instead of a 404. Single-flighted per (mediaType, tmdbId): two
    /// concurrent opens of the same unknown title share one TMDB fetch + upsert, exactly like Ask
    /// Reel's discovery firehose. Returns null when TMDB itself 404s the id (or the media type is
    /// unparseable) — the endpoint then 404s honestly.
    /// </summary>
    internal static async Task<Title?> HydrateOnDemandAsync(
        IReelDbContext db, ITmdbClient tmdb, CandidateGenerator candidateGenerator,
        TitleHydrator hydrator, ICatalogWorkCoordinator coordinator,
        string mediaType, long tmdbId, CancellationToken ct)
    {
        var type = mediaType.ToLowerInvariant() switch
        {
            "movie" => MediaType.Movie,
            "show" or "tv" => MediaType.Show,
            _ => (MediaType?)null,
        };
        if (type is null)
        {
            return null;
        }

        // CancellationToken.None inside the factory mirrors LiveSearchExpansionService: a
        // disconnecting waiter must not starve a concurrent sharer, and the hydrated row is global
        // catalogue data that stays useful regardless of who asked first.
        return await coordinator.RunOnceAsync($"title:hydrate:{type}:{tmdbId}", async () =>
        {
            var details = type == MediaType.Movie
                ? await tmdb.GetMovieAsync(tmdbId, CancellationToken.None)
                : await tmdb.GetTvAsync(tmdbId, CancellationToken.None);
            if (details is null)
            {
                return null;
            }

            // Synthesize the list-row equivalent and push it through the SAME upsert path live
            // search uses (UpsertAndCollectIdsAsync → UpsertCandidatesAsync), so dedup by
            // (MediaType, TmdbId) and the ??=-only field semantics hold here too.
            var item = new TmdbListItem(
                tmdbId,
                type == MediaType.Movie,
                details.Name ?? string.Empty,
                details.PosterPath,
                details.BackdropPath,
                details.Overview,
                details.Popularity,
                details.VoteAverage,
                details.VoteCount,
                details.ReleasedAt,
                details.GenreIds ?? [],
                details.OriginalLanguage);
            try
            {
                await candidateGenerator.UpsertCandidatesAsync([item], CancellationToken.None);
            }
            catch (DbUpdateException)
            {
                // A concurrent process (another replica; the single-flight is process-local)
                // inserted the same (MediaType, TmdbId) first. Detach our duplicate and fall
                // through to the re-resolve — the row exists either way.
                if (db is DbContext ctx)
                {
                    foreach (var entry in ctx.ChangeTracker.Entries<Title>()
                                 .Where(e => e.State == EntityState.Added).ToList())
                    {
                        entry.State = EntityState.Detached;
                    }
                }
            }

            var title = await db.Titles.FirstOrDefaultAsync(
                t => t.MediaType == type && t.TmdbId == tmdbId, CancellationToken.None);
            if (title is null)
            {
                return null;
            }

            if (title.LastMetadataRefreshAt is null)
            {
                await hydrator.HydrateBatchAsync([title], CancellationToken.None);
                try
                {
                    await db.SaveChangesAsync(CancellationToken.None);
                }
                catch (DbUpdateException)
                {
                    // Concurrent hydration conflict (cross-process) — same tolerance as live
                    // search's HydrateAsync: the other writer's data is equally fresh.
                }
            }

            return title;
        });
    }

    /// <summary>
    /// The persisted prediction for the Active artifact — scoring on demand when none exists yet,
    /// so a freshly-hydrated (or simply never-scored) title still answers with a predictedRating
    /// instead of a silent null. Re-reads after scoring because <see cref="OnDemandScorer"/>
    /// upserts the row; no Active model ⇒ no row ⇒ null, which the response already tolerates.
    /// </summary>
    internal static async Task<TitleScore?> ResolveScoreAsync(
        IReelDbContext db, OnDemandScorer scorer, Guid accountId, Guid titleId, CancellationToken ct)
    {
        var score = await ActiveScoreAsync(db, accountId, titleId, ct);
        if (score is not null)
        {
            return score;
        }

        await scorer.ScoreAsync(accountId, [titleId], DateTime.UtcNow, ct);
        return await ActiveScoreAsync(db, accountId, titleId, ct);
    }

    /// <summary>One stored contribution row — matches the shape BuildFeed persists in ContributionsJson
    /// (lowercase property names, so serialize/deserialize round-trips without options).</summary>
    private sealed record ContributionEntry(string feature, decimal value);

    private static Task<TitleScore?> ActiveScoreAsync(IReelDbContext db, Guid accountId, Guid titleId, CancellationToken ct) =>
        db.TitleScores
            .Where(s => s.AccountId == accountId && s.TitleId == titleId)
            .Join(db.ModelArtifacts.Where(a => a.Status == ArtifactStatus.Active),
                s => s.ModelArtifactId, a => a.Id, (s, a) => s)
            .FirstOrDefaultAsync(ct);
}
