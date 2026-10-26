using System.Text.Json;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Providers;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Notifications;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Providers;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// BuildDigest on real Postgres: the trailing-7-day window, the 7.0 predicted floor and its
/// relaxation, the zero-footprint eligibility rule, the new-on-provider suppression against last
/// week's digest, the (AccountId, WeekStart) upsert, and the push-subscription lifecycle (success
/// resets, gone deletes, fifth consecutive failure prunes). Skips when REEL_TEST_CONNECTION_STRING
/// is unset. Every candidate is pre-scored under the Active artifact so the on-demand scorer
/// stays un-invoked — these tests pin the digest's set logic, not the model.
/// </summary>
[Collection("postgres")]
public class DigestJobHandlerTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Window_floor_and_footprint_gate_the_items_and_reruns_upsert()
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var accountId = await SeedAccountAsync();

        // Eight titles spanning every gate. Five clear 7.0, which is the ThinDigestCount, so the
        // floor stays strict here and lowScore is genuinely excluded by it — the relaxed path has
        // its own tests below.
        //   freshShow    — show, first aired 2 days ago, 9.0  → in, "new-release"
        //   freshMovie   — movie, released 3 days ago, 8.5  → in, "new-release"
        //   freshMovie2  — movie, released 2 days ago, 8.2  → in, "new-release"
        //   freshMovie3  — movie, released 4 days ago, 7.8  → in, "new-release"
        //   staleMovie   — released 30 days ago, 9.9        → out: outside the window
        //   lowScore     — released 2 days ago, 6.9         → out: under the 7.0 floor
        //   watched      — released 1 day ago, 9.9, watched → out: interaction footprint
        //   onProvider   — old title, flatrate ARRIVED yesterday, 8.0 → in, "new-on-provider"
        var freshShow = await SeedTitleAsync(MediaType.Show, firstAiredAt: now.AddDays(-2));
        var freshMovie = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-3));
        var freshMovie2 = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-2));
        var freshMovie3 = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-4));
        var staleMovie = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-30));
        var lowScore = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-2));
        var watched = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-1));
        var onProvider = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddYears(-2));

        await SeedWatchedAsync(accountId, watched);
        var arrivedOn = await SeedAvailabilityAsync(onProvider, "US", now.AddDays(-1), firstSeenAt: now.AddDays(-1));
        await SeedActiveArtifactAndScoresAsync(accountId, new()
        {
            [freshShow] = 9.0m,
            [freshMovie] = 8.5m,
            [freshMovie2] = 8.2m,
            [freshMovie3] = 7.8m,
            [staleMovie] = 9.9m,
            [lowScore] = 6.9m,
            [watched] = 9.9m,
            [onProvider] = 8.0m,
        });

        await RunDigestAsync(accountId, new FakePushSender());
        var items = await ReadDigestItemsAsync(accountId);

        Assert.Equal(
            new[] { freshShow, freshMovie, freshMovie2, onProvider, freshMovie3 },
            items.Select(i => i.TitleId).ToArray());
        Assert.Equal(DigestReasons.NewRelease, items[0].Reason);
        Assert.Equal(DigestReasons.NewRelease, items[1].Reason);
        Assert.Equal(DigestReasons.NewOnProvider, items[3].Reason);
        Assert.Equal(arrivedOn, items[3].Provider);
        Assert.Null(items[0].Provider);
        Assert.Equal(9.0m, items[0].PredictedRating);
        // An honest digest says nothing about degradation.
        Assert.All(items, i => Assert.Null(i.DegradedReason));

        // A re-run inside the same ISO week refreshes the one row rather than adding a second.
        await RunDigestAsync(accountId, new FakePushSender());
        await using var db = fixture.CreateContext(Pinned(accountId));
        Assert.Equal(1, await db.AccountDigests.CountAsync(d => d.AccountId == accountId));
    }

    [Fact]
    public async Task Last_weeks_provider_items_stay_suppressed_and_push_lifecycle_runs()
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var accountId = await SeedAccountAsync();

        // Both titles are old catalogue that arrived on a flatrate provider this week; only the one
        // absent from LAST week's digest may ship — an arrival is announced once.
        var shownLastWeek = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddYears(-3));
        var genuinelyNew = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddYears(-3));
        await SeedAvailabilityAsync(shownLastWeek, "US", now.AddDays(-2), firstSeenAt: now.AddDays(-2));
        var arrivedOn = await SeedAvailabilityAsync(genuinelyNew, "US", now.AddDays(-2), firstSeenAt: now.AddDays(-2));
        await SeedActiveArtifactAndScoresAsync(accountId, new()
        {
            [shownLastWeek] = 9.5m,
            [genuinelyNew] = 8.0m,
        });
        await SeedPreviousWeekDigestAsync(accountId, shownLastWeek, now);

        var healthy = await SeedSubscriptionAsync(accountId, "https://push.example/healthy");
        var dead = await SeedSubscriptionAsync(accountId, "https://push.example/dead");
        var flaky = await SeedSubscriptionAsync(accountId, "https://push.example/flaky", failCount: 4);
        var sender = new FakePushSender
        {
            Outcomes =
            {
                ["https://push.example/dead"] = WebPushOutcome.Gone,
                ["https://push.example/flaky"] = WebPushOutcome.Failure,
            },
        };

        await RunDigestAsync(accountId, sender);

        var items = await ReadDigestItemsAsync(accountId);
        Assert.Equal(new[] { genuinelyNew }, items.Select(i => i.TitleId).ToArray());

        // One send per subscription, and the payload NAMES the single fresh pick, quotes its
        // number, says why it is there, and deep-links to it — no "1 new high-for-you titles"
        // pointing at the home page.
        Assert.Equal(3, sender.Sent.Count);
        var push = await ReadPushAsync(sender.Sent[0].PayloadJson, genuinelyNew);
        Assert.Equal($"{push.Name} \u2014 8.0 for you", push.Title);
        Assert.Equal($"Streaming on {arrivedOn}", push.Body);
        Assert.Equal($"/title/movie/{push.TmdbId}", push.Url);

        await using var db = fixture.CreateContext(Pinned(accountId));
        var survivor = await db.PushSubscriptions.SingleAsync(s => s.AccountId == accountId);
        Assert.Equal(healthy, survivor.Id);
        Assert.NotNull(survivor.LastSuccessAt);
        Assert.Equal(0, survivor.FailCount);

        // Gone deletes immediately; the flaky one just crossed the 5-failure prune line.
        Assert.False(await db.PushSubscriptions.AnyAsync(s => s.Id == dead));
        Assert.False(await db.PushSubscriptions.AnyAsync(s => s.Id == flaky));
    }

    [Fact]
    public async Task A_thin_week_relaxes_the_floor_and_records_why()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The live shape of the bug: a hard 7.0 sitting above the catalogue's p90 prediction. Two
        // arrivals clear it, three sit in the 6s — which is a thin week, not an empty one.
        var now = DateTime.UtcNow;
        var accountId = await SeedAccountAsync();
        var strong = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-2));
        var alsoStrong = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-3));
        var nearMiss = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-1));
        var lower = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-4));
        var tooLow = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-5));
        await SeedActiveArtifactAndScoresAsync(accountId, new()
        {
            [strong] = 7.4m,
            [alsoStrong] = 7.1m,
            [nearMiss] = 6.9m,
            [lower] = 6.2m,
            [tooLow] = 5.4m, // below the RELAXED floor: relaxing is not abandoning the floor
        });
        await SeedSubscriptionAsync(accountId, "https://push.example/thin");

        var sender = new FakePushSender();
        await RunDigestAsync(accountId, sender);
        var items = await ReadDigestItemsAsync(accountId);

        Assert.Equal(new[] { strong, alsoStrong, nearMiss, lower }, items.Select(i => i.TitleId).ToArray());
        Assert.All(items, i => Assert.NotNull(i.DegradedReason));
        Assert.Contains("7", items[0].DegradedReason!, StringComparison.Ordinal);
        Assert.Contains("6", items[0].DegradedReason!, StringComparison.Ordinal);
        Assert.Single(sender.Sent);

        // A relaxed digest's push says so, rather than selling the pick as if it cleared the bar.
        var push = await ReadPushAsync(sender.Sent[0].PayloadJson, strong);
        Assert.Equal($"{push.Name} \u2014 7.4 for you, plus 3 more this week", push.Title);
        Assert.Equal(items[0].DegradedReason, push.Body);
        Assert.Equal($"/title/movie/{push.TmdbId}", push.Url);
    }

    [Fact]
    public async Task Relaxing_the_floor_never_loosens_the_window_or_the_footprint_rule()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The floor is the model's opinion and is negotiable; the content rules are the user's
        // statement and are not. A relaxed digest must not smuggle in an old or an already-seen
        // title just because the bar came down.
        var now = DateTime.UtcNow;
        var accountId = await SeedAccountAsync();
        var fresh = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-2));
        var stale = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-40));
        var seen = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-1));
        await SeedWatchedAsync(accountId, seen);
        await SeedActiveArtifactAndScoresAsync(accountId, new()
        {
            [fresh] = 6.5m,
            [stale] = 9.9m,
            [seen] = 9.9m,
        });

        await RunDigestAsync(accountId, new FakePushSender());
        var items = await ReadDigestItemsAsync(accountId);

        Assert.Equal(new[] { fresh }, items.Select(i => i.TitleId).ToArray());
    }

    [Fact]
    public async Task A_week_with_nothing_above_the_relaxed_floor_sends_no_push_at_all()
    {
        if (!fixture.Available)
        {
            return;
        }

        // "0 new high-for-you titles" is what the hard floor delivered every week; it teaches the
        // user to ignore the notification. An empty digest row is still written — the rail reads it
        // and hides itself — but nothing is pushed.
        var now = DateTime.UtcNow;
        var accountId = await SeedAccountAsync();
        var weak = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-2));
        await SeedActiveArtifactAndScoresAsync(accountId, new() { [weak] = 5.2m });
        await SeedSubscriptionAsync(accountId, "https://push.example/quiet");

        var sender = new FakePushSender();
        await RunDigestAsync(accountId, sender);

        Assert.Empty(await ReadDigestItemsAsync(accountId));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Only_an_arrival_counts_as_new_on_provider_never_a_cache_refresh()
    {
        if (!fixture.Available)
        {
            return;
        }

        // IR2S-02, the live shape: the availability job had just re-fetched NCIS (2003), Southland
        // (2009) and Reign Over Me (2007), and the digest sold all of them as "Just landed on your
        // services". Every title here is old catalogue scored well above the floor, so the ONLY
        // thing deciding membership is whether the provider set changed inside the window.
        //   refreshed      — flatrate fetched yesterday, never observed arriving       → out
        //   arrivedLongAgo — flatrate arrival observed 20 days ago, re-fetched today  → out
        //   arrivedToRent  — a rental that arrived 2 days ago: not streaming now      → out
        //   otherRegion    — a flatrate arrival 2 days ago, but in GB, not US         → out
        //   arrivedFree    — a free offer that arrived 2 days ago                     → in
        //   arrivedTwice   — arrived on two subscriptions 3 days ago                  → in, the more prominent named
        var now = DateTime.UtcNow;
        var accountId = await SeedAccountAsync();
        var refreshed = await SeedTitleAsync(MediaType.Show, firstAiredAt: now.AddYears(-23));
        var arrivedLongAgo = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddYears(-19));
        var arrivedToRent = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddYears(-15));
        var otherRegion = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddYears(-6));
        var arrivedFree = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddYears(-4));
        var arrivedTwice = await SeedTitleAsync(MediaType.Show, firstAiredAt: now.AddYears(-17));

        await SeedAvailabilityAsync(refreshed, "US", now.AddDays(-1), firstSeenAt: null);
        await SeedAvailabilityAsync(arrivedLongAgo, "US", now.AddHours(-2), firstSeenAt: now.AddDays(-20));
        await SeedAvailabilityAsync(arrivedToRent, "US", now.AddDays(-2), firstSeenAt: now.AddDays(-2), kind: AvailabilityKind.Rent);
        await SeedAvailabilityAsync(otherRegion, "GB", now.AddDays(-2), firstSeenAt: now.AddDays(-2));
        var freeOn = await SeedAvailabilityAsync(arrivedFree, "US", now.AddDays(-2), firstSeenAt: now.AddDays(-2), kind: AvailabilityKind.Free);
        await SeedAvailabilityAsync(arrivedTwice, "US", now.AddDays(-3), firstSeenAt: now.AddDays(-3), displayPriority: 9);
        var prominent = await SeedAvailabilityAsync(arrivedTwice, "US", now.AddDays(-3), firstSeenAt: now.AddDays(-3), displayPriority: 1);

        await SeedActiveArtifactAndScoresAsync(accountId, new()
        {
            [refreshed] = 9.4m,
            [arrivedLongAgo] = 9.3m,
            [arrivedToRent] = 9.2m,
            [otherRegion] = 9.1m,
            [arrivedFree] = 8.6m,
            [arrivedTwice] = 8.4m,
        });

        var sender = new FakePushSender();
        await SeedSubscriptionAsync(accountId, "https://push.example/arrivals");
        await RunDigestAsync(accountId, sender);
        var items = await ReadDigestItemsAsync(accountId);

        Assert.Equal(new[] { arrivedFree, arrivedTwice }, items.Select(i => i.TitleId).ToArray());
        Assert.All(items, i => Assert.Equal(DigestReasons.NewOnProvider, i.Reason));
        Assert.Equal(freeOn, items[0].Provider);
        Assert.Equal(prominent, items[1].Provider);

        // The push names the provider and makes no claim about "your services".
        var push = await ReadPushAsync(Assert.Single(sender.Sent).PayloadJson, arrivedFree);
        Assert.Equal($"Streaming on {freeOn}", push.Body);
    }

    [Fact]
    public async Task Through_the_real_writer_a_refetch_is_not_an_arrival_and_a_new_offer_is()
    {
        if (!fixture.Available)
        {
            return;
        }

        // End to end through the one snapshot writer both refetch paths use, so the test cannot pass
        // on hand-placed FirstSeenAt values that the writer would never produce.
        var now = DateTime.UtcNow;
        var accountId = await SeedAccountAsync();
        var longRunning = await SeedTitleAsync(MediaType.Show, firstAiredAt: now.AddYears(-23));
        var movedToStreaming = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddMonths(-8));
        var tmdbProvider = Random.Shared.Next(100_000, int.MaxValue);
        var providerName = $"Netflix-fixture-{Guid.NewGuid():N}";
        TmdbWatchProvider Offer(AvailabilityKind kind) => new(tmdbProvider, providerName, "/n.png", 1, kind);

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            // First look, then the availability job's refresh a day later: the same flatrate offer.
            await AvailabilitySnapshot.ReplaceAsync(db, longRunning, "US", [Offer(AvailabilityKind.Flatrate)], now.AddDays(-1), CancellationToken.None);
            await AvailabilitySnapshot.ReplaceAsync(db, longRunning, "US", [Offer(AvailabilityKind.Flatrate)], now, CancellationToken.None);

            // Rent-only on first look; the next fetch finds it on the subscription too.
            await AvailabilitySnapshot.ReplaceAsync(db, movedToStreaming, "US", [Offer(AvailabilityKind.Rent)], now.AddDays(-2), CancellationToken.None);
            await AvailabilitySnapshot.ReplaceAsync(db, movedToStreaming, "US", [Offer(AvailabilityKind.Rent), Offer(AvailabilityKind.Flatrate)], now.AddDays(-1), CancellationToken.None);
            // And a further refresh must not restamp the arrival to "now".
            await AvailabilitySnapshot.ReplaceAsync(db, movedToStreaming, "US", [Offer(AvailabilityKind.Rent), Offer(AvailabilityKind.Flatrate)], now, CancellationToken.None);
        }

        await using (var verify = fixture.CreateContext(new AccountContext()))
        {
            var rows = await verify.TitleAvailabilities
                .Where(a => a.TitleId == longRunning || a.TitleId == movedToStreaming)
                .ToListAsync();
            Assert.Null(rows.Single(r => r.TitleId == longRunning).FirstSeenAt);
            Assert.Null(rows.Single(r => r.TitleId == movedToStreaming && r.Kind == AvailabilityKind.Rent).FirstSeenAt);
            var arrival = rows.Single(r => r.TitleId == movedToStreaming && r.Kind == AvailabilityKind.Flatrate);
            Assert.NotNull(arrival.FirstSeenAt);
            Assert.True(arrival.FirstSeenAt < now.AddHours(-12), "a refetch restamped the arrival");
            Assert.All(rows, r => Assert.True(r.FetchedAt > now.AddMinutes(-1)));
        }

        await SeedActiveArtifactAndScoresAsync(accountId, new()
        {
            [longRunning] = 9.5m,
            [movedToStreaming] = 8.0m,
        });
        await RunDigestAsync(accountId, new FakePushSender());
        var item = Assert.Single(await ReadDigestItemsAsync(accountId));

        Assert.Equal(movedToStreaming, item.TitleId);
        Assert.Equal(DigestReasons.NewOnProvider, item.Reason);
        Assert.Equal(providerName, item.Provider);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// XM-05: the digest is a suggestion surface, so the account's own For-you bar binds it — at
    /// build time, under the strict floor and under the relaxed one alike. A bar of 7.5 makes a thin
    /// week's 6.0 relaxation stop at 7.5, and the copy names the bar it actually used.
    /// </summary>
    [Fact]
    public async Task The_accounts_for_you_bar_binds_the_build_even_when_the_week_is_thin()
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var accountId = await SeedAccountAsync();
        var above = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-2));
        var between = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-3));
        var relaxedOnly = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-1));
        await SeedActiveArtifactAndScoresAsync(accountId, new()
        {
            [above] = 7.8m,
            [between] = 7.2m,       // clears the digest's own 7.0, not the account's 7.5
            [relaxedOnly] = 6.4m,   // clears the relaxed 6.0, not the account's 7.5
        });
        await SeedBarAsync(accountId, "7.5");

        await RunDigestAsync(accountId, new FakePushSender());
        var items = await ReadDigestItemsAsync(accountId);

        Assert.Equal(new[] { above }, items.Select(i => i.TitleId).ToArray());
        // Nothing was relaxed below the bar, so nothing claims it was.
        Assert.All(items, i => Assert.Null(i.DegradedReason));
    }

    /// <summary>
    /// A bar between the two floors: the thin-week relaxation stops at the account's bar, not at 6.0,
    /// and the degraded copy quotes the floor the digest really used.
    /// </summary>
    [Fact]
    public async Task A_bar_between_the_floors_is_where_the_relaxation_stops()
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var accountId = await SeedAccountAsync();
        var strong = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-2));
        var atBar = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-3));
        var underBar = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-1));
        await SeedActiveArtifactAndScoresAsync(accountId, new()
        {
            [strong] = 7.3m,
            [atBar] = 6.5m,
            [underBar] = 6.3m,
        });
        await SeedBarAsync(accountId, "6.5");

        await RunDigestAsync(accountId, new FakePushSender());
        var items = await ReadDigestItemsAsync(accountId);

        Assert.Equal(new[] { strong, atBar }, items.Select(i => i.TitleId).ToArray());
        Assert.All(items, i => Assert.Contains("relaxed to 6.5", i.DegradedReason, StringComparison.Ordinal));
    }

    /// <summary>
    /// WS-5's media-mix floor reaches the digest: a 90%-movies account whose week is ten strong shows
    /// and four good films gets the films seated (the lowest shows give way), not ten shows. A floor
    /// on representation, never a quota: no film under the digest's floor is pulled in to hit it.
    /// </summary>
    [Fact]
    public async Task The_digest_carries_the_accounts_media_mix()
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var accountId = await SeedAccountAsync();
        await SeedRatedMixAsync(accountId, movies: 9, shows: 1);

        var scores = new Dictionary<Guid, decimal>();
        var shows = new List<Guid>();
        var movies = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            var show = await SeedTitleAsync(MediaType.Show, firstAiredAt: now.AddDays(-2));
            scores[show] = 8.9m - (i * 0.1m);
            shows.Add(show);
        }

        for (var i = 0; i < 4; i++)
        {
            var movie = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-2));
            scores[movie] = 7.4m - (i * 0.1m);
            movies.Add(movie);
        }

        var underFloor = await SeedTitleAsync(MediaType.Movie, releasedAt: now.AddDays(-2));
        scores[underFloor] = 5.5m;
        await SeedActiveArtifactAndScoresAsync(accountId, scores);

        await RunDigestAsync(accountId, new FakePushSender());
        var items = await ReadDigestItemsAsync(accountId);

        Assert.Equal(10, items.Count);
        Assert.Equal(movies.ToHashSet(), items.Select(i => i.TitleId).Where(movies.Contains).ToHashSet());
        Assert.Equal(shows.Take(6), items.Select(i => i.TitleId).Where(shows.Contains));
        Assert.DoesNotContain(underFloor, items.Select(i => i.TitleId));
        // Still a ranking: best first.
        Assert.Equal(items.OrderByDescending(i => i.PredictedRating).Select(i => i.TitleId), items.Select(i => i.TitleId));
    }

    private async Task SeedBarAsync(Guid accountId, string value)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ContentFilters.Add(new Domain.Filters.ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = Domain.Filters.FilterKind.MinPredictedRating,
            Value = value,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Rated titles of each medium, outside the digest window so they are never candidates.</summary>
    private async Task SeedRatedMixAsync(Guid accountId, int movies, int shows)
    {
        var rated = new List<(Guid Id, MediaType Media)>();
        for (var i = 0; i < movies; i++)
        {
            rated.Add((await SeedTitleAsync(MediaType.Movie, releasedAt: DateTime.UtcNow.AddYears(-3)), MediaType.Movie));
        }

        for (var i = 0; i < shows; i++)
        {
            rated.Add((await SeedTitleAsync(MediaType.Show, firstAiredAt: DateTime.UtcNow.AddYears(-3)), MediaType.Show));
        }

        await using var db = fixture.CreateContext(new AccountContext());
        foreach (var (id, media) in rated)
        {
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = id,
                SubjectType = media == MediaType.Movie ? RatingSubjectType.Movie : RatingSubjectType.Show,
                Rating = 8,
                RatedAt = DateTime.UtcNow.AddDays(-30),
                FirstRatedAt = DateTime.UtcNow.AddDays(-30),
                SyncedAt = DateTime.UtcNow.AddDays(-30),
            });
        }

        await db.SaveChangesAsync();
    }

    private sealed class FakePushSender : IWebPushSender
    {
        public Dictionary<string, WebPushOutcome> Outcomes { get; } = [];

        public List<(string Endpoint, string PayloadJson)> Sent { get; } = [];

        public Task<WebPushResult> SendAsync(
            string endpoint, string p256dh, string auth, string payloadJson, CancellationToken ct)
        {
            Sent.Add((endpoint, payloadJson));
            var outcome = Outcomes.GetValueOrDefault(endpoint, WebPushOutcome.Success);
            return Task.FromResult(new WebPushResult(outcome, outcome == WebPushOutcome.Failure ? "boom" : null));
        }
    }

    /// <summary>The sent payload, paired with the top title's own row so the copy can be asserted
    /// against real values rather than re-derived from the fixture's random name.</summary>
    private async Task<(string Title, string Body, string Url, string Name, long TmdbId)> ReadPushAsync(
        string payloadJson, Guid topTitleId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = await db.Titles.SingleAsync(t => t.Id == topTitleId);
        using var doc = JsonDocument.Parse(payloadJson);
        return (
            doc.RootElement.GetProperty("title").GetString()!,
            doc.RootElement.GetProperty("body").GetString()!,
            doc.RootElement.GetProperty("url").GetString()!,
            title.Name,
            title.TmdbId!.Value);
    }

    private static AccountContext Pinned(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    /// <summary>Runs the handler exactly as the job runner does: context pinned to the account.</summary>
    private async Task RunDigestAsync(Guid accountId, FakePushSender sender)
    {
        await using var db = fixture.CreateContext(Pinned(accountId));
        var featureBuilder = new FeatureVectorBuilder(db);
        var engine = new FastTreeModelEngine();
        var scorer = new OnDemandScorer(
            db,
            featureBuilder,
            engine,
            new OnlineResidualModel(
                db, featureBuilder, engine,
                new MemoryCache(new MemoryCacheOptions()), NullLogger<OnlineResidualModel>.Instance),
            new DislikeSignal(db, new MemoryCache(new MemoryCacheOptions())),
            new RatingCalibrationService(db, new MemoryCache(new MemoryCacheOptions())));
        var handler = new BuildDigestJobHandler(
            db, new EligibilityQueryBuilder(db), scorer, featureBuilder, sender, NullLogger<BuildDigestJobHandler>.Instance);

        await handler.ExecuteAsync(
            new SyncJob { Id = Guid.NewGuid(), AccountId = accountId, Kind = JobKind.BuildDigest },
            CancellationToken.None);
    }

    private async Task<List<DigestItem>> ReadDigestItemsAsync(Guid accountId)
    {
        // Computed client-side: DigestWeek.MondayOf has no SQL translation.
        var weekStart = DigestWeek.MondayOf(DateTime.UtcNow);
        await using var db = fixture.CreateContext(Pinned(accountId));
        var digest = await db.AccountDigests.SingleAsync(
            d => d.AccountId == accountId && d.WeekStart == weekStart);
        return JsonSerializer.Deserialize<List<DigestItem>>(digest.ItemsJson) ?? [];
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"digest-{Guid.NewGuid():N}",
            DisplayName = "Digest Fixture",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(
        MediaType mediaType, DateTime? releasedAt = null, DateTime? firstAiredAt = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = mediaType,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"digest-fixture {Guid.NewGuid():N}",
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = ["drama"],
            ReleasedAt = releasedAt,
            FirstAiredAt = firstAiredAt,
            // Bottom of the popularity order so these fixtures never displace another
            // integration test's top-N assertions on the shared catalogue.
            TmdbPopularity = 0m,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task SeedWatchedAsync(Guid accountId, Guid titleId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.WatchedTitles.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Plays = 1,
            IsFullyWatched = true,
            FirstSyncedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>One offer on a provider of its own; returns that provider's (unique) name.</summary>
    private async Task<string> SeedAvailabilityAsync(
        Guid titleId, string region, DateTime fetchedAt, DateTime? firstSeenAt,
        AvailabilityKind kind = AvailabilityKind.Flatrate, int displayPriority = 5)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var provider = new StreamingProvider
        {
            Id = Guid.NewGuid(),
            TmdbProviderId = Random.Shared.Next(100_000, int.MaxValue),
            Name = $"provider-fixture-{Guid.NewGuid():N}",
            DisplayPriority = displayPriority,
        };
        db.StreamingProviders.Add(provider);
        db.TitleAvailabilities.Add(new TitleAvailability
        {
            Id = Guid.NewGuid(),
            TitleId = titleId,
            Region = region,
            ProviderId = provider.Id,
            Kind = kind,
            FetchedAt = fetchedAt,
            FirstSeenAt = firstSeenAt,
        });
        await db.SaveChangesAsync();
        return provider.Name;
    }

    /// <summary>
    /// One genuinely trained Active artifact plus a persisted TitleScore per entry. The model is
    /// REAL (small FastTree over the live schema, labels pinned near 5.0) because the catalogue is
    /// shared across the collection: other suites' fresh titles become unscored candidates for
    /// this account, the handler on-demand-scores them, and their ~5.0 predictions fall under the
    /// 7.0 floor — so the assertions stay exact without owning the whole catalogue.
    /// </summary>
    private async Task SeedActiveArtifactAndScoresAsync(Guid accountId, Dictionary<Guid, decimal> scores)
    {
        var rng = new Random(11);
        var rows = new List<float[]>();
        var labels = new List<float>();
        for (var i = 0; i < 200; i++)
        {
            var v = new float[FeatureSchema.VectorLength];
            for (var j = 0; j < v.Length; j++)
            {
                v[j] = (float)rng.NextDouble();
            }

            rows.Add(v);
            labels.Add(Math.Clamp(5f + (v[0] - 0.5f), 1f, 10f));
        }

        var trained = new FastTreeModelEngine().Train(
            new TrainingMatrix(rows.ToArray(), labels.ToArray(), FeatureSchema.AllNames()),
            TrainJobHandler.DefaultHyperparams);

        await using var db = fixture.CreateContext(new AccountContext());
        var artifact = new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = trained.ArtifactBytes,
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
            // The schema-compat shadow column projects 'version' out of this jsonb; without a
            // current version the artifact is invisible to every Active-artifact read.
            FeatureSchemaJson = JsonSerializer.Serialize(new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
        };
        db.Add(artifact);
        foreach (var (titleId, predicted) in scores)
        {
            db.TitleScores.Add(new TitleScore
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                ModelArtifactId = artifact.Id,
                PredictedRating = predicted,
                ScoredAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedPreviousWeekDigestAsync(Guid accountId, Guid titleId, DateTime now)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.AccountDigests.Add(new AccountDigest
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            WeekStart = DigestWeek.MondayOf(now).AddDays(-7),
            GeneratedAt = now.AddDays(-7),
            ItemsJson = JsonSerializer.Serialize(
                new List<DigestItem> { new(titleId, 9.5m, DigestReasons.NewOnProvider) }),
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedSubscriptionAsync(Guid accountId, string endpoint, int failCount = 0)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var subscription = new PushSubscription
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Endpoint = endpoint,
            P256dh = "p256dh-fixture",
            Auth = "auth-fixture",
            FailCount = failCount,
            CreatedAt = DateTime.UtcNow,
        };
        db.PushSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
        return subscription.Id;
    }
}

/// <summary>
/// Task 5: the digest's exposure writer records a TYPED why-source per card through
/// <see cref="Application.Ml.ClosedLoop.ExposureLog"/>. The line is the digest's own reason copy
/// unless a lens hook was cached for this reader, which is model text served from cache — and
/// logging that as "Digest" booked model text as deterministic copy.
/// </summary>
[Collection("postgres")]
public class DigestExposureEndpointTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    private const string Hook = "A heist that plays fair with you.";

    /// <summary>
    /// One predicate decides both the rendered line and its logged source, so they cannot disagree:
    /// an empty or whitespace hook is no hook — the card shows the reason copy and logs Digest.
    /// </summary>
    [Theory]
    [InlineData(null, "Reason copy", "Digest")]
    [InlineData("", "Reason copy", "Digest")]
    [InlineData("   ", "Reason copy", "Digest")]
    [InlineData("A heist that plays fair with you.", "A heist that plays fair with you.", "Cache")]
    public void The_why_source_follows_the_line_actually_rendered(string? hook, string expectedLine, string expectedSource)
    {
        var (line, source) = Api.Endpoints.DigestEndpoints.DigestWhyLine(hook, "Reason copy");
        Assert.Equal(expectedLine, line);
        Assert.Equal(expectedSource, source.Name);
    }

    /// <summary>
    /// End to end through <c>GET /api/digest/current</c>, both branches in one digest: the card with
    /// a cached lens hook renders the hook and is logged <c>Cache</c>; the card without one renders
    /// the reason copy — an arrival naming the provider its strip shows — and is logged
    /// <c>Digest</c>. Arrivals the card cannot back (IR2S-02) are neither rendered nor logged.
    /// </summary>
    [Fact]
    public async Task Reading_the_digest_logs_each_card_with_the_source_of_its_rendered_line()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"digest-exposure-{Guid.NewGuid():N}");
        var now = DateTime.UtcNow;
        var titleIds = new List<Guid>();
        var providerName = $"Netflix-fixture-{Guid.NewGuid():N}";
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            // 0: a release with a cached hook. 1: an arrival on a provider the card shows.
            // 2: a pre-IR2S-02 arrival — no provider recorded, picked off a cache refresh — that
            //    is streaming right now; it must still not render. 3: an arrival whose offer has
            //    lapsed, so the card would name nothing.
            for (var i = 0; i < 4; i++)
            {
                var title = new Title
                {
                    Id = Guid.NewGuid(),
                    MediaType = MediaType.Movie,
                    TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
                    TraktId = Random.Shared.NextInt64(1, long.MaxValue),
                    TraktSlug = $"digest-exposure-{Guid.NewGuid():N}",
                    Name = $"Digest Exposure {i} {Guid.NewGuid():N}",
                    Genres = ["drama"],
                    Language = "en",
                    CreatedAt = now,
                };
                db.Add(title);
                titleIds.Add(title.Id);
            }

            db.ModelArtifacts.Add(new ModelArtifact
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Version = 1,
                Algo = "FastTree",
                TrainingRunId = Guid.NewGuid(),
                Status = ArtifactStatus.Active,
                TrainedAt = now.AddDays(-1),
                FeatureSchemaJson = JsonSerializer.Serialize(
                    new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
            });
            db.AccountDigests.Add(new AccountDigest
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                WeekStart = DigestWeek.MondayOf(now),
                GeneratedAt = now,
                ItemsJson = JsonSerializer.Serialize(new[]
                {
                    new DigestItem(titleIds[0], 8.1m, DigestReasons.NewRelease),
                    new DigestItem(titleIds[1], 7.6m, DigestReasons.NewOnProvider, Provider: providerName),
                    new DigestItem(titleIds[2], 7.5m, DigestReasons.NewOnProvider),
                    new DigestItem(titleIds[3], 7.4m, DigestReasons.NewOnProvider, Provider: providerName),
                }),
            });
            var provider = new StreamingProvider
            {
                Id = Guid.NewGuid(),
                TmdbProviderId = Random.Shared.Next(100_000, int.MaxValue),
                Name = providerName,
                DisplayPriority = 1,
            };
            db.StreamingProviders.Add(provider);
            foreach (var (titleId, fetchedAt) in new[] { (titleIds[1], now.AddHours(-3)), (titleIds[2], now.AddHours(-3)), (titleIds[3], now.AddDays(-9)) })
            {
                db.TitleAvailabilities.Add(new TitleAvailability
                {
                    Id = Guid.NewGuid(),
                    TitleId = titleId,
                    Region = "ZA", // ReelApiFactory seeds every account in ZA
                    ProviderId = provider.Id,
                    Kind = AvailabilityKind.Flatrate,
                    FetchedAt = fetchedAt,
                    FirstSeenAt = titleId == titleIds[2] ? null : fetchedAt.AddDays(-1),
                });
            }

            // A cached lens note for the first title only — any profile hash serves, because the
            // digest reads a stale note as readily as a fresh one.
            db.TitleLensCopies.Add(new TitleLensCopy
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleIds[0],
                ProfileHash = "digest-exposure",
                PromptVersion = 1,
                LensJson = JsonSerializer.Serialize(new LensNote(
                    "A cold, procedural piece that keeps its pressure low and constant.",
                    Hook,
                    null,
                    [],
                    "tense",
                    "tonight-alone")),
                ModelId = "stub/deterministic-v1",
                Status = LensStatus.Done,
                GeneratedAt = now,
            });
            await db.SaveChangesAsync();
        }

        using var response = await client.GetAsync("/api/digest/current");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cards = body.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { titleIds[0], titleIds[1] }, cards.Select(c => c.GetProperty("titleId").GetGuid()).ToArray());
        Assert.Equal($"Streaming on {providerName}", cards[1].GetProperty("whyThis").GetString());
        Assert.DoesNotContain(cards, c => c.GetProperty("whyThis").GetString()!.Contains("your services", StringComparison.OrdinalIgnoreCase));

        // Only what was rendered is logged: the two unbacked arrivals were never shown.
        await using var verify = fixture.CreateContext(new AccountContext());
        var exposures = await verify.ExposureEvents
            .Where(e => e.AccountId == accountId && e.Surface == ExposureSurface.Digest)
            .ToDictionaryAsync(e => e.TitleId);
        Assert.Equal(new[] { titleIds[0], titleIds[1] }.OrderBy(id => id), exposures.Keys.OrderBy(id => id));
        Assert.Equal("Cache", exposures[titleIds[0]].WhySource);
        Assert.Equal(Hook, exposures[titleIds[0]].WhySentence);
        Assert.Equal("Digest", exposures[titleIds[1]].WhySource);
        Assert.Equal($"Streaming on {providerName}", exposures[titleIds[1]].WhySentence);
    }

    /// <summary>
    /// XM-04: the digest is frozen on Friday but read all week, so it is re-checked on every read. A
    /// card watched since the build, a card now in an excluded genre, and a card under a bar raised
    /// since the build are neither rendered nor logged; the clean card still is.
    /// </summary>
    [Fact]
    public async Task Reading_the_digest_drops_what_was_ruled_out_or_seen_since_the_build()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"digest-late-{Guid.NewGuid():N}");
        var now = DateTime.UtcNow;
        var ids = new List<Guid>();
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            string[][] genres = [["drama"], ["drama"], ["horror"], ["drama"]];
            for (var i = 0; i < 4; i++)
            {
                var title = new Title
                {
                    Id = Guid.NewGuid(),
                    MediaType = MediaType.Movie,
                    TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
                    TraktId = Random.Shared.NextInt64(1, long.MaxValue),
                    TraktSlug = $"digest-late-{Guid.NewGuid():N}",
                    Name = $"Digest Late {i} {Guid.NewGuid():N}",
                    Genres = genres[i],
                    Language = "en",
                    CreatedAt = now,
                };
                db.Add(title);
                ids.Add(title.Id);
            }

            db.AccountDigests.Add(new AccountDigest
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                WeekStart = DigestWeek.MondayOf(now),
                GeneratedAt = now.AddDays(-1),
                ItemsJson = JsonSerializer.Serialize(new[]
                {
                    new DigestItem(ids[0], 8.0m, DigestReasons.NewRelease),
                    new DigestItem(ids[1], 7.9m, DigestReasons.NewRelease),
                    new DigestItem(ids[2], 7.8m, DigestReasons.NewRelease),
                    new DigestItem(ids[3], 7.44m, DigestReasons.NewRelease),
                }),
            });

            // After the build: watched one, excluded a genre, raised the bar to 7.5 (7.44 shows as 7.4).
            db.WatchedTitles.Add(new WatchedTitle
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = ids[1],
                Plays = 1,
                IsFullyWatched = true,
                FirstSyncedAt = now,
                UpdatedAt = now,
            });
            db.ContentFilters.Add(new Domain.Filters.ContentFilter
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = Domain.Filters.FilterKind.ExcludeGenre,
                Value = "horror",
                CreatedAt = now,
            });
            db.ContentFilters.Add(new Domain.Filters.ContentFilter
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Kind = Domain.Filters.FilterKind.MinPredictedRating,
                Value = "7.5",
                CreatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        using var response = await client.GetAsync("/api/digest/current");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cards = body.RootElement.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("titleId").GetGuid()).ToArray();
        Assert.Equal(new[] { ids[0] }, cards);
    }
}
