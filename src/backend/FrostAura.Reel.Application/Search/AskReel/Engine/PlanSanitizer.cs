namespace FrostAura.Reel.Application.Search.AskReel.Engine;

/// <summary>
/// The server's half of the planner's rules (§4.5): what a prompt asks for, this guarantees.
///
/// <list type="bullet">
/// <item><b>References</b> only to handles the conversation actually showed — a handle the model
/// invented resolves to nothing.</item>
/// <item><b>Negations</b> only from the user's words: a negation none of whose forms appears in the
/// message is dropped, and so is one that merely restates a standing condition (those are applied by
/// eligibility and never stored in the state).</item>
/// <item><b>Concepts</b> never a negation cue, never the whole message echoed back, never filler or a
/// pointer ("the second one"), never a person or a title the plan names elsewhere.</item>
/// <item><b>At most one clarifying question per conversation.</b></item>
/// </list>
/// </summary>
public static class PlanSanitizer
{
    public static PlanResult Clean(PlanResult plan, string message, ConversationState state, FilterRuleSet standing)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(standing);
        if (plan.Failure is not null)
        {
            return plan;
        }

        var shown = state.LastPresented.Select(p => p.Handle).ToHashSet();
        var references = plan.References.Where(shown.Contains).ToList();

        var words = AskReelIntentSanitizer.Words(message);
        var standingValues = standing.ExcludeGenres.Concat(standing.ExcludeKeywords).ToList();
        bool Said(string term) =>
            AskReelIntentSanitizer.Words(term).All(w => words.Any(m => NegationTerms.EqualsAnyForm(m, w) || NegationTerms.EqualsAnyForm(w, m)));
        bool Standing(string term) => standingValues.Any(rule => NegationTerms.EqualsAnyForm(term, rule) || NegationTerms.EqualsAnyForm(rule, term));

        var delta = plan.Delta;
        var negations = delta.Negations.Where(n => Said(n) && !Standing(n)).ToList();
        var named = delta.People.Select(p => p.Name).Concat(delta.ReferenceNames).ToList();
        var concepts = delta.Concepts
            .Where(c => !AskReelIntentSanitizer.RejectedKeyword(c, message))
            .Where(c => !named.Any(n => string.Equals(n, c, StringComparison.OrdinalIgnoreCase)))
            // A concept the same message also negates is resolved by where it stands in the message:
            // the latest mention decides (the keyless reading's rule, applied to the model's output).
            .Where(c => !negations.Any(n => NegationTerms.EqualsAnyForm(c, n)) || AskedLast(message, c))
            .ToList();
        negations = negations.Where(n => !concepts.Any(c => NegationTerms.EqualsAnyForm(c, n))).ToList();

        var clarify = state.ClarifiedOnce ? null : plan.ClarifyQuestion;
        return plan with
        {
            References = references,
            Delta = delta with { Concepts = concepts, Negations = negations },
            ClarifyQuestion = clarify,
            ClarifyOptions = clarify is null ? [] : plan.ClarifyOptions.Take(3).ToList(),
        };
    }

    /// <summary>True when the keyless reading of <paramref name="message"/> ends with <paramref name="concept"/> asked FOR.</summary>
    private static bool AskedLast(string message, string concept)
    {
        var reading = AskReelIntentSanitizer.ReadKeyless(message);
        return reading.Positive.Any(p => NegationTerms.EqualsAnyForm(p, concept) || NegationTerms.EqualsAnyForm(concept, p));
    }
}
