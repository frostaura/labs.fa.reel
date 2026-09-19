using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The widened usage ledger on real Postgres. The thing under test is the null handling: three of
/// the five key columns are legitimately null (a Trakt call has no model), and under Postgres's
/// default NULLS DISTINCT the recorder's ON CONFLICT would never fire for those rows — the 15s
/// flush would insert a fresh duplicate every tick, forever. Only a real database can prove the
/// NULLS NOT DISTINCT index actually collides them.
/// </summary>
[Collection("postgres")]
public class ApiUsageLedgerTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Rows_with_null_model_and_purpose_upsert_instead_of_duplicating()
    {
        if (!fixture.Available)
        {
            return; // no Postgres available — covered in CI's backend-integration job
        }

        var recorder = new ApiUsageRecorder(ScopeFactory(), NullLogger<ApiUsageRecorder>.Instance);

        recorder.Record(ApiProvider.Trakt);
        await recorder.FlushAsync(CancellationToken.None);
        recorder.Record(ApiProvider.Trakt, calls: 2);
        await recorder.FlushAsync(CancellationToken.None);

        await using var db = fixture.CreateContext(new AccountContext());
        var rows = await db.ExternalApiUsages
            .Where(u => u.Provider == ApiProvider.Trakt)
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal(3, row.CallCount);
        Assert.Null(row.Model);
        Assert.Null(row.Purpose);
        Assert.Null(row.AccountId);
        // Never reported is not the same as zero — a provider that sends no usage block must leave
        // the ledger saying "unknown", or the cost dashboard quietly reads as free.
        Assert.Null(row.TokensUsed);
    }

    [Fact]
    public async Task Token_counts_accumulate_per_account_model_and_purpose()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = Guid.NewGuid();
        var recorder = new ApiUsageRecorder(ScopeFactory(), NullLogger<ApiUsageRecorder>.Instance);

        recorder.Record(ApiProvider.OpenRouter, ApiUsagePurpose.Rerank, "openai/gpt-5.4-mini", accountId, 100, 20);
        await recorder.FlushAsync(CancellationToken.None);
        recorder.Record(ApiProvider.OpenRouter, ApiUsagePurpose.Rerank, "openai/gpt-5.4-mini", accountId, 50, 5);
        // A different purpose on the same model and day is a different bucket — the whole point of
        // widening the key was that embeddings and chat stopped being indistinguishable.
        recorder.Record(ApiProvider.OpenRouter, ApiUsagePurpose.Embed, "text-embedding-3-small", accountId, 900);
        await recorder.FlushAsync(CancellationToken.None);

        await using var db = fixture.CreateContext(new AccountContext());
        var rows = await db.ExternalApiUsages
            .Where(u => u.AccountId == accountId)
            .ToListAsync();

        Assert.Equal(2, rows.Count);

        var rerank = rows.Single(r => r.Purpose == ApiUsagePurpose.Rerank);
        Assert.Equal(2, rerank.CallCount);
        Assert.Equal(150, rerank.PromptTokens);
        Assert.Equal(25, rerank.CompletionTokens);
        Assert.Equal(175, rerank.TokensUsed); // prompt + completion, kept for the existing readers

        var embed = rows.Single(r => r.Purpose == ApiUsagePurpose.Embed);
        Assert.Equal("text-embedding-3-small", embed.Model);
        Assert.Equal(900, embed.PromptTokens);
        // The embeddings API has no completion side; reporting one would be an invention.
        Assert.Null(embed.CompletionTokens);
        Assert.Equal(900, embed.TokensUsed);
    }

    private IServiceScopeFactory ScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddScoped<IAccountContext, AccountContext>();
        services.AddScoped(_ => fixture.CreateContext(new AccountContext()));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
