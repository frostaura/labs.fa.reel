using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Tests.IntegrationTests;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Tests.CrossReference;

/// <summary>
/// The two daily ceilings — the cross-reference one, and the re-rank FAMILY one that closes the
/// gap this product shipped with: four callers writing one <c>rerank</c> string against no budget
/// at all.
///
/// <para>Both budgets read the persisted ledger AND the recorder's un-flushed counters, because the
/// ledger flushes every fifteen seconds and one batch loop can spend a day's allowance well inside
/// a single window. The pending half is tested with a fake, since exercising it against a real
/// recorder would be a test of the flush timer.</para>
/// </summary>
[Collection("postgres")]
public class BudgetTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Both_cross_reference_ceilings_bind_independently()
    {
        if (!fixture.Available)
        {
            return;
        }

        // A call ceiling alone cannot stop a run batching sixty titles into one enormous prompt,
        // and a token ceiling alone cannot stop a thousand tiny ones. The pair is what makes it a
        // budget rather than a forecast.
        var accountId = await SeedAccountAsync();
        await RecordAsync(accountId, ApiUsagePurpose.CrossReference, calls: 79, tokens: 1_199_000);

        await using var db = fixture.CreateContext(new AccountContext());
        var budget = new CrossReferenceBudget(db, Config());
        Assert.True((await budget.AllowanceAsync(accountId, CancellationToken.None)).HasHeadroom);

        await RecordAsync(accountId, ApiUsagePurpose.CrossReference, calls: 1, tokens: 0);
        await using var db2 = fixture.CreateContext(new AccountContext());
        Assert.False((await new CrossReferenceBudget(db2, Config())
            .AllowanceAsync(accountId, CancellationToken.None)).HasHeadroom);
    }

    [Fact]
    public async Task The_cross_reference_budget_ignores_the_eval_purpose()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The whole reason `cross-reference-eval` is a different string: validating the judge must
        // never be able to consume the budget the judge runs on. Same separation as lens/lens-audit.
        var accountId = await SeedAccountAsync();
        await RecordAsync(accountId, ApiUsagePurpose.CrossReferenceEval, calls: 500, tokens: 9_000_000);

        await using var db = fixture.CreateContext(new AccountContext());
        var allowance = await new CrossReferenceBudget(db, Config()).AllowanceAsync(accountId, CancellationToken.None);

        Assert.Equal(0, allowance.CallsUsed);
        Assert.True(allowance.HasHeadroom);
    }

    [Fact]
    public async Task In_flight_spend_counts_against_the_cross_reference_ceiling()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await using var db = fixture.CreateContext(new AccountContext());
        var pending = new FakePending((79, 0));
        var allowance = await new CrossReferenceBudget(db, Config(), pending)
            .AllowanceAsync(accountId, CancellationToken.None);

        Assert.Equal(79, allowance.CallsUsed);
        Assert.True(allowance.HasHeadroom);

        var spent = await new CrossReferenceBudget(db, Config(), new FakePending((80, 0)))
            .AllowanceAsync(accountId, CancellationToken.None);
        Assert.False(spent.HasHeadroom);
    }

    [Fact]
    public async Task The_rerank_family_ceiling_counts_every_member_including_the_legacy_string()
    {
        if (!fixture.Available)
        {
            return;
        }

        // A ceiling that summed only the four new members would read a day with real historical
        // spend on it as empty.
        var accountId = await SeedAccountAsync();
        await RecordAsync(accountId, ApiUsagePurpose.Rerank, calls: 40, tokens: 80_000);
        await RecordAsync(accountId, ApiUsagePurpose.RerankFeed, calls: 40, tokens: 80_000);
        await RecordAsync(accountId, ApiUsagePurpose.RerankPage, calls: 40, tokens: 80_000);
        await RecordAsync(accountId, ApiUsagePurpose.RerankAsk, calls: 20, tokens: 40_000);
        await RecordAsync(accountId, ApiUsagePurpose.RerankExpand, calls: 10, tokens: 20_000);

        await using var db = fixture.CreateContext(new AccountContext());
        var allowance = await new RerankBudget(db, Config()).AllowanceAsync(accountId, CancellationToken.None);

        Assert.Equal(150, allowance.CallsUsed);
        Assert.Equal(300_000, allowance.TokensUsed);
        Assert.False(allowance.HasHeadroom);
    }

    [Fact]
    public async Task The_rerank_family_ceiling_ignores_purposes_outside_the_family()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await RecordAsync(accountId, ApiUsagePurpose.Lens, calls: 60, tokens: 120_000);
        await RecordAsync(accountId, ApiUsagePurpose.CrossReference, calls: 80, tokens: 1_200_000);
        await RecordAsync(accountId, ApiUsagePurpose.SearchAgent, calls: 40, tokens: 90_000);

        await using var db = fixture.CreateContext(new AccountContext());
        var allowance = await new RerankBudget(db, Config()).AllowanceAsync(accountId, CancellationToken.None);

        // Cross-reference in particular never shares this budget: a cold pass over ten thousand
        // titles would eat every other surface's allowance in an afternoon.
        Assert.Equal(0, allowance.CallsUsed);
        Assert.True(allowance.HasHeadroom);
    }

    [Fact]
    public async Task Background_rerank_stops_at_seventy_percent_while_the_interactive_path_keeps_going()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        // 105 of 150 calls is exactly 70%.
        await RecordAsync(accountId, ApiUsagePurpose.RerankFeed, calls: 105, tokens: 100_000);

        await using var db = fixture.CreateContext(new AccountContext());
        var allowance = await new RerankBudget(db, Config()).AllowanceAsync(accountId, CancellationToken.None);

        // The reserve is not for the feed. Ask Reel and the paginator keep the full ceiling: a
        // person mid-conversation being told the model is out of budget because a 3 a.m. feed build
        // spent it is the exact failure this gate exists to prevent.
        Assert.False(allowance.HasBackgroundHeadroom);
        Assert.True(allowance.HasHeadroom);
        Assert.Equal(45, allowance.CallsRemaining);
    }

    [Fact]
    public async Task The_token_half_of_the_background_fraction_binds_too()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await RecordAsync(accountId, ApiUsagePurpose.RerankFeed, calls: 10, tokens: 210_000);

        await using var db = fixture.CreateContext(new AccountContext());
        var allowance = await new RerankBudget(db, Config()).AllowanceAsync(accountId, CancellationToken.None);

        Assert.False(allowance.HasBackgroundHeadroom);
        Assert.True(allowance.HasHeadroom);
    }

    [Fact]
    public async Task Yesterdays_spend_does_not_count_against_today()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await RecordAsync(accountId, ApiUsagePurpose.CrossReference, calls: 500, tokens: 9_000_000, dayOffset: -1);
        await RecordAsync(accountId, ApiUsagePurpose.RerankFeed, calls: 500, tokens: 9_000_000, dayOffset: -1);

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.True((await new CrossReferenceBudget(db, Config()).AllowanceAsync(accountId, CancellationToken.None)).HasHeadroom);
        Assert.True((await new RerankBudget(db, Config()).AllowanceAsync(accountId, CancellationToken.None)).HasBackgroundHeadroom);
    }

    [Fact]
    public async Task Another_accounts_spend_does_not_count_against_this_one()
    {
        if (!fixture.Available)
        {
            return;
        }

        var mine = await SeedAccountAsync();
        var theirs = await SeedAccountAsync();
        await RecordAsync(theirs, ApiUsagePurpose.CrossReference, calls: 500, tokens: 9_000_000);
        await RecordAsync(theirs, ApiUsagePurpose.RerankFeed, calls: 500, tokens: 9_000_000);

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.True((await new CrossReferenceBudget(db, Config()).AllowanceAsync(mine, CancellationToken.None)).HasHeadroom);
        Assert.True((await new RerankBudget(db, Config()).AllowanceAsync(mine, CancellationToken.None)).HasBackgroundHeadroom);
    }

    [Fact]
    public async Task The_ceilings_are_configurable_and_a_zero_ceiling_admits_nothing()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var config = Config(new Dictionary<string, string?>
        {
            [CrossReferenceBudget.CallsSetting] = "0",
            [CrossReferenceBudget.TokensSetting] = "0",
            [RerankBudget.CallsSetting] = "0",
            [RerankBudget.TokensSetting] = "0",
        });

        await using var db = fixture.CreateContext(new AccountContext());
        Assert.False((await new CrossReferenceBudget(db, config).AllowanceAsync(accountId, CancellationToken.None)).HasHeadroom);
        Assert.False((await new RerankBudget(db, config).AllowanceAsync(accountId, CancellationToken.None)).HasHeadroom);
    }

    private static IConfiguration Config(Dictionary<string, string?>? overrides = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(overrides ?? []).Build();

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"xrefbudget-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task RecordAsync(Guid accountId, string purpose, long calls, long tokens, int dayOffset = 0)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ExternalApiUsages.Add(new ExternalApiUsage
        {
            Id = Guid.NewGuid(),
            Provider = ApiProvider.OpenRouter,
            Day = DateTime.UtcNow.Date.AddDays(dayOffset),
            AccountId = accountId,
            // A unique model per row: the ledger's key is (Provider, Day, AccountId, Model, Purpose)
            // and these tests deliberately write several rows for one purpose on one day, which is
            // what the real 15-second flush does across models. Reusing one model id would collide
            // on the unique index rather than accumulate.
            Model = $"test/{Guid.NewGuid():N}",
            Purpose = purpose,
            CallCount = calls,
            TokensUsed = tokens,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Un-flushed counters, without a flush timer in the test.</summary>
    private sealed class FakePending((long Calls, long Tokens) today) : IPendingApiUsage
    {
        public (long Calls, long Tokens) PendingToday(ApiProvider provider, Guid? accountId, string purpose) => today;

        public (long Calls, long Tokens) PendingTodayAllAccounts(ApiProvider provider, string purpose) => today;
    }
}
