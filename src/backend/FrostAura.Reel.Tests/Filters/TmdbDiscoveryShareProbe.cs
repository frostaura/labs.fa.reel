using System.Diagnostics;
using System.Globalization;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Infrastructure.Adapters;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Infrastructure.RateLimiting;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// The measurement behind the TMDB discovery arm's eligible-share gate (≥ 50%, from 22.8%): the 30
/// lane-C probes run against a real account's data and LIVE TMDB, the v1 call replicated beside the
/// arm, so every figure quoted for the arm can be reproduced rather than taken on trust.
///
/// <para><b>Opt-in only, never in CI.</b> It does nothing unless <c>REEL_TMDB_SHARE_PROBE=1</c>, and
/// then needs <c>REEL_PROBE_CONNECTION_STRING</c> (a SLOT copy of an account's data — never the live
/// database), <c>TMDB_READ_ACCESS_TOKEN</c> (TMDB is free; no paid provider is called) and optionally
/// <c>REEL_PROBE_ACCOUNT_ID</c> (default: the account with the most ratings). <c>REEL_PROBE_SWEEP=1</c>
/// adds the vote-floor and sort sweeps. Run it with
/// <c>--filter FullyQualifiedName~TmdbDiscoveryShareProbe --logger "console;verbosity=detailed"</c>;
/// it reads, it writes nothing.</para>
///
/// <para><b>Definitions.</b> Pulled: distinct TMDB items a call returned. Eligible: catalogue titles
/// inside the account's eligibility (the gate's definition, and lane C's). Provisional: new titles
/// that pass the list-row checks — counted separately, never as eligible. The v1 replica is lane C's:
/// one popularity page per medium with the six genre exclusions the model echoed, the whole message
/// as a title search, the 40 most popular kept.</para>
/// </summary>
public class TmdbDiscoveryShareProbe(ITestOutputHelper output)
{
    /// <summary>The 30 lane-C probes: id, message, the genres and concepts the model extracted.</summary>
    private static readonly (string Id, string Message, string[] Genres, string[] Concepts)[] Probes =
    [
        ("ninja", "ninja movies", ["action"], ["ninja"]),
        ("heist", "a clever heist movie", ["crime", "thriller"], ["heist"]),
        ("timeloop", "time loop movies", ["science-fiction"], ["time loop"]),
        ("sea", "survival at sea", ["drama", "adventure"], ["survival", "lost at sea"]),
        ("courtroom", "courtroom drama", ["drama"], ["courtroom"]),
        ("cyberpunk", "cyberpunk", ["science-fiction"], ["cyberpunk"]),
        ("slowhorror", "slow-burn psychological horror", ["horror"], ["slow burn", "psychological horror"]),
        ("feelsports", "feel-good sports underdog story", ["drama"], ["sports", "underdog"]),
        ("haunted", "haunted house horror", ["horror"], ["haunted house"]),
        ("serialkiller", "detective hunting a serial killer", ["crime", "thriller", "mystery"], ["serial killer", "detective"]),
        ("zombie", "zombie apocalypse", ["horror"], ["zombie", "zombie apocalypse"]),
        ("alieninvasion", "alien invasion", ["science-fiction"], ["alien invasion"]),
        ("submarine", "submarine thriller", ["thriller", "war"], ["submarine"]),
        ("vampire", "vampire movies", ["horror", "fantasy"], ["vampire"]),
        ("werewolf", "werewolf movies", ["horror"], ["werewolf"]),
        ("dinosaur", "dinosaurs", ["adventure", "science-fiction"], ["dinosaur"]),
        ("kaiju", "giant monster kaiju", ["science-fiction", "action"], ["kaiju", "giant monster"]),
        ("samurai", "samurai", ["action", "drama"], ["samurai"]),
        ("mafia", "mafia gangster movies", ["crime", "drama"], ["mafia", "gangster"]),
        ("con", "con artists and grifters", ["crime", "comedy"], ["con artist"]),
        ("foundfootage", "found footage horror", ["horror"], ["found footage"]),
        ("postapoc", "post-apocalyptic survival", ["science-fiction", "drama"], ["post-apocalyptic future"]),
        ("timetravel", "time travel", ["science-fiction"], ["time travel"]),
        ("ai", "artificial intelligence and robots", ["science-fiction"], ["artificial intelligence", "robot"]),
        ("boxing", "boxing movies", ["drama"], ["boxing"]),
        ("chess", "chess", ["drama"], ["chess"]),
        ("prison", "prison escape", ["crime", "thriller"], ["prison escape"]),
        ("spy", "spy espionage thriller", ["thriller", "action"], ["spy", "espionage"]),
        ("shark", "shark attack", ["horror", "thriller"], ["shark attack"]),
        ("hacker", "hacker thriller", ["thriller", "crime"], ["hacker"]),
    ];

    /// <summary>The six genres the model echoed as exclusions in the observed v1 turns (lane C).</summary>
    private static readonly string[] V1EchoedExclusions = ["animation", "comedy", "documentary", "history", "music", "romance"];

    [Fact]
    public async Task Measure_the_eligible_share_against_live_tmdb()
    {
        if (Environment.GetEnvironmentVariable("REEL_TMDB_SHARE_PROBE") != "1")
        {
            return;
        }

        var connection = Environment.GetEnvironmentVariable("REEL_PROBE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("REEL_PROBE_CONNECTION_STRING is required (a slot copy, never the live database).");
        var token = Environment.GetEnvironmentVariable("TMDB_READ_ACCESS_TOKEN")
            ?? throw new InvalidOperationException("TMDB_READ_ACCESS_TOKEN is required.");

        using var gate = new PriorityRateGate(40, TimeSpan.FromSeconds(1));
        var tmdb = BuildTmdb(gate, token);
        ReelDbContext Context() => new(
            new DbContextOptionsBuilder<ReelDbContext>().UseNpgsql(connection, npgsql => npgsql.UseVector()).Options,
            new AccountContext());

        Guid accountId;
        await using (var db = Context())
        {
            accountId = Guid.TryParse(Environment.GetEnvironmentVariable("REEL_PROBE_ACCOUNT_ID"), out var given)
                ? given
                : await db.UserRatings.GroupBy(r => r.AccountId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstAsync();
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"account {accountId:N}; {Probes.Length} probes; live TMDB at 40/s"));
        await V1Async(tmdb, Context, accountId);

        using var warm = new MemoryCache(new MemoryCacheOptions());
        await ArmAsync("arm 1.5 s, cold cache", tmdb, Context, accountId, TmdbDiscoveryArm.DefaultTimeout, () => new MemoryCache(new MemoryCacheOptions()));
        await ArmAsync("arm 1.5 s, warm cache", tmdb, Context, accountId, TmdbDiscoveryArm.DefaultTimeout, () => warm, warmFirst: true);
        await ArmAsync("arm 30 s", tmdb, Context, accountId, TimeSpan.FromSeconds(30), () => warm, perProbe: true);

        if (Environment.GetEnvironmentVariable("REEL_PROBE_SWEEP") == "1")
        {
            foreach (var votes in new[] { 50, 200 })
            {
                await ArmAsync($"arm 30 s, vote floor {votes}", tmdb, Context, accountId, TimeSpan.FromSeconds(30), () => warm, minVotes: votes);
            }

            foreach (var sort in new[] { TmdbDiscoverSort.VoteCount, TmdbDiscoverSort.VoteAverage, TmdbDiscoverSort.Newest })
            {
                await ArmAsync($"arm 30 s, sort {sort}", tmdb, Context, accountId, TimeSpan.FromSeconds(30), () => warm, sort: sort);
            }
        }
    }

    private async Task V1Async(ITmdbClient tmdb, Func<ReelDbContext> context, Guid accountId)
    {
        int pulled = 0, eligible = 0;
        foreach (var probe in Probes)
        {
            var keywordIds = new List<int>();
            foreach (var concept in probe.Concepts.Take(4))
            {
                if ((await tmdb.SearchKeywordsAsync(concept)).FirstOrDefault() is { } first)
                {
                    keywordIds.Add(first.Id);
                }
            }

            var items = new List<TmdbListItem>();
            foreach (var movies in new[] { true, false })
            {
                int? IdOf(string slug) =>
                    TmdbGenres.SlugToIds.TryGetValue(slug, out var ids) ? (movies ? ids.Movie : ids.Tv) : null;
                var genreIds = probe.Genres.Select(IdOf).OfType<int>().Distinct().ToList();
                var exclude = V1EchoedExclusions.Select(IdOf).OfType<int>().Distinct().ToList();
                items.AddRange(await tmdb.DiscoverByConceptAsync(
                    movies, genreIds, keywordIds, null, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1, excludeGenreIds: exclude));
                items.AddRange(await tmdb.SearchTitlesAsync(movies, probe.Message, 1));
            }

            var kept = items
                .OrderByDescending(i => i.Popularity)
                .GroupBy(i => (i.IsMovie, i.Id))
                .Select(g => g.First())
                .Take(40)
                .ToList();
            pulled += kept.Count;
            eligible += await CountEligibleAsync(context, accountId, kept);
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"v1 replica            pulled {pulled,5}  eligible {eligible,5}  share {Share(eligible, pulled)}"));
    }

    private async Task ArmAsync(
        string label,
        ITmdbClient tmdb,
        Func<ReelDbContext> context,
        Guid accountId,
        TimeSpan timeout,
        Func<IMemoryCache> cache,
        bool warmFirst = false,
        bool perProbe = false,
        int minVotes = TmdbDiscoveryArm.DefaultMinVoteCount,
        TmdbDiscoverSort sort = TmdbDiscoverSort.Popularity)
    {
        int pulled = 0, eligible = 0, provisional = 0, footprint = 0, timedOut = 0;
        var elapsed = new List<long>();
        foreach (var probe in Probes)
        {
            var request = new TmdbDiscoveryRequest(
                accountId, probe.Genres, probe.Concepts, [], [], MediaScope.Any, null, null, probe.Message, null, Want: 40, sort, minVotes);
            await using var db = context();
            var arm = new TmdbDiscoveryArm(tmdb, db, new EligibilityQueryBuilder(db), cache());
            if (warmFirst)
            {
                await arm.DiscoverAsync(request, TimeSpan.FromSeconds(30), CancellationToken.None);
            }

            var clock = Stopwatch.StartNew();
            var result = await arm.DiscoverAsync(request, timeout, CancellationToken.None);
            elapsed.Add(clock.ElapsedMilliseconds);

            var catalogueRejects = result.Items
                .Where(i => i.CatalogueTitleId is not null && !i.Eligible)
                .Select(i => i.CatalogueTitleId!.Value)
                .ToList();
            var seenOrRated = await db.WatchedTitles.Where(w => w.AccountId == accountId && catalogueRejects.Contains(w.TitleId)).Select(w => w.TitleId)
                .Union(db.UserRatings.Where(r => r.AccountId == accountId && catalogueRejects.Contains(r.TitleId)).Select(r => r.TitleId))
                .CountAsync();

            pulled += result.Items.Count;
            eligible += result.EligibleItems.Count;
            provisional += result.ProvisionalItems.Count;
            footprint += seenOrRated;
            timedOut += result.TimedOut ? 1 : 0;
            if (perProbe)
            {
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {probe.Id,-14} pulled {result.Items.Count,3}  eligible {result.EligibleItems.Count,3}  provisional {result.ProvisionalItems.Count,3}  seen/rated rejects {seenOrRated,3}  {result.ElapsedMs,5} ms"));
            }
        }

        elapsed.Sort();
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{label,-28} pulled {pulled,5}  eligible {eligible,5}  share {Share(eligible, pulled)}  provisional {provisional,4}  usable {Share(eligible + provisional, pulled)}  rejected-as-seen/rated {footprint,4}  timed out {timedOut,2}/{Probes.Length}  p50 {Percentile(elapsed, 0.5),5} ms  p95 {Percentile(elapsed, 0.95),5} ms"));
    }

    private static async Task<int> CountEligibleAsync(Func<ReelDbContext> context, Guid accountId, IReadOnlyList<TmdbListItem> items)
    {
        await using var db = context();
        var ids = items.Select(i => (long?)i.Id).Distinct().ToList();
        var rows = await new EligibilityQueryBuilder(db).EligibleTitles(accountId)
            .Where(t => ids.Contains(t.TmdbId))
            .Select(t => new { t.TmdbId, t.MediaType })
            .ToListAsync();
        return items.Count(i => rows.Any(r => r.TmdbId == i.Id && r.MediaType == (i.IsMovie ? MediaType.Movie : MediaType.Show)));
    }

    private static string Share(int part, int whole) =>
        whole == 0 ? "  n/a" : string.Create(CultureInfo.InvariantCulture, $"{100d * part / whole,5:F1}%");

    private static long Percentile(List<long> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    private static TmdbClient BuildTmdb(IRateGate gate, string token)
    {
        var services = new ServiceCollection().AddKeyedSingleton("tmdb", gate).BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TMDB_READ_ACCESS_TOKEN"] = token })
            .Build();
        var httpClient = new HttpClient { BaseAddress = new Uri("https://api.themoviedb.org/3/"), Timeout = TimeSpan.FromSeconds(30) };
        var recorder = new ApiUsageRecorder(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ApiUsageRecorder>.Instance);
        return new TmdbClient(httpClient, gate, recorder, configuration);
    }
}
