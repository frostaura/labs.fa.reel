using System.Reflection;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using FrostAura.Reel.Domain.Providers;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// IR2S-02 at the source: the RefreshAvailability job is what re-fetched NCIS the morning the digest
/// called it "just landed". Run the real job three times over one title and pin what each refetch
/// writes: a first look stamps nothing, a new subscription offer is stamped once, and a later
/// refresh of the same offer leaves that stamp exactly where it was.
/// </summary>
[Collection("postgres")]
public class RefreshAvailabilityArrivalTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_job_stamps_an_arrival_once_and_never_a_refresh()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titleId) = await SeedFeedTitleAsync();
        var tmdb = WatchProviderScript.Create();
        var script = (WatchProviderScript)(object)tmdb;
        var tmdbProvider = Random.Shared.Next(100_000, int.MaxValue);
        var name = $"Hulu-fixture-{Guid.NewGuid():N}";
        TmdbWatchProvider Offer(AvailabilityKind kind) => new(tmdbProvider, name, "/h.png", 2, kind);

        // 1. First look: rent only. Nothing is new on a first look.
        script.Next = [Offer(AvailabilityKind.Rent)];
        await RunAsync(accountId, tmdb);
        var first = await RowsAsync(titleId);
        Assert.Null(Assert.Single(first).FirstSeenAt);

        // 2. A day later the title is on the subscription as well: that offer arrived.
        await AgeAsync(titleId, TimeSpan.FromHours(25));
        script.Next = [Offer(AvailabilityKind.Rent), Offer(AvailabilityKind.Flatrate)];
        var beforeSecond = DateTime.UtcNow;
        await RunAsync(accountId, tmdb);
        var second = await RowsAsync(titleId);
        Assert.Null(second.Single(r => r.Kind == AvailabilityKind.Rent).FirstSeenAt);
        var arrivedAt = second.Single(r => r.Kind == AvailabilityKind.Flatrate).FirstSeenAt;
        Assert.NotNull(arrivedAt);
        Assert.True(arrivedAt >= beforeSecond.AddSeconds(-1));

        // 3. The cache refresh that used to make everything "just landed": same offers, new
        //    FetchedAt, and the arrival keeps its original moment.
        await AgeAsync(titleId, TimeSpan.FromHours(25));
        await RunAsync(accountId, tmdb);
        var third = await RowsAsync(titleId);
        Assert.Equal(2, third.Count);
        Assert.All(third, r => Assert.True(r.FetchedAt > DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(arrivedAt!.Value.AddHours(-25), third.Single(r => r.Kind == AvailabilityKind.Flatrate).FirstSeenAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Null(third.Single(r => r.Kind == AvailabilityKind.Rent).FirstSeenAt);
    }

    private async Task RunAsync(Guid accountId, ITmdbClient tmdb)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        await using var db = fixture.CreateContext(context);
        await new RefreshAvailabilityJobHandler(db, tmdb, NullLogger<RefreshAvailabilityJobHandler>.Instance)
            .ExecuteAsync(new SyncJob { Id = Guid.NewGuid(), AccountId = accountId, Kind = JobKind.RefreshAvailability }, CancellationToken.None);
    }

    private async Task<List<TitleAvailability>> RowsAsync(Guid titleId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await db.TitleAvailabilities.AsNoTracking().Where(a => a.TitleId == titleId && a.Region == "US").ToListAsync();
    }

    /// <summary>
    /// Moves the title's snapshot back in time, FirstSeenAt with it, so the job's 24-hour TTL lets
    /// the next run refetch — the shape of a real day passing.
    /// </summary>
    private async Task AgeAsync(Guid titleId, TimeSpan by)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var rows = await db.TitleAvailabilities.Where(a => a.TitleId == titleId).ToListAsync();
        foreach (var row in rows)
        {
            row.FetchedAt -= by;
            row.FirstSeenAt -= by;
        }

        await db.SaveChangesAsync();
    }

    private async Task<(Guid AccountId, Guid TitleId)> SeedFeedTitleAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"availability-{Guid.NewGuid():N}",
            DisplayName = "Availability Fixture",
            Region = "US",
            CreatedAt = now,
            LastSeenAt = now,
            PipelineStageChangedAt = now,
        };
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Show,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = $"availability-{Guid.NewGuid():N}",
            Name = $"Availability Fixture {Guid.NewGuid():N}",
            Genres = ["crime"],
            FirstAiredAt = now.AddYears(-23),
            CreatedAt = now,
        };
        var snapshot = new FeedSnapshot
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            ModelArtifactId = Guid.NewGuid(),
            GeneratedAt = now,
            Status = SnapshotStatus.Active,
        };
        db.Add(account);
        db.Add(title);
        db.FeedSnapshots.Add(snapshot);
        db.FeedItems.Add(new FeedItem
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            FeedSnapshotId = snapshot.Id,
            Row = FeedRowKind.Hero,
            Rank = 0,
            TitleId = title.Id,
            PredictedRating = 8.0m,
            WhyThisSentence = "fixture",
        });
        await db.SaveChangesAsync();
        return (account.Id, title.Id);
    }

    /// <summary>
    /// An <see cref="ITmdbClient"/> that answers watch providers from a script and refuses
    /// everything else, so the test fails loudly if the job starts calling TMDB for anything more.
    /// </summary>
    public class WatchProviderScript : DispatchProxy
    {
        public IReadOnlyList<TmdbWatchProvider> Next { get; set; } = [];

        public static ITmdbClient Create() => Create<ITmdbClient, WatchProviderScript>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(ITmdbClient.GetWatchProvidersAsync)
                ? Task.FromResult(Next)
                : throw new NotSupportedException($"RefreshAvailability called TMDB {targetMethod?.Name}.");
    }
}
