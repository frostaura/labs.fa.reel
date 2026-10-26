using FrostAura.Reel.Application.CrossReference;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Domain.Sync;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Tests.CrossReference;

/// <summary>
/// WS-1 task 5 — the Ask allowance and the re-rank family split — as the pure admission rule the
/// reserve-before-call gate applies, and as simulations of whole days at the sizes the ledger
/// actually shows. The gate itself, against a real ledger and concurrent callers, is
/// <c>IntegrationTests.LlmBudgetGateTests</c>.
///
/// <para><b>Observed sizes</b> (slot ledger <c>reel_ws1</c>, a copy of the founder's data): the
/// heaviest curation day is 2026-10-14 — 42 <c>rerank-feed</c> calls at 2,096 tokens and 54
/// <c>rerank-page</c> at 2,397. The heaviest Ask day is 2026-10-22 — 29 v1 turns made 93
/// <c>rerank-ask</c> calls at 2,153 tokens, 3.2 a turn. The gate RESERVES a call's worst case
/// (prompt plus completion ceiling): ~5.5k for a 9-title curation batch (2.2k prompt + 3.3k
/// ceiling) and ~4.3k for a 6-title Ask batch (2k prompt + 100 + 370 x 6), then releases it for the
/// recorded figure.</para>
/// </summary>
public class LlmAllowanceTests
{
    private const long Limit = RerankBudget.DefaultDailyCalls; // 240
    private const long TokenLimit = RerankBudget.DefaultDailyTokens; // 600k

    private const long CurationReserve = 5_500;
    private const long AskReserve = 4_300;

    // ── The admission rule (pure) ──────────────────────────────────────────────────────────

    [Fact]
    public void Ask_v1_stops_at_its_sixty_percent_share_and_page_and_feed_keep_the_reserve()
    {
        Assert.Equal(144, RerankBudget.Share(Limit, RerankBudget.AskShare));
        var a = Allowance(askCalls: 143);
        Assert.True(a.Admits(ApiUsagePurpose.RerankAsk, 1_000));
        a = Allowance(askCalls: 144);
        Assert.False(a.Admits(ApiUsagePurpose.RerankAsk, 1_000));
        Assert.False(a.Admits(ApiUsagePurpose.RerankExpand, 1_000));
        Assert.False(a.HasAskHeadroom);

        // With Ask at its full share the page still has the rest of the ceiling (96), and the feed
        // 70% of what Ask left (0.7 x 96 = 67).
        Assert.True(a.Admits(ApiUsagePurpose.RerankPage, 1_000));
        Assert.True(Allowance(askCalls: 144, otherCalls: 66).Admits(ApiUsagePurpose.RerankFeed, 1_000));
        Assert.False(Allowance(askCalls: 144, otherCalls: 67).Admits(ApiUsagePurpose.RerankFeed, 1_000));
        Assert.True(Allowance(askCalls: 144, otherCalls: 95).Admits(ApiUsagePurpose.RerankPage, 1_000));
        Assert.False(Allowance(askCalls: 144, otherCalls: 96).Admits(ApiUsagePurpose.RerankPage, 1_000));
    }

    [Fact]
    public void With_no_ask_spend_the_feed_keeps_its_old_seventy_percent_stop()
    {
        Assert.True(Allowance(otherCalls: 167).Admits(ApiUsagePurpose.RerankFeed, 1_000));
        Assert.False(Allowance(otherCalls: 168).Admits(ApiUsagePurpose.RerankFeed, 1_000));
        Assert.True(Allowance(otherCalls: 167).HasBackgroundHeadroom);
        Assert.False(Allowance(otherCalls: 168).HasBackgroundHeadroom);
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
    public void A_cross_reference_batch_is_admitted_only_if_its_worst_case_fits()
    {
        var batch = CrossReferenceAllowance.EstimateBatchTokens(60);
        Assert.InRange(batch, 20_000, 30_000);
        var day = new CrossReferenceAllowance(10, 80, 1_200_000 - batch + 1, 1_200_000);
        Assert.True(day.HasHeadroom);
        Assert.False(day.AdmitsBatch(60));
        Assert.True(day.AdmitsBatch(20));
        Assert.False(new CrossReferenceAllowance(80, 80, 0, 1_200_000).AdmitsBatch(1));
    }

    // ── Whole days at the observed sizes ──────────────────────────────────────────────────

    [Fact]
    public void Forty_v1_ask_turns_plus_the_heaviest_observed_curation_day_refuse_nothing_and_stay_under_the_ceiling()
    {
        // The task-5 acceptance on the path that ships today: Ask v1 bills rerank-ask. 40 turns at
        // the observed 3.2 calls a turn is 128 calls; the curation day is 2026-10-14's 42 + 54.
        var day = Simulate(AskV1Calls(128), HeaviestCurationDay(), Limit, TokenLimit);

        Assert.Equal(0, day.RefusedCuration);
        Assert.Equal(0, day.RefusedAsk);
        Assert.Equal(128 + 96, day.Family.CallsUsed);
        Assert.True(day.Family.CallsUsed <= Limit);
        Assert.True(day.Family.TokensUsed <= TokenLimit);
    }

    [Fact]
    public void Twice_as_much_v1_ask_is_capped_at_its_share_and_curation_still_refuses_nothing()
    {
        // The reserve is a guarantee, not a forecast: eighty turns want 256 calls, the share
        // admits 144, and the heaviest curation day still runs whole — in either order.
        foreach (var askFirst in new[] { true, false })
        {
            var day = Simulate(AskV1Calls(256), HeaviestCurationDay(), Limit, TokenLimit, askFirst);

            Assert.Equal(0, day.RefusedCuration);
            Assert.Equal(RerankBudget.Share(Limit, RerankBudget.AskShare), day.Family.AskCallsUsed);
            Assert.True(day.Family.CallsUsed <= Limit);
            Assert.True(day.Family.TokensUsed <= TokenLimit);
        }
    }

    [Fact]
    public void The_old_150_call_ceiling_starved_the_page_on_the_observed_day()
    {
        // Why the defaults moved: at 150 / 300k the 40% reserve is 60 calls and 120k tokens, and
        // the heaviest observed curation day needs 96 and 217k. Kept as a test so the derivation
        // in RerankBudget's note stays checkable.
        var day = Simulate(AskV1Calls(128), HeaviestCurationDay(), 150, 300_000);
        Assert.True(day.RefusedCuration > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(9)]
    public void The_request_judging_ceiling_is_above_the_feeds_whose_prompt_it_extends(int titles)
    {
        // Round-1 review defect 6: rerank-ask truncated 2 of ~20 calls at 100 + 111 x n (544 for 4
        // titles, 766 for 6). Its prompt is the feed's plus a satisfies verdict per entry, so its
        // per-title tail can never be shorter than the feed's measured one; a ceiling below the
        // feed's is the defect, whatever the batch size.
        var catalog = new LlmPurposeCatalog(new ConfigurationBuilder().Build());
        var feed = catalog.For(ApiUsagePurpose.RerankFeed).MaxTokensFor(titles);

        foreach (var purpose in new[] { ApiUsagePurpose.RerankAsk, ApiUsagePurpose.RerankExpand, ApiUsagePurpose.AskJudge })
        {
            Assert.True(catalog.For(purpose).MaxTokensFor(titles) > feed, $"{purpose} at {titles} titles");
        }

        // The 6-title answer that only finished on the 2x retry (inside 1,532) now fits the first attempt.
        Assert.True(catalog.For(ApiUsagePurpose.RerankAsk).MaxTokensFor(6) >= 1_532);
    }

    [Fact]
    public void Forty_v2_ask_turns_never_touch_the_family()
    {
        // Ask v2 bills its own purposes (ask-plan / ask-judge / ask-answer) against AskBudget, so
        // its forty turns leave the family to curation entirely.
        var config = new ConfigurationBuilder().Build();
        var v2 = Enumerable.Range(0, 40).SelectMany(_ => new[]
        {
            ApiUsagePurpose.AskPlan, ApiUsagePurpose.AskJudge, ApiUsagePurpose.AskJudge, ApiUsagePurpose.AskJudge, ApiUsagePurpose.AskAnswer,
        }).ToList();
        Assert.All(v2, p => Assert.False(ApiUsagePurpose.RerankFamily.Contains(p)));
        Assert.All(v2, p => Assert.NotNull(AskBudget.For(config, p)));

        var day = Simulate([], HeaviestCurationDay(), Limit, TokenLimit);
        Assert.Equal(0, day.RefusedCuration);
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────────────────

    private static RerankAllowance Allowance(long askCalls = 0, long otherCalls = 0) =>
        new(askCalls + otherCalls, Limit, (askCalls + otherCalls) * 1_000, TokenLimit, askCalls, askCalls * 1_000);

    private sealed record Call(string Purpose, long Reserve, long Recorded);

    private sealed record Day(RerankAllowance Family, int RefusedAsk, int RefusedCuration);

    private static List<Call> AskV1Calls(int calls) =>
        Enumerable.Repeat(new Call(ApiUsagePurpose.RerankAsk, AskReserve, 2_153), calls).ToList();

    private static List<Call> HeaviestCurationDay() =>
        Enumerable.Repeat(new Call(ApiUsagePurpose.RerankFeed, CurationReserve, 2_096), 42)
            .Concat(Enumerable.Repeat(new Call(ApiUsagePurpose.RerankPage, CurationReserve, 2_397), 54))
            .ToList();

    /// <summary>
    /// One day's calls, interleaved (or one stream first), each admitted at its RESERVED worst case
    /// through the gate's own rule and then counted at its recorded size — what the gate does.
    /// </summary>
    private static Day Simulate(
        IReadOnlyList<Call> ask, IReadOnlyList<Call> curation, long callsLimit, long tokensLimit, bool? askFirst = null)
    {
        var order = askFirst switch
        {
            true => ask.Concat(curation),
            false => curation.Concat(ask),
            null => Interleave(ask, curation),
        };
        var family = new RerankAllowance(0, callsLimit, 0, tokensLimit);
        int refusedAsk = 0, refusedCuration = 0;
        foreach (var call in order)
        {
            var isAsk = RerankBudget.IsAskMember(call.Purpose);
            if (!family.Admits(call.Purpose, call.Reserve))
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
                TokensUsed = family.TokensUsed + call.Recorded,
                AskCallsUsed = family.AskCallsUsed + (isAsk ? 1 : 0),
                AskTokensUsed = family.AskTokensUsed + (isAsk ? call.Recorded : 0),
            };
        }

        return new Day(family, refusedAsk, refusedCuration);
    }

    private static IEnumerable<Call> Interleave(IReadOnlyList<Call> a, IReadOnlyList<Call> b)
    {
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i < a.Count)
            {
                yield return a[i];
            }

            if (i < b.Count)
            {
                yield return b[i];
            }
        }
    }
}
