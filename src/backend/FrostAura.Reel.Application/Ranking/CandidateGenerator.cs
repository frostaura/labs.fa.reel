using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// Widens the catalog beyond the user's own history and does it under the account's OWN content
/// rules, so the titles it fetches are titles the eligibility layer can actually serve.
///
/// <para>
/// The shape of the problem it solves, measured on the founder's account (2026-09-09): ~421 of 5,319
/// catalogue titles are eligible, and the popularity retrieval arm alone over-fetches 700 — i.e.
/// retrieval already exceeds the entire eligible pool, so ranking has nothing left to choose from
/// and the hero floor is silently relaxed every build. Every unfiltered discover page made that
/// worse: ~92% of what it returned was ineligible by construction, and fetching page 2 of an
/// unfiltered query is simply more ineligible titles. Supply, not ranking, is the binding constraint.
/// </para>
///
/// <para>
/// So this class now does five things it did not do:
/// <list type="number">
/// <item>Projects the account's content rules INTO every discover call (language, year window,
/// crowd-rating floor, banned genres) via <see cref="AccountDiscoveryRules"/>, routed through
/// <see cref="ITmdbClient.DiscoverByConceptAsync"/> rather than a second query builder.</item>
/// <item>Reads DEEPER as the user consumes the reservoir, instead of page 1 for ever.</item>
/// <item>Seeds from three ordered, rotating pools — all-time loved, recently loved, and
/// saved-but-unwatched, which is the strongest declared intent the user produces and used to seed
/// nothing at all — across both TMDB collaborative endpoints.</item>
/// <item>Asks TMDB about the PEOPLE and KEYWORDS the user loves. Nothing in discovery was ever
/// person-aware, so "because you love this director" had no route into the pool.</item>
/// <item>Plans the movie/show mix from the user's own rated mix instead of a fixed 1:1.</item>
/// </list>
/// </para>
///
/// <para>
/// Every arm is bounded by one shared per-build call budget and drains the Reconcile rate-gate lane,
/// so widening supply cannot starve live search. The pool stays global catalog data; eligibility
/// filtering still happens at feed-build time through the <see cref="EligibilityQueryBuilder"/> —
/// pushing rules into the fetch narrows what is asked for, it never relaxes what is served.
/// </para>
/// </summary>
public class CandidateGenerator(
    IReelDbContext db,
    ITmdbClient tmdb,
    EligibilityQueryBuilder eligibility,
    ILogger<CandidateGenerator> logger)
{
    private const int TopGenreCount = 4;

    /// <summary>Seeds drawn from each of the three pools.</summary>
    private const int SeedsPerPool = 4;

    /// <summary>People OR-joined into the single credit-affinity discover arm.</summary>
    private const int PersonSeedCount = 3;

    /// <summary>Ratings a derived person-affinity needs behind it before it steers a TMDB call.</summary>
    private const int PersonAffinityMinRatings = 3;

    /// <summary>Explicit person ratings at or above this are treated as a loved person.</summary>
    private const short LovedPersonRating = 8;

    /// <summary>Loved-set keywords resolved to TMDB ids per build — each costs a search call.</summary>
    private const int LovedKeywordCount = 2;

    /// <summary>Loved titles scanned for keywords. Beyond this the frequency ranking stops moving.</summary>
    private const int KeywordScanTitles = 40;

    /// <summary>Days back the "fresh releases" arm reaches.</summary>
    private const int FreshReleaseDays = 90;

    /// <summary>
    /// Hard ceiling on TMDB calls one pool refresh may make. It is the reason every arm below is
    /// ordered rather than merely present: at full consumption the genre lane alone would want 20
    /// calls, and the small personal arms — seeds, people, keywords — are the ones that make the pool
    /// personal rather than merely bigger, so they are taken FIRST and the genre lane absorbs
    /// whatever is left. The old build spent up to 16 calls; this one is bounded at 40, inside the
    /// shared TMDB gate's budget.
    /// </summary>
    public const int MaxTmdbCallsPerBuild = 40;

    /// <summary>
    /// Widens the pool for one account and reports what it found: how many TMDB items came back
    /// across all arms, how many of those the catalogue had never seen, and — the number that
    /// actually answers "the picks get weaker every round" — how many of the NEW ones are eligible
    /// for this account under its own rules. Discovered-but-ineligible is the waste this class
    /// exists to remove, and it was invisible until it was counted.
    /// </summary>
    /// <param name="now">
    /// The build's timestamp, passed in rather than read here so seed rotation is a pure function of
    /// its inputs and a build can be replayed exactly from what the snapshot persisted — the same
    /// contract <see cref="AnchorRotation"/> holds for the row anchors.
    /// </param>
    public async Task<PoolRefreshResult> RefreshPoolAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, string region, DateTime now, CancellationToken ct)
    {
        var rules = await AccountDiscoveryRules.LoadAsync(db, accountId, ct);

        // A genre the user has excluded is not a "top genre" of theirs in any sense the pool cares
        // about, whatever their historical ratings say — Dean bans 8 of 25 including two of his
        // highest-COUNT genres, so up to half the discover budget used to fetch titles the
        // eligibility layer is guaranteed to reject.
        var topGenres = taste.GenreRatings
            .Where(kv => !rules.ExcludedGenreSlugs.Contains(kv.Key))
            .Select(kv => new
            {
                Genre = kv.Key,
                Score = (double)TasteMath.ShrunkenMean(kv.Value.Select(r => r.Rating).ToList(), taste.UserMean)
                    * Math.Log(1 + kv.Value.Count),
            })
            .OrderByDescending(g => g.Score)
            .ThenBy(g => g.Genre, StringComparer.Ordinal)
            .Take(TopGenreCount)
            .Select(g => g.Genre)
            .ToList();

        var consumed = await ConsumedSinceTrainingAsync(accountId, ct);
        var startPage = DiscoveryPaging.StartPage(consumed);
        var pages = DiscoveryPaging.PageCount(consumed);
        var rotation = AnchorRotation.OffsetFor(now);
        var media = new MediaPlanner((double)taste.MovieRatedShare);
        var budget = new CallBudget(MaxTmdbCallsPerBuild);
        var batches = new List<(DiscoveryArm Arm, IReadOnlyList<TmdbListItem> Items)>();

        // ── Arm 1: collaborative seeds ─────────────────────────────────────────────────────────
        // Three ordered pools of four, alternating between TMDB's two collaborative endpoints:
        // recommendations is behavioural, similar is content-derived, and running both over the same
        // seeds widens along two independent axes for one call each.
        //
        // Which endpoint a seed gets is a function of the SEED and the day, not of its position in
        // the list. Position looked equivalent and is not: the all-time pool itself rotates daily, so
        // a carried-over seed slides one place and the two rotations cancel — the seed would be asked
        // the identical question every day, which is the thing this alternation exists to prevent.
        var seeds = await BuildSeedsAsync(accountId, taste, rotation, ct);
        // What the seed arm COULD have drawn from, against what it did. Twelve seeds over a
        // thousand-anchor pool is a ~90-week rotation, and until this was counted the gap was an
        // argument rather than a number — it is the evidence B2's widening is gated on.
        var seedPoolSize = taste.TopLovedTitleIds.Count + taste.LovedAnchorPool.Count;
        var seedsUsed = 0;
        foreach (var seed in seeds)
        {
            if (!budget.TryTake())
            {
                break;
            }

            batches.Add((DiscoveryArm.Seed, ((rotation + seed.TmdbId) & 1L) == 1L
                ? await tmdb.GetSimilarAsync(seed.IsMovie, seed.TmdbId, ct)
                : await tmdb.GetRecommendationsAsync(seed.IsMovie, seed.TmdbId, ct)));
            seedsUsed++;
        }

        // ── Arm 2: the people the user loves ───────────────────────────────────────────────────
        // Nothing in discovery was person-aware, so a title could only ever enter the pool because of
        // its genre or its neighbours — never because the user has rated this director's work highly
        // eight times. One call, OR-joining the top three; movie-only, because /discover/tv has no
        // person parameter.
        var peopleIds = await TopPeopleTmdbIdsAsync(accountId, ct);
        if (peopleIds.Count > 0 && budget.TryTake())
        {
            batches.Add((DiscoveryArm.People, await tmdb.DiscoverByConceptAsync(
                movies: true, [], [], region, rules.ReleasedAfter, page: 1,
                rules.ExcludedGenreIds(true), null, rules.ReleasedBefore, null,
                rules.Options(peopleIds), ct)));
        }

        // ── Arm 3: the keywords the loved set is made of ───────────────────────────────────────
        var lovedKeywords = await TopLovedKeywordsAsync(taste, ct);
        var lovedKeywordIds = await ResolveKeywordIdsAsync(lovedKeywords, budget, ct);
        if (lovedKeywordIds.Count > 0 && !budget.Exhausted
            && media.NextIsMovie() is { } keywordIsMovie && budget.TryTake())
        {
            batches.Add((DiscoveryArm.Keyword, await tmdb.DiscoverByConceptAsync(
                keywordIsMovie, [], lovedKeywordIds, region, rules.ReleasedAfter, startPage,
                rules.ExcludedGenreIds(keywordIsMovie), null, rules.ReleasedBefore, null,
                rules.Options(), ct)));
        }

        // ── Arm 4: top genres TMDB has no id for ───────────────────────────────────────────────
        // An unmapped slug used to cost the build a whole discover arm and produce nothing but a log
        // line. TMDB has no genre id for "anime", "musical" or "sports", but it has keywords for all
        // of them, so the arm degrades to keyword discovery instead of disappearing.
        var unmapped = topGenres.Where(g => !TmdbGenres.SlugToIds.ContainsKey(g)).ToList();
        foreach (var slug in unmapped)
        {
            var keywordIds = await ResolveKeywordIdsAsync([slug], budget, ct);
            if (keywordIds.Count == 0 || budget.Exhausted
                || media.NextIsMovie() is not { } slugIsMovie || !budget.TryTake())
            {
                continue;
            }

            batches.Add((DiscoveryArm.UnmappedGenre, await tmdb.DiscoverByConceptAsync(
                slugIsMovie, [], keywordIds, region, rules.ReleasedAfter, startPage,
                rules.ExcludedGenreIds(slugIsMovie), null, rules.ReleasedBefore, null,
                rules.Options(), ct)));
        }

        // ── Arm 5: this week's trending ────────────────────────────────────────────────────────
        // Deliberately NOT filter-aware: /trending takes no discover parameters at all, so this arm
        // is the one place ineligible titles still arrive. Kept anyway — it is two calls and it is
        // the only arm that reflects what the world is watching right now rather than what this
        // account already implies.
        foreach (var trendingIsMovie in new[] { true, false })
        {
            if (budget.TryTake())
            {
                batches.Add((DiscoveryArm.Trending, await tmdb.GetTrendingAsync(trendingIsMovie, ct)));
            }
        }

        // ── Arm 6: fresh releases ──────────────────────────────────────────────────────────────
        var freshAfter = Latest(rules.ReleasedAfter, now.AddDays(-FreshReleaseDays));
        if (!budget.Exhausted && media.NextIsMovie() is { } freshIsMovie && budget.TryTake())
        {
            batches.Add((DiscoveryArm.Fresh, await tmdb.DiscoverByConceptAsync(
                freshIsMovie, [], [], region, freshAfter, page: 1,
                rules.ExcludedGenreIds(freshIsMovie), null, rules.ReleasedBefore, null,
                rules.Options(), ct)));
        }

        // ── Arm 7: the genre lane, page-major ──────────────────────────────────────────────────
        // Page-major rather than genre-major so that when the budget runs out every genre has been
        // read to the same depth, instead of genre 1 reaching page 5 while genre 4 was never asked.
        var mapped = topGenres
            .Where(g => TmdbGenres.SlugToIds.ContainsKey(g))
            .Select(g => (Slug: g, Ids: TmdbGenres.SlugToIds[g]))
            .ToList();
        var genresDiscovered = new List<string>();
        for (var offset = 0; offset < pages && !budget.Exhausted; offset++)
        {
            foreach (var (slug, ids) in mapped)
            {
                if (budget.Exhausted)
                {
                    break;
                }

                if (media.NextIsMovie(ids.Movie is not null, ids.Tv is not null) is not { } isMovie
                    || !budget.TryTake())
                {
                    continue;
                }

                var genreId = isMovie ? ids.Movie!.Value : ids.Tv!.Value;
                batches.Add((DiscoveryArm.Genre, await tmdb.DiscoverByConceptAsync(
                    isMovie, [genreId], [], region, rules.ReleasedAfter, startPage + offset,
                    rules.ExcludedGenreIds(isMovie), null, rules.ReleasedBefore, null,
                    rules.Options(), ct)));
                genresDiscovered.Add(slug);
            }
        }

        // FIRST-CLAIM attribution, matching the dedup below: the arm that returned an item first
        // owns it. An arm that would also have found it gets no credit, so these counts describe the
        // pool as assembled rather than what each arm could reach on its own — the same honesty
        // caveat the retrieval probe carries about its own arms.
        var claimedBy = new Dictionary<(bool IsMovie, long Id), DiscoveryArm>();
        foreach (var (arm, batchItems) in batches)
        {
            foreach (var item in batchItems)
            {
                claimedBy.TryAdd((item.IsMovie, item.Id), arm);
            }
        }

        var items = batches.SelectMany(b => b.Items)
            .GroupBy(i => (i.IsMovie, i.Id))
            .Select(g => g.First())
            .ToList();

        var upsert = await UpsertAndCollectAsync(items, ct);

        // The upsert returns ids in the order it created them, which is `items` order, so the two
        // zip — but the mapping is built from the rows themselves rather than from that coincidence,
        // because the day someone reorders the upsert this would silently start lying.
        var addedSet = upsert.AddedTitleIds.ToHashSet();
        var armByTitleId = addedSet.Count == 0
            ? []
            : await db.Titles
                .Where(t => addedSet.Contains(t.Id) && t.TmdbId != null)
                .Select(t => new { t.Id, t.MediaType, TmdbId = t.TmdbId!.Value })
                // Nullable on purpose: a row we cannot attribute is reported as attributed to
                // nothing, never folded into whichever arm happens to be the enum's default.
                .ToDictionaryAsync(
                    t => t.Id,
                    t => claimedBy.TryGetValue((t.MediaType == MediaType.Movie, t.TmdbId), out var arm)
                        ? (DiscoveryArm?)arm
                        : null,
                    ct);

        // How many of the brand-new titles this account can actually be shown. This is the number the
        // supply work is judged on — "discovered" and even "newly upserted" both count titles the
        // eligibility layer will reject, which is exactly how a build could report healthy discovery
        // while the servable pool stood still.
        //
        // It is NOT persisted: FeedBuildStat has no column for it and this branch takes no EF
        // migration, so it is logged and returned on PoolRefreshResult for the caller.
        // Run PER ARM. The single aggregate could say "twenty new eligible titles" for a build in
        // which the genre lane produced all twenty and every personal arm produced none — which is
        // the exact reallocation question B2 is gated on, and it was unanswerable.
        var eligibleAddedIds = upsert.AddedTitleIds.Count == 0
            ? []
            : await eligibility.EligibleTitles(accountId)
                .Where(t => addedSet.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync(ct);
        var newlyEligible = eligibleAddedIds.Count;

        var perArm = Enum.GetValues<DiscoveryArm>()
            .Select(arm => new ArmSupply(
                arm.ToString(),
                batches.Where(b => b.Arm == arm).Sum(b => b.Items.Count),
                armByTitleId.Count(kv => kv.Value == arm),
                eligibleAddedIds.Count(id => armByTitleId.GetValueOrDefault(id) == arm)))
            .Where(a => a.Discovered > 0)
            .ToList();

        logger.LogInformation(
            "Candidate pool refreshed: {Items} TMDB items ({Upserted} new, {Eligible} of them eligible) "
            + "from {Calls}/{Budget} calls across genres [{Genres}]; pages {StartPage}..{EndPage} "
            + "(consumed {Consumed}), media plan {Movies}m/{Shows}tv toward {Target:P0} movies.",
            items.Count, upsert.Added, newlyEligible, budget.Used, MaxTmdbCallsPerBuild,
            string.Join(", ", genresDiscovered.Distinct()), startPage, startPage + pages - 1, consumed,
            media.Movies, media.Shows, media.Target);

        if (budget.Exhausted)
        {
            logger.LogInformation(
                "Candidate pool hit its {Budget}-call ceiling for {AccountId}; the deepest genre pages were skipped this build.",
                MaxTmdbCallsPerBuild, accountId);
        }

        return new PoolRefreshResult(
            items.Count, upsert.Added, newlyEligible, budget.Used, seedPoolSize, seedsUsed, perArm);
    }

    /// <summary>
    /// What one pool refresh produced: items seen, titles genuinely new to the catalogue, how many of
    /// those new titles are eligible for this account, and the TMDB calls it cost.
    /// </summary>
    public readonly record struct PoolRefreshResult(
        int Discovered,
        int NewlyUpserted,
        int NewlyEligible,
        int TmdbCalls,
        int SeedPoolSize,
        int SeedsUsed,
        IReadOnlyList<ArmSupply> PerArm);

    /// <summary>
    /// One discovery arm's contribution: items returned, rows it was the first to create, and how
    /// many of those this account can actually be shown. The last number is the only one that
    /// answers "is this arm worth its call", and it is per-arm precisely because the aggregate hides
    /// a genre lane carrying arms that produce nothing.
    /// </summary>
    public readonly record struct ArmSupply(string Arm, int Discovered, int Upserted, int NewlyEligible);

    /// <summary>
    /// Upserts TMDB list items as global, TMDB-only <see cref="Title"/> rows (dedup by
    /// (MediaType, TmdbId); TraktId resolves lazily). Reused by the feed pool and live Ask Reel
    /// expansion. Summary-only — credits/keywords/embeddings come later via hydrate/enrich.
    /// Returns the count of newly-created rows.
    /// </summary>
    public async Task<int> UpsertCandidatesAsync(IReadOnlyList<TmdbListItem> items, CancellationToken ct) =>
        (await UpsertAndCollectAsync(items, ct)).Added;

    /// <summary>
    /// <see cref="UpsertCandidatesAsync"/>, additionally naming the rows it created. The ids are what
    /// make "how many of the new ones can this account actually be shown" answerable — a count alone
    /// cannot be joined back to the eligibility rule.
    /// </summary>
    public async Task<UpsertResult> UpsertAndCollectAsync(IReadOnlyList<TmdbListItem> items, CancellationToken ct)
    {
        var movieIds = items.Where(i => i.IsMovie).Select(i => i.Id).ToList();
        var tvIds = items.Where(i => !i.IsMovie).Select(i => i.Id).ToList();

        var existing = await db.Titles
            .Where(t => t.TmdbId != null
                && ((t.MediaType == MediaType.Movie && movieIds.Contains(t.TmdbId.Value))
                    || (t.MediaType == MediaType.Show && tvIds.Contains(t.TmdbId.Value))))
            .ToDictionaryAsync(t => (t.MediaType, TmdbId: t.TmdbId!.Value), ct);

        var added = new List<Guid>();
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
                added.Add(title.Id);
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
        return new UpsertResult(added.Count, added);
    }

    /// <summary>Rows created by one upsert, and their ids.</summary>
    public readonly record struct UpsertResult(int Added, IReadOnlyList<Guid> AddedTitleIds);

    /// <summary>One TMDB seed: the id and which endpoint family it belongs to.</summary>
    private readonly record struct Seed(long TmdbId, bool IsMovie);

    /// <summary>
    /// Which discovery arm a batch came from. Named rather than positional because the counts ride
    /// a jsonb column that outlives this file's arm ordering.
    /// </summary>
    public enum DiscoveryArm
    {
        /// <summary>Collaborative seeds — TMDB recommendations / similar over the account's own anchors.</summary>
        Seed,

        /// <summary>Credit-affinity discover over the directors and writers the account reliably likes.</summary>
        People,

        /// <summary>Keyword discover over the keywords the loved set is made of.</summary>
        Keyword,

        /// <summary>A top genre TMDB has no genre id for, degraded to keyword discover.</summary>
        UnmappedGenre,

        /// <summary>This week's trending — the one arm that is deliberately not filter-aware.</summary>
        Trending,

        /// <summary>Fresh releases inside the account's own rules.</summary>
        Fresh,

        /// <summary>The page-major genre lane, which absorbs whatever budget the personal arms leave.</summary>
        Genre,
    }

    /// <summary>
    /// Three ordered pools of four, concatenated and de-duplicated in order.
    ///
    /// <list type="bullet">
    /// <item><b>All-time loved</b> — <c>TopLovedTitleIds</c> in its own taste order, ROTATED by the
    /// day's offset, so a user whose top ten cannot move still gets a different four each day. The
    /// query used to <c>Take(5)</c> with no ORDER BY, so Postgres was free to return any five rows.</item>
    /// <item><b>Recently loved</b> — the recency-weighted anchor pool, which is where taste is NOW
    /// rather than where it has been.</item>
    /// <item><b>Saved-but-unwatched</b> — the live managed list, best predicted first. A save is the
    /// strongest declared intent the user produces and it seeded nothing at all before this.</item>
    /// </list>
    /// </summary>
    private async Task<List<Seed>> BuildSeedsAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, int rotation, CancellationToken ct)
    {
        var allTime = AnchorRotation.Rotate(taste.TopLovedTitleIds, rotation).Take(SeedsPerPool).ToList();
        var recent = taste.LovedAnchorPool.Take(SeedsPerPool).ToList();

        // Saved-but-unwatched, ordered by what the model thinks of them. TitleScore is read here as a
        // SERVING signal (its own contract: "the serving path reads these"); the leakage rule that
        // bars it is scoped to training and evaluation, neither of which this is.
        var savedIds = await db.ManagedListItems
            .Where(m => m.AccountId == accountId && m.RemovedAt == null)
            .Select(m => m.TitleId)
            .ToListAsync(ct);
        List<Guid> savedRanked = savedIds.Count == 0
            ? []
            : await db.TitleScores
                .Where(s => s.AccountId == accountId && savedIds.Contains(s.TitleId))
                .GroupBy(s => s.TitleId)
                .Select(g => new { TitleId = g.Key, Predicted = g.Max(s => s.PredictedRating) })
                .OrderByDescending(x => x.Predicted)
                .ThenBy(x => x.TitleId)
                .Take(SeedsPerPool)
                .Select(x => x.TitleId)
                .ToListAsync(ct);

        var ordered = allTime.Concat(recent).Concat(savedRanked).Distinct().ToList();
        if (ordered.Count == 0)
        {
            return [];
        }

        var resolved = await db.Titles
            .Where(t => ordered.Contains(t.Id) && t.TmdbId != null)
            .Select(t => new { t.Id, t.MediaType, TmdbId = t.TmdbId!.Value })
            .ToListAsync(ct);
        var byId = resolved.ToDictionary(t => t.Id);

        return ordered
            .Where(byId.ContainsKey)
            .Select(id => new Seed(byId[id].TmdbId, byId[id].MediaType == MediaType.Movie))
            .ToList();
    }

    /// <summary>
    /// TMDB person ids for the directors and writers this account most reliably likes: explicit
    /// person ratings first (an 8+ typed by hand outranks anything derived), then the derived credit
    /// affinities with enough ratings behind them to be more than one enthusiastic evening.
    /// </summary>
    private async Task<List<long>> TopPeopleTmdbIdsAsync(Guid accountId, CancellationToken ct)
    {
        var explicitIds = await db.UserPersonRatings
            .Where(r => r.AccountId == accountId && r.Rating >= LovedPersonRating)
            .OrderByDescending(r => r.Rating)
            .ThenByDescending(r => r.RatedAt)
            .Select(r => r.PersonId)
            .Take(PersonSeedCount)
            .ToListAsync(ct);

        var derivedIds = await db.PersonAffinities
            .Where(a => a.AccountId == accountId
                && (a.Role == CreditRole.Director || a.Role == CreditRole.Writer)
                && a.RatedTitleCount >= PersonAffinityMinRatings)
            .OrderByDescending(a => a.Affinity)
            .ThenBy(a => a.PersonId)
            .Select(a => a.PersonId)
            .Take(PersonSeedCount)
            .ToListAsync(ct);

        var personIds = explicitIds.Concat(derivedIds).Distinct().Take(PersonSeedCount).ToList();
        if (personIds.Count == 0)
        {
            return [];
        }

        var tmdbIds = await db.Persons
            .Where(p => personIds.Contains(p.Id) && p.TmdbId > 0)
            .Select(p => new { p.Id, p.TmdbId })
            .ToListAsync(ct);
        var byId = tmdbIds.ToDictionary(p => p.Id, p => p.TmdbId);

        return personIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    /// <summary>
    /// The keyword NAMES that recur most across the user's loved titles. Names, not ids, because
    /// <see cref="Title.Keywords"/> stores names — resolving them costs a search call each, which is
    /// why only the top few are ever resolved.
    /// </summary>
    private async Task<List<string>> TopLovedKeywordsAsync(FeatureVectorBuilder.TasteState taste, CancellationToken ct)
    {
        var lovedIds = taste.TopLovedTitleIds.Concat(taste.LovedAnchorPool).Distinct().Take(KeywordScanTitles).ToList();
        if (lovedIds.Count == 0)
        {
            return [];
        }

        var keywordSets = await db.Titles
            .Where(t => lovedIds.Contains(t.Id))
            .Select(t => t.Keywords)
            .ToListAsync(ct);

        return keywordSets
            .SelectMany(k => k)
            .Select(k => k.Trim().ToLowerInvariant())
            .Where(k => k.Length > 2)
            .GroupBy(k => k)
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(LovedKeywordCount)
            .Select(g => g.Key)
            .ToList();
    }

    /// <summary>Resolves keyword names to TMDB ids, one call each, inside the build's call budget.</summary>
    private async Task<List<int>> ResolveKeywordIdsAsync(
        IReadOnlyList<string> names, CallBudget budget, CancellationToken ct)
    {
        var ids = new List<int>();
        foreach (var name in names)
        {
            if (!budget.TryTake())
            {
                break;
            }

            var matches = await tmdb.SearchKeywordsAsync(name, ct);
            // TMDB orders keyword search by relevance; an exact name match is preferred when present
            // so "anime" does not resolve to "anime convention".
            var best = matches.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? matches.FirstOrDefault();
            if (best is not null)
            {
                ids.Add(best.Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Reactions and ratings this account has produced since the Active artifact was trained — the
    /// "how much of the reservoir has been consumed" signal that buys discovery depth. Ratings count
    /// on <c>FirstRatedAt</c>, not <c>RatedAt</c>: Trakt overwrites the latter on every walk, so a
    /// re-rate would silently move a row back across the boundary.
    /// </summary>
    private async Task<int> ConsumedSinceTrainingAsync(Guid accountId, CancellationToken ct)
    {
        var trainedAt = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .Select(a => (DateTime?)a.TrainedAt)
            .FirstOrDefaultAsync(ct);
        if (trainedAt is not { } since)
        {
            return 0;
        }

        var reactions = await db.UserTitleReactions
            .CountAsync(r => r.AccountId == accountId && r.RevokedAt == null && r.CreatedAt > since, ct);
        var ratings = await db.UserRatings
            .CountAsync(r => r.AccountId == accountId && r.FirstRatedAt > since, ct);
        return reactions + ratings;
    }

    private static DateTime? Latest(DateTime? left, DateTime right) =>
        left is { } value && value > right ? value : right;

    /// <summary>
    /// The build's TMDB allowance. Mutable and shared across arms on purpose: an allowance split
    /// per-arm up front cannot give an arm that needs nothing back to an arm that does.
    /// </summary>
    private sealed class CallBudget(int max)
    {
        public int Used { get; private set; }

        public bool Exhausted => Used >= max;

        public bool TryTake()
        {
            if (Used >= max)
            {
                return false;
            }

            Used++;
            return true;
        }
    }
}
