using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Application.Auth;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Ports.Trakt;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Background;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FrostAura.Reel.Tests.IntegrationTests.SyntheticWeek;

/// <summary>
/// The synthetic week (R1-36; program §5.2 WS-4/WS-5/WS-6 acceptance: "over 7 days" checks run on the
/// eval stack under a replayed synthetic week). Opt-in: it runs only when <c>REEL_SYNTHETIC_WEEK_DB</c>
/// names a cloned eval database (<c>scripts/eval/snapshot.sh clone reel_eval_template reel_ws7_week</c>),
/// and it CHANGES that database — a clone is spent by one run.
///
/// <para><b>How a week passes in half an hour.</b> 28 six-hour ticks. Each tick the account does what
/// a tick of a real week holds — a morning refresh, Home loads, saves, a not-interested, a watch and a rating scrobbled on
/// Trakt, a rating in the app, a watchlist add — the delta poll's own scheduling pass runs
/// (<c>TraktDeltaPollService.ScheduleAsync</c>), and the real job runner and outbox dispatcher work the
/// queue on wall time until it is empty. Then <see cref="DatabaseClock"/> ages the whole database by
/// six hours, so the next tick's code sees six hours gone. The Friday tick runs the weekly digest's
/// scheduling pass. Two quiet days carry no ratings at all (the retrain and stability checks need them).
/// Queue waits and run times are real: the work is measured on the clock it ran on, then aged with it.</para>
///
/// <para><b>Zero paid calls, zero network.</b> <see cref="ReelApiFactory"/> blanks every provider key and
/// runs every model adapter in stub mode; TMDB is <see cref="WeekTmdbClient"/>; Trakt is
/// <see cref="WeekTraktClient"/>.</para>
///
/// <para><b>Not in the week:</b> the nightly reconcile (its pass is inline in
/// <c>NightlyReconcileService.ExecuteAsync</c> behind a wall-clock wait for 03:00 UTC, with no seam a
/// harness can call — owed to WS-5 as a change request), so no nightly retrain, forced delta or
/// enrichment sweep runs, and the acceptance rows that need them say so.</para>
///
/// <para>Writes <c>.tmp/eval/week/&lt;run&gt;/ticks.jsonl</c> and <c>run.json</c> (whose <c>since</c> bounds the
/// week for <c>scripts/eval/week.mjs</c>, which runs the acceptance SQL).</para>
/// </summary>
[Collection("postgres")]
[Trait("Suite", "SyntheticWeek")]
public class SyntheticWeekRun(PostgresFixture fixture)
{
    public const string DatabaseVariable = "REEL_SYNTHETIC_WEEK_DB";

    private static readonly TimeSpan Tick = TimeSpan.FromHours(6);
    private const int Days = 7;
    private const int TicksPerDay = 4;
    private static readonly int[] QuietDays = [2, 5];
    private const int FridayIndex = 4;
    private static readonly TimeSpan IdleCap = TimeSpan.FromMinutes(12);

    [Fact]
    public async Task A_synthetic_week_runs_on_a_cloned_eval_database()
    {
        var target = Environment.GetEnvironmentVariable(DatabaseVariable);
        if (string.IsNullOrWhiteSpace(target) || !fixture.Available)
        {
            return;
        }

        if (!(target.StartsWith("reel_ws", StringComparison.Ordinal) || target.StartsWith("reel_eval_", StringComparison.Ordinal))
            || target.EndsWith("_template", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{DatabaseVariable}={target}: the week runs only on a clone (reel_ws* or reel_eval_*), never a template.");
        }

        var own = new NpgsqlConnectionStringBuilder(fixture.ConnectionString!) { Database = target }.ConnectionString;
        if (PostgresFixtureGate.LiveTargetRefusal(own) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        var runDir = Path.Combine(RepoRoot(), ".tmp", "eval", "week", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(runDir);
        var log = new StreamWriter(Path.Combine(runDir, "ticks.jsonl"));
        var started = DateTime.UtcNow;
        var trakt = new WeekTraktClient(started);
        var tmdb = new WeekTmdbClient(own);
        var random = new Random(20261027);

        await using var api = ReelApiFactory.ForDatabase(own).WithWebHostBuilder(host => host.ConfigureServices(services =>
        {
            services.RemoveAll<ITraktClient>();
            services.AddSingleton<ITraktClient>(trakt);
            services.RemoveAll<ITmdbClient>();
            services.AddSingleton<ITmdbClient>(tmdb);
            // The boot repairs a deploy runs once (WS-4), then the two loops a week of use needs.
            services.AddHostedService<DataRepairStartupService>();
            services.AddHostedService<JobRunnerService>();
            services.AddHostedService<TraktOutboxDispatcherService>();
        }));

        _ = api.Services; // starts the host: migrations, the runner and the dispatcher
        var accountId = await PrepareAccountAsync(api, trakt);
        var deltaPoll = ActivatorUtilities.CreateInstance<TraktDeltaPollService>(api.Services);
        var digest = ActivatorUtilities.CreateInstance<WeeklyDigestService>(api.Services);

        // REEL_SYNTHETIC_WEEK_TICKS shortens the week for a smoke run of the harness itself.
        var ticks = int.TryParse(Environment.GetEnvironmentVariable("REEL_SYNTHETIC_WEEK_TICKS"), out var t) && t is > 0 and < Days * TicksPerDay
            ? t
            : Days * TicksPerDay;
        var aged = TimeSpan.Zero;
        var stopped = (string?)null;
        for (var tick = 0; tick < ticks && stopped is null; tick++)
        {
            var day = tick / TicksPerDay;
            var slot = tick % TicksPerDay;
            var wall = Stopwatch.StartNew();
            var activity = new List<string>();
            if (tick > 0)
            {
                await using var connection = new NpgsqlConnection(own);
                await connection.OpenAsync();
                await DatabaseClock.AgeAsync(connection, Tick);
                aged += Tick;
            }

            // A fresh session every tick: access tokens expire on the wall clock, which the week does not move.
            using var client = await SignedInClientAsync(api, accountId);
            await ActAsync(api, client, trakt, accountId, day, slot, random, activity);
            var queued = await deltaPoll.ScheduleAsync(DateTime.UtcNow, 5, 15, CancellationToken.None);
            activity.Add($"delta-poll queued {queued}");
            if (day == FridayIndex && slot == 2)
            {
                activity.Add($"digest queued {await digest.ScheduleAsync(DateTime.UtcNow, CancellationToken.None)}");
            }

            var idle = await WaitIdleAsync(api, accountId);
            if (!idle)
            {
                stopped = $"tick {tick}: the queue was not idle after {IdleCap.TotalMinutes:0} minutes; aging a running job would inflate its run time, so the week stops here";
            }

            await log.WriteLineAsync(JsonSerializer.Serialize(new
            {
                tick,
                day,
                slot,
                wallSeconds = Math.Round(wall.Elapsed.TotalSeconds, 1),
                agedHours = aged.TotalHours,
                activity,
                idle,
                traktCalls = trakt.Calls.ToDictionary(),
                traktWrites = trakt.Writes.ToDictionary(),
                providerLookups = tmdb.ProviderLookups,
            }));
            await log.FlushAsync();
        }

        await log.DisposeAsync();
        // Every job the week enqueued was created after `started` on the wall clock and has been aged by
        // at most `aged`; everything the snapshot held was older than that before the first tick.
        var since = started - aged;
        await File.WriteAllTextAsync(Path.Combine(runDir, "run.json"), JsonSerializer.Serialize(new
        {
            database = target,
            startedAt = started,
            finishedAt = DateTime.UtcNow,
            agedHours = aged.TotalHours,
            since,
            accountId,
            stopped,
            quietDays = QuietDays,
            fridayIndex = FridayIndex,
            ticks,
            traktCalls = trakt.Calls.ToDictionary(),
            traktWrites = trakt.Writes.ToDictionary(),
            providerLookups = tmdb.ProviderLookups,
        }, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Null(stopped);
        Assert.False(trakt.Calls.ContainsKey("sync/watched/shows"), "a show snapshot was read — the week never moves the episode stamp");
    }

    /// <summary>
    /// The clone's account gets a synthetic Trakt connection (tokens protected by THIS host's key ring;
    /// the clone's live ring was deleted by the sanitiser), a cursor equal to the fake's first snapshot so
    /// the first delta reads only what moved, and a recent visit so the poll treats it as active.
    /// </summary>
    private static async Task<Guid> PrepareAccountAsync(WebApplicationFactory<Program> api, WeekTraktClient trakt)
    {
        using var scope = api.Services.CreateScope();
        using var system = scope.ServiceProvider.GetRequiredService<IAccountContext>().EnterSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var account = await db.Accounts.OrderBy(a => a.CreatedAt).FirstAsync();
        var now = DateTime.UtcNow;
        account.LastSeenAt = now;
        var connection = await db.TraktConnections.FirstOrDefaultAsync(c => c.AccountId == account.Id);
        if (connection is null)
        {
            connection = new TraktConnection { Id = Guid.NewGuid(), AccountId = account.Id };
            db.TraktConnections.Add(connection);
        }

        connection.AccessTokenEncrypted = protector.Protect("synthetic-week-access");
        connection.RefreshTokenEncrypted = protector.Protect("synthetic-week-refresh");
        connection.ExpiresAt = now.AddDays(90);
        connection.Status = ConnectionStatus.Active;
        connection.LastActivitiesJson = trakt.LastActivitiesJson();
        connection.LastDeltaSyncAt = now;
        await db.SaveChangesAsync();
        return account.Id;
    }

    /// <summary>One tick's activity. Morning: a refresh, Home and Saved. Afternoon: a save and a not-interested.
    /// Evening: a Trakt watch-and-rate of a saved movie, an in-app rating, a watchlist add. Night: Home.
    /// Quiet days rate nothing.</summary>
    private static async Task ActAsync(
        WebApplicationFactory<Program> api, HttpClient client, WeekTraktClient trakt, Guid accountId, int day, int slot, Random random, List<string> activity)
    {
        var quiet = QuietDays.Contains(day);
        switch (slot)
        {
            case 0 or 3:
                if (slot == 0)
                {
                    // The morning refresh (a paid plan's manual rebuild): without it the page rotates only
                    // when a rating or a reaction triggers a build, and the week's picks run dry by day 5.
                    activity.Add($"refresh {(int)(await client.PostAsync("/api/feed/rebuild", null)).StatusCode}");
                }

                activity.Add($"home {(int)(await client.GetAsync("/api/feed")).StatusCode}");
                activity.Add($"saved {(int)(await client.GetAsync("/api/saved")).StatusCode}");
                break;
            case 1:
                {
                    var picks = await PicksAsync(api, accountId, 2);
                    if (picks.Count > 0)
                    {
                        activity.Add($"save {picks[0].TmdbId} {(int)(await client.PostAsync($"/api/titles/movie/{picks[0].TmdbId}/reactions/save_for_later", null)).StatusCode}");
                    }

                    if (picks.Count > 1)
                    {
                        var response = await client.PostAsJsonAsync($"/api/titles/movie/{picks[1].TmdbId}/reactions/not_interested", new { reason = (string?)null });
                        activity.Add($"not-interested {picks[1].TmdbId} {(int)response.StatusCode}");
                    }

                    break;
                }

            case 2:
                {
                    var now = DateTime.UtcNow;
                    if (!quiet && await SavedMovieAsync(api, accountId) is { } saved)
                    {
                        trakt.Watch(saved, now);
                        trakt.Rate(saved, (short)random.Next(6, 11), now);
                        activity.Add($"trakt watch+rate {saved.Ids.Tmdb}");
                    }

                    var picks = await PicksAsync(api, accountId, 2);
                    if (!quiet && picks.Count > 0)
                    {
                        var response = await client.PostAsJsonAsync($"/api/titles/movie/{picks[0].TmdbId}/rating", new { rating = (short)random.Next(5, 11), markWatched = true });
                        activity.Add($"rate {picks[0].TmdbId} {(int)response.StatusCode}");
                    }

                    if (picks.Count > 1)
                    {
                        trakt.Watchlist(Movie(picks[1]), now);
                        activity.Add($"trakt watchlist {picks[1].TmdbId}");
                    }

                    break;
                }
        }
    }

    /// <summary>A client carrying the account's <c>reel_at</c> cookie, minted the way <see cref="ReelApiFactory.CreateSignedInClientAsync"/> mints it.</summary>
    private static async Task<HttpClient> SignedInClientAsync(WebApplicationFactory<Program> api, Guid accountId)
    {
        using var scope = api.Services.CreateScope();
        using var system = scope.ServiceProvider.GetRequiredService<IAccountContext>().EnterSystemScope();
        var account = await scope.ServiceProvider.GetRequiredService<ReelDbContext>().Accounts.FirstAsync(a => a.Id == accountId);
        var token = scope.ServiceProvider.GetRequiredService<SessionService>().MintAccessToken(account, DateTime.UtcNow);
        var cookies = new CookieContainerHandler();
        cookies.Container.Add(api.ClientOptions.BaseAddress, new System.Net.Cookie("reel_at", token, "/", api.ClientOptions.BaseAddress.Host));
        return api.CreateDefaultClient(cookies);
    }

    private sealed record Pick(long TmdbId, long TraktId, string Name, int? Year);

    private static TraktMovie Movie(Pick p) =>
        new(p.Name, p.Year, new TraktIds(p.TraktId, null, null, p.TmdbId), null, null, null, null, null, null, null, 0, null, null, null, null, null);

    /// <summary>Movies on the newest feed snapshot the account has not touched, best first.</summary>
    private static async Task<List<Pick>> PicksAsync(WebApplicationFactory<Program> api, Guid accountId, int count)
    {
        using var scope = api.Services.CreateScope();
        using var system = scope.ServiceProvider.GetRequiredService<IAccountContext>().EnterSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var snapshot = await db.FeedSnapshots.Where(s => s.AccountId == accountId).OrderByDescending(s => s.GeneratedAt).Select(s => s.Id).FirstOrDefaultAsync();
        var rows = await db.FeedItems
            .Where(i => i.FeedSnapshotId == snapshot)
            .Join(db.Titles, i => i.TitleId, t => t.Id, (i, t) => new { i.FinalScore, Title = t })
            .Where(x => x.Title.MediaType == MediaType.Movie && x.Title.TmdbId != null)
            .Where(x => !db.UserTitleReactions.Any(r => r.AccountId == accountId && r.TitleId == x.Title.Id && r.RevokedAt == null))
            .Where(x => !db.UserRatings.Any(r => r.AccountId == accountId && r.TitleId == x.Title.Id))
            .Where(x => !db.WatchedTitles.Any(w => w.AccountId == accountId && w.TitleId == x.Title.Id))
            .Select(x => new { x.FinalScore, Pick = new Pick(x.Title.TmdbId!.Value, x.Title.TraktId ?? WeekTraktClient.SyntheticTraktId(x.Title.TmdbId!.Value), x.Title.Name, x.Title.Year) })
            .ToListAsync();

        // A title can sit in two rows of one snapshot; best score first, ties by id, so a run is repeatable.
        // In memory: SQL would drop the ordering at the DISTINCT.
        return [.. rows
            .OrderByDescending(x => x.FinalScore).ThenBy(x => x.Pick.TmdbId)
            .Select(x => x.Pick)
            .DistinctBy(p => p.TmdbId)
            .Take(count)];
    }

    /// <summary>The oldest saved, unwatched movie — the one a person finally gets round to.</summary>
    private static async Task<TraktMovie?> SavedMovieAsync(WebApplicationFactory<Program> api, Guid accountId)
    {
        using var scope = api.Services.CreateScope();
        using var system = scope.ServiceProvider.GetRequiredService<IAccountContext>().EnterSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var pick = await db.UserTitleReactions
            .Where(r => r.AccountId == accountId && r.Kind == ReactionKind.SaveForLater && r.RevokedAt == null)
            .Join(db.Titles, r => r.TitleId, t => t.Id, (r, t) => new { r.CreatedAt, Title = t })
            .Where(x => x.Title.MediaType == MediaType.Movie && x.Title.TmdbId != null)
            .Where(x => !db.WatchedTitles.Any(w => w.AccountId == accountId && w.TitleId == x.Title.Id && w.IsFullyWatched))
            .OrderBy(x => x.CreatedAt)
            .Select(x => new Pick(x.Title.TmdbId!.Value, x.Title.TraktId ?? WeekTraktClient.SyntheticTraktId(x.Title.TmdbId!.Value), x.Title.Name, x.Title.Year))
            .FirstOrDefaultAsync();
        return pick is null ? null : Movie(pick);
    }

    /// <summary>Waits until no job is pending or running and no outbox row is due, or the cap passes.</summary>
    private static async Task<bool> WaitIdleAsync(WebApplicationFactory<Program> api, Guid accountId)
    {
        var deadline = DateTime.UtcNow + IdleCap;
        var quietPolls = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            using var scope = api.Services.CreateScope();
            using var system = scope.ServiceProvider.GetRequiredService<IAccountContext>().EnterSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
            var busy = await db.SyncJobs.AnyAsync(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running);
            var outbox = await db.TraktOutbox.AnyAsync(o => o.AccountId == accountId && o.Status == OutboxStatus.Pending && o.NextAttemptAt <= DateTime.UtcNow);
            // Two quiet polls in a row: a job that finishes can chain another within one poll interval.
            quietPolls = busy || outbox ? 0 : quietPolls + 1;
            if (quietPolls >= 3)
            {
                return true;
            }
        }

        return false;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "fa.reel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("fa.reel.slnx not found above the test assembly.");
    }
}
