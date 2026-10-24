using System.Collections.Concurrent;
using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Tests.IntegrationTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.CrossReference;

/// <summary>
/// WS-1 task 5 — the Ask allowance and the re-rank family split — first as the pure admission rule,
/// then through the reserve-before-call gate against a real ledger.
/// </summary>
[Collection("postgres")]
public class LlmAllowanceTests(PostgresFixture fixture)
{
    private const long Limit = RerankBudget.DefaultDailyCalls; // 150
    private const long TokenLimit = RerankBudget.DefaultDailyTokens;

    // ── The admission rule (pure) ──────────────────────────────────────────────────────────

    [Fact]
    public void Ask_v1_stops_at_its_sixty_percent_share_and_page_and_feed_keep_the_reserve()
    {
        var a = Allowance(askCalls: 89);
        Assert.True(a.Admits(ApiUsagePurpose.RerankAsk, 1_000));
        a = Allowance(askCalls: 90);
        Assert.False(a.Admits(ApiUsagePurpose.RerankAsk, 1_000));
        Assert.False(a.Admits(ApiUsagePurpose.RerankExpand, 1_000));
        Assert.False(a.HasAskHeadroom);

        // With Ask at its full share the page still has the rest of the ceiling, and the feed
        // 70% of what Ask left (0.7 x 60 = 42).
        Assert.True(a.Admits(ApiUsagePurpose.RerankPage, 1_000));
        Assert.True(Allowance(askCalls: 90, otherCalls: 41).Admits(ApiUsagePurpose.RerankFeed, 1_000));
        Assert.False(Allowance(askCalls: 90, otherCalls: 42).Admits(ApiUsagePurpose.RerankFeed, 1_000));
        Assert.True(Allowance(askCalls: 90, otherCalls: 59).Admits(ApiUsagePurpose.RerankPage, 1_000));
        Assert.False(Allowance(askCalls: 90, otherCalls: 60).Admits(ApiUsagePurpose.RerankPage, 1_000));
    }

    [Fact]
    public void With_no_ask_spend_the_feed_keeps_its_old_seventy_percent_stop()
    {
        Assert.True(Allowance(otherCalls: 104).Admits(ApiUsagePurpose.RerankFeed, 1_000));
        Assert.False(Allowance(otherCalls: 105).Admits(ApiUsagePurpose.RerankFeed, 1_000));
        Assert.True(Allowance(otherCalls: 104).HasBackgroundHeadroom);
        Assert.False(Allowance(otherCalls: 105).HasBackgroundHeadroom);
    }

    [Fact]
    public void A_call_that_would_pass_the_token_ceiling_is_refused_before_it_is_sent()
    {
        // "No overshoot": the reservation is the call's WORST case, so a call is admitted only if
        // even its full ceiling fits.
        var a = new RerankAllowance(10, Limit, TokenLimit - 1_500, TokenLimit);
        Assert.True(a.HasHeadroom); // the old read-then-spend check would have let it through
        Assert.False(a.Admits(ApiUsagePurpose.RerankPage, 2_000));
        Assert.True(a.Admits(ApiUsagePurpose.RerankPage, 1_500));
    }

    [Fact]
    public void Forty_ask_turns_plus_a_normal_feed_day_never_refuse_page_or_feed_and_stay_under_the_ceiling()
    {
        // The task-5 acceptance as a simulation of one day's call sequence. Ask v2 bills its own
        // purposes (ask-plan / ask-judge / ask-answer), so its forty turns never touch the family;
        // the feed day is the heaviest observed on the slot ledger without Ask (2026-10-14: 42
        // feed + 54 page). Every call is admitted through the same rule the gate applies.
        var askTurns = Enumerable.Range(0, 40).SelectMany(_ => new[]
        {
            ApiUsagePurpose.AskPlan, ApiUsagePurpose.AskJudge, ApiUsagePurpose.AskJudge, ApiUsagePurpose.AskJudge,
        });
        var feedDay = Enumerable.Repeat(ApiUsagePurpose.RerankFeed, 42)
            .Concat(Enumerable.Repeat(ApiUsagePurpose.RerankPage, 54));
        var refusedCuration = 0;
        var family = new RerankAllowance(0, Limit, 0, TokenLimit);
        foreach (var purpose in Interleave(askTurns, feedDay))
        {
            if (!ApiUsagePurpose.RerankFamily.Contains(purpose))
            {
                Assert.NotNull(AskBudget.For(new ConfigurationBuilder().Build(), purpose));
                continue;
            }

            if (!family.Admits(purpose, 2_000))
            {
                refusedCuration++;
                continue;
            }

            family = family with { CallsUsed = family.CallsUsed + 1, TokensUsed = family.TokensUsed + 2_000 };
        }

        Assert.Equal(0, refusedCuration);
        Assert.True(family.CallsUsed <= Limit);
        Assert.True(family.TokensUsed <= TokenLimit);
    }

    [Fact]
    public void Forty_v1_ask_turns_are_capped_so_curation_keeps_its_reserve()
    {
        // The v1 path still bills rerank-ask (four batches a turn). Its forty turns want 160 calls;
        // the share admits 90, and a curation day that fits the 40% reserve is never refused.
        var ask = Enumerable.Repeat(ApiUsagePurpose.RerankAsk, 160);
        var curation = Enumerable.Repeat(ApiUsagePurpose.RerankFeed, 30)
            .Concat(Enumerable.Repeat(ApiUsagePurpose.RerankPage, 30));
        var family = new RerankAllowance(0, Limit, 0, TokenLimit);
        var refusedAsk = 0;
        var refusedCuration = 0;
        foreach (var purpose in Interleave(ask, curation))
        {
            var isAsk = RerankBudget.IsAskMember(purpose);
            if (!family.Admits(purpose, 1_000))
            {
                if (isAsk)
                {
                    refusedAsk++;
                }
                else
                {
                    refusedCuration++;
                }

                continue;
            }

            family = family with
            {
                CallsUsed = family.CallsUsed + 1,
                TokensUsed = family.TokensUsed + 1_000,
                AskCallsUsed = family.AskCallsUsed + (isAsk ? 1 : 0),
                AskTokensUsed = family.AskTokensUsed + (isAsk ? 1_000 : 0),
            };
        }

        Assert.Equal(0, refusedCuration);
        Assert.Equal(70, refusedAsk);
        Assert.Equal(90, family.AskCallsUsed);
        Assert.Equal(150, family.CallsUsed);
    }

    // ── The gate, against a real ledger ────────────────────────────────────────────────────

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
        await RecordAsync(accountId, ApiUsagePurpose.RerankAsk, 90, 10_000);
        var gate = Gate();

        Assert.False((await gate.TryReserveAsync(accountId, ApiUsagePurpose.RerankAsk, 100, CancellationToken.None)).Admitted);
        Assert.True((await gate.TryReserveAsync(accountId, ApiUsagePurpose.RerankFeed, 100, CancellationToken.None)).Admitted);
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

    // ── Fixtures ───────────────────────────────────────────────────────────────────────────

    private static RerankAllowance Allowance(long askCalls = 0, long otherCalls = 0) =>
        new(askCalls + otherCalls, Limit, (askCalls + otherCalls) * 1_000, TokenLimit, askCalls, askCalls * 1_000);

    private static IEnumerable<string> Interleave(IEnumerable<string> a, IEnumerable<string> b)
    {
        using var x = a.GetEnumerator();
        using var y = b.GetEnumerator();
        bool hasX = x.MoveNext(), hasY = y.MoveNext();
        while (hasX || hasY)
        {
            if (hasX)
            {
                yield return x.Current;
                hasX = x.MoveNext();
            }

            if (hasY)
            {
                yield return y.Current;
                hasY = y.MoveNext();
            }
        }
    }

    private LlmCallGate Gate(Dictionary<string, string?>? settings = null, IPendingApiUsage? pending = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
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

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"llmallowance-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task RecordAsync(Guid accountId, string purpose, long calls, long tokens)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ExternalApiUsages.Add(new ExternalApiUsage
        {
            Id = Guid.NewGuid(),
            Provider = ApiProvider.OpenRouter,
            Day = DateTime.UtcNow.Date,
            AccountId = accountId,
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
}
