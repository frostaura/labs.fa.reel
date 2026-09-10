using System.Text.Json;
using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Api.Endpoints;

public static class FeedEndpoints
{
    public record CardProvider(string Name, string? LogoPath);

    /// <summary>
    /// The four extracted scalars the tonight-mood chips re-sort over, 0–1. Sent WITH the card so
    /// the re-sort is instant and entirely client-side: the chips reorder the snapshot the browser
    /// already has, they never trigger a rebuild — tonight's picks are a contract, and a mood is a
    /// way of reading them, not a reason to deal new ones.
    ///
    /// <para>Null on a title that has not been enriched, which is honest: a chip should decline to
    /// rank what it cannot see rather than treat missing data as a neutral 0.5 and shuffle it into
    /// the middle of every mood.</para>
    /// </summary>
    public record CardTexture(decimal Darkness, decimal Pacing, decimal Complexity, decimal Humor);

    /// <summary>
    /// The card contract. <c>PredictedRating</c> is "for you"; <c>CrowdRating</c> / <c>CrowdVotes</c>
    /// are the community number it is compared against, resolved once in
    /// <see cref="Domain.Catalog.CrowdRating"/> (TMDB, then Trakt, then null) so every surface agrees.
    /// </summary>
    /// <param name="Confidence">
    /// Hero cards only (null everywhere else): <c>"confident"</c> when the pick cleared the hero bar
    /// on its own merits, <c>"reaching"</c> when it is on the headline row only because nothing did.
    /// Derived at serve time from the persisted prediction — the relaxation is a property of the
    /// number, so it needs no column and no migration.
    /// </param>
    /// <param name="FloorUsed">The bar that card was measured against — <see cref="HeroConfidence.BarFor"/>.</param>
    /// <param name="Hook">
    /// The lens's one-line reason to press play tonight, where copy has been written for this
    /// account and title. Null everywhere it has not — a card must render identically well without
    /// it, because the budget, the provider and the guards can each withhold it.
    /// </param>
    /// <param name="LensClause">The first clause of the lens, for the card's two-line why.</param>
    /// <param name="Mood">
    /// The lens's closed-vocabulary mood, the strongest signal the chips re-sort over — but only
    /// present where copy has been written, which is why <paramref name="Texture"/> rides alongside
    /// it as the signal every enriched title has.
    /// </param>
    /// <param name="Texture">The extracted scalars the chips fall back to. See <see cref="CardTexture"/>.</param>
    public record FeedCard(
        Guid TitleId, string MediaType, long? TmdbId, string Name, int? Year, int? RuntimeMinutes,
        string? PosterPath, string? BackdropPath, string[] Genres,
        decimal PredictedRating, decimal? CrowdRating, int? CrowdVotes,
        string WhyThis, bool IsReleased, IReadOnlyList<CardProvider> Providers,
        // Optional and trailing on purpose: every other producer of this record — the digest, the
        // paginated lane — is serving a non-hero surface and has no bar to report.
        string? Confidence = null, decimal? FloorUsed = null,
        string? Hook = null, string? LensClause = null, string? Mood = null, CardTexture? Texture = null);

    public static void MapFeedEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/feed").RequireAccount();

        group.MapGet("/", async (IReelDbContext db, EligibilityQueryBuilder eligibility, FeatureVectorBuilder featureBuilder, ExposureLog exposures, LensService lens, TasteFactsBuilder tasteFacts, IMemoryCache cache, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            var snapshot = await db.FeedSnapshots
                .Where(s => s.AccountId == account.Id && s.Status == SnapshotStatus.Active)
                .FirstOrDefaultAsync(ct);
            if (snapshot is null)
            {
                return Results.Ok(new
                {
                    generatedAt = (DateTime?)null,
                    hero = Array.Empty<object>(),
                    rows = Array.Empty<object>(),
                    degradedReason = (string?)null,
                });
            }

            var items = await db.FeedItems
                .Where(i => i.FeedSnapshotId == snapshot.Id)
                .Join(db.Titles, i => i.TitleId, t => t.Id, (i, t) => new { Item = i, Title = t })
                .OrderBy(x => x.Item.Row).ThenBy(x => x.Item.Rank)
                .ToListAsync(ct);

            // The snapshot is materialized, so an interaction made since the build can leave a title
            // in the feed that no longer belongs there. Drop anything that has since picked up an
            // interaction footprint — marked seen (watched or rated), saved for later (a decision
            // already made; it lives on Saved), not-interested, or dropped — without waiting for a
            // rebuild. The rule is READ FROM EligibilityQueryBuilder rather than restated here: this
            // used to be a hand-copied union that omitted Dropped, which is precisely why a dropped
            // title kept its slot on tonight's picks (founder, 2026-08-07).
            var snapshotTitleIds = items.Select(x => x.Title.Id).ToList();
            // The cards this request backfills live. They are rendered and have NEVER existed as
            // FeedItem rows, so nothing else in the system records them — and they are precisely
            // the "next best down the list" picks the decay complaint is about. Collected here and
            // logged once, after the payload is settled, rather than mid-refill: the refill loop is
            // the hottest region of this file for concurrent edits, and an append to a list is the
            // smallest footprint that still captures every refilled card.
            var refilled = new List<ExposureCard>();
            var excludedTitleIds = await eligibility.InteractedTitleIdsAsync(account.Id, snapshotTitleIds, ct);
            if (excludedTitleIds.Count > 0)
            {
                // Rows shrink when a since-interacted title is dropped, leaving gaps until the next
                // rebuild. Refill each row in-kind from the next-best titles this snapshot's model
                // already scored (persisted TitleScores for account + active artifact) that are still
                // eligible and not already on the feed — so the hero/rows keep their built length
                // between rebuilds. Read-only + cheap: one indexed TitleScore read, capped at the
                // number of slots actually lost.
                var removedByRow = items
                    .Where(x => excludedTitleIds.Contains(x.Title.Id))
                    .GroupBy(x => (x.Item.Row, x.Item.AnchorTitleId))
                    .ToDictionary(g => g.Key, g => g.Count());
                items = items.Where(x => !excludedTitleIds.Contains(x.Title.Id)).ToList();

                var needed = removedByRow.Values.Sum();
                if (needed > 0)
                {
                    // Everything still on the feed or just dropped is off-limits for the refill.
                    var onFeed = items.Select(x => x.Title.Id).Concat(excludedTitleIds).ToHashSet();
                    var eligibleIds = eligibility.EligibleTitles(account.Id).Select(t => t.Id);
                    // Over-fetch (2× + headroom) so the in-memory maturity gate can drop over-ceiling
                    // titles and still leave enough survivors to fill every lost slot. Renderable-card
                    // gate (poster + tmdbId) mirrors the paginator so a backfill never floats a blank.
                    var candidates = await (
                        from s in db.TitleScores
                        join t in db.Titles on s.TitleId equals t.Id
                        where s.AccountId == account.Id && s.ModelArtifactId == snapshot.ModelArtifactId
                            && t.TmdbId != null && t.PosterPath != null
                            && !onFeed.Contains(s.TitleId) && eligibleIds.Contains(s.TitleId)
                        orderby s.PredictedRating descending
                        select new { s.PredictedRating, s.ContributionsJson, Title = t })
                        .Take((needed * 2) + 5)
                        .ToListAsync(ct);

                    var pool = candidates
                        .Where(c => EligibilityQueryBuilder.PassesMaturity(c.Title, account.Settings.MaturityCeiling))
                        .ToList();

                    // A backfilled card's why goes through the SAME deterministic templater as
                    // every built card — its persisted contributions plus the verified inputs the
                    // paginator uses (role-matched rated people, rated genres, closed-vocab tone) —
                    // never a hard-coded generic that repeats down the page.
                    var backfillContexts = new Dictionary<Guid, ExplanationTemplater.Context>();
                    if (pool.Count > 0)
                    {
                        // Same 2-minute cache entry the paginator's rails share, so this read
                        // costs a taste-state build at most once per browsing burst.
                        var taste = await cache.GetOrCreateAsync($"feedtaste:{account.Id}", async entry =>
                        {
                            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
                            return await featureBuilder.BuildTasteStateAsync(account.Id, DateTime.UtcNow, ct);
                        }) ?? await featureBuilder.BuildTasteStateAsync(account.Id, DateTime.UtcNow, ct);

                        var poolIds = pool.Select(c => c.Title.Id).ToList();
                        var personsByTitle = VerifiedPeople.BestByRole(
                            await VerifiedPeople.ForTitlesAsync(db, taste, poolIds, ct));
                        var toneByTitle = await db.TitleAttributes
                            .Where(a => poolIds.Contains(a.TitleId) && a.Tone != null)
                            .ToDictionaryAsync(a => a.TitleId, a => a.Tone, ct);
                        foreach (var c in pool)
                        {
                            backfillContexts[c.Title.Id] = new ExplanationTemplater.Context(
                                c.Title,
                                ParseContributions(c.ContributionsJson),
                                personsByTitle.GetValueOrDefault(c.Title.Id) ?? ExplanationTemplater.NoPersons,
                                c.Title.Genres.Where(taste.GenreRatings.ContainsKey).ToHashSet(),
                                AnchorName: null,
                                Tone: toneByTitle.GetValueOrDefault(c.Title.Id));
                        }
                    }

                    var takenWhys = items
                        .Select(x => x.Item.WhyThisSentence)
                        .ToHashSet(StringComparer.Ordinal);
                    var cursor = 0;
                    foreach (var ((row, anchorId), lost) in removedByRow)
                    {
                        var nextRank = items
                            .Where(x => x.Item.Row == row && x.Item.AnchorTitleId == anchorId)
                            .Select(x => x.Item.Rank).DefaultIfEmpty(-1).Max() + 1;
                        for (var k = 0; k < lost && cursor < pool.Count; k++)
                        {
                            var c = pool[cursor++];
                            var backfillRank = nextRank++;
                            var backfillWhy = ExplanationTemplater.BuildDistinctSentence(
                                backfillContexts[c.Title.Id], takenWhys);
                            items.Add(new
                            {
                                Item = new FeedItem
                                {
                                    Row = row,
                                    AnchorTitleId = anchorId,
                                    Rank = backfillRank,
                                    TitleId = c.Title.Id,
                                    PredictedRating = c.PredictedRating,
                                    WhyThisSentence = backfillWhy,
                                },
                                Title = c.Title,
                            });
                            refilled.Add(new ExposureCard(
                                c.Title.Id,
                                backfillRank,
                                c.PredictedRating,
                                WhySentence: backfillWhy,
                                // A backfill's number comes straight off a persisted TitleScore, so
                                // its base/residual/calibration decomposition is not recoverable
                                // here — it was discarded when the score was written. Left at zero
                                // rather than guessed at; the prediction itself is exact.
                                WhySource: ExposureWhySource.Templater,
                                RowKey: FeedPaginator.RowKeyFor(row, anchorId)));
                        }
                    }

                    items = items.OrderBy(x => x.Item.Row).ThenBy(x => x.Item.Rank).ToList();
                }
            }

            // The generic fallback may appear at most once per response payload: a second card with
            // nothing verifiable to say shows no caption rather than parroting the same boilerplate
            // (observed three times in one view, 2026-08-10). Runs over display order so the first
            // card shown keeps the sentence. Read-only request — the mutation is never saved.
            ExplanationTemplater.EnforceGenericOnce(items.Select(x => x.Item));

            var nowUtc = DateTime.UtcNow;

            // Best-effort streaming strips from the shared availability cache (populated as titles
            // are viewed; no extra TMDB calls here). Stream/flatrate providers only, top 3 by priority.
            var feedTitleIds = items.Select(x => x.Title.Id).ToList();
            var availCutoff = nowUtc.AddDays(-7);
            var providersByTitle = (await db.TitleAvailabilities
                    .Where(a => feedTitleIds.Contains(a.TitleId) && a.Region == account.Region
                        && a.Kind == Domain.Providers.AvailabilityKind.Flatrate && a.FetchedAt > availCutoff)
                    .Join(db.StreamingProviders, a => a.ProviderId, p => p.Id, (a, p) => new { a.TitleId, p.Name, p.LogoPath, p.DisplayPriority })
                    .ToListAsync(ct))
                .GroupBy(x => x.TitleId)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<CardProvider>)g.DistinctBy(x => x.Name).OrderBy(x => x.DisplayPriority)
                        .Take(3).Select(x => new CardProvider(x.Name, x.LogoPath)).ToList());

            // The bar the build required of the hero, re-derived here from the same shared constant.
            // The build relaxes it silently when nothing clears it — on live data that is the normal
            // case, not an edge — so the card has to be able to say which side of it a pick sits on.
            var minPredicted = ContentFilterRules.ParseRating(await db.ContentFilters
                .Where(f => f.AccountId == account.Id && f.Kind == FilterKind.MinPredictedRating)
                .Select(f => f.Value)
                .FirstOrDefaultAsync(ct));
            var heroBar = HeroConfidence.BarFor(minPredicted);

            // Lens copy for the whole page in one query. READ ONLY — this path never generates:
            // a page load must not be able to start an LLM call, which is the job's work and the
            // on-demand endpoint's. Where nothing has been written the card renders exactly as it
            // did before, on the templater's sentence.
            var lensTaste = await featureBuilder.BuildTasteStateAsync(account.Id, nowUtc, ct);
            var lensProfile = await tasteFacts.BuildAsync(account.Id, lensTaste, nowUtc, ct);
            var lensByTitle = await lens.ResolveManyAsync(
                account.Id, items.Select(x => x.Title.Id).ToList(), lensProfile.ProfileHash, ct);

            // The scalars behind the tonight-mood chips. One query for the page; the chips then
            // re-sort in the browser with no further round trip and no rebuild.
            var pageTitleIds = items.Select(x => x.Title.Id).ToList();
            var textures = await db.TitleAttributes
                .Where(a => pageTitleIds.Contains(a.TitleId) && a.Status == AttributeExtractionStatus.Done)
                .Select(a => new { a.TitleId, a.Darkness, a.Pacing, a.Complexity, a.Humor })
                .ToDictionaryAsync(
                    a => a.TitleId,
                    a => new CardTexture(a.Darkness, a.Pacing, a.Complexity, a.Humor),
                    ct);

            FeedCard ToCard(FeedItem item, Title title, bool hero = false)
            {
                var releaseDate = title.MediaType == MediaType.Movie ? title.ReleasedAt : title.FirstAiredAt;
                var isReleased = releaseDate is null || releaseDate <= nowUtc;
                var (crowd, crowdVotes) = Domain.Catalog.CrowdRating.Resolve(title);
                var note = lensByTitle.GetValueOrDefault(title.Id)?.Note;
                return new(
                    title.Id, title.MediaType.ToString(), title.TmdbId, title.Name, title.Year, title.RuntimeMinutes,
                    title.PosterPath, title.BackdropPath, title.Genres,
                    item.PredictedRating, crowd, crowdVotes, item.WhyThisSentence, isReleased,
                    providersByTitle.GetValueOrDefault(title.Id, []),
                    // Rows carry no label: they are selected against the account's own floor, not the
                    // hero bar, so "reaching" would be measuring them against a rule they never had.
                    hero ? HeroConfidence.For(item.PredictedRating, heroBar) : null,
                    hero ? heroBar : null,
                    note?.Hook,
                    note?.FirstClause(),
                    note?.Mood,
                    textures.GetValueOrDefault(title.Id));
            }

            var hero = items.Where(x => x.Item.Row == FeedRowKind.Hero)
                .Select(x => ToCard(x.Item, x.Title, hero: true))
                .ToList();

            // Log ONLY the refilled cards, and only the ones this response actually returns. The
            // built cards were logged by the job that planned them (Surface FeedHero / FeedRow) —
            // logging them again here would double every denominator on the track record, and a
            // feed the user reloads five times is not five exposures.
            //
            // Fire-and-forget in effect, never in fact: RecordAsync swallows its own failures, so
            // this cannot turn a rendered page into a 500.
            async Task LogRefillAsync(IReadOnlyCollection<Guid> renderedTitleIds)
            {
                var rendered = refilled.Where(c => renderedTitleIds.Contains(c.TitleId)).ToList();
                if (rendered.Count == 0)
                {
                    return;
                }

                await exposures.RecordAsync(
                    account.Id, ExposureSurface.FeedRefill, snapshot.Id, snapshot.ModelArtifactId,
                    DateTime.UtcNow, rendered, ct);
            }

            // Free tier: the 3-pick daily shortlist; rows are the paid surface (visible-locked
            // client-side; payloads stripped server-side so the data never leaks).
            var isPaid = account.Tier is AccountTier.Paid or AccountTier.Founder;
            if (!isPaid)
            {
                // Count every locked row, not just the because-you-loved anchors: the deep-cuts and
                // new-for-you rails are gated too, so undercounting them understated the upsell.
                var lockedRowCount = items.Where(x => x.Item.Row == FeedRowKind.BecauseYouLoved)
                        .Select(x => x.Item.AnchorTitleId).Distinct().Count()
                    + (items.Any(x => x.Item.Row == FeedRowKind.DeepCuts) ? 1 : 0)
                    + (items.Any(x => x.Item.Row == FeedRowKind.NewForYou) ? 1 : 0);
                // The free tier renders three hero cards and nothing else, so three is the honest
                // denominator — logging the whole payload would credit the product with cards it
                // stripped server-side.
                var freeHero = hero.Take(3).ToList();
                await LogRefillAsync(freeHero.Select(c => c.TitleId).ToHashSet());

                return Results.Ok(new
                {
                    generatedAt = (DateTime?)snapshot.GeneratedAt,
                    hero = freeHero,
                    rows = Array.Empty<object>(),
                    lockedRowCount,
                    // Why this build is not literally what the settings asked for (null when it is).
                    // A blank feed must always arrive with the reason attached.
                    snapshot.DegradedReason,
                });
            }

            var anchorIds = items.Where(x => x.Item.AnchorTitleId != null)
                .Select(x => x.Item.AnchorTitleId!.Value).Distinct().ToList();
            var anchors = await db.Titles.Where(t => anchorIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

            var rows = new List<object>();
            rows.AddRange(items
                .Where(x => x.Item.Row == FeedRowKind.BecauseYouLoved && x.Item.AnchorTitleId != null)
                .GroupBy(x => x.Item.AnchorTitleId!.Value)
                .Select(g => (object)new
                {
                    kind = "because-you-loved",
                    anchorTitleId = (Guid?)g.Key,
                    anchorName = anchors.GetValueOrDefault(g.Key, ""),
                    title = (string?)null,
                    items = g.OrderBy(x => x.Item.Rank).Select(x => ToCard(x.Item, x.Title)).ToList(),
                }));

            void AddKindRow(FeedRowKind kind, string slug, string label)
            {
                var rowItems = items.Where(x => x.Item.Row == kind)
                    .OrderBy(x => x.Item.Rank).Select(x => ToCard(x.Item, x.Title)).ToList();
                if (rowItems.Count > 0)
                {
                    rows.Add(new { kind = slug, anchorTitleId = (Guid?)null, anchorName = (string?)null, title = (string?)label, items = rowItems });
                }
            }

            AddKindRow(FeedRowKind.DeepCuts, "deep-cuts", "Deep cuts — under-the-radar, high for you");
            AddKindRow(FeedRowKind.NewForYou, "new-for-you", "New & for you");

            await LogRefillAsync(items.Select(x => x.Title.Id).ToHashSet());

            return Results.Ok(new
            {
                generatedAt = (DateTime?)snapshot.GeneratedAt,
                hero,
                rows,
                lockedRowCount = 0,
                snapshot.DegradedReason,
            });
        });

        group.MapGet("/continue-watching", async (IReelDbContext db, IAccountContext accountContext, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var entries = await db.ShowWatchProgresses
                .Where(p => p.AccountId == accountId
                    // Manually dropped shows leave Continue Watching (exclude-only, revocable).
                    && !db.UserTitleReactions.Any(r => r.AccountId == accountId && r.TitleId == p.TitleId
                        && r.Kind == ReactionKind.Dropped && r.RevokedAt == null))
                // ResumeLikelihood leads; ties (common — many shows compute the same likelihood) break
                // on recency so the show you touched most recently sits higher than a stale one.
                .OrderByDescending(p => p.ResumeLikelihood)
                .ThenByDescending(p => p.LastWatchedAt)
                .Take(15)
                .Join(db.Titles, p => p.TitleId, t => t.Id, (p, t) => new
                {
                    Title = t,
                    nextEpisodeSeason = p.NextEpisodeSeason,
                    nextEpisodeNumber = p.NextEpisodeNumber,
                    watchedEpisodes = p.WatchedEpisodeCount,
                    totalAired = p.TotalAiredEpisodes,
                    completionPct = p.CompletionPct,
                })
                .ToListAsync(ct);

            // Projected in memory rather than in SQL so the crowd rating comes from the ONE resolver
            // every other card surface uses, instead of a second, drifting copy of the fallback rule.
            return Results.Ok(entries.Select(x => new
            {
                titleId = x.Title.Id,
                mediaType = x.Title.MediaType.ToString(),
                tmdbId = x.Title.TmdbId,
                name = x.Title.Name,
                posterPath = x.Title.PosterPath,
                backdropPath = x.Title.BackdropPath,
                crowdRating = Domain.Catalog.CrowdRating.For(x.Title),
                crowdVotes = Domain.Catalog.CrowdRating.VotesFor(x.Title),
                x.nextEpisodeSeason,
                x.nextEpisodeNumber,
                x.watchedEpisodes,
                x.totalAired,
                x.completionPct,
            }));
        });

        // The "your model just learned" band above the hero. Its own endpoint rather than a field on
        // GET /api/feed: the feed payload is fetched constantly and this is a retrain-cadence number,
        // so bolting it on would make every feed read pay for two artifact lookups and a snapshot
        // diff. Returns 204 when there is nothing honest to say (no predecessor artifact, no
        // evaluation, no new labels) so the client renders nothing at all rather than an empty band.
        group.MapGet("/learned", async (ModelLearnedQuery learned, IAccountContext accountContext, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            if (await learned.GetAsync(accountId, ct) is not { } result)
            {
                return Results.NoContent();
            }

            return Results.Ok(new
            {
                trainedAt = result.TrainedAt,
                newRatingsSinceLast = result.NewRatingsSinceLast,
                calibrationBias = result.CalibrationBias,
                calibrationMae = result.CalibrationMae,
                maeImprovement = result.MaeImprovement,
                spearman = result.Spearman,
                picksChanged = result.PicksChanged,
            });
        });

        group.MapPost("/rebuild", async (IReelDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            // Manual refresh is a paid-surface capability, not a founder-only one — every paying tier
            // can force a regeneration (reactive rebuilds cover the rest automatically).
            if (account is null || account.Tier is not (AccountTier.Paid or AccountTier.Founder))
            {
                return Results.Forbid();
            }

            var inFlight = await db.SyncJobs.AnyAsync(
                j => j.AccountId == account.Id && j.Kind == JobKind.BuildFeed
                    && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
            if (!inFlight)
            {
                db.SyncJobs.Add(new SyncJob
                {
                    Id = Guid.NewGuid(),
                    AccountId = account.Id,
                    Kind = JobKind.BuildFeed,
                    Priority = 0,
                    EnqueuedAt = DateTime.UtcNow,
                    // The one reason that must rotate the page even though nothing about the
                    // account's data changed — FeedBuildPolicy forces a Fresh build for it.
                    TriggerReason = FeedBuildPolicy.ManualRefreshReason,
                });
                await db.SaveChangesAsync(ct);
            }

            return Results.Accepted();
        });

        // The infinite-scroll lane. Every row (and the hero) extends through this: the client sends
        // the row context + the title ids it has already shown, and gets the next best, never-seen
        // slice — scored live, deepened from TMDB when a lane runs thin. Rows are the paid surface,
        // so the free tier (3-pick hero shortlist) gets nothing here.
        group.MapPost("/more", async (MoreRequestDto body, FeedPaginator paginator, IReelDbContext db, ExposureLog exposures, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            if (account.Tier is not (AccountTier.Paid or AccountTier.Founder))
            {
                // Free tier has no paginated rows — signal true exhaustion so the client stops paging
                // rather than treating the empty page as a transient blip and retrying forever.
                return Results.Ok(new { items = Array.Empty<FeedCard>(), exhausted = true });
            }

            if (SlugToContext(body.Row) is not { } context)
            {
                return Results.BadRequest(new { error = $"unknown row '{body.Row}'" });
            }

            var excludeIds = (body.ExcludeTitleIds ?? [])
                .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                .Where(g => g is not null).Select(g => g!.Value).ToList();
            Guid? anchor = Guid.TryParse(body.AnchorTitleId, out var anchorId) ? anchorId : null;
            var limit = Math.Clamp(body.Limit ?? 12, 1, 30);

            var items = await paginator.MoreAsync(account, new MoreFeedRequest(context, anchor, body.Genre, excludeIds, limit, body.MediaType), ct);

            var nowUtc = DateTime.UtcNow;
            var providers = await ProvidersForAsync(db, account.Region, items.Select(i => i.Title.Id).ToList(), nowUtc, ct);
            var cards = items.Select(i => new FeedCard(
                i.Title.Id, i.Title.MediaType.ToString(), i.Title.TmdbId, i.Title.Name, i.Title.Year, i.Title.RuntimeMinutes,
                i.Title.PosterPath, i.Title.BackdropPath, i.Title.Genres,
                i.Predicted, Domain.Catalog.CrowdRating.For(i.Title), Domain.Catalog.CrowdRating.VotesFor(i.Title),
                i.WhyThis, i.IsReleased, providers.GetValueOrDefault(i.Title.Id, []))).ToList();

            // Every paginated card is a card the user is about to see and which exists nowhere else
            // in the record — /feed/more scores live and writes only a TitleScore, which keeps no
            // rank, no why-line and no timestamp of the serve. PageIndex is derived from how many
            // titles the client says it has already shown, which is the only depth signal the
            // request carries.
            if (cards.Count > 0 && items.Count > 0)
            {
                var artifactId = await db.ModelArtifacts
                    .Where(a => a.AccountId == account.Id && a.Status == ArtifactStatus.Active)
                    .OrderByDescending(a => a.Version)
                    .Select(a => (Guid?)a.Id)
                    .FirstOrDefaultAsync(ct);
                if (artifactId is { } activeArtifactId)
                {
                    var pageIndex = limit > 0 ? excludeIds.Count / limit : 0;
                    var rowKey = context == FeedRowContext.Genre && !string.IsNullOrWhiteSpace(body.Genre)
                        ? $"genre:{body.Genre}"
                        : context switch
                        {
                            FeedRowContext.BecauseYouLoved => $"loved:{anchor}",
                            FeedRowContext.Similar => $"similar:{anchor}",
                            _ => context.ToString().ToLowerInvariant(),
                        };

                    await exposures.RecordAsync(
                        account.Id,
                        SurfaceFor(context),
                        // No snapshot behind a paginated card — it was scored for this request. The
                        // idempotency key falls back to (account, title, surface, minute), which is
                        // the right grain: a double-tap on "load more" inside one minute returning
                        // the same slice is one exposure.
                        feedSnapshotId: null,
                        activeArtifactId,
                        nowUtc,
                        cards.Select((c, i) => new ExposureCard(
                            c.TitleId,
                            i,
                            c.PredictedRating,
                            WhySentence: c.WhyThis,
                            WhySource: ExposureWhySource.Unknown,
                            RowKey: rowKey,
                            PageIndex: pageIndex)).ToList(),
                        ct);
                }
            }

            // The paginator already tried to deepen this lane straight from TMDB before returning, so an
            // empty page here means the lane is genuinely dry — an explicit exhaustion signal the client
            // can trust (vs a caught 500/429/offline, which it must NOT latch on). A full page implies more.
            return Results.Ok(new { items = cards, exhausted = cards.Count == 0 });
        });

        // The genre rails the home page renders beneath the personalised rows: the user's top
        // genres by affinity × volume, minus anything they've content-filtered out. The rails
        // themselves fill via /more?row=genre, so this only names them.
        group.MapGet("/genres", async (FeatureVectorBuilder featureBuilder, IReelDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            if (account.Tier is not (AccountTier.Paid or AccountTier.Founder))
            {
                return Results.Ok(new { genres = Array.Empty<object>() });
            }

            // The rails themselves IGNORE the genre filters once opened (a rail's genre is the user's
            // explicit request in that moment — see FeedPaginator.IgnoresGenreFilters). What is
            // OFFERED here is a different question, and the answer is the standing preference: we do
            // not put a "Documentary" rail in front of someone who filtered documentaries out, and an
            // include-list narrows the offer to the genres on it. Navigating to such a rail directly
            // still works; we simply never suggest it.
            var excluded = await db.ContentFilters
                .Where(f => f.AccountId == account.Id && f.Kind == FilterKind.ExcludeGenre)
                .Select(f => f.Value).ToListAsync(ct);
            var included = await db.ContentFilters
                .Where(f => f.AccountId == account.Id && f.Kind == FilterKind.IncludeGenre)
                .Select(f => f.Value).ToListAsync(ct);

            var taste = await featureBuilder.BuildTasteStateAsync(account.Id, DateTime.UtcNow, ct);
            var genres = taste.GenreRatings
                .Where(kv => !excluded.Contains(kv.Key)
                    && (included.Count == 0 || included.Contains(kv.Key))
                    && kv.Value.Count >= 3)
                .Select(kv => new
                {
                    slug = kv.Key,
                    label = Prettify(kv.Key),
                    score = (double)TasteMath.ShrunkenMean(kv.Value.Select(r => r.Rating).ToList(), taste.UserMean) * Math.Log(1 + kv.Value.Count),
                })
                .OrderByDescending(g => g.score)
                .Take(8)
                .Select(g => new { g.slug, g.label })
                .ToList();

            return Results.Ok(new { genres });
        });
    }

    public record MoreRequestDto(string Row, string? AnchorTitleId, string? Genre, string[]? ExcludeTitleIds, int? Limit, string? MediaType = null);

    /// <summary>
    /// Which exposure surface a paginated row belongs to. A genre rail is its own surface because
    /// "does the genre rail convert" is a different product question from "does tonight's page
    /// convert"; the detail page's similar rail likewise. Everything else is feed pagination.
    /// </summary>
    private static ExposureSurface SurfaceFor(FeedRowContext context) => context switch
    {
        FeedRowContext.Genre => ExposureSurface.GenreRail,
        FeedRowContext.Similar => ExposureSurface.Similar,
        _ => ExposureSurface.FeedPage,
    };

    private static FeedRowContext? SlugToContext(string? slug) => slug switch
    {
        "hero" or "tonights-picks" => FeedRowContext.Hero,
        "because-you-loved" => FeedRowContext.BecauseYouLoved,
        "deep-cuts" => FeedRowContext.DeepCuts,
        "new-for-you" => FeedRowContext.NewForYou,
        "genre" => FeedRowContext.Genre,
        "similar" => FeedRowContext.Similar,
        _ => null,
    };

    /// <summary>
    /// A TitleScore's persisted contributions in the shape the templater consumes. The json is
    /// written by the scoring passes as <c>[{"feature":"…","value":…}]</c>; anything unreadable
    /// degrades to "no contributions" (the templater then has only its generic floor to offer,
    /// and the response-level dedupe keeps that floor from repeating).
    /// </summary>
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
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }

    private static string Prettify(string slug) =>
        string.Join(' ', slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>
    /// Flatrate provider strip (top 3 by display priority, 7-day freshness) for a batch of
    /// titles. Internal because the digest and blend surfaces reuse the exact same read —
    /// a second copy of this query is how the read-side TTL and the write-side TTL drift.
    /// </summary>
    internal static async Task<Dictionary<Guid, IReadOnlyList<CardProvider>>> ProvidersForAsync(
        IReelDbContext db, string region, IReadOnlyList<Guid> titleIds, DateTime nowUtc, CancellationToken ct)
    {
        if (titleIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<CardProvider>>();
        }

        var cutoff = nowUtc.AddDays(-7);
        return (await db.TitleAvailabilities
                .Where(a => titleIds.Contains(a.TitleId) && a.Region == region
                    && a.Kind == Domain.Providers.AvailabilityKind.Flatrate && a.FetchedAt > cutoff)
                .Join(db.StreamingProviders, a => a.ProviderId, p => p.Id, (a, p) => new { a.TitleId, p.Name, p.LogoPath, p.DisplayPriority })
                .ToListAsync(ct))
            .GroupBy(x => x.TitleId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CardProvider>)g.DistinctBy(x => x.Name).OrderBy(x => x.DisplayPriority)
                    .Take(3).Select(x => new CardProvider(x.Name, x.LogoPath)).ToList());
    }
}
