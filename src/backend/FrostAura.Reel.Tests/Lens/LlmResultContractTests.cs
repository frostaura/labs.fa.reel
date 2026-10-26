using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Lens;

/// <summary>
/// The typed LLM-result contract the other lanes compile against: <see cref="ResultSource"/>,
/// <see cref="Satisfies"/>, <see cref="AgentFailureKind"/>, their persisted string forms, and the
/// debias step that must carry sources rather than launder them.
///
/// <para>The rule under test everywhere here is the one the old code broke: provenance is assigned
/// by whatever produced a value and is never inferred from the value. An unknown stored source must
/// read back as <c>Fallback</c>, because the opposite mistake is how thousands of stub sentences
/// were logged as model output.</para>
/// </summary>
public class LlmResultContractTests
{
    private static readonly Guid Title = Guid.Parse("44444444-0000-0000-0000-000000000000");

    [Theory]
    [InlineData("Llm", ResultSource.Llm)]
    [InlineData("Facet", ResultSource.Facet)]
    [InlineData("Fallback", ResultSource.Fallback)]
    [InlineData("llm", ResultSource.Fallback)] // case is part of the persisted name
    [InlineData("0", ResultSource.Fallback)] // Enum.TryParse alone would read this as Llm
    [InlineData("Llm,Facet", ResultSource.Fallback)]
    [InlineData("Templater", ResultSource.Fallback)] // a value a later build might write
    [InlineData("", ResultSource.Fallback)]
    [InlineData(null, ResultSource.Fallback)]
    public void An_unknown_stored_source_reads_back_as_fallback_never_as_model_output(string? stored, ResultSource expected)
    {
        Assert.Equal(expected, LlmResultText.ParseSource(stored));
    }

    [Fact]
    public void Every_source_and_failure_kind_round_trips_through_its_persisted_name()
    {
        Assert.All(Enum.GetValues<ResultSource>(), s => Assert.Equal(s, LlmResultText.ParseSource(LlmResultText.Name(s))));
        Assert.All(Enum.GetValues<AgentFailureKind>(), f => Assert.Equal(f, LlmResultText.ParseFailure(f.ToString())));
        Assert.Equal(AgentFailureKind.Unknown, LlmResultText.ParseFailure("RateLimited"));
        Assert.Equal(AgentFailureKind.Unknown, LlmResultText.ParseFailure("3"));
    }

    [Theory]
    [InlineData("yes", Satisfies.Yes)]
    [InlineData(" YES ", Satisfies.Yes)]
    [InlineData("partial", Satisfies.Partial)]
    [InlineData("no", Satisfies.No)]
    [InlineData("maybe", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void An_unreadable_verdict_is_unjudged_rather_than_no(string? wire, Satisfies? expected)
    {
        Assert.Equal(expected, LlmResultText.ParseSatisfies(wire));
    }

    [Fact]
    public void Every_verdict_round_trips_through_its_wire_form()
    {
        Assert.All(Enum.GetValues<Satisfies>(), v => Assert.Equal(v, LlmResultText.ParseSatisfies(LlmResultText.Wire(v))));
    }

    [Fact]
    public void The_model_answered_constructor_states_its_sources_and_leaves_the_verdict_unjudged()
    {
        var result = new RerankResult(Title, 0.8d, "A tense one for you");

        Assert.Equal(ResultSource.Llm, result.FitSource);
        Assert.Equal(ResultSource.Llm, result.WhySource);
        Assert.Null(result.Satisfies);
    }

    [Fact]
    public void A_reply_defaults_to_not_degraded_and_the_degraded_copy_claims_nothing()
    {
        var intent = new SearchIntent([], ["ninja"], [], "ninja", null, null);
        var reply = new AgentReply("Four ninja films.", intent);

        Assert.False(reply.Degraded);
        Assert.Null(reply.Failure);
        // No echo slot, no genre lens, no promise of results.
        Assert.DoesNotContain("{", AgentReply.DegradedReply, StringComparison.Ordinal);
        Assert.DoesNotContain("pick", AgentReply.DegradedReply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("here are", AgentReply.DegradedReply, StringComparison.OrdinalIgnoreCase);
    }

    // ── The degraded copy follows the cause (IREV2-SYS-02) ──────────────────────────────────

    public static TheoryData<AgentFailureKind?, string?, string> DegradedCopyByCause => new()
    {
        { AgentFailureKind.BudgetSpent, "budget_spent", AgentReply.BudgetSpentReply },
        { AgentFailureKind.BudgetSpent, null, AgentReply.BudgetSpentReply },
        { AgentFailureKind.ProviderRefused, "payment_required", AgentReply.OutOfCreditReply },
        { AgentFailureKind.ProviderUnavailable, "payment_required", AgentReply.OutOfCreditReply },
        { AgentFailureKind.ProviderRefused, "unauthorized", AgentReply.KeyRejectedReply },
        { AgentFailureKind.ProviderUnavailable, "unauthorized", AgentReply.KeyRejectedReply },
        { AgentFailureKind.ProviderRefused, "forbidden", AgentReply.ProviderRefusedReply },
        { AgentFailureKind.ProviderRefused, "error", AgentReply.ProviderRefusedReply },
        { AgentFailureKind.ProviderUnavailable, null, AgentReply.ProviderRefusedReply },
        { AgentFailureKind.NoModel, null, AgentReply.NoModelReply },
        // A transient failure is the only one "in a moment" is true of — whatever code rides along.
        { AgentFailureKind.TimedOut, "timed_out", AgentReply.DegradedReply },
        { AgentFailureKind.ProviderError, "rate_limited", AgentReply.DegradedReply },
        { AgentFailureKind.ProviderError, "payment_required", AgentReply.DegradedReply },
        { AgentFailureKind.Truncated, "no_answer", AgentReply.DegradedReply },
        { AgentFailureKind.Unknown, null, AgentReply.DegradedReply },
        { null, null, AgentReply.DegradedReply },
    };

    [Theory]
    [MemberData(nameof(DegradedCopyByCause))]
    public void The_degraded_reply_is_chosen_by_the_failure(AgentFailureKind? kind, string? code, string expected) =>
        Assert.Equal(expected, FrostAura.Reel.Application.Llm.LlmFailureCodes.DegradedReply(kind, code));

    [Fact]
    public void Every_degraded_reply_claims_nothing_and_only_a_transient_one_says_to_wait()
    {
        foreach (var kind in Enum.GetValues<AgentFailureKind>())
        {
            foreach (var code in new[] { null, "budget_spent", "payment_required", "unauthorized", "forbidden", "timed_out" })
            {
                var copy = FrostAura.Reel.Application.Llm.LlmFailureCodes.DegradedReply(kind, code);

                Assert.False(string.IsNullOrWhiteSpace(copy));
                Assert.DoesNotContain("{", copy, StringComparison.Ordinal);
                Assert.DoesNotContain("pick", copy, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("here are", copy, StringComparison.OrdinalIgnoreCase);
                var waitable = kind is not (AgentFailureKind.BudgetSpent or AgentFailureKind.NoModel
                    or AgentFailureKind.ProviderRefused or AgentFailureKind.ProviderUnavailable);
                Assert.Equal(waitable, copy.Contains("in a moment", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void Every_status_code_a_degraded_turn_can_carry_has_its_reply()
    {
        // The code is ReasonCode's own answer, so the two functions must agree on every refusal
        // status: a 402 that reads payment_required on the status panel reads "out of credit" here.
        Assert.Equal(
            AgentReply.OutOfCreditReply,
            FrostAura.Reel.Application.Llm.LlmFailureCodes.DegradedReply(
                AgentFailureKind.ProviderRefused,
                FrostAura.Reel.Application.Llm.LlmFailureCodes.ReasonCode(AgentFailureKind.ProviderRefused, 402)));
        Assert.Equal(
            AgentReply.KeyRejectedReply,
            FrostAura.Reel.Application.Llm.LlmFailureCodes.DegradedReply(
                AgentFailureKind.ProviderUnavailable,
                FrostAura.Reel.Application.Llm.LlmFailureCodes.ReasonCode(AgentFailureKind.ProviderUnavailable, breakerReason: "unauthorized")));
        Assert.Equal(
            AgentReply.BudgetSpentReply,
            FrostAura.Reel.Application.Llm.LlmFailureCodes.DegradedReply(
                AgentFailureKind.BudgetSpent,
                FrostAura.Reel.Application.Llm.LlmFailureCodes.ReasonCode(AgentFailureKind.BudgetSpent)));
    }

    // ── PositionDebiasedFit carries sources ─────────────────────────────────────────────────

    [Fact]
    public void A_fallback_fit_never_enters_the_mean_of_model_fits()
    {
        // A stub fit is predicted/10. Averaged with a model verdict it yields a number that is
        // neither, and either label on it would be a lie. Varied in both orders, because a version
        // that only skipped a fallback arriving SECOND would pass the other order.
        foreach (var fallbackFirst in new[] { true, false })
        {
            var debiased = new PositionDebiasedFit();
            var model = new RerankResult(Title, 0.9d, "A shinobi tale for you");
            var stub = new RerankResult(Title, 0.3d, ResultSource.Fallback, "A action pick aligned with your taste.", ResultSource.Fallback);
            debiased.Observe(fallbackFirst ? stub : model);
            debiased.Observe(fallbackFirst ? model : stub);

            var result = debiased.For(Title)!;
            Assert.Equal(0.9d, result.Fit, 6);
            Assert.Equal(ResultSource.Llm, result.FitSource);
            Assert.Equal("A shinobi tale for you", result.Why);
            Assert.Equal(ResultSource.Llm, result.WhySource);
            Assert.Equal(2, debiased.CountFor(Title));
        }
    }

    [Fact]
    public void A_title_only_ever_judged_by_fallbacks_reads_back_as_a_fallback()
    {
        var debiased = new PositionDebiasedFit();
        debiased.Observe(new RerankResult(Title, 0.4d, ResultSource.Fallback, "first", ResultSource.Fallback));
        debiased.Observe(new RerankResult(Title, 0.6d, ResultSource.Fallback, "second", ResultSource.Fallback));

        var result = debiased.For(Title)!;

        Assert.Equal(0.5d, result.Fit, 6);
        Assert.Equal(ResultSource.Fallback, result.FitSource);
        Assert.Equal(ResultSource.Fallback, result.WhySource);
        Assert.Equal("first", result.Why);
    }

    [Fact]
    public void A_model_fit_with_a_guard_replaced_why_keeps_both_labels_apart()
    {
        // The voice guard's shape: the model's fit, the stub's sentence. Debiasing must not
        // promote the sentence to Llm because the fit was.
        var debiased = new PositionDebiasedFit();
        debiased.Observe(new RerankResult(Title, 0.7d, ResultSource.Llm, "A drama pick aligned with your taste.", ResultSource.Fallback, Satisfies.Partial));

        var result = debiased.For(Title)!;

        Assert.Equal(ResultSource.Llm, result.FitSource);
        Assert.Equal(ResultSource.Fallback, result.WhySource);
        Assert.Equal(Satisfies.Partial, result.Satisfies);
    }

    [Fact]
    public void The_first_request_verdict_is_kept_and_a_later_unjudged_one_does_not_erase_it()
    {
        var debiased = new PositionDebiasedFit();
        debiased.Observe(new RerankResult(Title, 0.8d, ResultSource.Llm, "a", ResultSource.Llm, Satisfies.No));
        debiased.Observe(new RerankResult(Title, 0.6d, ResultSource.Llm, "b", ResultSource.Llm));
        debiased.Observe(new RerankResult(Title, 0.4d, ResultSource.Llm, "c", ResultSource.Llm, Satisfies.Yes));

        Assert.Equal(Satisfies.No, debiased.For(Title)!.Satisfies);
        Assert.Equal(0.6d, debiased.For(Title)!.Fit, 6);
    }
}
