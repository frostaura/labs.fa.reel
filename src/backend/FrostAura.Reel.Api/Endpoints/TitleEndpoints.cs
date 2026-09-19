using System.Text.Json;
using FrostAura.Reel.Application.Abstractions;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
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

    /// <summary>How many titles the rate grid asks for at once.</summary>
    private const int RateSuggestionCount = 30;

    /// <summary>
    /// How many rows each half materialises before the in-memory maturity gate runs, so a
    /// restricted profile still lands a full grid.
    /// </summary>
    private const int OverFetch = 120;

    /// <summary>The popularity fallback's "enough people have seen it to rate from memory" gate.</summary>
    private const int PopularFallbackMinVotes = 200;

    /// <summary>Which half of the rate grid a suggestion came from — the page labels them differently.</summary>
    internal static class RateSuggestionSources
    {
        /// <summary>Watched and never rated: a memory, not a guess.</summary>
        public const string Watched = "watched";

        /// <summary>The popularity pool, for an account with nothing watched-but-unrated left.</summary>
        public const string Popular = "popular";
    }

    /// <summary>
    /// One card on the rate grid. <c>Source</c> says which half it came from, because the page
    /// headlines the two differently: a watched title is a memory, a popular one is a guess.
    /// Serialized camelCase by the Web defaults, matching every other payload here.
    /// </summary>
    internal sealed record RateSuggestion(
        Guid TitleId,
        string MediaType,
        long? TmdbId,
        string Name,
        int? Year,
        string? PosterPath,
        decimal? CrowdRating,
        int? CrowdVotes,
        string Source);

    private static RateSuggestion ToRateSuggestion(Title t, string source) => new(
        t.Id,
        t.MediaType.ToString(),
        t.TmdbId,
        t.Name,
        t.Year,
        t.PosterPath,
        Domain.Catalog.CrowdRating.For(t),
        Domain.Catalog.CrowdRating.VotesFor(t),
        source);

    public static void MapTitleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/titles").RequireAccount();

        // The label engine. It used to be the top 30 catalogue titles by TMDB popularity that the
        // user had not rated — no content filters (so it could offer titles from genres the account
        // has excluded), no uncertainty targeting, and, worst of all, NO preference for the titles
        // the user has already WATCHED and never scored. Those are the highest-information,
        // zero-recall-burden labels in the database (~1,300 of them for the founder: 3,353 watched
        // against 2,044 rated) and the one screen built to collect labels could not see them.
        //
        // Now: watched-but-unrated FIRST, ordered by the cheapest honest uncertainty proxy we have
        // — how close the model's prediction sits to the user's own mean, because a prediction at
        // the mean is the model declining to commit — then the popularity pool as a fallback for a
        // cold account. Both halves respect the account's content rules.
        group.MapGet("/rate-suggestions", async (
                IReelDbContext db, IAccountContext accountContext, EligibilityQueryBuilder eligibility,
                CancellationToken ct) =>
            Results.Ok(await BuildRateSuggestionsAsync(db, eligibility, accountContext.AccountId!.Value, ct)));

        group.MapGet("/{mediaType}/{tmdbId:long}", async (
            string mediaType, long tmdbId,
            IReelDbContext db, IAccountContext accountContext,
            ITmdbClient tmdb, CandidateGenerator candidateGenerator, TitleHydrator hydrator,
            ICatalogWorkCoordinator coordinator, OnDemandScorer scorer, FeatureVectorBuilder featureBuilder,
            DislikeSignal dislikes, Application.Lens.LensService lens, TasteFactsBuilder tasteFacts,
            CancellationToken ct) =>
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
            // The taste state is built once and shared with the why-line fallback below.
            var taste = score is null
                ? null
                : await featureBuilder.BuildTasteStateAsync(accountId, DateTime.UtcNow, ct);
            IReadOnlyList<ContributionDetails.Detail> contributionDetails = taste is null
                ? []
                : await ContributionDetails.BuildAsync(db, taste, title, ct);

            // The one personalised sentence used to VANISH on tap: cards render it, the modal
            // showed the raw TMDB overview instead. The persisted line from the account's Active
            // snapshot is the authoritative one — it is the exact text this user was shown — and
            // when the title is not in the current build the same deterministic templater every
            // card goes through rebuilds it from the persisted contributions. No score, no
            // contributions, nothing verifiable to cite ⇒ null, and the modal renders nothing.
            var whyThis = await SnapshotWhyAsync(db, accountId, title.Id, ct)
                ?? await TemplatedWhyAsync(db, taste, title, score, ct);

            // "Through your lens", where copy has been written. READ ONLY on this path: the modal
            // renders instantly from what exists, and the client asks the dedicated /lens endpoint
            // for a generation if it wants one. That split is what keeps opening a title fast —
            // the whole page must never wait on a provider.
            var lensView = taste is null
                ? null
                : LensEndpoints.ToResponse(await lens.ResolveAsync(
                    accountId,
                    title.Id,
                    await tasteFacts.CurrentHashAsync(accountId, DateTime.UtcNow, ct),
                    ct));

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
                    contributionDetails = [.. contributionDetails, ContributionDetails.DislikedSimilarity(title.Genres, profile)];
                }

                // The account-level taste prior behind where this title RANKS — as opposed to the
                // demotion above, which is already inside the number. Both are honest and they are
                // different claims, so they get different bars: the prediction is what the model
                // believes about the title, the prior is what the account's own history says about
                // the kind of thing it is.
                if (taste is not null)
                {
                    var (priorBars, priorDetails) = ContributionDetails.TastePriorBreakdown(
                        TastePrior.From(taste), title);
                    contributions.AddRange(priorBars.Select(b => new ContributionEntry(b.Feature, b.Value)));
                    contributionDetails = [.. contributionDetails, .. priorDetails];
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
                whyThis,
                lens = lensView,
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
    /// The rate-grid query, extracted so it can be exercised against real Postgres — the only place
    /// its content-filter and ordering SQL can be trusted. See the route above for the WHY.
    /// </summary>
    internal static async Task<List<RateSuggestion>> BuildRateSuggestionsAsync(
        IReelDbContext db, EligibilityQueryBuilder eligibility, Guid accountId, CancellationToken ct)
    {
        var ceiling = await eligibility.MaturityCeilingAsync(accountId, ct);

        // The user's own mean, over whole-title ratings only (season/episode rows would skew a
        // number that is about how this person scores things). Null on a cold account, which is
        // exactly when the uncertainty proxy is meaningless — that case orders by recency.
        var userMean = await db.UserRatings
            .Where(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .Select(r => (decimal?)r.Rating)
            .AverageAsync(ct);

        // Content rules — not EligibleTitles: this surface is the opposite of discovery. It
        // WANTS titles with a watch footprint. Rated titles and live (unrevoked) NotInterested /
        // Dropped reactions are still excluded: "rate more" must not re-offer what the user
        // already scored, or already pushed away.
        var rateable = eligibility.ContentFilteredTitles(accountId)
            .Where(t => t.TmdbId != null && t.PosterPath != null
                && !db.UserRatings.Any(r => r.AccountId == accountId && r.TitleId == t.Id)
                && !db.UserTitleReactions.Any(r => r.AccountId == accountId && r.TitleId == t.Id
                    && r.RevokedAt == null
                    && (r.Kind == ReactionKind.NotInterested || r.Kind == ReactionKind.Dropped)));

        // Scalars only — id, when it was last watched, and the Active artifact's prediction. The
        // ordering happens in memory over these three columns rather than in SQL: EF cannot order a
        // projection whose members are themselves correlated subqueries, and the watched-unrated
        // set is a few thousand rows of (guid, timestamp, numeric) at worst.
        var watchedRows = await rateable
            .Where(t => db.WatchedTitles.Any(w => w.AccountId == accountId && w.TitleId == t.Id))
            .Select(t => new
            {
                TitleId = t.Id,
                LastWatchedAt = db.WatchedTitles
                    .Where(w => w.AccountId == accountId && w.TitleId == t.Id)
                    .Select(w => w.LastWatchedAt)
                    .FirstOrDefault(),
                Predicted = db.TitleScores
                    .Where(s => s.AccountId == accountId && s.TitleId == t.Id)
                    .Join(
                        db.ModelArtifacts.Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active),
                        s => s.ModelArtifactId, a => a.Id, (s, a) => (decimal?)s.PredictedRating)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        // Uncertainty first: the closer a prediction sits to the user's own mean, the less the
        // model has committed, and the more a real rating tells it. Unscored titles sort LAST
        // rather than first — a missing prediction is no evidence of uncertainty, and putting them
        // ahead of measured near-mean picks would be a guess dressed as a ranking. With nothing
        // rated yet there is no mean and no proxy, so the whole half falls back to recency.
        var orderedIds = watchedRows
            .OrderBy(x => userMean is null || x.Predicted is null ? 1 : 0)
            .ThenBy(x => userMean is { } m && x.Predicted is { } p ? Math.Abs(p - m) : 0m)
            .ThenByDescending(x => x.LastWatchedAt ?? DateTime.MinValue)
            .Select(x => x.TitleId)
            // Over-fetch: the maturity ceiling cannot order in SQL (certification ordinals are app
            // logic), so it is applied in memory after materialising — the same shape every search
            // surface uses.
            .Take(OverFetch)
            .ToList();

        var watchedById = await db.Titles
            .Where(t => orderedIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);
        var watched = EligibilityQueryBuilder
            .WithinMaturity(orderedIds.Where(watchedById.ContainsKey).Select(id => watchedById[id]), ceiling)
            .Take(RateSuggestionCount)
            .ToList();
        var watchedIds = watched.Select(t => t.Id).ToHashSet();

        var suggestions = watched
            .Select(t => ToRateSuggestion(t, RateSuggestionSources.Watched))
            .ToList();

        // Fallback for a cold (or thoroughly-rated) account: the popularity pool, now filtered
        // by the same content rules. Only titles with no watch footprint — the watched ones are
        // already above, and offering them twice would be the same ask asked worse.
        if (suggestions.Count < RateSuggestionCount)
        {
            var popular = await rateable
                .Where(t => t.TmdbVoteCount > PopularFallbackMinVotes
                    && !watchedIds.Contains(t.Id)
                    && !db.WatchedTitles.Any(w => w.AccountId == accountId && w.TitleId == t.Id))
                .OrderByDescending(t => t.TmdbPopularity)
                .Take(OverFetch)
                .ToListAsync(ct);

            suggestions.AddRange(EligibilityQueryBuilder
                .WithinMaturity(popular, ceiling)
                .Take(RateSuggestionCount - suggestions.Count)
                .Select(t => ToRateSuggestion(t, RateSuggestionSources.Popular)));
        }

        return suggestions;
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
    /// The why-line this account was actually shown for this title, read from the Active feed
    /// snapshot. Preferred over any rebuild: it is the sentence on the card the user tapped, so the
    /// modal cannot quietly disagree with the rail it opened from. Null when the title is not in
    /// the current build (the shortlist is a fraction of the eligible pool).
    /// </summary>
    internal static async Task<string?> SnapshotWhyAsync(
        IReelDbContext db, Guid accountId, Guid titleId, CancellationToken ct)
    {
        var why = await db.FeedItems
            .Where(i => i.AccountId == accountId && i.TitleId == titleId)
            .Join(
                db.FeedSnapshots.Where(s => s.AccountId == accountId && s.Status == SnapshotStatus.Active),
                i => i.FeedSnapshotId, s => s.Id, (i, s) => i.WhyThisSentence)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(why) ? null : why;
    }

    /// <summary>
    /// The templater fallback for a title outside the current build. Cheap by construction: the
    /// taste state is already built for the contribution bars, the contributions are already
    /// persisted on the score, and the two extra reads are single-title lookups. Returns null
    /// rather than the generic boilerplate — a modal that has nothing verifiable to say says
    /// nothing, exactly as the feed's once-per-response guard intends.
    /// </summary>
    internal static async Task<string?> TemplatedWhyAsync(
        IReelDbContext db, FeatureVectorBuilder.TasteState? taste, Title title, TitleScore? score, CancellationToken ct)
    {
        if (taste is null || score is null)
        {
            return null;
        }

        var contributions = ParseContributions(score.ContributionsJson);
        if (contributions.Length == 0)
        {
            return null;
        }

        var personsByTitle = VerifiedPeople.BestByRole(
            await VerifiedPeople.ForTitlesAsync(db, taste, [title.Id], ct));
        var tone = await db.TitleAttributes
            .Where(a => a.TitleId == title.Id && a.Tone != null)
            .Select(a => a.Tone)
            .FirstOrDefaultAsync(ct);

        var sentence = ExplanationTemplater.BuildSentence(new ExplanationTemplater.Context(
            title,
            contributions,
            personsByTitle.GetValueOrDefault(title.Id) ?? ExplanationTemplater.NoPersons,
            title.Genres.Where(taste.GenreRatings.ContainsKey).ToHashSet(),
            AnchorName: null,
            Tone: tone));

        // The generic fallback is not personalisation — it is the templater admitting it found
        // nothing. Never worth pushing above the synopsis.
        return sentence == ExplanationTemplater.GenericFallback ? null : sentence;
    }

    /// <summary>Persisted ContributionsJson → the templater's (feature, contribution) pairs.</summary>
    private static (string Feature, float Contribution)[] ParseContributions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray()
                .Select(e => (
                    Feature: e.GetProperty("feature").GetString() ?? string.Empty,
                    Contribution: e.GetProperty("value").GetSingle()))
                .Where(x => x.Feature.Length > 0)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
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
