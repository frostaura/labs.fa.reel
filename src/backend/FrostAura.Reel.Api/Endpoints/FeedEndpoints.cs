using System.Text.Json;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Api.Endpoints;

public static class FeedEndpoints
{
    public record CardProvider(string Name, string? LogoPath);

    /// <summary>
    /// The card contract. <c>PredictedRating</c> is "for you"; <c>CrowdRating</c> / <c>CrowdVotes</c>
    /// are the community number it is compared against, resolved once in
    /// <see cref="Domain.Catalog.CrowdRating"/> (TMDB, then Trakt, then null) so every surface agrees.
    /// </summary>
    public record FeedCard(
        Guid TitleId, string MediaType, long? TmdbId, string Name, int? Year, int? RuntimeMinutes,
        string? PosterPath, string? BackdropPath, string[] Genres,
        decimal PredictedRating, decimal? CrowdRating, int? CrowdVotes,
        string WhyThis, bool IsReleased, IReadOnlyList<CardProvider> Providers);

    public static void MapFeedEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/feed").RequireAccount();

        group.MapGet("/", async (IReelDbContext db, EligibilityQueryBuilder eligibility, FeatureVectorBuilder featureBuilder, IMemoryCache cache, HttpContext http, CancellationToken ct) =>
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
                            items.Add(new
                            {
                                Item = new FeedItem
                                {
                                    Row = row,
                                    AnchorTitleId = anchorId,
                                    Rank = nextRank++,
                                    TitleId = c.Title.Id,
                                    PredictedRating = c.PredictedRating,
                                    WhyThisSentence = ExplanationTemplater.BuildDistinctSentence(
                                        backfillContexts[c.Title.Id], takenWhys),
                                },
                                Title = c.Title,
                            });
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

            FeedCard ToCard(FeedItem item, Title title)
            {
                var releaseDate = title.MediaType == MediaType.Movie ? title.ReleasedAt : title.FirstAiredAt;
                var isReleased = releaseDate is null || releaseDate <= nowUtc;
                var (crowd, crowdVotes) = Domain.Catalog.CrowdRating.Resolve(title);
                return new(
                    title.Id, title.MediaType.ToString(), title.TmdbId, title.Name, title.Year, title.RuntimeMinutes,
                    title.PosterPath, title.BackdropPath, title.Genres,
                    item.PredictedRating, crowd, crowdVotes, item.WhyThisSentence, isReleased,
                    providersByTitle.GetValueOrDefault(title.Id, []));
            }

            var hero = items.Where(x => x.Item.Row == FeedRowKind.Hero)
                .Select(x => ToCard(x.Item, x.Title))
                .ToList();

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
                return Results.Ok(new
                {
                    generatedAt = (DateTime?)snapshot.GeneratedAt,
                    hero = hero.Take(3),
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
        group.MapPost("/more", async (MoreRequestDto body, FeedPaginator paginator, IReelDbContext db, HttpContext http, CancellationToken ct) =>
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
