using System.Net;
using System.Text.Json;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// HREV1-PLAT-3: <c>GET /api/metrics/pipeline</c> is open to every signed-in account, and its
/// <c>apiUsage</c> block read <c>ExternalApiUsages</c> filtered on the day alone — so a Free account
/// was shown the founder's per-day OpenRouter and Trakt call counts. The ledger is not
/// <c>IAccountScoped</c> (system rows carry a null account), so no global query filter catches it;
/// the endpoint has to. A non-founder sees its own rows only; the founder, who operates the system,
/// sees every account's and the catalogue-wide (null-account) rows.
///
/// <para>Each row carries a call count no other test writes, so the assertions are about THIS
/// class's rows and hold on a shared database.</para>
/// </summary>
[Collection("postgres")]
public class MetricsPipelineUsageTenancyTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Theory]
    [InlineData(AccountTier.Free)]
    [InlineData(AccountTier.Paid)]
    public async Task A_non_founder_sees_only_its_own_api_usage(AccountTier tier)
    {
        if (!fixture.Available)
        {
            return;
        }

        var (caller, founder, other) = await SeedAccountsAsync(tier);
        var counts = await SeedUsageAsync(caller, founder, other);

        var usage = await ApiUsageCallCountsAsync(caller);

        Assert.Contains(counts.Caller, usage);
        Assert.DoesNotContain(counts.Founder, usage);
        Assert.DoesNotContain(counts.Other, usage);
        Assert.DoesNotContain(counts.System, usage);
    }

    [Fact]
    public async Task A_non_founder_with_no_usage_of_its_own_sees_none()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The defect's exact shape: a fresh Free account with nothing of its own, on a ledger full of
        // other accounts' and the system's rows.
        var (caller, founder, other) = await SeedAccountsAsync(AccountTier.Free);
        await SeedUsageAsync(callerOrNull: null, founder, other);

        var usage = await ApiUsageCallCountsAsync(caller);

        Assert.Empty(usage);
    }

    [Fact]
    public async Task The_founder_sees_every_account_and_the_system_rows()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (caller, founder, other) = await SeedAccountsAsync(AccountTier.Free);
        var counts = await SeedUsageAsync(caller, founder, other);

        var usage = await ApiUsageCallCountsAsync(founder);

        Assert.Contains(counts.Caller, usage);
        Assert.Contains(counts.Founder, usage);
        Assert.Contains(counts.Other, usage);
        Assert.Contains(counts.System, usage);
    }

    [Fact]
    public async Task The_rest_of_the_response_stays_scoped_to_the_caller()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The jobs and builds blocks were already filtered by account; pin that they stay so, so a
        // future "founder sees everything" change to one block cannot widen the others.
        var (caller, founder, _) = await SeedAccountsAsync(AccountTier.Free);
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
            db.SyncJobs.Add(new SyncJob
            {
                Id = Guid.NewGuid(),
                AccountId = founder,
                Kind = JobKind.EnrichCatalog,
                Status = JobStatus.Succeeded,
                ProgressMessage = "founder-only-progress-marker",
                EnqueuedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var client = await api.CreateSignedInClientAsync(caller);
        var body = await client.GetStringAsync("/api/metrics/pipeline");

        Assert.DoesNotContain("founder-only-progress-marker", body, StringComparison.Ordinal);
    }

    private async Task<(Guid Caller, Guid Founder, Guid Other)> SeedAccountsAsync(AccountTier callerTier)
    {
        var caller = await api.SeedAccountAsync($"usage-caller-{Guid.NewGuid():N}");
        var founder = await api.SeedAccountAsync($"usage-founder-{Guid.NewGuid():N}");
        var other = await api.SeedAccountAsync($"usage-other-{Guid.NewGuid():N}");
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        var accounts = await db.Accounts.IgnoreQueryFilters()
            .Where(a => a.Id == caller || a.Id == founder || a.Id == other)
            .ToListAsync();
        accounts.Single(a => a.Id == caller).Tier = callerTier;
        accounts.Single(a => a.Id == founder).Tier = AccountTier.Founder;
        accounts.Single(a => a.Id == other).Tier = AccountTier.Paid;
        await db.SaveChangesAsync();
        return (caller, founder, other);
    }

    private sealed record UsageCounts(long Caller, long Founder, long Other, long System);

    /// <summary>
    /// One OpenRouter row today for each account and one catalogue-wide row, each with a unique call
    /// count (and a unique model, so the ledger's unique index never collides across runs).
    /// </summary>
    private async Task<UsageCounts> SeedUsageAsync(Guid? callerOrNull, Guid founder, Guid other)
    {
        long Unique() => Random.Shared.NextInt64(10_000_000, 9_000_000_000);
        var counts = new UsageCounts(Unique(), Unique(), Unique(), Unique());
        var today = DateTime.UtcNow.Date;
        var model = $"test/usage-tenancy-{Guid.NewGuid():N}";
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();

        ExternalApiUsage Row(Guid? accountId, long callCount) => new()
        {
            Id = Guid.NewGuid(),
            Provider = ApiProvider.OpenRouter,
            Day = today,
            AccountId = accountId,
            Model = model,
            CallCount = callCount,
        };

        if (callerOrNull is { } caller)
        {
            db.ExternalApiUsages.Add(Row(caller, counts.Caller));
        }

        db.ExternalApiUsages.AddRange(Row(founder, counts.Founder), Row(other, counts.Other), Row(null, counts.System));
        await db.SaveChangesAsync();
        return counts;
    }

    private async Task<List<long>> ApiUsageCallCountsAsync(Guid accountId)
    {
        using var client = await api.CreateSignedInClientAsync(accountId);
        using var response = await client.GetAsync("/api/metrics/pipeline");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. json.RootElement.GetProperty("apiUsage").EnumerateArray().Select(u => u.GetProperty("callCount").GetInt64())];
    }
}
