using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The exposure log's idempotency key, made Ask-Reel-aware — and proved not to have moved for any
/// other surface.
///
/// <para><b>The defect.</b> The unique index was
/// <c>(AccountId, TitleId, Surface, FeedSnapshotId, MinuteBucket) NULLS NOT DISTINCT</c>, and
/// <c>FeedSnapshotId</c> is null on every Ask Reel row by definition — so the key collapsed to
/// (account, title, minute). Two turns a few seconds apart that both recommended a title wrote ONE
/// row, stamped with the FIRST turn's <c>SourceTurnId</c>. A thirteen-turn session measured
/// eighteen picks with no exposure row at all, including one turn that got none of its seven: it
/// had returned the same picks as the turn twenty seconds before it, inside the same minute. Both
/// halves are damage — the denominator of every accuracy number computed from this append-only log
/// was short by whatever the repeat rate happened to be, and the rows that did survive attributed
/// the serve to the wrong turn.</para>
///
/// <para><b>The fix is a key change, not a data change.</b> Nothing is updated and nothing is
/// back-filled; what changes is what a future insert collides with. That is the only remedy an
/// append-only log permits, and it is why the rows already written stay exactly as they are.</para>
/// </summary>
[Collection("postgres")]
public class AskReelExposureTurnKeyTests(PostgresFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 11, 2, 14, 30, DateTimeKind.Utc);

    /// <summary>
    /// The repro, as a test: two turns, one minute, one shared title, two rows — each pointing at
    /// the turn that actually served it.
    /// </summary>
    [Fact]
    public async Task Two_turns_in_one_minute_sharing_a_title_each_write_their_own_row()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titleId, artifactId) = await SeedAsync("turn-key");
        var firstTurn = Guid.NewGuid();
        var secondTurn = Guid.NewGuid();

        await RecordAsync(accountId, artifactId, titleId, Now, firstTurn);
        // Twenty seconds later — the same wall-clock minute, which is the whole point.
        await RecordAsync(accountId, artifactId, titleId, Now.AddSeconds(20), secondTurn);

        await using var verify = fixture.CreateContext(Scoped(accountId));
        var rows = await verify.ExposureEvents
            .Where(e => e.Surface == ExposureSurface.AskReel)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal([firstTurn, secondTurn], rows.Select(r => r.SourceTurnId));
        // One minute bucket, so the old key really would have collapsed these two.
        Assert.Single(rows.Select(r => r.MinuteBucket).Distinct());
    }

    /// <summary>
    /// The probe: one field held still. The SAME turn recorded twice inside one minute is still one
    /// exposure — an idempotent retry of a turn is the user looking at one serve, and widening the
    /// key must not have re-opened the duplicate path the key exists to close.
    /// </summary>
    [Fact]
    public async Task One_turn_recorded_twice_in_a_minute_is_still_one_row()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titleId, artifactId) = await SeedAsync("turn-key-idem");
        var turnId = Guid.NewGuid();

        await RecordAsync(accountId, artifactId, titleId, Now, turnId);
        await RecordAsync(accountId, artifactId, titleId, Now.AddSeconds(11), turnId);

        await using var verify = fixture.CreateContext(Scoped(accountId));
        Assert.Equal(
            1, await verify.ExposureEvents.CountAsync(e => e.Surface == ExposureSurface.AskReel));
    }

    /// <summary>
    /// The regression guard that matters most: a feed surface carries a null <c>SourceTurnId</c>,
    /// the index is NULLS NOT DISTINCT, and those nulls still collide. Three refetches of one page
    /// inside one minute remain one exposure, exactly as before — the change is additive at the tail
    /// of the key and invisible to every surface that has no turn.
    /// </summary>
    [Fact]
    public async Task A_feed_surface_dedupes_exactly_as_it_did_before()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, titleId, artifactId) = await SeedAsync("turn-key-feed");
        var snapshotId = await SeedSnapshotAsync(accountId, artifactId);

        var log = LogFor(accountId, out var db);
        await using (db)
        {
            for (var i = 0; i < 3; i++)
            {
                await log.RecordAsync(
                    accountId, ExposureSurface.FeedRefill, snapshotId, artifactId, Now.AddSeconds(i * 7),
                    [new ExposureCard(titleId, 0, 8.42m)], CancellationToken.None);
            }
        }

        await using var verify = fixture.CreateContext(Scoped(accountId));
        var rows = await verify.ExposureEvents.Where(e => e.Surface == ExposureSurface.FeedRefill).ToListAsync();
        Assert.Single(rows);
        Assert.Null(rows[0].SourceTurnId);
    }

    private async Task RecordAsync(Guid accountId, Guid artifactId, Guid titleId, DateTime at, Guid turnId)
    {
        var log = LogFor(accountId, out var db);
        await using (db)
        {
            await log.RecordAsync(
                accountId, ExposureSurface.AskReel, feedSnapshotId: null, artifactId, at,
                [new ExposureCard(titleId, 0, 7.25m)], CancellationToken.None, turnId);
        }
    }

    private ExposureLog LogFor(Guid accountId, out ReelDbContext db)
    {
        db = fixture.CreateContext(Scoped(accountId));
        return new ExposureLog(db, NullLogger<ExposureLog>.Instance);
    }

    private static AccountContext Scoped(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    private async Task<Guid> SeedSnapshotAsync(Guid accountId, Guid artifactId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var snapshot = new FeedSnapshot
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ModelArtifactId = artifactId,
            GeneratedAt = Now,
            Status = SnapshotStatus.Active,
        };
        db.FeedSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        return snapshot.Id;
    }

    /// <summary>
    /// The artifact is a bare id, not a row: the exposure log carries a soft reference to it and
    /// never joins, so a real <c>ModelArtifact</c> would make the fixture heavier without making it
    /// more truthful.
    /// </summary>
    private async Task<(Guid AccountId, Guid TitleId, Guid ArtifactId)> SeedAsync(string prefix)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"askreel-exposure-{prefix}-{Guid.NewGuid():N}",
            CreatedAt = Now,
            LastSeenAt = Now,
            PipelineStageChangedAt = Now,
        };
        db.Accounts.Add(account);

        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TraktId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = $"{prefix}-{Guid.NewGuid():N}",
            Name = $"{prefix} title",
            Genres = ["thriller"],
            CreatedAt = Now,
        };
        db.Titles.Add(title);

        await db.SaveChangesAsync();
        return (account.Id, title.Id, Guid.NewGuid());
    }
}
