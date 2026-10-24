using System.Collections.Concurrent;
using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The two daily ceilings — the cross-reference one, and the re-rank FAMILY one that closes the
/// gap this product shipped with: four callers writing one <c>rerank</c> string against no budget
/// at all.
///
/// <para>Both budgets read the persisted ledger AND the recorder's un-flushed counters, because the
/// ledger flushes every fifteen seconds and one batch loop can spend a day's allowance well inside
/// a single window. The pending half is tested with a fake, since exercising it against a real
/// recorder would be a test of the flush timer.</para>
///
/// <para>Then the reserve-before-call gate (WS-1 task 5) that every budgeted purpose passes through,
/// against the same real ledger. These live under <c>IntegrationTests</c> so CI's integration job —
/// which filters on that namespace — actually runs them; in the unit job they had no database and
/// returned silently, so the "twenty callers, five admissions" proof was never checked.</para>
/// </summary>
[Collection("postgres")]
public class LlmBudgetGateTests(PostgresFixture fixture)
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
        await RecordAsync(accountId, ApiUsagePurpose.Rerank, calls: 60, tokens: 150_000);
        await RecordAsync(accountId, ApiUsagePurpose.RerankFeed, calls: 60, tokens: 150_000);
        await RecordAsync(accountId, ApiUsagePurpose.RerankPage, calls: 60, tokens: 150_000);
        await RecordAsync(accountId, ApiUsagePurpose.RerankAsk, calls: 40, tokens: 100_000);
        await RecordAsync(accountId, ApiUsagePurpose.RerankExpand, calls: 20, tokens: 50_000);

        await using var db = fixture.CreateContext(new AccountContext());
        var allowance = await new RerankBudget(db, Config()).AllowanceAsync(accountId, CancellationToken.None);

        Assert.Equal(RerankBudget.DefaultDailyCalls, allowance.CallsUsed); // 240
        Assert.Equal(RerankBudget.DefaultDailyTokens, allowance.TokensUsed); // 600k
        Assert.Equal(60, allowance.AskCallsUsed);
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
        // 168 of 240 calls is exactly 70%.
        await RecordAsync(accountId, ApiUsagePurpose.RerankFeed, calls: 168, tokens: 100_000);

        await using var db = fixture.CreateContext(new AccountContext());
        var allowance = await new RerankBudget(db, Config()).AllowanceAsync(accountId, CancellationToken.None);

        // The reserve is not for the feed. Ask Reel and the paginator keep the full ceiling: a
        // person mid-conversation being told the model is out of budget because a 3 a.m. feed build
        // spent it is the exact failure this gate exists to prevent.
        Assert.False(allowance.HasBackgroundHeadroom);
        Assert.True(allowance.HasHeadroom);
        Assert.Equal(72, allowance.CallsRemaining);
    }

    [Fact]
    public async Task The_token_half_of_the_background_fraction_binds_too()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        // 420k of 600k tokens is exactly 70%.
        await RecordAsync(accountId, ApiUsagePurpose.RerankFeed, calls: 10, tokens: 420_000);

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

    // ── The cross-reference batch pre-check (the gate's own rule) ─────────────────────────────

    [Fact]
    public async Task A_day_with_less_than_one_batch_of_headroom_admits_no_cross_reference_batch()
    {
        if (!fixture.Available)
        {
            return;
        }

        // HasHeadroom said yes to any day under the ceiling; the gate then refused the ~24k-token
        // batch and the run was recorded as the judge answering none. AdmitsBatch is the gate's rule.
        var accountId = await SeedAccountAsync();
        var batch = CrossReferenceAllowance.EstimateBatchTokens(60);
        await RecordAsync(accountId, ApiUsagePurpose.CrossReference, calls: 10, tokens: CrossReferenceBudget.DefaultDailyTokens - batch + 1);

        await using var db = fixture.CreateContext(new AccountContext());
        var allowance = await new CrossReferenceBudget(db, Config()).AllowanceAsync(accountId, CancellationToken.None);
        Assert.True(allowance.HasHeadroom);
        Assert.False(allowance.AdmitsBatch(60));
        Assert.True(allowance.AdmitsBatch(1));
    }

    // ── The reserve-before-call gate, against a real ledger ──────────────────────────────────

    [Fact]
    public async Task Concurrent_callers_cannot_overshoot_an_ask_ceiling()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var (callsSetting, _) = AskBudget.Settings("ASK_PLAN");
        var gate = Gate(new() { [callsSetting] = "5" });

        // Twenty callers race for five calls; nobody releases (every call is still in flight).
        var admissions = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => gate.TryReserveAsync(accountId, ApiUsagePurpose.AskPlan, 1_000, CancellationToken.None)));

        Assert.Equal(5, admissions.Count(a => a.Admitted));
        Assert.All(admissions.Where(a => !a.Admitted), a => Assert.Contains("ask-plan ceiling", a.Refusal, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Concurrent_callers_cannot_overshoot_the_rerank_familys_token_ceiling()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The token half, on the family: 100k tokens of headroom and twenty concurrent 9-title
        // page batches reserving ~3.9k each — exactly 25 would fit, so twenty all fit; at 30k of
        // headroom exactly 7 do. Vary the fixture, not just the code.
        foreach (var (headroom, expected) in new[] { (100_000L, 20), (30_000L, 7) })
        {
            var accountId = await SeedAccountAsync();
            await RecordAsync(accountId, ApiUsagePurpose.RerankPage, calls: 1, tokens: RerankBudget.DefaultDailyTokens - headroom);
            var gate = Gate();
            var admissions = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(_ => gate.TryReserveAsync(accountId, ApiUsagePurpose.RerankPage, 3_900, CancellationToken.None)));
            Assert.Equal(expected, admissions.Count(a => a.Admitted));
        }
    }

    [Fact]
    public async Task A_released_reservation_is_replaced_by_the_recorded_call_so_the_total_still_holds()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var (callsSetting, _) = AskBudget.Settings("ASK_JUDGE");
        var pending = new CountingPending();
        var gate = Gate(new() { [callsSetting] = "3" }, pending);

        var admitted = 0;
        for (var i = 0; i < 10; i++)
        {
            using var admission = await gate.TryReserveAsync(accountId, ApiUsagePurpose.AskJudge, 500, CancellationToken.None);
            if (admission.Admitted)
            {
                admitted++;
                pending.Add(accountId, ApiUsagePurpose.AskJudge, 1, 500); // what the client records before release
            }
        }

        Assert.Equal(3, admitted);
    }

    [Fact]
    public async Task The_ledger_counts_and_each_ask_purpose_has_its_own_ceiling()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await RecordAsync(accountId, ApiUsagePurpose.AskPlan, AskBudget.DefaultPlanCalls, 10_000);
        var gate = Gate();

        Assert.False((await gate.TryReserveAsync(accountId, ApiUsagePurpose.AskPlan, 100, CancellationToken.None)).Admitted);
        Assert.True((await gate.TryReserveAsync(accountId, ApiUsagePurpose.AskJudge, 100, CancellationToken.None)).Admitted);
        Assert.True((await gate.TryReserveAsync(accountId, ApiUsagePurpose.RerankPage, 100, CancellationToken.None)).Admitted);
    }

    [Fact]
    public async Task The_gate_holds_v1_ask_to_its_share_of_the_family()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await RecordAsync(accountId, ApiUsagePurpose.RerankAsk, RerankBudget.Share(RerankBudget.DefaultDailyCalls, RerankBudget.AskShare), 10_000);
        var gate = Gate();

        Assert.False((await gate.TryReserveAsync(accountId, ApiUsagePurpose.RerankAsk, 100, CancellationToken.None)).Admitted);
        Assert.True((await gate.TryReserveAsync(accountId, ApiUsagePurpose.RerankFeed, 100, CancellationToken.None)).Admitted);
    }

    [Fact]
    public async Task The_gate_refuses_a_cross_reference_batch_the_day_cannot_hold()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await RecordAsync(accountId, ApiUsagePurpose.CrossReference, calls: 10, tokens: CrossReferenceBudget.DefaultDailyTokens - 20_000);
        var gate = Gate();

        var refused = await gate.TryReserveAsync(accountId, ApiUsagePurpose.CrossReference, 24_000, CancellationToken.None);
        Assert.False(refused.Admitted);
        Assert.Contains("cross-reference ceiling", refused.Refusal, StringComparison.Ordinal);
        Assert.True((await gate.TryReserveAsync(accountId, ApiUsagePurpose.CrossReference, 19_000, CancellationToken.None)).Admitted);
    }

    [Fact]
    public async Task Catalogue_wide_work_and_ungated_purposes_are_unmetered()
    {
        if (!fixture.Available)
        {
            return;
        }

        var gate = Gate(new() { [AskBudget.Settings("ASK_PLAN").Calls] = "0" });
        Assert.Same(LlmAdmission.Unmetered, await gate.TryReserveAsync(null, ApiUsagePurpose.AskPlan, 1, CancellationToken.None));
        Assert.Same(LlmAdmission.Unmetered, await gate.TryReserveAsync(Guid.NewGuid(), ApiUsagePurpose.Lens, 1, CancellationToken.None));
        Assert.Same(LlmAdmission.Unmetered, await gate.TryReserveAsync(Guid.NewGuid(), ApiUsagePurpose.Probe(ApiUsagePurpose.AskPlan), 1, CancellationToken.None));
    }

    private LlmCallGate Gate(Dictionary<string, string?>? settings = null, IPendingApiUsage? pending = null)
    {
        var config = Config(settings);
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddScoped<IReelDbContext>(_ => fixture.CreateContext(new AccountContext()));
        if (pending is not null)
        {
            services.AddSingleton(pending);
        }

        services.AddScoped(sp => new RerankBudget(sp.GetRequiredService<IReelDbContext>(), config, sp.GetService<IPendingApiUsage>()));
        services.AddScoped(sp => new CrossReferenceBudget(sp.GetRequiredService<IReelDbContext>(), config, sp.GetService<IPendingApiUsage>()));
        var provider = services.BuildServiceProvider();
        return new LlmCallGate(provider.GetRequiredService<IServiceScopeFactory>(), config, NullLogger<LlmCallGate>.Instance);
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

    private sealed class CountingPending : IPendingApiUsage
    {
        private readonly ConcurrentDictionary<(Guid, string), (long, long)> _counts = new();

        public void Add(Guid account, string purpose, long calls, long tokens) =>
            _counts.AddOrUpdate((account, purpose), (calls, tokens), (_, v) => (v.Item1 + calls, v.Item2 + tokens));

        public (long Calls, long Tokens) PendingToday(ApiProvider provider, Guid? accountId, string purpose) =>
            accountId is { } a && _counts.TryGetValue((a, purpose), out var v) ? v : (0, 0);

        public (long Calls, long Tokens) PendingTodayAllAccounts(ApiProvider provider, string purpose) => (0, 0);
    }

    /// <summary>Un-flushed counters, without a flush timer in the test.</summary>
    private sealed class FakePending((long Calls, long Tokens) today) : IPendingApiUsage
    {
        public (long Calls, long Tokens) PendingToday(ApiProvider provider, Guid? accountId, string purpose) => today;

        public (long Calls, long Tokens) PendingTodayAllAccounts(ApiProvider provider, string purpose) => today;
    }
}
