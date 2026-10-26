using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The two founder-reported feed-build defects of 2026-08-07, pinned end-to-end on real Postgres.
///
/// <b>Empty feed.</b> The founder combined exclude-documentary, crowd ≥ 6.0, for-you ≥ 5.5,
/// year ≥ 2000 and English and got <c>hero: 0, rows: 0</c> from a job that reported Succeeded —
/// while ~250 eligible, scorable candidates sat in the pool. The proximate cause was the online
/// residual head saturating its clamp (covered arithmetically in <c>OnlineResidualSaturationTests</c>),
/// which pushed every prediction ~3 points down until nothing could clear the account's
/// MinPredictedRating. This test pins the structural half: whatever the model's output scale does,
/// a build with a healthy candidate pool must never produce a SILENT blank: when the account's own
/// "for you" bar holds every card back, the snapshot says so (the bar itself holds — FD-4, XM-07).
///
/// <b>Frozen anchors.</b> "Because you loved The Great Wall / The Bone Collector" never changed,
/// because the anchors were the strict top of a rating-first order that a large rating history
/// freezes permanently. A user-forced refresh over identical ratings must anchor differently.
///
/// <b>Churning anchors</b> — the over-correction of the fix for the previous defect. Rotating per
/// build meant the 5-minute delta-sync poll re-dealt the page up to 3×/hour in QA. The stable
/// "tonight" contract pinned here: a same-day background rebuild with unchanged inputs writes no
/// new snapshot at all; a user reaction vacates exactly its own slot and keeps the rest; a manual
/// refresh still visibly rotates.
///
/// Skips when REEL_TEST_CONNECTION_STRING is unset (no DB in the run).
/// </summary>
[Collection("postgres")]
public class BuildFeedNeverEmptyTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    /// <summary>
    /// The plot direction this test instance's loved titles and candidates share. Drawn from the top
    /// of the 1536 axes, which the per-title fixtures elsewhere in the collection (axis 0…N) never
    /// reach, so an anchor's 50-neighbour radius here is made of this test's own titles.
    /// </summary>
    private readonly int _sharedAxis = Random.Shared.Next(900, 1536);

    /// <summary>Above anything the seeded model can predict — so the strict floor admits nothing.</summary>
    private const decimal UnreachableFloor = 9.9m;

    [Fact]
    public async Task A_bar_nothing_clears_deals_no_card_under_it_and_says_so()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // The structural half of the empty-feed defect, re-stated under FD-4's default (XM-07): the
        // bar is the user's, so a build whose healthy pool holds nothing above it deals nothing below
        // it — and the page is never SILENTLY empty: the reason names the bar. Until 2026-10-26 the
        // build relaxed the bar to the shortlist's 80th percentile here and dealt the cards under it.
        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);
        await AddContentFilterAsync(accountId, FilterKind.MinPredictedRating, ContentFilterRules.FormatRating(UnreachableFloor));

        await RunBuildAsync(accountId);

        await using var db = Fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        var items = await db.FeedItems.Where(i => i.FeedSnapshotId == snapshot.Id).ToListAsync();

        Assert.All(items, i => Assert.True(i.PredictedRating >= UnreachableFloor, $"{i.PredictedRating} is under the bar"));
        Assert.NotNull(snapshot.DegradedReason);
        Assert.Contains("9.9", snapshot.DegradedReason);
        Assert.DoesNotContain("relaxed", snapshot.DegradedReason);
    }

    [Fact]
    public async Task Every_card_the_build_plans_is_written_to_the_exposure_log_exactly_once()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // The build is the primary write site, and it is the one where a duplicate is structurally
        // impossible: the snapshot id is minted inside the same method. What this pins is the other
        // half — that the exposure log is a FAITHFUL copy of what was planned, card for card, with
        // the hero separated from the rails so "does the hero convert" stays answerable.
        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);

        await RunBuildAsync(accountId);

        await using var db = Fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        var items = await db.FeedItems.Where(i => i.FeedSnapshotId == snapshot.Id).ToListAsync();
        var exposures = await db.ExposureEvents.Where(e => e.AccountId == accountId).ToListAsync();

        Assert.NotEmpty(items);
        Assert.Equal(items.Count, exposures.Count);
        Assert.Equal(
            items.Select(i => i.TitleId).OrderBy(id => id).ToList(),
            exposures.Select(e => e.TitleId).OrderBy(id => id).ToList());

        Assert.All(exposures, e =>
        {
            Assert.Equal(snapshot.Id, e.FeedSnapshotId);
            Assert.Equal(snapshot.ModelArtifactId, e.ModelArtifactId);
            Assert.NotEqual(0m, e.PredictedRating);
            Assert.False(string.IsNullOrWhiteSpace(e.WhySource));
        });

        var heroTitleIds = items.Where(i => i.Row == FeedRowKind.Hero).Select(i => i.TitleId).ToHashSet();
        Assert.NotEmpty(heroTitleIds);
        Assert.All(
            exposures.Where(e => heroTitleIds.Contains(e.TitleId)),
            e => Assert.Equal(ExposureSurface.FeedHero, e.Surface));
        Assert.All(
            exposures.Where(e => !heroTitleIds.Contains(e.TitleId)),
            e => Assert.Equal(ExposureSurface.FeedRow, e.Surface));

        // The number in the log is the model's claim at two decimals, not the card's one-decimal
        // rendering: a calibration join spanning both resolutions inherits a 0.05 quantisation
        // floor, so one resolution is chosen and it is this one.
        var sample = exposures.First(e => heroTitleIds.Contains(e.TitleId));
        var card = items.Single(i => i.TitleId == sample.TitleId);
        Assert.True(Math.Abs(sample.PredictedRating - card.PredictedRating) <= 0.05m);
    }

    [Fact]
    public async Task A_suppressed_no_op_rebuild_logs_nothing_and_the_prune_leaves_the_log_alone()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // Two founder complaints meet here. Eight rebuilds fired in nine minutes of queueing, and
        // the ones that changed nothing rendered nothing — so counting them as exposures would
        // inflate every denominator on the track record eightfold. And the 7-snapshot prune that
        // evicted the whole of that session's history must no longer be able to touch the durable
        // record, which is the condition on keeping the prune at 7 at all.
        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);

        await RunBuildAsync(accountId);

        Guid firstSnapshotId;
        int afterFirstBuild;
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            firstSnapshotId = (await ActiveSnapshotAsync(db, accountId)).Id;
            afterFirstBuild = await db.ExposureEvents.CountAsync(e => e.AccountId == accountId);
        }

        // A background rebuild over unchanged inputs: FeedBuildPolicy returns Preserve, SameFeed
        // matches, and AssembleAsync returns before it ever reaches the snapshot write.
        await RunBuildAsync(accountId);

        await using var verify = Fixture.CreateContext(new AccountContext());
        Assert.Equal(firstSnapshotId, (await ActiveSnapshotAsync(verify, accountId)).Id);
        Assert.Equal(afterFirstBuild, await verify.ExposureEvents.CountAsync(e => e.AccountId == accountId));

        // And the exposures still point at a snapshot id even after it could be pruned — the
        // reference is soft by design, so nothing cascades.
        Assert.All(
            await verify.ExposureEvents.Where(e => e.AccountId == accountId).ToListAsync(),
            e => Assert.Equal(firstSnapshotId, e.FeedSnapshotId));
    }

    [Fact]
    public async Task A_build_that_clears_the_floor_is_not_marked_degraded()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // The control case: the relaxation must be reserved for the broken path. A feed that honours
        // the user's floor on its own must carry no reason at all, or the UI would cry wolf forever.
        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);

        await RunBuildAsync(accountId);

        await using var db = Fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        Assert.Null(snapshot.DegradedReason);
        Assert.NotEmpty(await db.FeedItems.Where(i => i.FeedSnapshotId == snapshot.Id).ToListAsync());
    }

    [Fact]
    public async Task A_manual_refresh_supersedes_the_snapshot_and_anchors_on_different_titles()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);

        await RunBuildAsync(accountId);
        var firstSnapshotId = (await ActiveSnapshotIdAndCountAsync(accountId)).ActiveId;
        var first = await AnchorsOfActiveSnapshotAsync(accountId);

        // Nothing about the account changes between the two builds — same ratings, same catalogue.
        // Under the old rating-first selection this returned an identical anchor set for ever; under
        // day-stability alone it would be suppressed. The refresh button must beat both: a new
        // snapshot, rotated away from the one it replaces.
        await RunBuildAsync(accountId, FeedBuildPolicy.ManualRefreshReason);
        var (secondSnapshotId, _) = await ActiveSnapshotIdAndCountAsync(accountId);
        var second = await AnchorsOfActiveSnapshotAsync(accountId);

        Assert.NotEqual(firstSnapshotId, secondSnapshotId);
        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public async Task A_same_day_background_rebuild_with_unchanged_inputs_writes_no_new_snapshot()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);

        await RunBuildAsync(accountId);
        var (firstActiveId, firstCount) = await ActiveSnapshotIdAndCountAsync(accountId);

        // The founder's churn case: the 5-minute delta-sync poll found nothing new, rebuilt anyway,
        // and re-dealt the page. Now the rebuild runs, re-scores, assembles the identical feed —
        // and suppresses the write, so the page the user is reading does not even flicker.
        await RunBuildAsync(accountId, "delta-sync");
        var (secondActiveId, secondCount) = await ActiveSnapshotIdAndCountAsync(accountId);

        Assert.Equal(firstActiveId, secondActiveId);
        Assert.Equal(firstCount, secondCount);
    }

    [Fact]
    public async Task Rating_a_hero_title_vacates_only_its_slot_and_keeps_the_rest_of_the_hero()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // ISOLATION, for the reason spelled out on the dislike-demotion test below: the catalogue is
        // GLOBAL, so in a full run these 80 candidates compete against every other class's fixtures
        // for a shortlist cut far smaller than the pool. The rails are then dealt out of a set this
        // test does not control, and whether the reactive rebuild happens to re-deal them is a
        // property of the run's ordering rather than of Preserve's continuity contract — which is
        // exactly the flake this test showed. The allow-list is the product's own "only these
        // titles are eligible" mechanism; not one assertion below changed.
        var fixtureGenre = $"hero-vacancy-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        await AddContentFilterAsync(accountId, FilterKind.IncludeGenre, fixtureGenre);
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80, genre: fixtureGenre);
        await SeedActiveArtifactAsync(accountId);

        await RunBuildAsync(accountId);
        var heroBefore = await HeroOfActiveSnapshotAsync(accountId);
        var anchorsBefore = await AnchorsOfActiveSnapshotAsync(accountId);
        Assert.True(heroBefore.Count >= 2, "the seeded pool must fill more than one hero slot for this test to mean anything.");

        // The user rates a hero pick from the card — "seen" by the eligibility rule, so it must
        // leave the feed immediately. This is the reactive rebuild ReactionEndpoints enqueues.
        var ratedTitleId = heroBefore[heroBefore.Count / 2];
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = ratedTitleId,
                SubjectType = RatingSubjectType.Movie,
                Rating = 9,
                RatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await RunBuildAsync(accountId, "in-app rating");
        var heroAfter = await HeroOfActiveSnapshotAsync(accountId);
        var anchorsAfter = await AnchorsOfActiveSnapshotAsync(accountId);

        // Exactly the rated slot vacated: the surviving picks keep their order at the front, the
        // backfill lands behind them, and the rails the user was reading do not re-deal.
        Assert.DoesNotContain(ratedTitleId, heroAfter);
        var expectedKept = heroBefore.Where(id => id != ratedTitleId).ToList();
        Assert.Equal(expectedKept, heroAfter.Take(expectedKept.Count));
        Assert.Equal(heroBefore.Count, heroAfter.Count); // the vacated slot was backfilled
        Assert.True(anchorsBefore.SetEquals(anchorsAfter), "a reaction on a hero pick must not re-deal the rails.");
    }

    [Fact]
    public async Task A_preserve_rebuild_never_grows_a_rail_the_page_did_not_have()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // The generalisation of the defect the hero-vacancy test above catches only when the day's
        // rotation offset happens to expose it: a rail that was DROPPED for thinness leaves no
        // FeedItem, so a Preserve rebuild reading its anchors back from the page cannot tell that
        // slot from one a user action vacated, and re-draws it. The page then GROWS a row — a new
        // heading and ten new cards — on a background rebuild the user never asked for, which is
        // precisely what the stable-"tonight" contract exists to prevent.
        //
        // Deterministic by construction, with no dependence on which anchor the rotation lands on:
        // every loved title shares the candidates' genre, so ANY anchor the re-draw picks would
        // deal a rail. The first build is starved into fewer than three rails by supply alone, more
        // supply then arrives (a catalogue seed, an enrichment pass — the ordinary case), and the
        // rebuild must still show the page the user is looking at.
        var fixtureGenre = $"rail-growth-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        await AddContentFilterAsync(accountId, FilterKind.IncludeGenre, fixtureGenre);
        await SeedLovedTitlesAsync(accountId, 12, genre: fixtureGenre);
        await SeedEligibleTitlesAsync(22, genre: fixtureGenre);
        await SeedActiveArtifactAsync(accountId);

        await RunBuildAsync(accountId);
        var anchorsBefore = await AnchorsOfActiveSnapshotAsync(accountId);
        Assert.InRange(anchorsBefore.Count, 0, 2); // starved: this is the dropped-rail state under test

        // Supply arrives between the two builds. Nothing the user did — so nothing on the page moves.
        await SeedEligibleTitlesAsync(60, genre: fixtureGenre);

        await RunBuildAsync(accountId);
        var anchorsAfter = await AnchorsOfActiveSnapshotAsync(accountId);

        Assert.True(
            anchorsBefore.SetEquals(anchorsAfter),
            $"a Preserve rebuild must not add a rail: had {anchorsBefore.Count}, now {anchorsAfter.Count}.");
    }

    private async Task<(Guid ActiveId, int Count)> ActiveSnapshotIdAndCountAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var activeId = (await ActiveSnapshotAsync(db, accountId)).Id;
        return (activeId, await db.FeedSnapshots.CountAsync(s => s.AccountId == accountId));
    }

    /// <summary>The Active snapshot's hero title ids, in rank order.</summary>
    private async Task<List<Guid>> HeroOfActiveSnapshotAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        return await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshot.Id && i.Row == FeedRowKind.Hero)
            .OrderBy(i => i.Rank)
            .Select(i => i.TitleId)
            .ToListAsync();
    }

    private async Task<HashSet<Guid>> AnchorsOfActiveSnapshotAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        return (await db.FeedItems
                .Where(i => i.FeedSnapshotId == snapshot.Id && i.AnchorTitleId != null)
                .Select(i => i.AnchorTitleId!.Value)
                .Distinct()
                .ToListAsync())
            .ToHashSet();
    }

    private static async Task<FeedSnapshot> ActiveSnapshotAsync(ReelDbContext db, Guid accountId) =>
        await db.FeedSnapshots.FirstAsync(s => s.AccountId == accountId && s.Status == SnapshotStatus.Active);

    [Fact]
    public async Task Llm_whys_are_persisted_at_build_time_and_a_preserve_rebuild_never_recalls_the_agent()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);

        // ── Fresh build: the agent's canned per-title lines land on the persisted FeedItems ────
        var agent = new CannedWhyAgent();
        await RunBuildAsync(accountId, agent: agent);

        await using var db = Fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        var items = await db.FeedItems.Where(i => i.FeedSnapshotId == snapshot.Id).ToListAsync();

        Assert.NotEmpty(items);
        Assert.True(agent.RerankCalls > 0, "the Fresh build should have curated whys through the agent");
        Assert.All(items, i => Assert.StartsWith("Shares the slow-burn dread you love", i.WhyThisSentence));

        // And no rail repeats a sentence — the founder's verbatim complaint.
        foreach (var rail in items.GroupBy(i => (i.Row, i.AnchorTitleId)))
        {
            Assert.Equal(rail.Count(), rail.Select(i => i.WhyThisSentence).Distinct().Count());
        }

        // ── Same-day background rebuild (Preserve): kept cards keep their whys, zero new spend ─
        var callsAfterFresh = agent.RerankCalls;
        await RunBuildAsync(accountId, agent: agent);

        var snapshotAfter = await ActiveSnapshotAsync(db, accountId);
        var itemsAfter = await db.FeedItems.Where(i => i.FeedSnapshotId == snapshotAfter.Id).ToListAsync();

        Assert.Equal(callsAfterFresh, agent.RerankCalls);
        Assert.All(itemsAfter, i => Assert.StartsWith("Shares the slow-burn dread you love", i.WhyThisSentence));
    }

    [Fact]
    public async Task An_anti_recommendation_agent_line_is_discarded_for_the_templater_sentence()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // Live regression 2026-08-10: the agent's honest negatives ("You may bounce off the
        // home-invasion gore…") were accepted verbatim onto hero and rail cards. A pick surface
        // must never argue against its own pick — the line is discarded for the templater fallback.
        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);

        var agent = new AntiRecommendationWhyAgent();
        await RunBuildAsync(accountId, agent: agent);

        await using var db = Fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        var items = await db.FeedItems.Where(i => i.FeedSnapshotId == snapshot.Id).ToListAsync();

        Assert.NotEmpty(items);
        Assert.True(agent.RerankCalls > 0, "the build should have consulted the agent");
        Assert.All(items, i => Assert.DoesNotContain("bounce off", i.WhyThisSentence, StringComparison.OrdinalIgnoreCase));

        // The refused line is a failed model pass: an anchored card falls back to its templated
        // evidence line, every other card shows no line rather than soft templater copy.
        Assert.All(items.Where(i => i.AnchorTitleId != null), i => Assert.False(string.IsNullOrWhiteSpace(i.WhyThisSentence)));
        Assert.All(items.Where(i => i.AnchorTitleId == null), i =>
        {
            Assert.Equal("Fallback", i.WhySource);
            Assert.Equal(string.Empty, i.WhyThisSentence);
        });
    }

    [Fact]
    public async Task A_low_fit_hero_is_swapped_for_the_next_spare_candidate()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // The hero coherence floor: the agent judges every original hero pick a poor fit (≤ 0.3),
        // so each slot must be re-dealt to a spare candidate — and the replacements carry their own
        // accepted agent lines, not the condemned picks' captions.
        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);

        var agent = new LowFitFirstHeroBatchAgent();
        await RunBuildAsync(accountId, agent: agent);

        await using var db = Fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        var items = await db.FeedItems.Where(i => i.FeedSnapshotId == snapshot.Id).ToListAsync();
        var hero = items.Where(i => i.Row == FeedRowKind.Hero).OrderBy(i => i.Rank).ToList();

        Assert.NotEmpty(hero);
        Assert.NotEmpty(agent.CondemnedTitleIds);
        Assert.Empty(hero.Select(i => i.TitleId).Intersect(agent.CondemnedTitleIds));
        Assert.All(hero, i => Assert.StartsWith("Better pick for tonight", i.WhyThisSentence));

        // The swap re-seats the hero only — no condemned title may sneak onto another rail either,
        // and no title appears twice across the feed.
        Assert.Equal(items.Count, items.Select(i => i.TitleId).Distinct().Count());
    }

    [Fact]
    public async Task A_seen_enough_dislike_keeps_similar_titles_out_of_the_hero()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // Phase 8b: the founder's "dislikes must be taken into account", pinned on the feed build.
        // Eight otherwise-equivalent candidates; the victim's plot embedding duplicates a declined
        // (seen-enough) title's, the rest are mutually orthogonal. Whatever base score the model
        // hands the victim, the dislike demotion (~1.2 for an identical plot) must keep it out of
        // the hero while its seven clean twins fill the slots.
        //
        // ISOLATION, and why it is not optional: this database is shared with every other
        // integration test, so in a full run the catalogue reaches ~3,700 titles while the shortlist
        // cut is 220. Without isolation these nine compete for a slot against unrelated fixtures,
        // and whether they get one is a property of the synthetic model's random splits rather than
        // of the behaviour under test — it passed for as long as it did by luck, and it went red the
        // moment FeatureSchema v4 renumbered the vector and moved those splits. Worse, the failure
        // mode was silent: with the victim never scored at all, "the victim is not in the hero"
        // passes vacuously. An IncludeGenre allow-list is the product's OWN mechanism for "only
        // these titles are eligible", so the test borrows it instead of inventing something.
        const string fixtureGenre = "dislike-demotion-Fixture";
        var accountId = await SeedAccountAsync();
        await AddContentFilterAsync(accountId, FilterKind.IncludeGenre, fixtureGenre);
        await SeedLovedTitlesAsync(accountId, 12);

        Guid victimId;
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var disliked = NewTitle("declined saturation", popularity: 1.5m, genre: fixtureGenre);
            db.Add(disliked);
            db.TitleEmbeddings.Add(NewEmbedding(disliked.Id, axis: 0));
            db.UserTitleReactions.Add(new UserTitleReaction
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = disliked.Id,
                Kind = ReactionKind.NotInterested,
                Reason = ReactionReason.SeenEnough,
                CreatedAt = DateTime.UtcNow,
            });

            var victim = NewTitle("victim candidate", popularity: 2.5m, genre: fixtureGenre);
            victimId = victim.Id;
            db.Add(victim);
            db.TitleEmbeddings.Add(NewEmbedding(victim.Id, axis: 0)); // cosine 1 to the disliked plot

            for (var i = 1; i <= 7; i++)
            {
                var clean = NewTitle($"clean candidate {i}", popularity: 2m + (i * 0.001m), genre: fixtureGenre);
                db.Add(clean);
                db.TitleEmbeddings.Add(NewEmbedding(clean.Id, axis: i)); // orthogonal to everything
            }

            await db.SaveChangesAsync();
        }

        await SeedActiveArtifactAsync(accountId);
        await RunBuildAsync(accountId);

        await using var verify = Fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(verify, accountId);
        var items = await verify.FeedItems.Where(i => i.FeedSnapshotId == snapshot.Id).ToListAsync();
        var heroIds = items.Where(i => i.Row == FeedRowKind.Hero).Select(i => i.TitleId).ToList();

        Assert.NotEmpty(heroIds);

        // The victim must have been SCORED before "not in the hero" means anything: a title that
        // never reached the 220-title shortlist is absent from the hero for a reason that has
        // nothing to do with the dislike demotion this test exists to prove.
        var victimScores = await verify.TitleScores
            .Where(s => s.AccountId == accountId && s.TitleId == victimId)
            .Select(s => s.PredictedRating)
            .ToListAsync();
        Assert.True(victimScores.Count == 1, "the victim must reach the shortlist for this test to mean anything");
        Assert.DoesNotContain(victimId, heroIds);

        // And the demotion is visible in the persisted scores: the victim sits clearly below every
        // hero pick, not coincidentally just past the cut line.
        var victimScore = victimScores[0];
        var heroFloor = await verify.TitleScores
            .Where(s => s.AccountId == accountId && heroIds.Contains(s.TitleId))
            .MinAsync(s => s.PredictedRating);
        Assert.True(
            heroFloor - victimScore >= 0.5m,
            $"victim ({victimScore}) should sit well below the weakest hero pick ({heroFloor})");
    }

    /// <summary>A unit plot embedding along one axis — identical (cos 1) or orthogonal (cos 0) pairs.</summary>
    private static TitleEmbedding NewEmbedding(Guid titleId, int axis)
    {
        var vector = new float[1536];
        vector[axis] = 1f;
        return new TitleEmbedding
        {
            TitleId = titleId,
            Embedding = new Pgvector.Vector(vector),
            EmbeddingModel = "test",
            SourceTextHash = $"hash-{titleId:N}",
            CreatedAt = DateTime.UtcNow,
        };
    }

    [Fact]
    public async Task A_failed_rerank_never_reaches_a_card_the_fit_cache_or_the_log_as_model_output()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // Every batch fails the way a truncated completion does: WS-1's agent hands back its stub —
        // fit = predicted/10 and "A {genre} pick aligned with your taste." — labelled Fallback.
        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);
        using var fitCache = new MemoryCache(new MemoryCacheOptions());
        var agent = new SourcedAgent(ResultSource.Fallback, ResultSource.Fallback);

        await RunBuildAsync(accountId, agent: agent, fitCache: fitCache);

        Assert.True(agent.RerankCalls > 0); // the passes ran — the guard below is not vacuous
        await using var db = Fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        var items = await db.FeedItems.Where(i => i.FeedSnapshotId == snapshot.Id).ToListAsync();
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.DoesNotContain("aligned with your taste", i.WhyThisSentence));
        Assert.All(items, i => Assert.DoesNotMatch("^A [aeiou]", i.WhyThisSentence));

        // Nothing was cached: a fallback replayed for six hours is how one truncated batch became
        // 281 Cache-sourced stub exposures.
        Assert.All(items, i => Assert.False(fitCache.TryGetValue(
            FeedPaginator.FitKey(((ISearchAgent)agent).CacheSalt(ApiUsagePurpose.RerankFeed), accountId, i.TitleId, FeedPaginator.RowKeyFor(i.Row, i.AnchorTitleId)), out _)));

        var exposures = await db.ExposureEvents.Where(e => e.AccountId == accountId).ToListAsync();
        Assert.NotEmpty(exposures);
        Assert.DoesNotContain(exposures, e => e.WhySource is "Llm" or "Cache" or "Unknown");
        Assert.Contains(exposures, e => e.WhySource == "Fallback");
        Assert.All(exposures, e => Assert.Null(e.LlmFit));
        Assert.All(exposures, e => Assert.NotNull(e.BaseScore)); // the build knows its breakdown

        // A failed pass shows the templated ANCHORED line or nothing — and the snapshot stores which.
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.WhySource)));
        Assert.All(items.Where(i => i.AnchorTitleId == null && i.WhySource == "Fallback"), i => Assert.Equal(string.Empty, i.WhyThisSentence));
        Assert.All(items.Where(i => i.AnchorTitleId != null), i => Assert.False(string.IsNullOrWhiteSpace(i.WhyThisSentence)));

        // The model was handed each anchored card's checked link, naming the anchor by title.
        var anchoredInputs = agent.Inputs.Where(x => x.Anchor is not null).ToList();
        Assert.NotEmpty(anchoredInputs);
        Assert.All(anchoredInputs, x => Assert.Contains(
            x.Input.Context!.TopContributions,
            c => c.Contains(x.Anchor!, StringComparison.Ordinal) && !c.Contains("anchor", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_voice_guard_rejection_keeps_the_models_fit_and_drops_only_its_sentence()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // The judged fit is real; only the sentence was replaced (WhySource=Fallback). The fit may
        // be logged and cached; the sentence may not be shown.
        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);
        var agent = new SourcedAgent(ResultSource.Llm, ResultSource.Fallback);

        await RunBuildAsync(accountId, agent: agent);

        await using var db = Fixture.CreateContext(new AccountContext());
        var exposures = await db.ExposureEvents.Where(e => e.AccountId == accountId).ToListAsync();
        Assert.Contains(exposures, e => e.LlmFit == 0.77m);
        Assert.DoesNotContain(exposures, e => e.WhySource is "Llm" or "Cache" or "Unknown");
        Assert.All(exposures, e => Assert.DoesNotContain(SourcedAgent.Sentence, e.WhySentence ?? string.Empty));
    }

    [Fact]
    public async Task Every_anchored_card_is_inside_its_anchors_radius_with_shared_evidence_and_anchors_are_nines_and_tens()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12); // ratings cycle 8, 9, 10
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);

        await RunBuildAsync(accountId);

        await using var db = Fixture.CreateContext(new AccountContext());
        var snapshot = await ActiveSnapshotAsync(db, accountId);
        var anchored = await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshot.Id && i.Row == FeedRowKind.BecauseYouLoved)
            .ToListAsync();
        Assert.NotEmpty(anchored);

        var evidence = await AnchorRowGate.EvidenceAsync(
            db, anchored.Select(i => (i.AnchorTitleId!.Value, i.TitleId)).ToList(), CancellationToken.None);
        Assert.All(anchored, i => Assert.NotEmpty(evidence[(i.AnchorTitleId!.Value, i.TitleId)]));

        // The admitting evidence is STORED on the row — it is what the chips render and what the
        // deploy purge reads — and equals what the gate found; every row stores its why-source.
        Assert.All(anchored, i => Assert.Equal(
            evidence[(i.AnchorTitleId!.Value, i.TitleId)], AnchorRowGate.Deserialize(i.EvidenceJson)));
        Assert.All(
            await db.FeedItems.Where(i => i.FeedSnapshotId == snapshot.Id).ToListAsync(),
            i => Assert.NotEqual(WhySourceKind.Unknown, ExposureWhySource.Parse(i.WhySource).Kind));

        foreach (var rail in anchored.GroupBy(i => i.AnchorTitleId!.Value))
        {
            var admissions = await AnchorRowGate.AdmitAsync(
                db, cache: null, rail.Key, rail.Select(i => i.TitleId).ToList(), CancellationToken.None);
            Assert.All(rail, i => Assert.True(admissions[i.TitleId].Admitted));

            var anchorRating = await db.UserRatings
                .Where(r => r.AccountId == accountId && r.TitleId == rail.Key)
                .Select(r => r.Rating)
                .SingleAsync();
            Assert.True(anchorRating >= 9, $"anchor rated {anchorRating}");
        }
    }

    [Fact]
    public async Task The_deploy_purge_replaces_a_legacy_snapshots_stub_lines_that_a_preserve_build_would_keep()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);
        await RunBuildAsync(accountId);

        // The pre-deploy state: tonight's snapshot carries the stub a failed rerank wrote and the
        // cache and Preserve path carried for days.
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            var snapshot = await ActiveSnapshotAsync(db, accountId);
            foreach (var item in await db.FeedItems.Where(i => i.FeedSnapshotId == snapshot.Id).Take(5).ToListAsync())
            {
                item.WhyThisSentence = "A action pick aligned with your taste.";
            }

            await db.SaveChangesAsync();
        }

        // An ordinary same-day rebuild is Preserve, and its no-op suppression never re-reads a
        // kept caption — which is exactly why the purge has to force Fresh.
        await RunBuildAsync(accountId, "delta-sync");
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            Assert.Single(await UntrustedSnapshotSweep.FindAsync(db, CancellationToken.None), f => f.AccountId == accountId);
        }

        await RunBuildAsync(accountId, FeedBuildPolicy.TrustPurgeReason);

        await using var verify = Fixture.CreateContext(new AccountContext());
        var rebuilt = await ActiveSnapshotAsync(verify, accountId);
        var whys = await verify.FeedItems.Where(i => i.FeedSnapshotId == rebuilt.Id).Select(i => i.WhyThisSentence).ToListAsync();
        Assert.NotEmpty(whys);
        Assert.DoesNotContain(whys, w => w.Contains("aligned with your taste", StringComparison.Ordinal));
        Assert.DoesNotContain(await UntrustedSnapshotSweep.FindAsync(verify, CancellationToken.None), f => f.AccountId == accountId);
    }

    [Fact]
    public async Task An_anchor_whose_neighbourhood_cannot_fill_a_rail_is_passed_over_for_the_next()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // Six fresh 10s at the head of the loved pool, each alone in its own direction of the plot
        // space: nothing eligible sits inside their radius, so no honest rail can be dealt on them.
        var accountId = await SeedAccountAsync();
        await SeedLovedTitlesAsync(accountId, 12);
        var orphans = new List<Guid>();
        await using (var db = Fixture.CreateContext(new AccountContext()))
        {
            for (var i = 0; i < 6; i++)
            {
                var orphan = NewTitle($"orphan {i}", popularity: 1.2m, "drama");
                orphan.Keywords = [$"orphan-{i}"];
                db.Add(orphan);
                db.TitleEmbeddings.Add(NewEmbedding(orphan.Id, 600 + (_sharedAxis % 290) + i));
                db.UserRatings.Add(new UserRating
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = orphan.Id,
                    SubjectType = RatingSubjectType.Movie,
                    Rating = 10,
                    RatedAt = DateTime.UtcNow.AddDays(-1),
                });
                orphans.Add(orphan.Id);
            }

            await db.SaveChangesAsync();
        }

        await SeedEligibleTitlesAsync(80);
        await SeedActiveArtifactAsync(accountId);

        await RunBuildAsync(accountId);

        var anchors = await AnchorsOfActiveSnapshotAsync(accountId);
        Assert.NotEmpty(anchors);
        Assert.Empty(anchors.Intersect(orphans));
    }

    /// <summary>Answers every batch with fixed sources — a stand-in for each WS-1 failure shape.</summary>
    private sealed class SourcedAgent(ResultSource fitSource, ResultSource whySource) : ISearchAgent
    {
        public const string Sentence = "This is the line the source says may not be shown";

        public int RerankCalls;

        /// <summary>Every (anchor name, candidate) pair the build handed the model.</summary>
        public System.Collections.Concurrent.ConcurrentBag<(string? Anchor, RerankInput Input)> Inputs { get; } = [];

        public bool IsAvailable => true;

        public string ModelId => "sourced-agent";

        public Task<AgentReply> InterpretAsync(IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles, CancellationToken ct = default, string? standingFilters = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null, string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default)
        {
            Interlocked.Increment(ref RerankCalls);
            foreach (var title in titles)
            {
                Inputs.Add((anchorName, title));
            }

            return Task.FromResult<IReadOnlyList<RerankResult>>(titles
                .Select(t => fitSource == ResultSource.Fallback
                    ? new RerankResult(
                        t.TitleId, (double)(t.Predicted ?? 6m) / 10d, ResultSource.Fallback,
                        $"A {t.Genres.FirstOrDefault() ?? "drama"} pick aligned with your taste.", ResultSource.Fallback)
                    : new RerankResult(t.TitleId, 0.77d, fitSource, $"{Sentence} {t.TitleId:N}", whySource))
                .ToList());
        }
    }

    /// <summary>Counts rerank calls and answers a distinct, guard-passing second-person line per title.</summary>
    private sealed class CannedWhyAgent : ISearchAgent
    {
        public int RerankCalls;

        public bool IsAvailable => true;

        public string ModelId => "canned-why-agent";

        public Task<AgentReply> InterpretAsync(IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles, CancellationToken ct = default, string? standingFilters = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null, string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default)
        {
            Interlocked.Increment(ref RerankCalls);
            return Task.FromResult<IReadOnlyList<RerankResult>>(titles
                .Select(t => new RerankResult(t.TitleId, 0.8d, $"Shares the slow-burn dread you love — {t.TitleId:N}"))
                .ToList());
        }
    }

    /// <summary>Honest but anti-recommendation whys (distinct per title, second-person, high fit) —
    /// exactly the live 2026-08-10 shape the acceptance guard must discard.</summary>
    private sealed class AntiRecommendationWhyAgent : ISearchAgent
    {
        public int RerankCalls;

        public bool IsAvailable => true;

        public string ModelId => "anti-rec-why-agent";

        public Task<AgentReply> InterpretAsync(IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles, CancellationToken ct = default, string? standingFilters = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null, string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default)
        {
            Interlocked.Increment(ref RerankCalls);
            return Task.FromResult<IReadOnlyList<RerankResult>>(titles
                .Select(t => new RerankResult(t.TitleId, 0.8d, $"You may bounce off the home-invasion gore — {t.TitleId:N}"))
                .ToList());
        }
    }

    /// <summary>
    /// Condemns the FIRST hero-query batch it sees (fit 0.1 — the original hero picks) and blesses
    /// everything after (fit 0.9 with a distinct accepted line), so the build's swap pass must
    /// re-deal every hero slot to a spare. Rail queries are always blessed.
    /// </summary>
    private sealed class LowFitFirstHeroBatchAgent : ISearchAgent
    {
        private const string HeroQuery = "the best titles for you to watch tonight";

        public readonly HashSet<Guid> CondemnedTitleIds = [];

        private int heroCalls;

        public bool IsAvailable => true;

        public string ModelId => "low-fit-hero-agent";

        public Task<AgentReply> InterpretAsync(IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles, CancellationToken ct = default, string? standingFilters = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
            IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null, string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default)
        {
            if (query == HeroQuery && Interlocked.Increment(ref heroCalls) == 1)
            {
                foreach (var t in titles)
                {
                    CondemnedTitleIds.Add(t.TitleId);
                }

                return Task.FromResult<IReadOnlyList<RerankResult>>(titles
                    .Select(t => new RerankResult(t.TitleId, 0.1d, $"A tepid match at best — {t.TitleId:N}"))
                    .ToList());
            }

            return Task.FromResult<IReadOnlyList<RerankResult>>(titles
                .Select(t => new RerankResult(t.TitleId, 0.9d, $"Better pick for tonight — {t.TitleId:N}"))
                .ToList());
        }
    }

    private async Task RunBuildAsync(
        Guid accountId, string? triggerReason = null, ISearchAgent? agent = null, IMemoryCache? fitCache = null)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var featureBuilder = new FeatureVectorBuilder(db);
        var eligibility = new EligibilityQueryBuilder(db);
        var tmdb = new OfflineTmdbClient();
        var handler = new BuildFeedJobHandler(
            db,
            featureBuilder,
            new CandidateGenerator(db, tmdb, eligibility, NullLogger<CandidateGenerator>.Instance),
            new CandidateRetriever(db, eligibility),
            eligibility,
            new FeedStarvationDiagnostic(db, eligibility),
            new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
            new FastTreeModelEngine(),
            new OnlineResidualModel(
                db, featureBuilder, new FastTreeModelEngine(),
                new Microsoft.Extensions.Caching.Memory.MemoryCache(
                    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                NullLogger<OnlineResidualModel>.Instance),
            new RatingCalibrationService(
                db,
                new Microsoft.Extensions.Caching.Memory.MemoryCache(
                    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            new DislikeSignal(
                db,
                new Microsoft.Extensions.Caching.Memory.MemoryCache(
                    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            agent ?? new UnavailableSearchAgent(),
            new TasteFactsBuilder(
                db,
                new DislikeSignal(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
                featureBuilder,
                new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            new PersonalisationContext(db, new DislikeSignal(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()))),
            fitCache ?? new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            new NoopEventHub(),
            new ExposureLog(db, NullLogger<ExposureLog>.Instance),
            NullLogger<BuildFeedJobHandler>.Instance);

        await handler.ExecuteAsync(
            new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = JobKind.BuildFeed,
                EnqueuedAt = DateTime.UtcNow,
                TriggerReason = triggerReason,
            },
            CancellationToken.None);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"buildfeed-{Guid.NewGuid():N}",
            Tier = AccountTier.Founder,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>
    /// Loved titles spread across years of RatedAt. The spread is the point: a rating-first order
    /// treats them as interchangeable and freezes on the highest, whereas the recency-weighted
    /// anchor pool has something to rotate through.
    /// </summary>
    private async Task SeedLovedTitlesAsync(Guid accountId, int count, string genre = "drama")
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        for (var i = 0; i < count; i++)
        {
            var title = NewTitle($"loved {i}", popularity: 1m + (i * 0.01m), genre);
            db.Add(title);
            // The loved titles and the candidates share one plot direction and one story keyword,
            // so a because-you-loved rail has an anchor neighbourhood to draw from AND evidence to
            // admit it on (AnchorRowGate). The direction is private to this test instance — the
            // catalogue is shared across the collection.
            db.TitleEmbeddings.Add(NewEmbedding(title.Id, _sharedAxis));
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = (short)(8 + (i % 3)),
                RatedAt = DateTime.UtcNow.AddDays(-30 * i),
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedEligibleTitlesAsync(int count, string genre = "drama")
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        for (var i = 0; i < count; i++)
        {
            // Popularity kept low so this seeding never perturbs the popularity head the other
            // integration tests in this shared-catalogue collection assert against.
            var title = NewTitle($"candidate {i}", popularity: 2m + (i * 0.001m), genre);
            db.Add(title);
            db.TitleEmbeddings.Add(NewEmbedding(title.Id, _sharedAxis));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A renderable, eligible title. <c>LastMetadataRefreshAt</c> is set so the build's hydration
    /// phase is a no-op — this suite is about assembly, not about the TMDB round trip.
    /// </summary>
    private static Title NewTitle(string name, decimal popularity, string genre = "drama") => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = string.Empty,
        Name = $"{name} {Guid.NewGuid():N}",
        Language = "en", // eligibility defaults to English-only with no language filter configured
        PosterPath = "/p.jpg",
        Genres = [genre], // a shared genre ⇒ genre-Jaccard similarity clears the row threshold
        Keywords = ["heist"], // shared story evidence for the anchored-row gate
        Year = 2015,
        RuntimeMinutes = 110,
        TraktRating = 7.2m,
        TmdbVoteAverage = 7.4m,
        TmdbPopularity = popularity,
        ReleasedAt = new DateTime(2015, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        LastMetadataRefreshAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
    };

    private async Task AddContentFilterAsync(Guid accountId, FilterKind kind, string value)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = kind,
            Value = value,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A tiny FastTree over the real schema, labelled well below the unreachable floor.</summary>
    private async Task SeedActiveArtifactAsync(Guid accountId)
    {
        var rng = new Random(23);
        var names = FeatureSchema.AllNames();
        var rows = new float[240][];
        var labels = new float[240];
        for (var i = 0; i < rows.Length; i++)
        {
            var v = new float[names.Length];
            for (var j = 0; j < v.Length; j++)
            {
                v[j] = (float)rng.NextDouble();
            }

            rows[i] = v;
            labels[i] = Math.Clamp(3f + (4f * v[0]), 1f, 8f);
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix(rows, labels, names),
            """{"Trees":40,"Leaves":8,"MinExamplesPerLeaf":5,"LearningRate":0.2}""");

        await using var db = Fixture.CreateContext(new AccountContext());
        db.ModelArtifacts.Add(new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = trained.ArtifactBytes,
            FeatureSchemaJson = System.Text.Json.JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names }),
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>TMDB port that yields nothing, so the build runs offline and the assertions rest on
    /// the seeded catalogue rather than on whatever discover happens to return today.</summary>
    private sealed class OfflineTmdbClient : ITmdbClient
    {
        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) => Task.FromResult<TmdbTitleDetails?>(null);

        // The bulk-seed walk is not exercised by this fixture; SeedCatalogJobHandlerTests owns its own
        // scripted TMDB client. An empty page reads as "this partition is exhausted", which is the
        // only honest answer a fake that was never given a corpus can give.
        public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            Task.FromResult(new TmdbDiscoverPage([], page, 0, 0));

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) => Task.FromResult<TmdbPerson?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds, string? region, DateTime? releasedAfter, int page, IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null, DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
    }

    private sealed class NoopEventHub : IPipelineEventHub
    {
        public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data) { }

        public async IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(
            Guid accountId, long? lastEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
