namespace FrostAura.Reel.Application.Search.AskReel.Engine;

/// <summary>
/// The two plans no model wrote (§4.10).
///
/// <para><b>Carried forward</b> — the planner failed twice. The previous state is searched again
/// with only ADDITIVE words from the new message, and those as soft preferences: a word that made it
/// into the hard concepts would AND-filter the catalogue by a term nobody resolved ("funnier" is not
/// a keyword any title carries), and an ordinal or a negation phrase must never become something to
/// search for. Words the user ruled out are kept as their negations.</para>
///
/// <para><b>Keyless</b> — no model is configured at all. The message is read by the deterministic
/// keyless reader: the words it asks for become the topic, the words it rules out the negations.
/// Nothing will judge the results, so the engine shows them as unchecked.</para>
/// </summary>
public static class FallbackPlans
{
    public static PlanResult CarriedForward(PlanResult failed, string message, ConversationState state)
    {
        ArgumentNullException.ThrowIfNull(failed);
        ArgumentNullException.ThrowIfNull(state);
        var reading = AskReelIntentSanitizer.ReadKeyless(message);
        var known = state.Hard.Concepts.Select(c => c.UserWords).ToList();
        var additive = reading.Positive
            .Where(w => !known.Any(k => NegationTerms.EqualsAnyForm(k, w) || NegationTerms.EqualsAnyForm(w, k)))
            .ToList();
        return new PlanResult(
            state.Version == 0 ? TurnAct.NewRequest : TurnAct.Refine,
            ConversationStateDelta.None with
            {
                // A first turn has nothing to carry: its positive words are all it has, so they are its topic.
                Concepts = state.Version == 0 ? reading.Positive : [],
                Moods = state.Version == 0 ? [] : additive,
                Negations = reading.Negated,
            },
            [],
            [new ToolRequest(AskTool.SearchCatalog)],
            null,
            [],
            PlanSource.CarriedForward,
            failed.Meta,
            null,
            failed.Failure);
    }

    public static PlanResult Keyless(string message, ConversationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var reading = AskReelIntentSanitizer.ReadKeyless(message);
        return new PlanResult(
            reading.Positive.Count > 0 || state.Version == 0 ? TurnAct.NewRequest : TurnAct.Refine,
            ConversationStateDelta.None with { Concepts = reading.Positive, Negations = reading.Negated },
            [],
            [new ToolRequest(AskTool.SearchCatalog)],
            null,
            [],
            PlanSource.CarriedForward,
            LlmCallMeta.None);
    }
}
