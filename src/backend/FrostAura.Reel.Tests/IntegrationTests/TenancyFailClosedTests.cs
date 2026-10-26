using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Tenancy fails closed (J-11): an account-scoped query with no account pinned and no system scope
/// is refused under <see cref="UnscopedReadMode.Throw"/>, logged once per call site under
/// <see cref="UnscopedReadMode.Warn"/>, and never changes what a pinned or system-scoped query reads.
/// </summary>
[Collection("postgres")]
public class TenancyFailClosedTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Under_Throw_an_unpinned_scoped_query_is_refused_while_pinned_system_and_catalogue_reads_work()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (a, b) = await SeedTwoAccountsAsync();
        var strict = new AccountContext(new UnscopedReadPolicy(UnscopedReadMode.Throw));
        await using var db = fixture.CreateContext(strict);

        // Unpinned, outside system scope: refused — the generic filter, SyncJob's and ModelArtifact's.
        var refused = await Assert.ThrowsAsync<UnscopedTenantQueryException>(() =>
            db.ContentFilters.Where(f => f.AccountId == a || f.AccountId == b).ToListAsync());
        Assert.Contains(nameof(Under_Throw_an_unpinned_scoped_query_is_refused_while_pinned_system_and_catalogue_reads_work), refused.CallSite);
        await Assert.ThrowsAsync<UnscopedTenantQueryException>(() => db.SyncJobs.CountAsync(j => j.AccountId == a));
        await Assert.ThrowsAsync<UnscopedTenantQueryException>(() => db.ModelArtifacts.AnyAsync(m => m.AccountId == a));
        // A write through the filters is a query too.
        await Assert.ThrowsAsync<UnscopedTenantQueryException>(() =>
            db.ContentFilters.Where(f => f.AccountId == a).ExecuteDeleteAsync());

        // The shared catalogue and the accounts table carry no tenancy filter, so nothing is asked.
        Assert.True(await db.Accounts.AnyAsync(x => x.Id == a));
        _ = await db.Titles.Take(1).ToListAsync();

        // Deliberate cross-account work: system scope sees both accounts, and closing it closes it.
        using (strict.EnterSystemScope())
        {
            Assert.Equal(2, await db.ContentFilters.CountAsync(f => f.AccountId == a || f.AccountId == b));
            Assert.Equal(2, await db.SyncJobs.CountAsync(j => j.AccountId == a || j.AccountId == b));
        }

        Assert.False(strict.IsSystemScope);
        await Assert.ThrowsAsync<UnscopedTenantQueryException>(() => db.ContentFilters.CountAsync(f => f.AccountId == a));

        // Pinned: exactly that account's rows, system scope or not.
        strict.SetAccount(a);
        Assert.Equal(1, await db.ContentFilters.CountAsync(f => f.AccountId == a || f.AccountId == b));
        using (strict.EnterSystemScope())
        {
            Assert.Equal(1, await db.ContentFilters.CountAsync(f => f.AccountId == a || f.AccountId == b));
        }
    }

    [Fact]
    public async Task Under_Warn_an_unpinned_query_reads_everything_and_names_its_call_site_once()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (a, b) = await SeedTwoAccountsAsync();
        var log = new RecordingLogger<UnscopedReadPolicy>();
        var policy = new UnscopedReadPolicy(UnscopedReadMode.Warn, log);
        await using var db = fixture.CreateContext(new AccountContext(policy));

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(2, await db.ContentFilters.CountAsync(f => f.AccountId == a || f.AccountId == b));
        }

        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains($"{typeof(TenancyFailClosedTests).FullName}.{nameof(Under_Warn_an_unpinned_query_reads_everything_and_names_its_call_site_once)}", line.Message);

        // A second context in the same process shares the policy, and the site is already reported.
        await using var again = fixture.CreateContext(new AccountContext(policy));
        Assert.Equal(2, await again.ContentFilters.CountAsync(f => f.AccountId == a || f.AccountId == b));
        Assert.Single(log.Lines);
    }

    [Fact]
    public void A_hand_built_context_is_tooling_and_never_refuses()
    {
        // Fixtures and `dotnet ef` build contexts by hand; they stand in for system scope.
        Assert.True(new AccountContext().PermitUnscopedRead());
        Assert.Equal(UnscopedReadMode.Allow, UnscopedReadPolicy.Permissive.Mode);
    }

    [Theory]
    [InlineData(null, UnscopedReadPolicy.DefaultMode)]
    [InlineData("", UnscopedReadPolicy.DefaultMode)]
    [InlineData("throw", UnscopedReadMode.Throw)]
    [InlineData("Warn", UnscopedReadMode.Warn)]
    [InlineData("Allow", UnscopedReadMode.Allow)]
    [InlineData("Thrown", UnscopedReadPolicy.DefaultMode)]
    [InlineData("7", UnscopedReadPolicy.DefaultMode)]
    public void The_mode_comes_from_configuration_and_a_typo_never_switches_tenancy_off(string? raw, UnscopedReadMode expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(UnscopedReadPolicy.ConfigurationKey, raw)])
            .Build();

        Assert.Equal(expected, UnscopedReadPolicy.FromConfiguration(configuration, null).Mode);
    }

    [Fact]
    public void System_scope_nests_and_a_handle_disposed_twice_closes_only_itself()
    {
        var context = new AccountContext(new UnscopedReadPolicy(UnscopedReadMode.Throw));
        Assert.Throws<UnscopedTenantQueryException>(() => context.PermitUnscopedRead());

        var outer = context.EnterSystemScope();
        var inner = context.EnterSystemScope();
        inner.Dispose();
        inner.Dispose();
        Assert.True(context.IsSystemScope);
        Assert.True(context.PermitUnscopedRead());

        outer.Dispose();
        Assert.False(context.IsSystemScope);
        Assert.Throws<UnscopedTenantQueryException>(() => context.PermitUnscopedRead());
    }

    private async Task<(Guid A, Guid B)> SeedTwoAccountsAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        var accounts = Enumerable.Range(0, 2).Select(i => new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"fail-closed-{i}-{Guid.NewGuid():N}",
            CreatedAt = now,
            LastSeenAt = now,
            PipelineStageChangedAt = now,
        }).ToList();
        db.Accounts.AddRange(accounts);
        foreach (var account in accounts)
        {
            db.ContentFilters.Add(new ContentFilter { Id = Guid.NewGuid(), AccountId = account.Id, Kind = FilterKind.ExcludeGenre, Value = "horror", CreatedAt = now });
            db.SyncJobs.Add(new SyncJob { Id = Guid.NewGuid(), AccountId = account.Id, Kind = JobKind.Train, Status = JobStatus.Succeeded, EnqueuedAt = now, CompletedAt = now });
        }

        await db.SaveChangesAsync();
        return (accounts[0].Id, accounts[1].Id);
    }
}
