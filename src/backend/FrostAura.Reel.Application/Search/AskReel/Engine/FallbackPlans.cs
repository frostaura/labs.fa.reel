namespace FrostAura.Reel.Application.Search.AskReel.Engine;

/// <summary>
/// The two plans no model wrote (§4.10).
///
/// <para><b>Carried forward</b> — the planner failed twice. The previous search is searched again
/// with only ADDITIVE words from the new message, and those as soft preferences: a word that made it
/// into the hard concepts would AND-filter the catalogue by a term nobody resolved ("funnier" is not
/// a keyword any title carries), and an ordinal or a negation phrase must never become something to
/// search for. Words the user ruled out are kept as their negations.</para>
///
/// <para><b>Keyless</b> — no model is configured at all. The message is read by the deterministic
/// keyless reader: the words it asks for become the topic, the words it rules out the negations.
/// Nothing will judge the results, so the engine shows them as unchecked.</para>
///
/// <para>Both read a mood as a mood and a year phrase as a year window (HREV3-ASK-E,
/// <see cref="AskReelIntentSanitizer.ReadKeylessRequest"/>): "tense heist movies from the last ten
/// years" with the planner down searched the hard topics "tense heist" and "ten years", found nothing
/// tagged either, and showed three unrelated titles.</para>
///
/// <para>Both read the message through <see cref="FallbackReader"/> (HREV4-ASK-05, HREV4-RET-02): a
/// question about a card is answered from its facts, "like &lt;title&gt;" is a reference, a comparison
/// is a soft steer, a connective or a generic noun is never a topic, and a negation is the phrase the
/// user wrote.</para>
///
/// <para>Both keep the user's topic as the PHRASE it was written in ("time travel", "liam neeson"),
/// never as separate words each required on its own, and both take the media the user's words name
/// (<see cref="PlanSanitizer.UserMedia"/>) — the sanitiser that does both for a model's plan never
/// sees these (HREV1-ASK-06, HREV1-ASK-02).</para>
/// </summary>
public static class FallbackPlans
{
    public static PlanResult CarriedForward(PlanResult failed, string message, ConversationState state, int? currentYear = null)
    {
        ArgumentNullException.ThrowIfNull(failed);
        ArgumentNullException.ThrowIfNull(state);
        var reading = FallbackReader.Read(Readable(message, state), state, currentYear ?? DateTime.UtcNow.Year);
        if (reading.ItemQuestion)
        {
            return ItemQuestion(reading.QuestionName, message, failed.Meta, failed.Failure);
        }

        if (reading.Chitchat)
        {
            return Chitchat(failed.Meta, failed.Failure);
        }

        var known = state.Hard.Concepts.Select(c => c.UserWords).ToList();
        var additive = reading.Concepts
            .Where(w => !known.Any(k => NegationTerms.EqualsAnyForm(k, w) || NegationTerms.EqualsAnyForm(w, k)))
            .Where(w => !reading.YearWords.Contains(w, StringComparer.Ordinal))
            .ToList();
        // First means nothing searched yet, not answer one: after an opening small talk, item question
        // or clarifying question there is no search to carry (HREV7-ASK-01).
        var first = !state.HoldsSearch;
        return new PlanResult(
            first ? TurnAct.NewRequest : TurnAct.Refine,
            ConversationStateDelta.None with
            {
                // A first turn has nothing to carry: its topics are all it has, so they are its
                // requirement — less the moods, the comparisons and the year window it names, which
                // are read as what they are. A later turn adds its words only as soft preferences.
                Concepts = first ? reading.Concepts : [],
                Genres = first ? reading.Genres : [],
                Moods = first ? reading.Moods : [.. reading.Moods, .. additive],
                Directions = reading.Directions,
                Years = reading.Years,
                Negations = reading.Negations,
                ReferenceNames = reading.ReferenceNames,
                Media = reading.Media,
            },
            [],
            [new ToolRequest(reading.ReferenceNames.Count > 0 ? AskTool.SimilarTo : AskTool.SearchCatalog)],
            null,
            [],
            PlanSource.CarriedForward,
            failed.Meta,
            null,
            failed.Failure);
    }

    public static PlanResult Keyless(string message, ConversationState state, int? currentYear = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var reading = FallbackReader.Read(Readable(message, state), state, currentYear ?? DateTime.UtcNow.Year);
        if (reading.ItemQuestion)
        {
            return ItemQuestion(reading.QuestionName, message, LlmCallMeta.None, null);
        }

        if (reading.Chitchat)
        {
            return Chitchat(LlmCallMeta.None, null);
        }

        return new PlanResult(
            reading.NamesSomething || !state.HoldsSearch ? TurnAct.NewRequest : TurnAct.Refine,
            ConversationStateDelta.None with
            {
                Concepts = reading.Concepts,
                Genres = reading.Genres,
                Moods = reading.Moods,
                Directions = reading.Directions,
                Years = reading.Years,
                Negations = reading.Negations,
                ReferenceNames = reading.ReferenceNames,
                Media = reading.Media,
            },
            [],
            [new ToolRequest(reading.ReferenceNames.Count > 0 ? AskTool.SimilarTo : AskTool.SearchCatalog)],
            null,
            [],
            PlanSource.CarriedForward,
            LlmCallMeta.None);
    }

    /// <summary>
    /// The words a fallback reads a request from: the message, less a clause that rejects a card on
    /// screen. "I didn't like the second one" was the new request "didn't", and "the second one is
    /// terrible" the topic "terrible" — each throwing the conversation's topic away (HREV6-ASK-02).
    /// The rejection itself is the sanitiser's to apply (<see cref="PlanSanitizer.RejectPointed"/>).
    /// </summary>
    private static string Readable(string message, ConversationState state) =>
        state.LastPresented.Count > 0 && AskReelPointer.Rejected(message) is not null
            ? AskReelPointer.WithoutRejectedClause(message)
            : message;

    /// <summary>
    /// A question about a card no model read: answered from the title's facts, the search left where it
    /// was (the conversation's media included). The pointer is resolved by the sanitiser's rule
    /// (<see cref="PlanSanitizer.PointFallback"/>), the subject by name when the message names one —
    /// "what is Better Call Saul about?" names it outright (HREV5-ASK-01).
    /// </summary>
    private static PlanResult ItemQuestion(string? name, string message, LlmCallMeta meta, PlanFailure? failure) =>
        new(
            TurnAct.ItemQuestion,
            ConversationStateDelta.None,
            [],
            [new ToolRequest(AskTool.TitleFacts, Name: name)],
            null,
            [],
            PlanSource.CarriedForward,
            meta,
            name is null ? null : message,
            failure);

    /// <summary>
    /// "What can you do?" read with no model: what Ask Reel does, in one line, and nothing searched —
    /// searched, it showed sixteen random unchecked titles (HREV5-ASK-01).
    /// </summary>
    private static PlanResult Chitchat(LlmCallMeta meta, PlanFailure? failure) =>
        new(TurnAct.Chitchat, ConversationStateDelta.None, [], [], null, [], PlanSource.CarriedForward, meta, null, failure);
}
