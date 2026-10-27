using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// HREV2-D-02: a show's creators are stored, and the shows hydrated before they were are back-filled.
///
/// <para>Breaking Bad's title page listed thirteen episode directors under "Directors", Vince Gilligan
/// ninth, and no "Created by" anywhere. <c>TmdbClient</c> already parsed <c>created_by</c>, but nothing
/// below it read the field: hydration dropped it, so no credit, no API field and no UI line could ever
/// carry it. A show's creator is its auteur the way a film's director is — these tests pin that the
/// role reaches the database, in TMDB's order, and that every show already in the catalogue is fetched
/// once more to get it, without the back-fill ever re-fetching the same show for ever.</para>
/// </summary>
[Collection("postgres")]
public class TitleCreatorCreditsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Hydrating_a_show_stores_each_creator_as_a_creator_credit_in_tmdbs_order()
    {
        if (!fixture.Available)
        {
            return;
        }

        var people = new People();
        // The co-creator is already a known person (an actor elsewhere): hydration must reuse the
        // row, not trip the unique TmdbId index with a second one.
        var coCreator = await SeedPersonAsync(people.CoCreator, "Known Co-Creator", "/known.jpg");
        var show = await SeedShowAsync(refreshedAt: null);
        var tmdb = Script.Create(out var script);
        script.Details[show.TmdbId!.Value] = BreakingBadLike(show.TmdbId.Value, people);

        try
        {
            await HydrateAsync(show.Id, tmdb);

            await using var db = fixture.CreateContext(new AccountContext());
            var credits = await CreditsAsync(db, show.Id);

            Assert.Equal(
                [(people.Creator, 0), (people.CoCreator, 1)],
                credits.Where(c => c.Role == CreditRole.Creator).Select(c => (c.TmdbId, c.CastOrder ?? -1)).ToList());

            // The creator also directed episodes: that credit is kept, the creator credit is separate.
            Assert.Contains(credits, c => c.TmdbId == people.Creator && c.Role == CreditRole.Director);
            Assert.Contains(credits, c => c.TmdbId == people.EpisodeDirector && c.Role == CreditRole.Director);
            Assert.Contains(credits, c => c.TmdbId == people.Lead && c.Role == CreditRole.Actor);

            var creator = await db.Persons.AsNoTracking().SingleAsync(p => p.TmdbId == people.Creator);
            Assert.Equal("Show Creator", creator.Name);
            Assert.Equal("/creator.jpg", creator.ProfilePath);
            Assert.Equal(coCreator, (await db.Persons.AsNoTracking().SingleAsync(p => p.TmdbId == people.CoCreator)).Id);
        }
        finally
        {
            await CleanupAsync([show.Id], people);
        }
    }

    /// <summary>
    /// Varied fixture: TMDB names creators and nobody else. Hydration used to return before looking at
    /// any person when cast and crew were both empty, which would have dropped the creators with them.
    /// </summary>
    [Fact]
    public async Task A_show_whose_only_people_are_its_creators_still_gets_them()
    {
        if (!fixture.Available)
        {
            return;
        }

        var people = new People();
        var show = await SeedShowAsync(refreshedAt: null);
        var tmdb = Script.Create(out var script);
        script.Details[show.TmdbId!.Value] = Details(show.TmdbId.Value, [], [],
            [new TmdbCreator(people.Creator, "Show Creator", null)]);

        try
        {
            await HydrateAsync(show.Id, tmdb);

            await using var db = fixture.CreateContext(new AccountContext());
            var credit = Assert.Single(await CreditsAsync(db, show.Id));
            Assert.Equal((people.Creator, CreditRole.Creator, 0), (credit.TmdbId, credit.Role, credit.CastOrder));
        }
        finally
        {
            await CleanupAsync([show.Id], people);
        }
    }

    [Fact]
    public async Task Hydrating_the_same_show_again_adds_no_second_creator_credit()
    {
        if (!fixture.Available)
        {
            return;
        }

        var people = new People();
        var show = await SeedShowAsync(refreshedAt: null);
        var tmdb = Script.Create(out var script);
        script.Details[show.TmdbId!.Value] = BreakingBadLike(show.TmdbId.Value, people);

        try
        {
            await HydrateAsync(show.Id, tmdb);
            await HydrateAsync(show.Id, tmdb);

            await using var db = fixture.CreateContext(new AccountContext());
            var creators = (await CreditsAsync(db, show.Id)).Where(c => c.Role == CreditRole.Creator).ToList();
            Assert.Equal(2, creators.Count);
            Assert.Equal(1, await db.Persons.CountAsync(p => p.TmdbId == people.Creator));
        }
        finally
        {
            await CleanupAsync([show.Id], people);
        }
    }

    /// <summary>A show TMDB lists no creator for, and a film (whose payload has no such field), get none.</summary>
    [Fact]
    public async Task No_creator_credit_is_invented_for_a_show_without_one_or_for_a_film()
    {
        if (!fixture.Available)
        {
            return;
        }

        var people = new People();
        var show = await SeedShowAsync(refreshedAt: null);
        var film = await SeedShowAsync(refreshedAt: null, mediaType: MediaType.Movie);
        var tmdb = Script.Create(out var script);
        script.Details[show.TmdbId!.Value] = Details(show.TmdbId.Value,
            [new TmdbCastMember(people.Lead, "Lead Actor", "Walter", 0, null, "Acting")], [], []);
        script.Details[film.TmdbId!.Value] = Details(film.TmdbId.Value,
            [], [new TmdbCrewMember(people.EpisodeDirector, "Film Director", "Director", "Directing", null)], []);

        try
        {
            await HydrateAsync(show.Id, tmdb);
            await HydrateAsync(film.Id, tmdb);

            await using var db = fixture.CreateContext(new AccountContext());
            Assert.False(await db.TitleCredits.AnyAsync(c =>
                (c.TitleId == show.Id || c.TitleId == film.Id) && c.Role == CreditRole.Creator));
            Assert.Single(await CreditsAsync(db, show.Id));
            Assert.Single(await CreditsAsync(db, film.Id));
        }
        finally
        {
            await CleanupAsync([show.Id, film.Id], people);
        }
    }

    /// <summary>
    /// The back-fill's selection. Its watermark is the oldest refresh among shows that already hold a
    /// creator credit: only code that stores creators can write one, and that code stamps the refresh
    /// at the same moment, so a creator-less show refreshed BEFORE the watermark was hydrated by code
    /// that dropped them. One refreshed after it was read by code that would have stored them — TMDB
    /// simply names nobody — and must never be fetched again for that reason.
    /// </summary>
    [Fact]
    public async Task The_backfill_selects_creatorless_shows_refreshed_before_the_first_stored_creator()
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var people = new People();
        var credited = await SeedShowAsync(refreshedAt: now.AddDays(-20));
        var stale = await SeedShowAsync(refreshedAt: now.AddDays(-30));
        var afterTheWatermark = await SeedShowAsync(refreshedAt: now.AddDays(-10));
        var staleFilm = await SeedShowAsync(refreshedAt: now.AddDays(-30), mediaType: MediaType.Movie);
        var neverHydrated = await SeedShowAsync(refreshedAt: null);
        var noTmdbId = await SeedShowAsync(refreshedAt: now.AddDays(-30), tmdbId: null);
        await SeedCreatorCreditAsync(credited.Id, people.Creator);
        var ids = new[] { credited, stale, afterTheWatermark, staleFilm, neverHydrated, noTmdbId }.Select(t => t.Id).ToList();

        try
        {
            await using var db = fixture.CreateContext(new AccountContext());
            async Task<HashSet<Guid>> Due() =>
                (await CreatorBackfillPolicy.Due(db, now).Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToListAsync()).ToHashSet();

            Assert.Equal([stale.Id], await Due());

            // A database that has never stored a creator: every hydrated show predates the code that
            // stores them, so the watermark is "now" and the shows refreshed after -20 days qualify too.
            await db.TitleCredits.Where(c => c.TitleId == credited.Id).ExecuteDeleteAsync();
            Assert.False(
                await db.TitleCredits.AnyAsync(c => c.Role == CreditRole.Creator),
                "Another test left a creator credit in the shared catalogue; it moves this test's watermark.");
            Assert.Equal(new HashSet<Guid> { stale.Id, afterTheWatermark.Id, credited.Id }, await Due());
        }
        finally
        {
            await CleanupAsync(ids, people);
        }
    }

    /// <summary>
    /// End to end through the global sweep: the back-fill spends only what supply and the refresh
    /// sweep leave of the day's ceiling, fetches the stale shows most-voted first, stores what it
    /// finds, and never fetches the same show twice — including the one TMDB names no creator for.
    /// </summary>
    [Fact]
    public async Task The_global_sweep_backfills_stale_shows_last_and_never_refetches_them()
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var people = new People();
        var credited = await SeedShowAsync(refreshedAt: now.AddDays(-20));
        await SeedCreatorCreditAsync(credited.Id, people.CoCreator);
        var stale = await SeedShowAsync(refreshedAt: now.AddDays(-30), votes: int.MaxValue);
        var staleWithoutCreator = await SeedShowAsync(refreshedAt: now.AddDays(-31), votes: int.MaxValue - 1);
        var afterTheWatermark = await SeedShowAsync(refreshedAt: now.AddDays(-10), votes: int.MaxValue);
        // New supply outranks the back-fill even when the back-fill's shows are the most-voted rows
        // in the catalogue.
        var supply = await SeedShowAsync(refreshedAt: null, votes: 1);
        var ids = new[] { credited, stale, staleWithoutCreator, afterTheWatermark, supply }.Select(t => t.Id).ToList();

        var tmdb = Script.Create(out var script);
        script.Details[stale.TmdbId!.Value] = Details(stale.TmdbId.Value, [], [],
            [new TmdbCreator(people.Creator, "Show Creator", "/creator.jpg")]);
        script.Details[staleWithoutCreator.TmdbId!.Value] = Details(staleWithoutCreator.TmdbId.Value,
            [new TmdbCastMember(people.Lead, "Lead Actor", null, 0, null, "Acting")], [], []);
        var started = DateTime.UtcNow;

        try
        {
            // Exactly what the earlier tiers need: nothing left over, so the back-fill fetches nothing.
            await SweepAsync(tmdb, extra: 0);
            Assert.Contains(supply.TmdbId!.Value, script.Calls);
            Assert.DoesNotContain(stale.TmdbId.Value, script.Calls);
            Assert.DoesNotContain(staleWithoutCreator.TmdbId.Value, script.Calls);

            // Two over: both stale shows, most-voted first, and never the one past the watermark.
            script.Calls.Clear();
            await SweepAsync(tmdb, extra: 2);
            Assert.Equal([stale.TmdbId.Value, staleWithoutCreator.TmdbId.Value], script.Calls.Where(script.Details.ContainsKey));

            await using (var db = fixture.CreateContext(new AccountContext()))
            {
                var creator = Assert.Single(await CreditsAsync(db, stale.Id), c => c.Role == CreditRole.Creator);
                Assert.Equal(people.Creator, creator.TmdbId);
                Assert.False(await db.TitleCredits.AnyAsync(c => c.TitleId == staleWithoutCreator.Id && c.Role == CreditRole.Creator));
            }

            // Self-terminating: with room to spare, neither is fetched again.
            script.Calls.Clear();
            await SweepAsync(tmdb, extra: 2);
            Assert.DoesNotContain(stale.TmdbId.Value, script.Calls);
            Assert.DoesNotContain(staleWithoutCreator.TmdbId.Value, script.Calls);
            Assert.DoesNotContain(afterTheWatermark.TmdbId!.Value, script.Calls);
        }
        finally
        {
            await CleanupAsync(ids, people);
            // A hydration queues a global enrich; left pending it would answer another test's
            // "is one in flight?" for it.
            await using var cleanup = fixture.CreateContext(new AccountContext());
            await cleanup.SyncJobs
                .Where(j => j.AccountId == null && j.Kind == JobKind.EnrichCatalog && j.Status == JobStatus.Pending && j.EnqueuedAt >= started)
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task The_follow_through_counts_the_creator_backfill_as_hydration_work()
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var people = new People();
        var credited = await SeedShowAsync(refreshedAt: now.AddDays(-20));
        await SeedCreatorCreditAsync(credited.Id, people.Creator);
        var stale = await SeedShowAsync(refreshedAt: now.AddDays(-30));
        var ids = new[] { credited.Id, stale.Id };

        try
        {
            await using var db = fixture.CreateContext(new AccountContext());
            var result = await CatalogFollowThroughScheduler.RunAsync(
                db, new CatalogHydrationBudget(db, null), extractorVersion: 1, now, CancellationToken.None);
            Assert.True(result.CreatorBackfillDue >= 1);
        }
        finally
        {
            await CleanupAsync(ids, people);
            await using var cleanup = fixture.CreateContext(new AccountContext());
            await cleanup.SyncJobs
                .Where(j => j.AccountId == null && j.Status == JobStatus.Pending && j.EnqueuedAt >= now.AddMinutes(-1)
                    && (j.Kind == JobKind.HydrateCatalog || j.Kind == JobKind.EnrichCatalog))
                .ExecuteDeleteAsync();
        }
    }

    /// <summary>Any one of the three backlogs is work; none of them is not.</summary>
    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(1, 0, 0, true)]
    [InlineData(0, 1, 0, true)]
    [InlineData(0, 0, 1, true)]
    public void Hydration_work_is_any_of_the_three_backlogs(int backlog, int refreshDue, int creatorBackfillDue, bool expected)
    {
        var result = new CatalogFollowThrough(
            backlog, new DailyAllowance(0, 10), false, false, new EnrichmentBacklog(0, 0, 0, null), refreshDue, creatorBackfillDue);
        Assert.Equal(expected, result.HasHydrationWork);
    }

    private async Task SweepAsync(ITmdbClient tmdb, int extra)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        var used = (await new CatalogHydrationBudget(db, null).AllowanceAsync(now, CancellationToken.None)).Used;
        // What the tiers ahead of the back-fill want right now, whatever the shared catalogue holds.
        var ahead = await MetadataRefreshPolicy.Due(db, now).CountAsync()
            + await CatalogFollowThroughScheduler.PendingHydration(db).CountAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CatalogHydrationBudget.DailyTitlesKey] = (used + ahead + extra).ToString(CultureInfo.InvariantCulture),
            })
            .Build();
        var handler = new HydrateCatalogJobHandler(
            db,
            new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
            new NoopEventHub(),
            new CatalogHydrationBudget(db, configuration),
            configuration,
            NullLogger<HydrateCatalogJobHandler>.Instance);
        await handler.ExecuteAsync(new SyncJob { Id = Guid.NewGuid(), AccountId = null, Kind = JobKind.HydrateCatalog }, CancellationToken.None);
    }

    private async Task HydrateAsync(Guid titleId, ITmdbClient tmdb)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = await db.Titles.SingleAsync(t => t.Id == titleId);
        await new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance).HydrateBatchAsync([title], CancellationToken.None);
        await db.SaveChangesAsync();
    }

    private static async Task<List<(long TmdbId, CreditRole Role, int? CastOrder)>> CreditsAsync(
        Infrastructure.Persistence.ReelDbContext db, Guid titleId) =>
        (await db.TitleCredits.AsNoTracking()
            .Where(c => c.TitleId == titleId)
            .Join(db.Persons, c => c.PersonId, p => p.Id, (c, p) => new { p.TmdbId, c.Role, c.CastOrder })
            .OrderBy(c => c.CastOrder)
            .ToListAsync())
        .Select(c => (c.TmdbId, c.Role, c.CastOrder))
        .ToList();

    private async Task<Title> SeedShowAsync(
        DateTime? refreshedAt, int votes = 10, MediaType mediaType = MediaType.Show, long? tmdbId = 0)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = mediaType,
            TmdbId = tmdbId == 0 ? Random.Shared.NextInt64(1_000_000_000, long.MaxValue) : tmdbId,
            TraktSlug = $"creators-{Guid.NewGuid():N}",
            Name = $"Creator Fixture {Guid.NewGuid():N}",
            Genres = ["drama"],
            TmdbVoteCount = votes,
            // Long released, so no row here is ever "recent" and due its weekly refresh.
            FirstAiredAt = mediaType == MediaType.Show ? new DateTime(2008, 1, 20, 0, 0, 0, DateTimeKind.Utc) : null,
            ReleasedAt = mediaType == MediaType.Movie ? new DateTime(2008, 1, 20, 0, 0, 0, DateTimeKind.Utc) : null,
            LastMetadataRefreshAt = refreshedAt,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title;
    }

    private async Task<Guid> SeedPersonAsync(long tmdbId, string name, string? profilePath)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var person = new Person { Id = Guid.NewGuid(), TmdbId = tmdbId, Name = name, ProfilePath = profilePath, KnownForDepartment = "Acting" };
        db.Persons.Add(person);
        await db.SaveChangesAsync();
        return person.Id;
    }

    private async Task SeedCreatorCreditAsync(Guid titleId, long personTmdbId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var person = await db.Persons.SingleOrDefaultAsync(p => p.TmdbId == personTmdbId);
        if (person is null)
        {
            person = new Person { Id = Guid.NewGuid(), TmdbId = personTmdbId, Name = "Seeded Creator" };
            db.Persons.Add(person);
        }

        db.TitleCredits.Add(new TitleCredit { Id = Guid.NewGuid(), TitleId = titleId, PersonId = person.Id, Role = CreditRole.Creator, CastOrder = 0 });
        await db.SaveChangesAsync();
    }

    private async Task CleanupAsync(IReadOnlyCollection<Guid> titleIds, People people)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        await db.TitleCredits.Where(c => titleIds.Contains(c.TitleId)).ExecuteDeleteAsync();
        await db.Titles.Where(t => titleIds.Contains(t.Id)).ExecuteDeleteAsync();
        var personIds = people.All;
        await db.Persons.Where(p => personIds.Contains(p.TmdbId)).ExecuteDeleteAsync();
    }

    private static TmdbTitleDetails BreakingBadLike(long tmdbId, People people) => Details(
        tmdbId,
        [new TmdbCastMember(people.Lead, "Lead Actor", "Walter White", 0, "/lead.jpg", "Acting")],
        [
            new TmdbCrewMember(people.EpisodeDirector, "Episode Director", "Director", "Directing", null),
            new TmdbCrewMember(people.Creator, "Show Creator", "Director", "Directing", "/creator.jpg"),
        ],
        [new TmdbCreator(people.Creator, "Show Creator", "/creator.jpg"), new TmdbCreator(people.CoCreator, "Co-Creator", null)]);

    private static TmdbTitleDetails Details(
        long tmdbId, TmdbCastMember[] cast, TmdbCrewMember[] crew, IReadOnlyList<TmdbCreator> creators) =>
        new(tmdbId, "/p.jpg", "/b.jpg", 12m, 8.9m, 15_000, 47, "A chemistry teacher.", null, 62, cast, crew, null, [],
            Creators: creators);

    /// <summary>Random TMDB person ids, so a parallel or earlier fixture's people never collide.</summary>
    private sealed class People
    {
        public long Creator { get; } = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        public long CoCreator { get; } = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        public long EpisodeDirector { get; } = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        public long Lead { get; } = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        public long[] All => [Creator, CoCreator, EpisodeDirector, Lead];
    }

    private sealed class NoopEventHub : IPipelineEventHub
    {
        public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data) { }

        public async IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(
            Guid accountId, long? lastEventId, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>
    /// Answers TV and film details from <see cref="Details"/> and records every fetch. A title it has
    /// no script for — anything else the shared catalogue holds that an earlier tier refreshes — reads
    /// as "not at TMDB", which stamps it and changes nothing else about it.
    /// </summary>
    public class Script : DispatchProxy
    {
        public ConcurrentDictionary<long, TmdbTitleDetails> Details { get; } = [];

        public List<long> Calls { get; } = [];

        public static ITmdbClient Create(out Script script)
        {
            var proxy = Create<ITmdbClient, Script>();
            script = (Script)(object)proxy;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name is not (nameof(ITmdbClient.GetTvAsync) or nameof(ITmdbClient.GetMovieAsync)))
            {
                throw new NotSupportedException($"Creator hydration called TMDB {targetMethod?.Name}.");
            }

            var id = (long)args![0]!;
            lock (Calls)
            {
                Calls.Add(id);
            }

            return Task.FromResult(Details.TryGetValue(id, out var details) ? details : null);
        }
    }
}
