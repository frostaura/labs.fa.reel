using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Sync;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Background;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Infrastructure.Security;
using FrostAura.Reel.Tests.Fakes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// R1-27: a Trakt token this host can no longer decrypt (the key ring that sealed it is gone) used to
/// fail DeltaSync on all three attempts, and the poll re-enqueued it every minute — forever. Now the
/// token store marks the connection Revoked, the sync fails once and is not retried, and the poll
/// stops picking the connection up. The key ring loss is real here: each token is sealed by one
/// ephemeral Data Protection provider and read by another, exactly what a lost key ring does.
/// </summary>
[Collection("postgres")]
public class UnreadableTraktTokenTests(PostgresFixture fixture)
{
    private readonly SecretProtector lostRing = new(new EphemeralDataProtectionProvider());
    private readonly SecretProtector hostRing = new(new EphemeralDataProtectionProvider());

    public static TheoryData<string, int, bool> UnreadableSecrets => new()
    {
        // Which secret is unreadable, days until the access token expires, and whether the access
        // token is sealed by the host's ring. A far expiry reads the access token straight off the
        // row; a near one refreshes, which reads the refresh token — while the access token is still
        // valid, so the proactive-refresh fallback must not swallow it.
        { "access", 60, false },
        { "access", 1, false },
        { "refresh", 1, true },
    };

    [Theory]
    [MemberData(nameof(UnreadableSecrets))]
    public async Task DeltaSync_fails_once_marks_the_link_for_reauth_and_the_poll_stops(string which, int expiresInDays, bool accessReadable)
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var accountId = await SeedAsync(now, expiresInDays, accessReadable, refreshReadable: which != "refresh");

        await using var db = Pinned(accountId);
        var job = new SyncJob { Id = Guid.NewGuid(), AccountId = accountId, Kind = JobKind.DeltaSync, EnqueuedAt = now };
        var handler = new DeltaSyncJobHandler(
            db, new StubTraktClient(), TokenStore(db), null!, null!, null!, null!, null!, new RecordingEventHub(),
            new ConfigurationBuilder().Build(), NullLogger<DeltaSyncJobHandler>.Instance);

        var failure = await Assert.ThrowsAsync<NonRetryableJobException>(() => handler.ExecuteAsync(job, CancellationToken.None));
        Assert.True(JobRunnerService.IsNonRetryable(failure));
        var cause = Assert.IsType<TraktTokenUnreadableException>(failure.InnerException);
        Assert.Contains($"stored {which} token", cause.Message, StringComparison.Ordinal);

        // The persisted error reads as the reconnect state, and carries no token material.
        Assert.Equal(JobFailureReason.Unauthorized, JobFailureReason.Classify(failure.Message));

        await AssertRevokedAndNotPolledAsync(accountId, now);
    }

    [Fact]
    public async Task FullIngest_fails_once_too()
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var accountId = await SeedAsync(now, expiresInDays: 60, accessReadable: false, refreshReadable: true);

        await using var db = Pinned(accountId);
        var job = new SyncJob { Id = Guid.NewGuid(), AccountId = accountId, Kind = JobKind.FullIngest, EnqueuedAt = now };
        var handler = new FullIngestJobHandler(db, TokenStore(db), null!, new RecordingEventHub(), NullLogger<FullIngestJobHandler>.Instance);

        var failure = await Assert.ThrowsAsync<NonRetryableJobException>(() => handler.ExecuteAsync(job, CancellationToken.None));
        Assert.IsType<TraktTokenUnreadableException>(failure.InnerException);
        await AssertRevokedAndNotPolledAsync(accountId, now);
    }

    [Fact]
    public async Task A_readable_token_leaves_the_link_active_and_the_failure_retryable()
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var accountId = await SeedAsync(now, expiresInDays: 60, accessReadable: true, refreshReadable: true);

        await using var db = Pinned(accountId);
        var job = new SyncJob { Id = Guid.NewGuid(), AccountId = accountId, Kind = JobKind.DeltaSync, EnqueuedAt = now };
        var handler = new DeltaSyncJobHandler(
            db, new StubTraktClient(), TokenStore(db), null!, null!, null!, null!, null!, new RecordingEventHub(),
            new ConfigurationBuilder().Build(), NullLogger<DeltaSyncJobHandler>.Instance);

        // The token decrypts, so the handler reaches Trakt; the stub refuses that call, which is an
        // ordinary (retryable) failure and says nothing about the link.
        var failure = await Assert.ThrowsAsync<NotSupportedException>(() => handler.ExecuteAsync(job, CancellationToken.None));
        Assert.False(JobRunnerService.IsNonRetryable(failure));

        await using var read = fixture.CreateContext(new AccountContext());
        Assert.Equal(ConnectionStatus.Active, (await read.TraktConnections.SingleAsync(c => c.AccountId == accountId)).Status);
    }

    public static TheoryData<string, int> RelinkedUnderneath => new()
    {
        // Which stale secret fails to decrypt, and days until the stale access token expires. A far
        // expiry reads the stale access token off the caller's entity; a near one re-reads the access
        // token from the row (readable, re-linked) and then reads the stale refresh token.
        { "access", 60 },
        { "refresh", 1 },
    };

    /// <summary>
    /// A job holds the old, unreadable token, the user re-links Trakt, and only then does the job
    /// try to decrypt what it holds. The failure is about the token the job read, not the one the
    /// row holds now, so the fresh link must stay Active and the poll must keep syncing it.
    /// </summary>
    [Theory]
    [MemberData(nameof(RelinkedUnderneath))]
    public async Task A_relink_after_the_job_read_the_old_token_is_not_revoked(string which, int expiresInDays)
    {
        if (!fixture.Available)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var accountId = await SeedAsync(now, expiresInDays, accessReadable: which != "access", refreshReadable: which != "refresh");

        await using var db = Pinned(accountId);
        var stale = await db.TraktConnections.SingleAsync(c => c.AccountId == accountId);

        // The re-link (TraktOAuthService) writes a fresh pair this host can read. The re-linked
        // access token is near expiry too, so the refresh variant still has to read a refresh token.
        await using (var relink = fixture.CreateContext(new AccountContext()))
        {
            var row = await relink.TraktConnections.SingleAsync(c => c.AccountId == accountId);
            row.AccessTokenEncrypted = hostRing.Protect("relinked-access-" + Guid.NewGuid().ToString("N"));
            row.RefreshTokenEncrypted = hostRing.Protect("relinked-refresh-" + Guid.NewGuid().ToString("N"));
            row.ExpiresAt = now.AddDays(expiresInDays);
            row.Status = ConnectionStatus.Active;
            await relink.SaveChangesAsync();
        }

        string? token = null;
        Exception? failure = null;
        try
        {
            token = await TokenStore(db).GetValidAccessTokenAsync(stale, CancellationToken.None);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Not the re-link failure: that would fail the job for good and tell the user, who has just
        // re-linked, to re-link again. A stale read is an ordinary, retryable miss, or (when the
        // re-linked access token is still valid) no miss at all.
        Assert.False(failure is TraktTokenUnreadableException, failure?.Message);
        Assert.True(failure is not null || token!.StartsWith("relinked-access-", StringComparison.Ordinal), token);

        await using (var read = fixture.CreateContext(new AccountContext()))
        {
            Assert.Equal(ConnectionStatus.Active, (await read.TraktConnections.SingleAsync(c => c.AccountId == accountId)).Status);
        }

        Assert.NotEqual(ConnectionStatus.Revoked, stale.Status);
    }

    private async Task AssertRevokedAndNotPolledAsync(Guid accountId, DateTime now)
    {
        await using (var read = fixture.CreateContext(new AccountContext()))
        {
            Assert.Equal(ConnectionStatus.Revoked, (await read.TraktConnections.SingleAsync(c => c.AccountId == accountId)).Status);
        }

        await using var services = Services();
        var poll = new TraktDeltaPollService(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IConfiguration>(),
            NullLogger<TraktDeltaPollService>.Instance);
        var pass = now.AddHours(7);
        await poll.ScheduleAsync(pass, 5, 15, CancellationToken.None);

        await using var after = fixture.CreateContext(new AccountContext());
        var queuedForIt = await after.SyncJobs.AnyAsync(j => j.AccountId == accountId && j.Kind == JobKind.DeltaSync);

        // The pass also queued every other fixture account in the shared test database; take those
        // back so no later test's runner claims them.
        await after.SyncJobs.Where(j => j.Kind == JobKind.DeltaSync && j.EnqueuedAt == pass).ExecuteDeleteAsync();
        Assert.False(queuedForIt);
    }

    private TraktTokenStore TokenStore(ReelDbContext db) =>
        new(db, new StubTraktClient(), hostRing, NullLogger<TraktTokenStore>.Instance);

    private ReelDbContext Pinned(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return fixture.CreateContext(context);
    }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<IAccountContext>(_ => new AccountContext());
        services.AddDbContext<ReelDbContext>(o => o.UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector()));
        return services.BuildServiceProvider(validateScopes: true);
    }

    private async Task<Guid> SeedAsync(DateTime now, int expiresInDays, bool accessReadable, bool refreshReadable)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"unreadable-{Guid.NewGuid():N}",
            DisplayName = "Unreadable Token Fixture",
            CreatedAt = now,
            LastSeenAt = now,
            PipelineStageChangedAt = now,
        };
        db.Add(account);
        db.TraktConnections.Add(new TraktConnection
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            AccessTokenEncrypted = (accessReadable ? hostRing : lostRing).Protect("access-" + Guid.NewGuid().ToString("N")),
            RefreshTokenEncrypted = (refreshReadable ? hostRing : lostRing).Protect("refresh-" + Guid.NewGuid().ToString("N")),
            ExpiresAt = now.AddDays(expiresInDays),
            Status = ConnectionStatus.Active,
        });
        await db.SaveChangesAsync();
        return account.Id;
    }
}
