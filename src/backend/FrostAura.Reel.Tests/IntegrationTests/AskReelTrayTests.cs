using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Search;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The tray — the cards the account's rating floor put under the "Show the N under your bar"
/// fence — and the one rule it kept escaping.
///
/// <para><b>The defect.</b> The founder-locked rule is <i>never show things I've already seen</i>,
/// and the no-repeat set is derived server-side as <c>SELECT DISTINCT "TmdbId"</c> over the
/// conversation's stored cards. Below-floor cards were emitted on the wire and never written, so
/// that query could not contain them. An end-to-end smoke run on 2026-09-10 measured the
/// consequence: turn 1 put 14 titles under the bar, and turn 2 served <b>13 of those same 14</b> as
/// its top-of-grid picks — the user looking at the same 13 titles twice on one screen, once in the
/// grid and once in the still-open tray below it. Legal by the letter of the old record, and
/// exactly the thing the rule exists to stop.</para>
///
/// <para><b>What is pinned here.</b> That a page cannot re-offer a tray title (through the real
/// pipeline, so the reproduction is the defect rather than a model of it); that a resume hands the
/// tray back <i>as</i> a tray and never merges it into the picks; that storing the tray moved the
/// exposure log by exactly nothing; and that a tray card the user has since rated comes back in
/// neither list, because eligibility governs both.</para>
///
/// <para>Inherits <see cref="EmbeddedCatalogTestBase"/> because a real turn embeds whatever it
/// discovered: this class leaves vectors in the shared HNSW index exactly as a hand-seeding fixture
/// does, and the source guard cannot see that.</para>
/// </summary>
[Collection("postgres")]
public class AskReelTrayTests(PostgresFixture fixture, ReelApiFactory api)
    : EmbeddedCatalogTestBase(fixture), IClassFixture<ReelApiFactory>
{
    /// <summary>The first of this class's scripted TMDB ids — see <see cref="DisposeAsync"/>.</summary>
    private const long ScriptedTmdbIdFirst = 774001;

    /// <summary>How many scripted rows the discovery client hands back. See <see cref="BelowCount"/>.</summary>
    private const int ScriptedTmdbIdCount = 14;

    /// <summary>The first TMDB id this class SEEDS by hand, above the scripted block.</summary>
    private const long SeededTmdbIdFirst = 775001;

    /// <summary>One past the last id this class may create, in either block. See <see cref="DisposeAsync"/>.</summary>
    private const long CatalogueIdEnd = 776000;

    /// <summary>
    /// How many of a scored batch the scripted engine puts under the bar.
    ///
    /// <para>Four, and the arithmetic matters: the floor only BINDS when at least
    /// <c>MinFloorRelaxedCount</c> (6) cards clear it, and otherwise it relaxes and there is no tray
    /// at all. Scoring all but the last four above the bar means any batch of ten or more produces
    /// a bound floor and a non-empty tray, which is what this class needs to exist.</para>
    /// </summary>
    private const int BelowCount = 4;

    /// <summary>
    /// Removes this class's catalogue rows, not just its vectors — the same duty, and for the same
    /// measured reason, as <see cref="AskReelEndpointTests.DisposeAsync"/>: a later class's
    /// enrichment sweep embeds whatever Pending titles it finds, and those vectors then sit in the
    /// shared HNSW index while the semantic-arm tests run.
    ///
    /// <para>Exposure rows are deliberately not touched. They are append-only, and this class
    /// asserts ON their count rather than cleaning up after it.</para>
    /// </summary>
    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (!Fixture.Available)
        {
            return;
        }

        await using var db = Fixture.CreateContext(new AccountContext());
        var titleIds = await db.Titles
            .Where(t => t.TmdbId >= ScriptedTmdbIdFirst && t.TmdbId < CatalogueIdEnd)
            .Select(t => t.Id)
            .ToListAsync();
        if (titleIds.Count == 0)
        {
            return;
        }

        await db.SearchTurnCards.Where(c => titleIds.Contains(c.TitleId)).ExecuteDeleteAsync();
        await db.Titles.Where(t => titleIds.Contains(t.Id)).ExecuteDeleteAsync();
    }

    // ── The no-repeat set ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE REPRODUCTION, through the real endpoint and the real pipeline: a page never re-offers a
    /// title the previous turn's tray already showed.
    ///
    /// <para>The fixture is what makes the floor bind — an Active artifact plus a scripted
    /// <see cref="IModelEngine"/> that scores all but the last four of any batch above the bar, and
    /// a real <c>MinPredictedRating</c> filter row at 6.0. Without a model there are no predictions,
    /// every card trivially clears the floor, and the tray this test is about cannot exist; that is
    /// why a floor set on a model-less account proves nothing and is not what is done here.</para>
    ///
    /// <para>The second request repeats the first question verbatim, so the server DERIVES it as a
    /// page — which is the smoke run's exact shape ("Show me more") and the one where the two lists
    /// end up on screen together.</para>
    ///
    /// <para>Both of the page's lists are checked. Re-offering a tray title as a pick is the
    /// headline defect, but re-offering it into the page's own tray is the same repeat wearing a
    /// different fence.</para>
    /// </summary>
    [Fact]
    public async Task A_page_never_re_offers_a_title_the_tray_already_showed()
    {
        if (!Fixture.Available)
        {
            return;
        }

        using var factory = WithScriptedCatalogueAndModel();
        var (accountId, client) = await SignInAsync(factory, $"tray-norepeat-{Guid.NewGuid():N}");
        using (client)
        {
            // Everything already in the shared catalogue is out of this account's pool, so the only
            // titles either turn can reach are the scripted client's.
            await MarkExistingTitlesSeenAsync(accountId);
            await SeedActiveArtifactAsync(accountId);
            await SetRatingFloorAsync(accountId, "6.0");

            const string question = "a brooding futuristic thriller";
            var first = await AskAsync(client, new { query = question, clientTurnId = Guid.NewGuid() });
            var conversationId = ConversationId(first);

            // Taken off the WIRE, not out of the table. What the user saw is the fact the rule is
            // about, and `done.belowFloor` carried it long before anything stored it — so this
            // assertion reads identically against the broken build and the fixed one, and the test
            // fails on the repeat itself rather than on the absence of the column that fixes it.
            var tray = TmdbIds(first, "belowFloor");
            Assert.True(
                tray.Count > 0,
                "the fixture must put cards under the bar for this test to mean anything — a relaxed "
                + $"floor has no tray. done.droppedByFloor was {DroppedByFloor(first)}.");

            // Same question, new clientTurnId: the server derives this as a PAGE of the question
            // already asked, which is what "Show me more" sends.
            var page = await AskAsync(client, new
            {
                query = question,
                conversationId,
                clientTurnId = Guid.NewGuid(),
            });

            // THE ASSERTION. 13 of 14 on the smoke run; zero of any number here.
            Assert.Empty(TmdbIds(page, "results").Intersect(tray));
            // The same repeat behind a different fence — a tray title re-trayed is still a title
            // the user has already been shown twice.
            Assert.Empty(TmdbIds(page, "belowFloor").Intersect(tray));

            // And the mechanism, stated last so a failure above names the defect rather than the
            // implementation detail: what the user saw is what the server stored.
            Assert.Equal(tray.Order(), (await TrayTmdbIdsAsync(conversationId)).Order());
        }
    }

    /// <summary>
    /// The same guarantee, stated as the property that produces it: the tray is in the stored
    /// no-repeat set, on the same terms as a pick.
    ///
    /// <para>Asserted against the query the endpoint actually runs — distinct <c>TmdbId</c> over
    /// the conversation's cards, unfiltered by <c>BelowFloor</c> — rather than against a second
    /// turn, so a failure names the cause instead of a symptom.</para>
    /// </summary>
    [Fact]
    public async Task The_stored_no_repeat_set_contains_the_tray()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"tray-shownset-{Guid.NewGuid():N}");
        var conversationId = await SeedTurnWithTrayAsync(accountId, picks: 3, tray: 2);

        await using var db = ScopedContext(accountId);
        var shown = await db.SearchTurnCards
            .Where(c => c.ConversationId == conversationId && c.TmdbId != null)
            .Select(c => c.TmdbId!.Value)
            .Distinct()
            .ToListAsync();
        var tray = await db.SearchTurnCards
            .Where(c => c.ConversationId == conversationId && c.BelowFloor)
            .Select(c => c.TmdbId!.Value)
            .ToListAsync();

        Assert.Equal(5, shown.Count);
        Assert.Equal(2, tray.Count);
        Assert.Empty(tray.Except(shown));
    }

    // ── The resume ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A resume restores the tray AS a tray: <c>belowFloor</c> carries it, <c>cards</c> does not.
    ///
    /// <para>Both halves are the assertion. A resume that dropped the tray would show a thread whose
    /// fence had silently emptied; a resume that merged it into <c>cards</c> would promote cards
    /// under the bar into the grid, which is the one thing the floor exists to prevent.</para>
    /// </summary>
    [Fact]
    public async Task A_resume_returns_the_tray_and_keeps_it_out_of_the_cards()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync(api, $"tray-resume-{Guid.NewGuid():N}");
        using (client)
        {
            var conversationId = await SeedTurnWithTrayAsync(accountId, picks: 4, tray: 3);
            var trayTitleIds = await TrayTitleIdsAsync(conversationId);

            using var response = await client.GetAsync($"/api/search/conversations/{conversationId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = payload.RootElement;

            Assert.Equal(4, root.GetProperty("cards").GetArrayLength());
            Assert.Equal(3, root.GetProperty("belowFloor").GetArrayLength());
            Assert.Equal(0, root.GetProperty("removed").GetInt32());

            // Named ids rather than counts, so a projection that put the tray in both lists — or
            // sorted one list into the other — cannot pass by arithmetic.
            Assert.Empty(TitleIds(root, "cards").Intersect(trayTitleIds));
            Assert.Equal(trayTitleIds.Order(), TitleIds(root, "belowFloor").Order());

            // Stored rank is the position the answer had, in the tray exactly as in the grid.
            Assert.Equal(trayTitleIds, TitleIds(root, "belowFloor"));
        }
    }

    // ── Eligibility governs both lists ──────────────────────────────────────────────────────

    /// <summary>
    /// THE FIXTURE PROBE: a tray card the user has since rated comes back in NEITHER list.
    ///
    /// <para>A rating implies the title was seen, and the founder-locked rule names Ask Reel
    /// explicitly. Storing the tray created a second place for a card to survive that rule, and this
    /// is the probe that would catch a partition done BEFORE the eligibility filter rather than
    /// after it — the ordering mistake is invisible in every count-based assertion above, because
    /// the tray would still have the right number of cards in it.</para>
    ///
    /// <para>It also pins <c>removed</c> spanning both lists. A card that vanished out of the tray
    /// is as invisible to the user as one that vanished out of the grid, and a silent drop is
    /// indistinguishable from a bug.</para>
    /// </summary>
    [Fact]
    public async Task A_tray_card_the_user_rated_comes_back_in_neither_list()
    {
        if (!Fixture.Available)
        {
            return;
        }

        var (accountId, client) = await SignInAsync(api, $"tray-rated-{Guid.NewGuid():N}");
        using (client)
        {
            var conversationId = await SeedTurnWithTrayAsync(accountId, picks: 3, tray: 3);
            var trayTitleIds = await TrayTitleIdsAsync(conversationId);
            var ratedTitleId = trayTitleIds[1];

            await using (var db = Fixture.CreateContext(new AccountContext()))
            {
                var now = DateTime.UtcNow;
                db.UserRatings.Add(new UserRating
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = ratedTitleId,
                    SubjectType = RatingSubjectType.Movie,
                    Rating = 8,
                    RatedAt = now,
                    FirstRatedAt = now,
                });
                await db.SaveChangesAsync();
            }

            using var response = await client.GetAsync($"/api/search/conversations/{conversationId}");
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = payload.RootElement;

            Assert.DoesNotContain(ratedTitleId, TitleIds(root, "belowFloor"));
            Assert.DoesNotContain(ratedTitleId, TitleIds(root, "cards"));
            Assert.Equal(3, root.GetProperty("cards").GetArrayLength());
            Assert.Equal(2, root.GetProperty("belowFloor").GetArrayLength());
            Assert.Equal(1, root.GetProperty("removed").GetInt32());
        }
    }

    // ── The exposure log did not move ───────────────────────────────────────────────────────

    /// <summary>
    /// Storing the tray wrote no exposures. An <c>ExposureEvent</c> is evidence that the product
    /// RECOMMENDED a title at a rank; a card the account's own floor pushed under the fence was
    /// displayed and explicitly not recommended, so logging it would put titles the floor rejected
    /// into the denominator of every accuracy number computed from the log — irreversibly, because
    /// the log is append-only and nothing downstream could tell the two apart afterwards.
    ///
    /// <para>Asserted as an equality against the PICK count rather than as "did not grow", so a
    /// turn that logged the tray and one that logged nothing at all are distinguishable. The turn
    /// runs with an Active artifact precisely so exposures are written at all — without one the
    /// pipeline logs nothing and the assertion would hold for the wrong reason.</para>
    /// </summary>
    [Fact]
    public async Task The_tray_writes_no_exposures()
    {
        if (!Fixture.Available)
        {
            return;
        }

        using var factory = WithScriptedCatalogueAndModel();
        var (accountId, client) = await SignInAsync(factory, $"tray-exposure-{Guid.NewGuid():N}");
        using (client)
        {
            await MarkExistingTitlesSeenAsync(accountId);
            await SeedActiveArtifactAsync(accountId);
            await SetRatingFloorAsync(accountId, "6.0");

            var turn = await AskAsync(client, new
            {
                query = "a brooding futuristic thriller",
                clientTurnId = Guid.NewGuid(),
            });
            var conversationId = ConversationId(turn);

            var tray = await TrayTmdbIdsAsync(conversationId);
            Assert.True(tray.Count > 0, "the fixture must put cards under the bar for this to mean anything");

            await using var db = ScopedContext(accountId);
            var picks = await db.SearchTurnCards
                .CountAsync(c => c.ConversationId == conversationId && !c.BelowFloor);
            var exposures = await db.ExposureEvents.CountAsync(e => e.AccountId == accountId);

            Assert.Equal(picks, exposures);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A host with a deterministic catalogue AND a deterministic model, which is what a bound floor
    /// needs: the floor compares predictions, and predictions exist only where an Active artifact
    /// does. Swapping <see cref="IModelEngine"/> rather than <c>OnDemandScorer</c> keeps the whole
    /// real scoring path — feature vectors, the residual head, the calibrator, the dislike demotion
    /// — and dictates only the number the model would have produced.
    /// </summary>
    private WebApplicationFactory<Program> WithScriptedCatalogueAndModel() =>
        api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITmdbClient>();
            services.AddSingleton<ITmdbClient>(
                new ScriptedDiscoveryTmdbClient(Discoverable(ScriptedTmdbIdFirst, ScriptedTmdbIdCount)));
            services.RemoveAll<IModelEngine>();
            services.AddSingleton<IModelEngine>(new SplitScoreModelEngine(BelowCount));
        }));

    /// <summary>
    /// A seeded account plus a client already carrying its session cookie, against whichever host
    /// the test is driving. The token is minted by the base host's own <see cref="SessionService"/>
    /// — same signing secret, same database — so both hosts accept it.
    /// </summary>
    private async Task<(Guid AccountId, HttpClient Client)> SignInAsync(
        WebApplicationFactory<Program> factory, string slug)
    {
        var accountId = await api.SeedAccountAsync(slug);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.ReelDbContext>();
        var account = await db.Accounts.IgnoreQueryFilters().FirstAsync(a => a.Id == accountId);
        var token = scope.ServiceProvider.GetRequiredService<SessionService>().MintAccessToken(account, DateTime.UtcNow);

        var cookies = new CookieContainerHandler();
        cookies.Container.Add(
            factory.ClientOptions.BaseAddress,
            new Cookie("reel_at", token, "/", factory.ClientOptions.BaseAddress.Host));
        return (accountId, factory.CreateDefaultClient(cookies));
    }

    private Infrastructure.Persistence.ReelDbContext ScopedContext(Guid accountId)
    {
        var scope = new AccountContext();
        scope.SetAccount(accountId);
        return Fixture.CreateContext(scope);
    }

    /// <summary>Every title already in the shared catalogue, marked watched for this account.</summary>
    private async Task MarkExistingTitlesSeenAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        foreach (var titleId in await db.Titles.Select(t => t.Id).ToListAsync())
        {
            db.WatchedTitles.Add(new WatchedTitle
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                Plays = 1,
                IsFullyWatched = true,
                LastWatchedAt = now,
                FirstSyncedAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// An Active artifact with bogus bytes — <see cref="SplitScoreModelEngine"/> never reads them.
    /// The feature-schema version is not decoration: the shadow column projects it out of this
    /// jsonb, and an artifact without the current version is invisible to every Active-artifact
    /// read, which would leave the account model-less and the floor unbound.
    /// </summary>
    private async Task SeedActiveArtifactAsync(Guid accountId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.Add(new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            ArtifactBytes = [1],
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
            FeatureSchemaJson = JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
        });
        await db.SaveChangesAsync();
    }

    private async Task SetRatingFloorAsync(Guid accountId, string value)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = FilterKind.MinPredictedRating,
            Value = value,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<long>> TrayTmdbIdsAsync(Guid conversationId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        return await db.SearchTurnCards
            .Where(c => c.ConversationId == conversationId && c.BelowFloor && c.TmdbId != null)
            .Select(c => c.TmdbId!.Value)
            .ToListAsync();
    }

    private async Task<IReadOnlyList<Guid>> TrayTitleIdsAsync(Guid conversationId)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        return await db.SearchTurnCards
            .Where(c => c.ConversationId == conversationId && c.BelowFloor)
            .OrderBy(c => c.Rank)
            .Select(c => c.TitleId)
            .ToListAsync();
    }

    /// <summary>
    /// A conversation with one completed ask, <paramref name="picks"/> stored picks and
    /// <paramref name="tray"/> stored tray cards over real catalogue rows, seeded directly.
    ///
    /// <para>Direct rather than through a live turn on purpose: what the READ side does with a
    /// stored answer is the thing under test, and driving the pipeline to produce one would make
    /// every assertion here depend on the ranker's mood. The rows are the rows the endpoint writes,
    /// including the single rank counter running across both blocks — the picks take 0..n-1 and the
    /// tray continues at n, which is what keeps the unique <c>(TurnId, Rank)</c> satisfied.</para>
    /// </summary>
    private async Task<Guid> SeedTurnWithTrayAsync(Guid accountId, int picks, int tray)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        var conversation = new SearchConversation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Title = "a seeded thread with a tray",
            CreatedAt = now,
            LastTurnAt = now,
            TurnCount = 2,
            LastCardsAt = picks + tray > 0 ? now : null,
        };
        db.SearchConversations.Add(conversation);

        var assistant = new SearchTurn
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ConversationId = conversation.Id,
            Role = "assistant",
            Text = "Here is what I found.",
            CreatedAt = now,
            Sequence = 1,
            ClientTurnId = Guid.NewGuid(),
            Kind = SearchTurnKind.Ask,
            Status = SearchTurnStatus.Completed,
            Reason = "4 picks from your catalogue · 3 picks shown below your 6.0 bar",
            CompletedAt = now,
        };
        db.SearchTurns.AddRange(
            new SearchTurn
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ConversationId = conversation.Id,
                Role = "user",
                Text = "a seeded question",
                CreatedAt = now,
                Sequence = 0,
                Kind = SearchTurnKind.Ask,
                Status = SearchTurnStatus.Completed,
                CompletedAt = now,
            },
            assistant);

        for (var rank = 0; rank < picks + tray; rank++)
        {
            var belowFloor = rank >= picks;
            var tmdbId = SeededTmdbIdFirst + Interlocked.Increment(ref _seededTitleCounter);
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TmdbId = tmdbId,
                TraktSlug = $"tray-seeded-{tmdbId}",
                Name = $"Tray Seeded Title {tmdbId}",
                Year = 2015,
                Genres = ["science-fiction"],
                Language = "en",
            };
            db.Titles.Add(title);
            db.SearchTurnCards.Add(new SearchTurnCard
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                ConversationId = conversation.Id,
                TurnId = assistant.Id,
                TitleId = title.Id,
                TmdbId = tmdbId,
                Rank = rank,
                BelowFloor = belowFloor,
                Similarity = 0.42,
                PredictedRating = belowFloor ? 4.5m : 7.25m,
                LlmFit = 0.812m,
                Blend = 0.6543m,
                Why = "Seeded why-line.",
                WhySource = "Llm",
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync();
        return conversation.Id;
    }

    /// <summary>Hands out distinct seeded TMDB ids across every test in the class.</summary>
    private static int _seededTitleCounter;

    private static IReadOnlyList<Guid> TitleIds(JsonElement root, string property) =>
        root.GetProperty(property).EnumerateArray().Select(c => c.GetProperty("titleId").GetGuid()).ToList();

    private static IReadOnlyList<TmdbListItem> Discoverable(long firstTmdbId, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new TmdbListItem(
                firstTmdbId + i, true, $"Tray Title {firstTmdbId + i}", "/t.jpg", null,
                "A brooding, futuristic search for what is real.", 40m - i, 7.4m, 900,
                new DateTime(2008 + i, 6, 16, 0, 0, 0, DateTimeKind.Utc), [878], "en"))
            .ToList();

    private static Guid ConversationId(SseStream stream) =>
        Assert.Single(stream.Frames, f => f.Event == "conversation").Data.GetProperty("id").GetGuid();

    private static int DroppedByFloor(SseStream stream) =>
        Assert.Single(stream.Frames, f => f.Event == "done").Data.GetProperty("droppedByFloor").GetInt32();

    /// <summary>
    /// The TMDB ids in one array of the terminal frame.
    ///
    /// <para><c>TryGetProperty</c>, not <c>GetProperty</c>: a turn whose pool came back empty emits
    /// a SHORT <c>done</c> — <c>results</c> and <c>reason</c> and nothing else — and an empty pool
    /// is the correct post-fix outcome for the page in
    /// <see cref="A_page_never_re_offers_a_title_the_tray_already_showed"/>, since the tray titles
    /// are now the only ones left and they are excluded. An absent array is an empty one here; the
    /// shape of a starved turn's frame is not this class's subject.</para>
    /// </summary>
    private static IReadOnlyList<long> TmdbIds(SseStream stream, string property) =>
        Assert.Single(stream.Frames, f => f.Event == "done").Data.TryGetProperty(property, out var array)
            ? array
                .EnumerateArray()
                .Select(c => c.GetProperty("tmdbId"))
                .Where(id => id.ValueKind == JsonValueKind.Number)
                .Select(id => id.GetInt64())
                .ToList()
            : [];

    private static async Task<SseStream> AskAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/api/search/ask", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return ParseSse(await response.Content.ReadAsStringAsync());
    }

    private sealed record SseFrame(string Event, JsonElement Data);

    private sealed record SseStream(string Raw, IReadOnlyList<SseFrame> Frames);

    /// <summary>
    /// Parses the body the way a browser does. Comment frames (a line beginning <c>:</c>) are
    /// keepalives and carry no event, so they are skipped rather than parsed.
    /// </summary>
    private static SseStream ParseSse(string raw)
    {
        var frames = new List<SseFrame>();
        var chunks = raw.Split("\n\n");
        for (var i = 0; i < chunks.Length; i++)
        {
            var chunk = chunks[i];
            if (i == chunks.Length - 1)
            {
                Assert.Equal(string.Empty, chunk);
                break;
            }

            if (chunk.StartsWith(':'))
            {
                continue;
            }

            var match = Regex.Match(
                chunk,
                @"^(id: (?<id>\d+)\n)?event: (?<event>[a-z][a-z-]*)\ndata: (?<data>\{.*\})$",
                RegexOptions.Singleline);
            Assert.True(match.Success, $"Malformed SSE frame: {chunk}");
            frames.Add(new SseFrame(
                match.Groups["event"].Value,
                JsonDocument.Parse(match.Groups["data"].Value).RootElement.Clone()));
        }

        return new SseStream(raw, frames);
    }

    /// <summary>
    /// A model that scores every row of a batch above the bar except the last
    /// <paramref name="belowCount"/>, which it puts well under.
    ///
    /// <para>By ROW INDEX, not by title, and deliberately: <c>OnDemandScorer</c> hands the engine
    /// feature vectors with no ids attached, so an engine cannot name the title it is scoring and a
    /// test that pretended otherwise would be asserting against its own guess. Nothing here needs to
    /// — the tests read back WHICH cards landed in the tray from the rows the turn wrote. What the
    /// fixture must guarantee is only that some cards clear the floor and some do not, in a ratio
    /// that leaves at least <c>MinFloorRelaxedCount</c> clearing so the floor binds rather than
    /// relaxes.</para>
    ///
    /// <para>The artifact bytes are never read, which is the point of swapping the engine rather
    /// than training a model: a real artifact would cost a training run and would make every
    /// assertion in this class depend on what FastTree happened to learn.</para>
    /// </summary>
    private sealed class SplitScoreModelEngine(int belowCount) : IModelEngine
    {
        private const float Above = 8.4f;

        private const float Below = 4.2f;

        public TrainedModel Train(TrainingMatrix matrix, string hyperparamsJson) =>
            throw new NotSupportedException("The tray fixture scores; it never trains.");

        public float[] Score(byte[] artifactBytes, float[][] features) => Split(features.Length);

        public float[] ScoreCached(Guid modelKey, byte[] artifactBytes, float[][] features) =>
            Split(features.Length);

        public ScoredRow[] ScoreWithContributions(
            byte[] artifactBytes, float[][] features, string[] featureNames, int topK = 5) =>
            Split(features.Length).Select(s => new ScoredRow(s, [])).ToArray();

        public ScoredRow[] ScoreWithContributionsCached(
            Guid modelKey, byte[] artifactBytes, float[][] features, string[] featureNames, int topK = 5) =>
            Split(features.Length).Select(s => new ScoredRow(s, [])).ToArray();

        private float[] Split(int rows) =>
            Enumerable.Range(0, rows).Select(i => i < rows - belowCount ? Above : Below).ToArray();
    }

    /// <summary>
    /// A catalogue that answers concept discovery with a fixed set of movies and nothing else, so a
    /// turn through the real endpoint has a real, deterministic pool to rank. Everything beyond
    /// discovery answers empty: the relaxation ladder and the last-resort arm must not quietly
    /// re-introduce titles this class excluded.
    /// </summary>
    private sealed class ScriptedDiscoveryTmdbClient(IReadOnlyList<TmdbListItem> rows) : ITmdbClient
    {
        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

        public Task<IReadOnlyList<TmdbListItem>> DiscoverByConceptAsync(
            bool movies, IReadOnlyList<int> genreIds, IReadOnlyList<int> keywordIds,
            string? region, DateTime? releasedAfter, int page,
            IReadOnlyList<int>? excludeGenreIds = null, IReadOnlyList<int>? excludeKeywordIds = null,
            DateTime? releasedBefore = null, int? maxRuntimeMinutes = null, TmdbDiscoverOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(movies ? rows : []);

        public Task<IReadOnlyList<TmdbListItem>> SearchTitlesAsync(bool movies, string query, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbPerson?> SearchPersonAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<TmdbPerson?>(null);

        public Task<IReadOnlyList<TmdbListItem>> GetPersonCreditsAsync(long personId, PersonCreditRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<TmdbTitleDetails?> GetMovieAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbTitleDetails?> GetTvAsync(long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitleDetails?>(null);

        public Task<TmdbDiscoverPage> DiscoverSeedPageAsync(bool movies, int year, int minVoteCount, int page, CancellationToken ct = default) =>
            Task.FromResult(new TmdbDiscoverPage([], page, 0, 0));

        public Task<IReadOnlyList<TmdbListItem>> DiscoverAsync(
            bool movies, int? genreId, string? region, DateTime? releasedAfter, int page, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetTrendingAsync(bool movies, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetRecommendationsAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbListItem>> GetSimilarAsync(bool movies, long tmdbId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListItem>>([]);

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(long tmdbId, bool movie, string region, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
    }
}
