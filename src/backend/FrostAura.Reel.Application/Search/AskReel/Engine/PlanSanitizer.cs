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
/// <item><b>A pointer resolves to the rank the user named</b> ("the second one", "#3", "the last
/// one"): filled from the newest answer when the plan named no handle, moved to that rank when it named
/// one at another (R1-30).</item>
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
        var references = Pointed(plan.Act, plan.References.Where(shown.Contains).ToList(), message, state.LastPresented);

        var words = AskReelIntentSanitizer.Words(message);
        var standingValues = standing.ExcludeGenres.Concat(standing.ExcludeKeywords).ToList();
        bool Said(string term) =>
            AskReelIntentSanitizer.Words(term).All(w => words.Any(m => NegationTerms.EqualsAnyForm(m, w) || NegationTerms.EqualsAnyForm(w, m)));
        bool Standing(string term) => standingValues.Any(rule => NegationTerms.EqualsAnyForm(term, rule) || NegationTerms.EqualsAnyForm(rule, term));

        var delta = plan.Delta;
        var negations = delta.Negations.Where(n => Said(n) && !Standing(n)).ToList();
        var named = delta.People.Select(p => p.Name).Concat(delta.ReferenceNames).ToList();
        var concepts = delta.Concepts
            .Where(c => !Rejected(c, message))
            .Where(c => !named.Any(n => string.Equals(n, c, StringComparison.OrdinalIgnoreCase)))
            // A concept the same message also negates is resolved by where it stands in the message:
            // the latest mention decides (the keyless reading's rule, applied to the model's output).
            .Where(c => !negations.Any(n => NegationTerms.EqualsAnyForm(c, n)) || AskedLast(message, c))
            .ToList();
        negations = negations.Where(n => !concepts.Any(c => NegationTerms.EqualsAnyForm(c, n))).ToList();

        // The user's own media word is never lost to a plan that left media unset: "how many ninja
        // movies are left?" was counted over movies AND shows. Only a message naming exactly one.
        var mediaNamed = Retrieval.MediaScopes.From(AskReelCarry.MediaNamed(message));
        var media = delta.Media ?? (mediaNamed == Retrieval.MediaScope.Any ? null : mediaNamed);

        var clarify = state.ClarifiedOnce ? null : plan.ClarifyQuestion;
        return plan with
        {
            References = references,
            Delta = delta with { Concepts = concepts, Negations = negations, Media = media },
            ClarifyQuestion = clarify,
            ClarifyOptions = clarify is null ? [] : plan.ClarifyOptions.Take(3).ToList(),
        };
    }

    /// <summary>The acts a pointer at a card belongs to; anything else keeps the plan's references as they are.</summary>
    private static readonly IReadOnlySet<TurnAct> PointingActs = new HashSet<TurnAct>
    {
        TurnAct.Refine, TurnAct.CritiqueItem, TurnAct.ItemQuestion,
    };

    /// <summary>
    /// Words that say the pointer is at an EARLIER answer ("the second one you showed me first time
    /// round", "the first one from before"). Which answer that is, is the model's to read from the
    /// handles; with none named, nothing is filled.
    /// </summary>
    private static readonly IReadOnlySet<string> EarlierCues = new HashSet<string>(StringComparer.Ordinal)
    {
        "earlier", "before", "previous", "previously", "originally", "round", "ago", "time",
    };

    /// <summary>
    /// The references with the user's pointer honoured (R1-30). "More like the second one" resolved
    /// once in six live runs: the planner — at effort none — mostly returned no handle at all, and the
    /// turn then searched the conversation's topic instead of near the card. The RANK is in the user's
    /// words, so it is never left to the model:
    /// <list type="bullet">
    /// <item>no handle named: the newest answer's card at that rank, unless the message points at an
    /// earlier answer (that choice is the model's) or the newest answer has no card there;</item>
    /// <item>exactly one handle named at another rank: the card at the named rank in the SAME answer,
    /// when that answer has one — the model's choice of answer stands, its miscount does not;</item>
    /// <item>two or more handles, a message with no pointer, or an act a pointer does not belong to:
    /// untouched.</item>
    /// </list>
    /// </summary>
    internal static List<TurnHandle> Pointed(
        TurnAct act, List<TurnHandle> references, string message, IReadOnlyList<PresentedRef> presented)
    {
        if (!PointingActs.Contains(act) || references.Count > 1 || presented.Count == 0
            || AskReelPointer.Ordinal(message) is not { } position)
        {
            return references;
        }

        if (references.Count == 0 && AskReelIntentSanitizer.Words(message).Any(EarlierCues.Contains))
        {
            return references;
        }

        var answer = references.Count == 1 ? references[0].Turn : presented.Max(p => p.Handle.Turn);
        var ranks = presented.Where(p => p.Handle.Turn == answer).Select(p => p.Handle.Rank).ToList();
        if (ranks.Count == 0)
        {
            return references;
        }

        var rank = position == AskReelPointer.Last ? ranks.Max() : position;
        return ranks.Contains(rank) ? [new TurnHandle(answer, rank)] : references;
    }

    /// <summary>The words a message of at most this many is allowed to be its own topic in.</summary>
    internal const int ShortTopicWords = 4;

    /// <summary>
    /// A concept is rejected by the v1 keyword rule (a negation cue, filler, a pointer, the whole
    /// message echoed) — except that a SHORT message which is exactly its topic keeps it: "survival at
    /// sea" is the request, not an echo of one. The echo rule was written for a sentence searched as a
    /// keyword ("something brooding and futuristic for tonight"); applied to a three-word topic it
    /// emptied the request, and an empty request judged sixteen random titles to a none-found.
    /// </summary>
    private static bool Rejected(string concept, string message)
    {
        if (!AskReelIntentSanitizer.RejectedKeyword(concept, message))
        {
            return false;
        }

        var words = AskReelIntentSanitizer.Words(concept);
        var whole = AskReelIntentSanitizer.Words(message);
        var isItsOwnTopic = whole.Count <= ShortTopicWords && words.SequenceEqual(whole, StringComparer.Ordinal);
        // Every other reason still applies: judged as if no message were there to echo.
        return !isItsOwnTopic || AskReelIntentSanitizer.RejectedKeyword(concept, string.Empty);
    }

    /// <summary>True when the keyless reading of <paramref name="message"/> ends with <paramref name="concept"/> asked FOR.</summary>
    private static bool AskedLast(string message, string concept)
    {
        var reading = AskReelIntentSanitizer.ReadKeyless(message);
        return reading.Positive.Any(p => NegationTerms.EqualsAnyForm(p, concept) || NegationTerms.EqualsAnyForm(concept, p));
    }
}
