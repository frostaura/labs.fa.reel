using System.Text.Json;
using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Providers;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
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
    /// <param name="Evidence">
    /// On an ANCHORED row only (because-you-loved, more-like-this): what this card verifiably shares
    /// with the anchor — a person in the same capacity, a keyword, a theme — strongest first, at most
    /// three. It is the evidence <see cref="AnchorRowGate"/> admitted the card on, so every anchored
    /// card carries at least one. Null on every other row.
    /// </param>
    public record FeedCard(
        Guid TitleId, string MediaType, long? TmdbId, string Name, int? Year, int? RuntimeMinutes,
        string? PosterPath, string? BackdropPath, string[] Genres,
        decimal PredictedRating, decimal? CrowdRating, int? CrowdVotes,
        string WhyThis, bool IsReleased, IReadOnlyList<CardProvider> Providers,
        // Optional and trailing on purpose: every other producer of this record — the digest, the
        // paginated lane — is serving a non-hero surface and has no bar to report.
        string? Confidence = null, decimal? FloorUsed = null,
        string? Hook = null, string? LensClause = null, string? Mood = null, CardTexture? Texture = null,
        IReadOnlyList<SharedEvidence>? Evidence = null);

    /// <summary>The deep-cuts row title that may claim "high for you" — see <see cref="DeepCutsTitle"/>.</summary>
    public const string DeepCutsHighForYouTitle = "Deep cuts — under-the-radar, high for you";

    /// <summary>The deep-cuts row title when the row's numbers do not back "high for you".</summary>
    public const string DeepCutsPlainTitle = "Deep cuts — under the radar";

    /// <summary>
    /// The deep-cuts row title: "high for you" only when the row's mean prediction is at or above
    /// the account's median rating (<see cref="AccountRatingMedian"/>). With no ratings there is no
    /// median to clear, so the claim is not made.
    /// </summary>
    internal static string DeepCutsTitle(IReadOnlyCollection<decimal> rowPredictions, decimal? accountMedian) =>
        rowPredictions.Count > 0 && accountMedian is { } median && rowPredictions.Average() >= median
            ? DeepCutsHighForYouTitle
            : DeepCutsPlainTitle;

    public static void MapFeedEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/feed").RequireAccount();

        group.MapGet("/", async (IReelDbContext db, EligibilityQueryBuilder eligibility, FeatureVectorBuilder featureBuilder, ExposureLog exposures, CrossReferenceFactors crossReference, LensService lens, TasteFactsBuilder tasteFacts, RatingCalibrationService calibration, IMemoryCache cache, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            // The measured band behind every "for you" number on the page (FD-4 default): what a
            // displayed prediction has typically been off by against the ratings this account went on
            // to give — the same calibrator, and so the same "± 1.1", the Saved queue shows. Null until
            // the closed loop has pairs: an unmeasured band is not a band.
            var expectedAbsoluteError = await TitleEndpoints.ExpectedAbsoluteErrorAsync(calibration, account.Id, ct);

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
                    expectedAbsoluteError,
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

            // The same holds for a content rule or a "for you" bar saved after the build (XM-03,
            // XM-07): the snapshot was dealt under yesterday's rules, and "airtight on every channel"
            // includes the minutes before the rebuild the save queued. Rules and the maturity ceiling
            // are read from EligibilityQueryBuilder like the footprint; the bar is the build's own
            // comparison (ForYouBar.Clears, held to the badge), so a card the build would not deal
            // now is not served.
            var minPredicted = await ForYouBar.ReadAsync(db, account.Id, ct);
            var forYouBar = minPredicted ?? 0m;
            var allowedByRules = (await eligibility.ContentFilteredTitles(account.Id)
                    .Where(t => snapshotTitleIds.Contains(t.Id))
                    .Select(t => t.Id)
                    .ToListAsync(ct))
                .ToHashSet();
            var ruledOut = items
                .Where(x => !allowedByRules.Contains(x.Title.Id)
                    || !EligibilityQueryBuilder.PassesMaturity(x.Title, account.Settings.MaturityCeiling))
                .Select(x => x.Title.Id)
                .ToHashSet();
            var underBar = items
                .Where(x => !ruledOut.Contains(x.Title.Id) && !ForYouBar.Clears(x.Item.PredictedRating, forYouBar))
                .Select(x => x.Title.Id)
                .ToHashSet();
            excludedTitleIds = [.. excludedTitleIds, .. ruledOut, .. underBar];

            // One clock for the read: the refill's "new" window and the cards' released flag agree.
            var nowUtc = DateTime.UtcNow;

            // A reason written while the bar still relaxed describes cards this read no longer serves.
            var degradedReason = ForYouBar.IsRetiredRelaxation(snapshot.DegradedReason) ? null : snapshot.DegradedReason;
            if (underBar.Count > 0)
            {
                var heldBack = ForYouBar.HeldBackOnRead(forYouBar, underBar.Count);
                degradedReason = degradedReason is null ? heldBack : $"{degradedReason} {heldBack}";
            }

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
                // The deep-cuts row's popularity bound, read off every deep cut the build dealt
                // (including the ones just dropped) before they leave the list — see FeedRowRules.
                var deepCutBound = FeedRowRules.DeepCutBoundFromDealt(
                    items.Where(x => x.Item.Row == FeedRowKind.DeepCuts).Select(x => x.Title));
                items = items.Where(x => !excludedTitleIds.Contains(x.Title.Id)).ToList();

                var needed = removedByRow.Values.Sum();
                if (needed > 0)
                {
                    // Everything still on the feed or just dropped is off-limits for the refill.
                    var onFeed = items.Select(x => x.Title.Id).Concat(excludedTitleIds).ToHashSet();
                    var eligibleIds = eligibility.EligibleTitles(account.Id).Select(t => t.Id);
                    // Over-fetch so the in-memory maturity gate can drop over-ceiling titles AND the
                    // taste prior below can genuinely re-order rather than merely re-shuffle the two
                    // titles that would have been taken anyway — a prior applied to a pool the size
                    // of the hole it fills is arithmetic with no consequence. Renderable-card gate
                    // (poster + tmdbId) mirrors the paginator so a backfill never floats a blank.
                    // The bar as a SQL bound on the stored 2 dp column: exactly ForYouBar.Clears.
                    var lowestClearing = ForYouBar.LowestStoredClearing(forYouBar);
                    var candidates = await (
                        from s in db.TitleScores
                        join t in db.Titles on s.TitleId equals t.Id
                        where s.AccountId == account.Id && s.ModelArtifactId == snapshot.ModelArtifactId
                            && t.TmdbId != null && t.PosterPath != null
                            && !onFeed.Contains(s.TitleId) && eligibleIds.Contains(s.TitleId)
                            && (forYouBar <= 0m || s.PredictedRating >= lowestClearing)
                        orderby s.PredictedRating descending
                        select new { s.PredictedRating, s.ContributionsJson, Title = t })
                        .Take((needed * 6) + 20)
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

                        // The refill is the one path that can put a card on tonight's page without a
                        // build having chosen it, so it has to rank the way the build ranks. The
                        // ordering is the persisted TitleScore — unchanged, still what the card shows
                        // — TIMES the account's taste prior and the title's crowd term, the build's
                        // OrderPrior. Sorted in memory rather than in SQL, because the prior needs the
                        // title's genres and the account's history and neither is expressible in the
                        // ORDER BY above.
                        var prior = TastePrior.From(taste);
                        pool = [.. pool.OrderByDescending(c =>
                            prior.Rank(c.PredictedRating, c.Title) * (decimal)CrowdPrior.FactorFor(c.Title))];

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
                                Tone: toneByTitle.GetValueOrDefault(c.Title.Id),
                                AccountContrarianOffset: taste.OverallContrarianOffset);
                        }
                    }

                    var takenWhys = items
                        .Select(x => x.Item.WhyThisSentence)
                        .ToHashSet(StringComparer.Ordinal);
                    // A refill into an ANCHORED row obeys the same admission rule the build dealt
                    // that row under — the anchor's neighbour radius plus shared evidence — or the
                    // seat stays empty. A because-you-loved row with one unexplainable stranger in it
                    // is the exact failure the gate exists to stop.
                    var admittedByAnchor = new Dictionary<Guid, IReadOnlyDictionary<Guid, AnchorRowGate.Admission>>();
                    foreach (var anchorId in removedByRow.Keys
                                 .Where(k => k.Row == FeedRowKind.BecauseYouLoved && k.AnchorTitleId != null)
                                 .Select(k => k.AnchorTitleId!.Value)
                                 .Distinct())
                    {
                        admittedByAnchor[anchorId] = await AnchorRowGate.AdmitAsync(
                            db, cache, anchorId, pool.Select(c => c.Title.Id).ToList(), ct);
                    }

                    // A refill into a LABELLED kind row obeys the row's own rule the same way: a
                    // "New & for you" seat takes only a recent release and a "Deep cuts" seat only a
                    // title under the radar at the deep-cuts floor, or the seat stays empty — the
                    // label is a claim about every card under it (HREV6-RET-01).
                    bool FitsRow(FeedRowKind row, decimal predicted, Title title) => row switch
                    {
                        FeedRowKind.DeepCuts => FeedRowRules.IsDeepCut(predicted, title, forYouBar, deepCutBound),
                        FeedRowKind.NewForYou => FeedRowRules.IsNewForYou(predicted, title, forYouBar, nowUtc),
                        _ => true,
                    };

                    var placed = new HashSet<Guid>();
                    foreach (var ((row, anchorId), lost) in removedByRow)
                    {
                        var nextRank = items
                            .Where(x => x.Item.Row == row && x.Item.AnchorTitleId == anchorId)
                            .Select(x => x.Item.Rank).DefaultIfEmpty(-1).Max() + 1;
                        var gate = anchorId is { } gatedAnchor ? admittedByAnchor.GetValueOrDefault(gatedAnchor) : null;
                        var fits = pool
                            .Where(c => !placed.Contains(c.Title.Id)
                                && FitsRow(row, c.PredictedRating, c.Title)
                                && (gate is null || (gate.TryGetValue(c.Title.Id, out var admission) && admission.Admitted)))
                            .Take(lost)
                            .ToList();
                        foreach (var c in fits)
                        {
                            placed.Add(c.Title.Id);
                            var backfillRank = nextRank++;
                            var backfillContext = backfillContexts[c.Title.Id];
                            if (gate is not null && gate.TryGetValue(c.Title.Id, out var admitted))
                            {
                                backfillContext = backfillContext with { AnchorEvidence = admitted.Evidence };
                            }

                            var backfillWhy = ExplanationTemplater.BuildDistinctSentence(backfillContext, takenWhys);
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
                                    EvidenceJson = gate is not null && gate.TryGetValue(c.Title.Id, out var refillAdmission)
                                        ? AnchorRowGate.Serialize(refillAdmission.Evidence)
                                        : null,
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
                                // here — it was discarded when the score was written. The breakdown
                                // is left NULL (unknown), never zero; the prediction itself is exact.
                                WhySource: backfillWhy.Length == 0 ? ExposureWhySource.None : ExposureWhySource.Templater,
                                RowKey: FeedPaginator.RowKeyFor(row, anchorId)));
                        }
                    }

                    items = items.OrderBy(x => x.Item.Row).ThenBy(x => x.Item.Rank).ToList();
                }
            }

            // The chips on anchored cards: the evidence each card was admitted on, as the build
            // stored it on the row — no catalogue reads on the Home path, and a chip cannot drift
            // away from the reason the card was dealt for. Only a row written before the evidence
            // was stored (a snapshot the deploy purge has not rebuilt yet) is read live.
            var storedEvidence = new Dictionary<(Guid, Guid), IReadOnlyList<SharedEvidence>>();
            var legacyAnchored = new List<(Guid AnchorId, Guid TitleId)>();
            foreach (var x in items.Where(x => x.Item.Row == FeedRowKind.BecauseYouLoved && x.Item.AnchorTitleId != null))
            {
                if (AnchorRowGate.Deserialize(x.Item.EvidenceJson) is { } stored)
                {
                    storedEvidence[(x.Item.AnchorTitleId!.Value, x.Title.Id)] = stored;
                }
                else
                {
                    legacyAnchored.Add((x.Item.AnchorTitleId!.Value, x.Title.Id));
                }
            }

            var evidence = legacyAnchored.Count == 0
                ? storedEvidence
                : (await AnchorRowGate.EvidenceAsync(db, legacyAnchored, ct))
                    .Concat(storedEvidence)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

            // Best-effort streaming strips from the shared availability cache (populated as titles
            // are viewed; no extra TMDB calls here). Streaming-now offers only (StreamingNowPolicy:
            // subscription, free or ads, 7-day freshness), top 3 by priority.
            var feedTitleIds = items.Select(x => x.Title.Id).ToList();
            var providersByTitle = (await StreamingNowPolicy.Offers(db.TitleAvailabilities, feedTitleIds, account.Region, nowUtc)
                    .Join(db.StreamingProviders, a => a.ProviderId, p => p.Id, (a, p) => new { a.TitleId, p.Name, p.LogoPath, p.DisplayPriority })
                    .ToListAsync(ct))
                .GroupBy(x => x.TitleId)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<CardProvider>)g.DistinctBy(x => x.Name).OrderBy(x => x.DisplayPriority)
                        .Take(3).Select(x => new CardProvider(x.Name, x.LogoPath)).ToList());

            // The bar the build required of the hero, re-derived here from the same shared constant.
            // The never-blank backfill reaches below it when nothing clears it — on live data that is
            // the normal case, not an edge — so the card has to be able to say which side of it a
            // pick sits on.
            var heroBar = HeroConfidence.BarFor(minPredicted);

            // Lens copy for the whole page in one query. READ ONLY — this path never generates:
            // a page load must not be able to start an LLM call, which is the job's work and the
            // on-demand endpoint's. Where nothing has been written the card renders exactly as it
            // did before, on the templater's sentence.
            var profileHash = await tasteFacts.CurrentHashAsync(account.Id, nowUtc, ct);
            var lensByTitle = await lens.ResolveManyAsync(
                account.Id, items.Select(x => x.Title.Id).ToList(), profileHash, ct);

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
                // A persisted line is served only if it passes the serve-time guard: the stub
                // sentence a failed rerank wrote, the retired era template and the a/an defect are
                // shown as no line at all, until the next build replaces them.
                return new(
                    title.Id, title.MediaType.ToString(), title.TmdbId, title.Name, title.Year, title.RuntimeMinutes,
                    title.PosterPath, title.BackdropPath, title.Genres,
                    // The badge's own number (one decimal), so the client's toFixed(1) cannot
                    // print a digit the bar was not held to.
                    ForYouBar.Shown(item.PredictedRating), crowd, crowdVotes, FeedWhyGuard.Servable(item.WhyThisSentence), isReleased,
                    providersByTitle.GetValueOrDefault(title.Id, []),
                    // Rows carry no label: they are selected against the account's own floor, not the
                    // hero bar, so "reaching" would be measuring them against a rule they never had.
                    hero ? HeroConfidence.For(item.PredictedRating, heroBar) : null,
                    hero ? heroBar : null,
                    LensGuards.ServableHook(note),
                    note?.FirstClause(),
                    note?.Mood,
                    textures.GetValueOrDefault(title.Id),
                    item.AnchorTitleId is { } anchorOf
                        ? evidence.GetValueOrDefault((anchorOf, title.Id)) ?? []
                        : null);
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

                // The cross-reference standing at the moment of the serve, stamped here rather than
                // where the card was built: a refill happens minutes or hours after the snapshot,
                // and the pass may well have judged the title in between. What the exposure row has
                // to record is what was known WHEN THE CARD WAS SHOWN, which is now.
                var verdicts = await crossReference.ForAsync(
                    account.Id, rendered.Select(c => c.TitleId).ToList(), ct);
                rendered = rendered
                    .Select(c => verdicts.TryGetValue(c.TitleId, out var v) && v.ExposureLabel is { } label
                        ? c with
                        {
                            XrefVerdict = label,
                            XrefConfidence = Math.Round(
                                (decimal)v.Confidence, 3, MidpointRounding.AwayFromZero),
                        }
                        : c)
                    .ToList();

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
                    degradedReason,
                    expectedAbsoluteError,
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

            // "High for you" is a claim about the row's numbers, so it is checked against them: the
            // row's mean prediction has to reach the account's own median rating. A row of 6.5s
            // under a reader whose median is 7 is under-the-radar, not high for them (2026-10-25).
            var deepCutPredictions = items.Where(x => x.Item.Row == FeedRowKind.DeepCuts)
                .Select(x => x.Item.PredictedRating).ToList();
            var deepCutsTitle = DeepCutsTitle(
                deepCutPredictions,
                deepCutPredictions.Count == 0 ? null : await AccountRatingMedian.ForAsync(db, account.Id, ct));
            AddKindRow(FeedRowKind.DeepCuts, "deep-cuts", deepCutsTitle);
            AddKindRow(FeedRowKind.NewForYou, "new-for-you", "New & for you");

            await LogRefillAsync(items.Select(x => x.Title.Id).ToHashSet());

            return Results.Ok(new
            {
                generatedAt = (DateTime?)snapshot.GeneratedAt,
                hero,
                rows,
                lockedRowCount = 0,
                degradedReason,
                expectedAbsoluteError,
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
                spearman = result.Spearman,
                picksChanged = result.PicksChanged,
            });
        });

        // ── The cross-reference band's payload ──────────────────────────────────────────────
        // 204 whenever there is nothing honest to say — no active model, or a pool nothing has ever
        // judged and nothing is judging right now. The band renders null on a 204, which is the
        // right output for "no news": a band that said "cross-referenced 0 titles" would be the
        // product padding, and the whole reason this surface may quote numbers at all is that it
        // only ever quotes real ones.
        //
        // Free for every tier, on the same reasoning as the track record: a scorecard behind a
        // paywall reads as a scorecard being hidden.
        group.MapGet("/cross-reference", async (
            CrossReferenceStatusQuery status, IAccountContext accountContext, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            if (await status.GetAsync(accountId, ct) is not { } result)
            {
                return Results.NoContent();
            }

            return Results.Ok(new
            {
                running = result.Running,
                judged = result.Judged,
                total = result.Total,
                kept = result.Kept,
                ruledOut = result.RuledOut,
                unsure = result.Unsure,
                noSignal = result.NoSignal,
                gating = result.Gating,
            });
        });

        group.MapPost("/rebuild", async (IReelDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            // Manual refresh is a paid-surface capability, not a founder-only one — every paying tier
            // can force a regeneration (reactive rebuilds cover the rest automatically).
            if (account is null || account.Tier is not (AccountTier.Paid or AccountTier.Founder))
            {
                return EndpointHelpers.Forbidden("paid plan required");
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
        group.MapPost("/more", async (MoreRequestDto body, FeedPaginator paginator, IReelDbContext db, ExposureLog exposures, CrossReferenceFactors crossReference, ISupplyReporter supply, HttpContext http, CancellationToken ct) =>
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

            var pageExcludeIds = (body.PageExcludeTitleIds ?? [])
                .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                .Where(g => g is not null).Select(g => g!.Value).ToList();

            var page = await paginator.MorePageAsync(
                account,
                new MoreFeedRequest(
                    context, anchor, body.Genre, excludeIds, limit, body.MediaType, pageExcludeIds, body.PageToken),
                ct);
            var items = page.Items;

            var nowUtc = DateTime.UtcNow;
            var providers = await ProvidersForAsync(db, account.Region, items.Select(i => i.Title.Id).ToList(), nowUtc, ct);
            var cards = items.Select(i => new FeedCard(
                i.Title.Id, i.Title.MediaType.ToString(), i.Title.TmdbId, i.Title.Name, i.Title.Year, i.Title.RuntimeMinutes,
                i.Title.PosterPath, i.Title.BackdropPath, i.Title.Genres,
                ForYouBar.Shown(i.Predicted), Domain.Catalog.CrowdRating.For(i.Title), Domain.Catalog.CrowdRating.VotesFor(i.Title),
                i.WhyThis, i.IsReleased, providers.GetValueOrDefault(i.Title.Id, []),
                Evidence: i.Evidence)).ToList();

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

                    // What the cross-reference said about these titles at the moment they were
                    // served. A paginated card is scored live, so this is the only place the
                    // standing behind it is ever recorded — and null here honestly means the pass
                    // had not reached the title yet, not that it had nothing to say.
                    var verdicts = await crossReference.ForAsync(
                        account.Id, cards.Select(c => c.TitleId).ToList(), ct);

                    var byTitle = items.ToDictionary(i => i.Title.Id);
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
                            // The scored value, not the card's one-decimal badge: the log keeps 2 dp.
                            byTitle[c.TitleId].Predicted,
                            // The decomposition and the source are carried from where the card was
                            // scored and captioned — the paginator — rather than left at zero and
                            // Unknown, which is what 1,800+ paginated rows used to record.
                            BaseScore: byTitle[c.TitleId].BaseScore,
                            CalibrationDelta: byTitle[c.TitleId].CalibrationDelta,
                            ResidualApplied: byTitle[c.TitleId].ResidualApplied,
                            DemotionApplied: byTitle[c.TitleId].DemotionApplied,
                            LlmFit: byTitle[c.TitleId].LlmFit,
                            WhySentence: c.WhyThis,
                            WhySource: byTitle[c.TitleId].WhySource,
                            RowKey: rowKey,
                            PageIndex: pageIndex,
                            XrefVerdict: verdicts.TryGetValue(c.TitleId, out var verdict)
                                ? verdict.ExposureLabel
                                : null,
                            XrefConfidence: verdicts.TryGetValue(c.TitleId, out var scored)
                                    && scored.ExposureLabel is not null
                                ? Math.Round((decimal)scored.Confidence, 3, MidpointRounding.AwayFromZero)
                                : null)).ToList(),
                        ct);
                }
            }

            // The paginator already tried to deepen this lane straight from TMDB before returning, so an
            // empty page here means the lane is genuinely dry — an explicit exhaustion signal the client
            // can trust (vs a caught 500/429/offline, which it must NOT latch on). A full page implies more.
            // A genre rail that came up short says how much of its genre the account's own
            // conditions hold out (WS-5 task 9, D-16), so "your conditions hide N — see why" can
            // replace a silently thin rail. Counted through WS-3's supply reporter — the same
            // eligibility builder that serves the titles — and only when the rail is short.
            var railIsShort = cards.Count < limit;
            var railSupply = railIsShort && context == FeedRowContext.Genre && !string.IsNullOrWhiteSpace(body.Genre)
                ? await GenreRailSupplyAsync(db, supply, account.Id, body.Genre!, body.MediaType, [.. cards.Select(c => c.TitleId)], ct)
                : null;
            // An anchored rail (more like this, because you loved) that came up short says what its
            // window lost to the anchored gate and to the for-you bar (HREV2-RET-05), so its empty
            // copy names the real cause instead of the account's conditions. Null on every other row
            // and on a full page.
            var anchoredSupply = railIsShort && page.Anchored is { } window
                ? new AnchoredSupply(window.GateHeldBack, window.BelowBar)
                : null;
            return Results.Ok(new { items = cards, exhausted = cards.Count == 0, supply = railSupply, anchoredSupply });
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

    /// <summary>The <c>POST /api/feed/more</c> body.</summary>
    /// <param name="Row">The row slug.</param>
    /// <param name="AnchorTitleId">The row's anchor, for anchored rows.</param>
    /// <param name="Genre">The genre slug, for a genre rail.</param>
    /// <param name="ExcludeTitleIds">Titles THIS row has already shown — also the row's paging depth.</param>
    /// <param name="Limit">Page size, clamped to 1–30.</param>
    /// <param name="MediaType"><c>movie</c>, <c>show</c> or null.</param>
    /// <param name="PageExcludeTitleIds">
    /// Titles already rendered ELSEWHERE on the page (other rails, tonight's picks): excluded from
    /// this slice but not counted as this row's depth.
    /// </param>
    /// <param name="PageToken">
    /// One opaque nonce (≤ 64 chars) per page view, sent on every rail request of that page, so the
    /// server can keep concurrent rails from serving the same title twice. Optional.
    /// </param>
    /// <summary>
    /// The supply behind a short genre rail: titles of the genre no condition touches that are
    /// already seen, and those a standing condition holds out. Null — not a guess — when the account
    /// has an include-genre rule: the rail waives include-genre (FD-6) while the reporter counts it,
    /// so the number would overstate what the conditions hide on this rail.
    /// </summary>
    internal static async Task<RailSupply?> GenreRailSupplyAsync(
        IReelDbContext db, ISupplyReporter supply, Guid accountId, string genre, string? mediaType,
        IReadOnlyCollection<Guid> shown, CancellationToken ct)
    {
        var waivesIncludeGenre = await db.ContentFilters
            .AnyAsync(f => f.AccountId == accountId && f.Kind == FilterKind.IncludeGenre, ct);
        if (waivesIncludeGenre)
        {
            return null;
        }

        var media = mediaType switch
        {
            "movie" => MediaScope.Movies,
            "show" => MediaScope.Shows,
            _ => MediaScope.Any,
        };
        // WS-3's rail form (CR-6): the genre as the slug alone — its aliases, the anime composite and
        // the TV stand-ins, never a title whose NAME contains the word — with the rail's own cards
        // counted as shown rather than as supply still to come.
        var report = await supply.ForGenreRailAsync(accountId, genre, media, shown, ct);
        return new RailSupply(report.ExcludedByConditions, report.AlreadySeen, report.ClearingFloor);
    }

    /// <summary>What a short rail's supply looks like. <c>HiddenByConditions</c> is the N in "your conditions hide N".</summary>
    public sealed record RailSupply(int HiddenByConditions, int AlreadySeen, int ClearingFloor);

    /// <summary>
    /// What a short anchored rail's window lost: <c>GateHeldBack</c> titles above the for-you bar the
    /// anchored gate refused (nothing checkable shared with the anchor, or too far from it), and
    /// <c>BelowBar</c> titles under the bar. Neither is the account's conditions, which the window had
    /// already applied.
    /// </summary>
    public sealed record AnchoredSupply(int GateHeldBack, int BelowBar);

    public record MoreRequestDto(
        string Row, string? AnchorTitleId, string? Genre, string[]? ExcludeTitleIds, int? Limit, string? MediaType = null,
        string[]? PageExcludeTitleIds = null, string? PageToken = null);

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
    /// Streaming-now provider strip (<see cref="StreamingNowPolicy"/>: subscription, free or ads,
    /// 7-day freshness; top 3 by display priority) for a batch of titles. Internal because the digest and blend surfaces reuse the exact same read —
    /// a second copy of this query is how the read-side TTL and the write-side TTL drift.
    /// </summary>
    internal static async Task<Dictionary<Guid, IReadOnlyList<CardProvider>>> ProvidersForAsync(
        IReelDbContext db, string region, IReadOnlyList<Guid> titleIds, DateTime nowUtc, CancellationToken ct)
    {
        if (titleIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<CardProvider>>();
        }

        return (await StreamingNowPolicy.Offers(db.TitleAvailabilities, titleIds, region, nowUtc)
                .Join(db.StreamingProviders, a => a.ProviderId, p => p.Id, (a, p) => new { a.TitleId, p.Name, p.LogoPath, p.DisplayPriority })
                .ToListAsync(ct))
            .GroupBy(x => x.TitleId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CardProvider>)g.DistinctBy(x => x.Name).OrderBy(x => x.DisplayPriority)
                    .Take(3).Select(x => new CardProvider(x.Name, x.LogoPath)).ToList());
    }
}
