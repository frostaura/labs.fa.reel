using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Application.Ingestion;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Concurrency;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The Home-surface claims the 2026-10-25 founder-walk review found false, each checked on real
/// Postgres: "high for you" on a deep-cuts row whose numbers sit below the reader's median (R1-14);
/// an empty or retired why-line rendering as a clean, caption-less card (R1-51); the CR-9 stub
/// repair script, run exactly as shipped (R1-51); page claims seeded from the weekly digest, and the
/// negative cache that answers a TMDB 404 without asking again (R1-50).
/// </summary>
[Collection("postgres")]
public class FeedSurfaceHonestyTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    // ── R1-14: "Deep cuts — high for you" only when the row clears the reader's median ────────

    [Theory]
    // Founder shape: a 6.4–6.6 row (mean 6.5) under a median of 7 — the episode ratings that
    // would drag the median down to 3 do not count, because a row is a list of titles.
    [InlineData(new short[] { 6, 7, 7, 8, 8 }, false)]
    // The same row under a median of 6 does clear it.
    [InlineData(new short[] { 5, 6, 6, 7, 9 }, true)]
    // An even count: median 6.5, which a 6.5 mean exactly meets.
    [InlineData(new short[] { 6, 6, 7, 7 }, true)]
    public async Task Deep_cuts_claims_high_for_you_only_when_the_row_clears_the_median(short[] titleRatings, bool highForYou)
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"deep-cuts-{Guid.NewGuid():N}");
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
            account.Tier = AccountTier.Paid;

            var snapshot = Snapshot(accountId, SnapshotStatus.Active);
            db.FeedSnapshots.Add(snapshot);
            var hero = NewTitle("Hero pick");
            db.Titles.Add(hero);
            db.FeedItems.Add(Item(snapshot, hero, FeedRowKind.Hero, 0, 7.9m, "You rate Tony Gilroy 7.8 on average."));
            var rank = 0;
            foreach (var predicted in new[] { 6.4m, 6.5m, 6.6m })
            {
                var cut = NewTitle($"Deep cut {rank}");
                db.Titles.Add(cut);
                db.FeedItems.Add(Item(snapshot, cut, FeedRowKind.DeepCuts, rank++, predicted, "Right in your drama wheelhouse."));
            }

            foreach (var rating in titleRatings)
            {
                var rated = NewTitle($"Rated {rating}");
                db.Titles.Add(rated);
                db.UserRatings.Add(Rating(accountId, rated.Id, RatingSubjectType.Movie, rating));
            }

            var show = NewTitle("A long show");
            db.Titles.Add(show);
            for (var episode = 1; episode <= 20; episode++)
            {
                db.UserRatings.Add(Rating(accountId, show.Id, RatingSubjectType.Episode, 3, season: 1, episode: episode));
            }

            await db.SaveChangesAsync();
        }

        using var body = await GetJsonAsync(client, "/api/feed");
        var deepCuts = body.RootElement.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("kind").GetString() == "deep-cuts");

        Assert.Equal(
            highForYou ? FeedEndpoints.DeepCutsHighForYouTitle : FeedEndpoints.DeepCutsPlainTitle,
            deepCuts.GetProperty("title").GetString());
    }

    // ── R1-51: an empty or refused line renders as a clean card with no caption ───────────────

    [Fact]
    public async Task An_empty_or_retired_line_is_served_as_an_empty_caption_on_an_intact_card()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"empty-why-{Guid.NewGuid():N}");
        Guid emptyId, retiredId, keptId, genericId, splicedId;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
            account.Tier = AccountTier.Paid;
            var snapshot = Snapshot(accountId, SnapshotStatus.Active);
            db.FeedSnapshots.Add(snapshot);
            var empty = NewTitle("Repaired stub");
            var retired = NewTitle("Retired boilerplate");
            var kept = NewTitle("Honest line");
            var generic = NewTitle("Generic stub");
            var spliced = NewTitle("Spliced generic stub");
            db.Titles.AddRange(empty, retired, kept, generic, spliced);
            db.FeedItems.Add(Item(snapshot, empty, FeedRowKind.Hero, 0, 7.6m, string.Empty, "Fallback"));
            db.FeedItems.Add(Item(snapshot, retired, FeedRowKind.Hero, 1, 7.4m, "Scores well even by your contrarian standards."));
            db.FeedItems.Add(Item(snapshot, kept, FeedRowKind.Hero, 2, 7.2m, "You rate Tony Gilroy 7.8 on average."));

            // IR2S-01: the templater's retired generic sentence as persisted by a pre-fix build —
            // alone (lower-cased, no full stop) and spliced after an anchor clause.
            db.FeedItems.Add(Item(snapshot, generic, FeedRowKind.Hero, 3, 7.1m, "strong fit with your taste profile"));
            db.FeedItems.Add(Item(snapshot, spliced, FeedRowKind.Hero, 4, 7.0m,
                "Explores murder mystery, like Sherlock: The Abominable Bride, and Strong fit with your taste profile."));
            await db.SaveChangesAsync();
            (emptyId, retiredId, keptId, genericId, splicedId) = (empty.Id, retired.Id, kept.Id, generic.Id, spliced.Id);
        }

        using var body = await GetJsonAsync(client, "/api/feed");
        var hero = body.RootElement.GetProperty("hero").EnumerateArray()
            .ToDictionary(c => c.GetProperty("titleId").GetGuid());

        foreach (var blank in new[] { emptyId, retiredId, genericId })
        {
            var card = hero[blank];
            // An empty STRING — never null, never the generic sentence, never a placeholder — so
            // RecCardHero's `card.whyThis && …` renders no caption and RecCard's line is empty.
            Assert.Equal(JsonValueKind.String, card.GetProperty("whyThis").ValueKind);
            Assert.Equal(string.Empty, card.GetProperty("whyThis").GetString());
            Assert.False(string.IsNullOrEmpty(card.GetProperty("name").GetString()));
            Assert.Equal("/p.jpg", card.GetProperty("posterPath").GetString());
            Assert.True(card.GetProperty("predictedRating").GetDecimal() > 0m);
        }

        Assert.Equal("You rate Tony Gilroy 7.8 on average.", hero[keptId].GetProperty("whyThis").GetString());
        Assert.Equal(
            "Explores murder mystery, like Sherlock: The Abominable Bride.",
            hero[splicedId].GetProperty("whyThis").GetString());
    }

    // ── R1-51: the CR-9 repair script, exactly as shipped ─────────────────────────────────────

    [Fact]
    public async Task The_stub_repair_script_blanks_active_stub_lines_and_nothing_else()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"cr9-{Guid.NewGuid():N}");
        const string Stub = "A action pick aligned with your taste.";
        const string Real = "A tense heist pick that the crowd backs too.";
        const string Generic = "Strong fit with your taste profile.";
        const string SplicedGeneric = "Written by Arthur Conan Doyle, like Sherlock, and Strong fit with your taste profile.";
        const string TasteReal = "Strong fit with your taste for heists, and you rate Michael Mann 8.1.";
        Guid activeStub, activeReal, oldStub, exposureId, activeGeneric, activeSpliced, activeTasteReal;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var active = Snapshot(accountId, SnapshotStatus.Active);
            var superseded = Snapshot(accountId, SnapshotStatus.Superseded);
            db.FeedSnapshots.AddRange(active, superseded);
            var a = NewTitle("Stubbed");
            var b = NewTitle("Real line");
            var c = NewTitle("Old stub");
            db.Titles.AddRange(a, b, c);
            var stubItem = Item(active, a, FeedRowKind.Hero, 0, 7.1m, Stub, "Fallback");
            var realItem = Item(active, b, FeedRowKind.Hero, 1, 7.0m, Real, "Llm");
            var oldItem = Item(superseded, c, FeedRowKind.Hero, 0, 6.9m, Stub, "Fallback");
            var d = NewTitle("Generic");
            var e = NewTitle("Spliced generic");
            var f = NewTitle("Real line about taste");
            db.Titles.AddRange(d, e, f);
            var genericItem = Item(active, d, FeedRowKind.Hero, 2, 6.8m, Generic);
            var splicedItem = Item(active, e, FeedRowKind.Hero, 3, 6.7m, SplicedGeneric);
            var tasteRealItem = Item(active, f, FeedRowKind.Hero, 4, 6.6m, TasteReal, "Llm");
            db.FeedItems.AddRange(stubItem, realItem, oldItem, genericItem, splicedItem, tasteRealItem);
            (activeGeneric, activeSpliced, activeTasteReal) = (genericItem.Id, splicedItem.Id, tasteRealItem.Id);
            var exposure = new ExposureEvent
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = a.Id,
                OccurredAt = DateTime.UtcNow,
                Surface = ExposureSurface.FeedHero,
                Rank = 0,
                FeedSnapshotId = active.Id,
                ModelArtifactId = active.ModelArtifactId,
                PredictedRating = 7.1m,
                WhySentence = Stub,
                WhySource = "Fallback",
            };
            db.ExposureEvents.Add(exposure);
            await db.SaveChangesAsync();
            (activeStub, activeReal, oldStub, exposureId) = (stubItem.Id, realItem.Id, oldItem.Id, exposure.Id);
        }

        var script = await File.ReadAllTextAsync(RepairScriptPath());
        await RunScriptAsync(script);
        await RunScriptAsync(script); // idempotent: the second run matches nothing and changes nothing

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var items = await db.FeedItems.Where(i => i.AccountId == accountId).ToDictionaryAsync(i => i.Id);
            Assert.Equal(string.Empty, items[activeStub].WhyThisSentence);
            Assert.Equal("Fallback", items[activeStub].WhySource); // provenance is kept
            Assert.Equal(Real, items[activeReal].WhyThisSentence);
            Assert.Equal(Stub, items[oldStub].WhyThisSentence);    // superseded: never served, never touched

            // The retired generic sentence: blanked alone, cut back to its anchor clause when spliced,
            // and a real line that merely opens "Strong fit with your taste …" is left alone.
            Assert.Equal(string.Empty, items[activeGeneric].WhyThisSentence);
            Assert.Equal("Written by Arthur Conan Doyle, like Sherlock.", items[activeSpliced].WhyThisSentence);
            Assert.Equal(TasteReal, items[activeTasteReal].WhyThisSentence);

            // The append-only log is never edited.
            Assert.Equal(Stub, (await db.ExposureEvents.SingleAsync(e => e.Id == exposureId)).WhySentence);

            // A blank is not a refused line, so the deploy purge stages no paid build for it.
            var findings = await UntrustedSnapshotSweep.FindAsync(db, CancellationToken.None);
            Assert.DoesNotContain(findings, f => f.AccountId == accountId);
        }
    }

    // ── R1-50: page claims seeded from the digest ─────────────────────────────────────────────

    [Theory]
    [InlineData(0, true)]    // this week's digest: its titles belong to the digest rail
    [InlineData(-7, false)]  // last week's digest is not on the page
    public async Task A_rail_never_claims_a_title_the_weeks_digest_shows(int digestWeekOffsetDays, bool held)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"claims-digest-{Guid.NewGuid():N}");
        var now = DateTime.UtcNow;
        List<Guid> ordered;
        Guid digestA, digestB, snapshotTitle;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var snapshot = Snapshot(accountId, SnapshotStatus.Active);
            db.FeedSnapshots.Add(snapshot);
            var titles = Enumerable.Range(0, 6).Select(i => NewTitle($"Pool {i}")).ToList();
            db.Titles.AddRange(titles);
            snapshotTitle = titles[0].Id;
            db.FeedItems.Add(Item(snapshot, titles[0], FeedRowKind.Hero, 0, 7.5m, "x"));
            (digestA, digestB) = (titles[1].Id, titles[2].Id);
            db.AccountDigests.Add(new AccountDigest
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                WeekStart = DigestWeek.MondayOf(now.AddDays(digestWeekOffsetDays)),
                GeneratedAt = now,
                ItemsJson = JsonSerializer.Serialize(new[]
                {
                    new DigestItem(digestA, 7.4m, DigestReasons.NewRelease),
                    // A digest title that is also tonight's pick stays the snapshot's.
                    new DigestItem(snapshotTitle, 7.5m, DigestReasons.NewOnProvider),
                    new DigestItem(digestB, 7.2m, DigestReasons.NewOnProvider),
                }),
            });
            await db.SaveChangesAsync();
            ordered = titles.Select(t => t.Id).ToList();
        }

        await using var claimDb = fixture.CreateContext(new AccountContext());
        var claimed = await FeedPageClaims.ClaimAsync(
            claimDb, accountId, Guid.NewGuid().ToString("N"), "genre:drama", ordered, 6, now, CancellationToken.None);

        Assert.DoesNotContain(snapshotTitle, claimed);
        Assert.Equal(!held, claimed.Contains(digestA));
        Assert.Equal(!held, claimed.Contains(digestB));
        Assert.Contains(ordered[3], claimed);
    }

    [Fact]
    public async Task An_unreadable_digest_claims_nothing_and_breaks_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"claims-bad-digest-{Guid.NewGuid():N}");
        var now = DateTime.UtcNow;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            db.AccountDigests.Add(new AccountDigest
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                WeekStart = DigestWeek.MondayOf(now),
                GeneratedAt = now,
                ItemsJson = "{\"not\":\"an array\"}",
            });
            await db.SaveChangesAsync();
        }

        var free = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await using var claimDb = fixture.CreateContext(new AccountContext());
        var claimed = await FeedPageClaims.ClaimAsync(
            claimDb, accountId, Guid.NewGuid().ToString("N"), "genre:drama", free, 2, now, CancellationToken.None);

        Assert.Equal(free, claimed);
    }

    // ── R1-50: the TMDB negative cache ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_tmdb_404_is_answered_from_the_negative_cache_the_second_time()
    {
        if (!fixture.Available)
        {
            return;
        }

        var tmdb = CountingNotFoundTmdb.Create();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var coordinator = new CatalogWorkCoordinator();
        var missing = Random.Shared.NextInt64(10_000_000, long.MaxValue);

        Assert.Null(await HydrateAsync(tmdb, coordinator, cache, "movie", missing));
        Assert.Equal(1, CountingNotFoundTmdb.Fetches(tmdb));

        // A re-open of the same dead link asks nobody, and answers well inside the 1 s budget.
        var clock = Stopwatch.StartNew();
        Assert.Null(await HydrateAsync(tmdb, coordinator, cache, "movie", missing));
        clock.Stop();
        Assert.Equal(1, CountingNotFoundTmdb.Fetches(tmdb));
        Assert.True(clock.ElapsedMilliseconds < 1000, $"cached 404 took {clock.ElapsedMilliseconds} ms");

        // The cache is keyed by media type and id: the same id as a show, and another id, still ask.
        Assert.Null(await HydrateAsync(tmdb, coordinator, cache, "show", missing));
        Assert.Null(await HydrateAsync(tmdb, coordinator, cache, "movie", missing - 1));
        Assert.Equal(3, CountingNotFoundTmdb.Fetches(tmdb));

        // Expired (or evicted), the id is asked about again — a 404 is remembered, not believed forever.
        cache.Remove(TitleEndpoints.TmdbNotFoundKey(MediaType.Movie, missing));
        Assert.Null(await HydrateAsync(tmdb, coordinator, cache, "movie", missing));
        Assert.Equal(4, CountingNotFoundTmdb.Fetches(tmdb));
        Assert.Equal(TimeSpan.FromHours(1), TitleEndpoints.TmdbNotFoundTtl);
    }

    [Fact]
    public async Task A_tmdb_failure_that_is_not_a_404_is_never_cached_as_missing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var tmdb = CountingNotFoundTmdb.Create(throwInstead: true);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var missing = Random.Shared.NextInt64(10_000_000, long.MaxValue);

        await Assert.ThrowsAsync<HttpRequestException>(() => HydrateAsync(tmdb, new CatalogWorkCoordinator(), cache, "movie", missing));
        Assert.False(cache.TryGetValue(TitleEndpoints.TmdbNotFoundKey(MediaType.Movie, missing), out _));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private async Task<Title?> HydrateAsync(
        ITmdbClient tmdb, CatalogWorkCoordinator coordinator, IMemoryCache cache, string mediaType, long tmdbId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await TitleEndpoints.HydrateOnDemandAsync(
            db, tmdb,
            new CandidateGenerator(db, tmdb, new EligibilityQueryBuilder(db), NullLogger<CandidateGenerator>.Instance),
            new TitleHydrator(db, tmdb, NullLogger<TitleHydrator>.Instance),
            coordinator, mediaType, tmdbId, CancellationToken.None, cache);
    }

    private async Task RunScriptAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The shipped script, found from the test assembly up to the repository root.</summary>
    private static string RepairScriptPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "fa.reel.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(
            directory!.FullName, "src", "backend", "FrostAura.Reel.Application", "Ranking", "StubWhyLineRepair.sql");
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static FeedSnapshot Snapshot(Guid accountId, SnapshotStatus status) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        ModelArtifactId = Guid.NewGuid(),
        GeneratedAt = DateTime.UtcNow,
        Status = status,
    };

    private static Title NewTitle(string name) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = $"surface-honesty-{Guid.NewGuid():N}",
        Name = name,
        Year = 2016,
        PosterPath = "/p.jpg",
        CreatedAt = DateTime.UtcNow,
    };

    private static FeedItem Item(
        FeedSnapshot snapshot, Title title, FeedRowKind row, int rank, decimal predicted, string why, string whySource = "Templater") => new()
        {
            Id = Guid.NewGuid(),
            AccountId = snapshot.AccountId,
            FeedSnapshotId = snapshot.Id,
            Row = row,
            Rank = rank,
            TitleId = title.Id,
            PredictedRating = predicted,
            WhyThisSentence = why,
            WhySource = whySource,
        };

    private static UserRating Rating(
        Guid accountId, Guid titleId, RatingSubjectType subject, short rating, int season = 0, int episode = 0) => new()
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = subject,
            SeasonNumber = season,
            EpisodeNumber = episode,
            Rating = rating,
            RatedAt = DateTime.UtcNow.AddDays(-30),
            FirstRatedAt = DateTime.UtcNow.AddDays(-30),
            SyncedAt = DateTime.UtcNow,
        };

    /// <summary>
    /// A TMDB port whose detail lookups 404 (or fail outright) and are counted; every other call is
    /// <see cref="SilentTmdbClient"/>'s empty answer.
    /// </summary>
    public class CountingNotFoundTmdb : DispatchProxy
    {
        private readonly SilentTmdbClient _silent = new();
        private int _fetches;
        private bool _throw;

        public static ITmdbClient Create(bool throwInstead = false)
        {
            var proxy = Create<ITmdbClient, CountingNotFoundTmdb>();
            ((CountingNotFoundTmdb)(object)proxy)._throw = throwInstead;
            return proxy;
        }

        public static int Fetches(ITmdbClient client) => ((CountingNotFoundTmdb)(object)client)._fetches;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name is nameof(ITmdbClient.GetMovieAsync) or nameof(ITmdbClient.GetTvAsync))
            {
                Interlocked.Increment(ref _fetches);
                return _throw
                    ? Task.FromException<TmdbTitleDetails?>(new HttpRequestException("TMDB 503"))
                    : Task.FromResult<TmdbTitleDetails?>(null);
            }

            return targetMethod!.Invoke(_silent, args);
        }
    }
}
