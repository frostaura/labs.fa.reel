using FrostAura.Reel.Application.Search.Retrieval;

namespace FrostAura.Reel.Application.Search.AskReel.Engine;

/// <summary>
/// How a plan changes the conversation's state (§4.6). Pure and deterministic, so every rule is
/// pinned by unit tests rather than by a model's good behaviour.
///
/// <para><b>What the planner can and cannot do to the state.</b> It proposes a delta; this class
/// decides what the delta means. A <see cref="TurnAct.NewRequest"/> replaces the requirements; every
/// other search act merges into them. Negations come only from the user's words, and the LATEST
/// mention of a word decides it: "none of these are ninja movies — I want ninja movies" asks for ninja
/// twice, so a word asked for again stops being a negation, and a word ruled out again stops being a
/// concept. Standing conditions never enter the state at all; the planner is told so, and the
/// sanitiser upstream strips any it copies anyway.</para>
/// </summary>
public static class ConversationStateReducer
{
    /// <summary>How many answers' cards the state keeps as <see cref="ConversationState.LastPresented"/>.</summary>
    public const int PresentedAnswers = 3;

    /// <summary>The state after <paramref name="plan"/>, before this turn's own cards are known.</summary>
    /// <param name="mediaFilter">The client's explicit media filter, which outranks the plan's.</param>
    public static ConversationState Apply(ConversationState state, PlanResult plan, MediaScope mediaFilter)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plan);

        if (!TurnActs.Searches(plan.Act))
        {
            // A question about a card or small talk leaves the search exactly where it was.
            return state with { Version = state.Version + 1 };
        }

        var delta = plan.Delta;
        var replace = plan.Act == TurnAct.NewRequest;
        var baseHard = replace ? HardConstraints.None : state.Hard;
        var baseSoft = replace ? SoftPreferences.None : state.Soft;
        var baseNegations = replace ? [] : state.UserNegations;

        var conceptWords = Clean(delta.Concepts);
        var negationWords = Clean(delta.Negations);

        // The latest mention decides: a word asked for in this message is no longer ruled out, and a
        // word ruled out in this message is no longer asked for.
        var negations = baseNegations
            .Where(n => !conceptWords.Any(c => NegationTerms.EqualsAnyForm(c, n) || NegationTerms.EqualsAnyForm(n, c)))
            .Concat(negationWords)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var keptConcepts = baseHard.Concepts
            .Where(c => !negationWords.Any(n => NegationTerms.EqualsAnyForm(c.UserWords, n) || NegationTerms.EqualsAnyForm(n, c.UserWords)))
            .ToList();

        var concepts = Merge(keptConcepts, conceptWords.Select(w => new ConceptTerm(w, [], [])));
        concepts = Merge(concepts, Clean(delta.Genres).Select(g => new ConceptTerm(g, [], [g])));

        var people = baseHard.People
            .Concat(delta.People.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
            .DistinctBy(p => (p.Name.Trim().ToLowerInvariant(), p.Role))
            .ToList();

        var media = mediaFilter != MediaScope.Any ? mediaFilter : delta.Media ?? baseHard.Media;
        var hard = new HardConstraints(
            concepts,
            people,
            delta.Years ?? baseHard.Years,
            delta.MaxRuntimeMinutes ?? baseHard.MaxRuntimeMinutes,
            media);

        var directions = baseSoft.Directions
            .Where(d => !delta.Directions.Any(n => string.Equals(n.Attribute, d.Attribute, StringComparison.OrdinalIgnoreCase)))
            .Concat(delta.Directions)
            .ToList();
        var moods = baseSoft.Moods.Concat(Clean(delta.Moods)).Distinct(StringComparer.Ordinal).ToList();

        var references = ResolveHandles(plan.References, state.LastPresented);
        var rejected = state.Rejected.ToList();
        var referenceIds = baseSoft.ReferenceTitleIds.ToList();
        var steers = delta.Directions.Count > 0 || delta.Moods.Count > 0 || delta.Concepts.Count > 0 || delta.Genres.Count > 0;
        switch (plan.Act)
        {
            case TurnAct.CritiqueSet:
                // "None of these are X": every card the newest answer showed is rejected, and X is a
                // requirement from now on (it is in the concepts above, never in the negations).
                rejected.AddRange(NewestAnswer(state.LastPresented).Select(p => p.TitleId));
                break;
            case TurnAct.CritiqueItem when plan.RejectsReferences || !steers:
            case not TurnAct.CritiqueItem when plan.RejectsReferences:
                // "Not the first one", "I didn't like the second one": the card is rejected and nothing
                // is searched near it — as a reference it steered the search TOWARDS the card the user
                // turned down, and the judge was told "Like: <that card>" (HREV6-ASK-02).
                rejected.AddRange(references);
                referenceIds.RemoveAll(references.Contains);
                break;
            case TurnAct.CritiqueItem:
                // "More heart than T2.1": the critiqued titles are not shown again, and the direction
                // is read relative to them, so they become the reference the soft steer pivots on.
                rejected.AddRange(references);
                referenceIds = references.ToList();
                break;
            default:
                // "More like the second one": a reference is searched NEAR, and never returned.
                referenceIds.AddRange(references);
                break;
        }

        return state with
        {
            Version = state.Version + 1,
            Hard = hard,
            Soft = new SoftPreferences(moods, directions, referenceIds.Distinct().ToList()),
            UserNegations = negations,
            Rejected = rejected.Distinct().ToList(),
        };
    }

    /// <summary>
    /// "More like the third one" is a PIVOT to that card (HREV1-RET-02): the search runs near the
    /// referenced titles (<c>similar_to</c>), and the earlier turn's person and topics stop being hard
    /// filters unless this message restates them. After "Liam Neeson movies", "more like the third
    /// one" kept Liam Neeson as a hard filter, so retrieval stayed inside his filmography minus the
    /// four titles already shown, nothing was eligible, and the turn dead-ended with relaxations about
    /// the Liam Neeson pool instead of titles like the one pointed at. Media, years, runtime and the
    /// user's own negations still hold — they narrow what "like it" may be, they do not replace it.
    /// Only a <see cref="TurnAct.Refine"/> whose references resolved, in a message that asks for
    /// likeness; a critique ("less dark than T2.1") steers relative to the card and keeps the search.
    /// </summary>
    public static ConversationState SimilarPivot(ConversationState next, PlanResult plan, string? message)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(plan);
        var words = AskReelIntentSanitizer.Words(message);
        if (plan.Act != TurnAct.Refine
            || ResolveHandles(plan.References, next.LastPresented).Count == 0
            || !words.Any(LikenessWords.Contains))
        {
            return next;
        }

        var concepts = next.Hard.Concepts
            .Where(c => NegationTerms.MatchesPhrase(message ?? string.Empty, c.UserWords)
                        || c.Genres.Any(g => NegationTerms.MatchesPhrase(message ?? string.Empty, g)))
            .ToList();
        var people = next.Hard.People
            .Where(p => AskReelIntentSanitizer.Words(p.Name).Any(n => n.Length >= 3 && words.Contains(n)))
            .ToList();
        return next with { Hard = next.Hard with { Concepts = concepts, People = people } };
    }

    /// <summary>The words that ask for titles LIKE a card: "more like the third one", "something similar to T1.2".</summary>
    private static readonly IReadOnlySet<string> LikenessWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "like", "similar", "alike", "resembling", "resembles",
    };

    /// <summary>The state with this turn's cards as the newest answer, keeping the last <see cref="PresentedAnswers"/> answers.</summary>
    public static ConversationState Presented(ConversationState state, IReadOnlyList<PresentedRef> thisAnswer)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (thisAnswer.Count == 0)
        {
            return state;
        }

        var keep = state.LastPresented
            .Select(p => p.Handle.Turn)
            .Where(t => t != thisAnswer[0].Handle.Turn)
            .Distinct()
            .OrderByDescending(t => t)
            .Take(PresentedAnswers - 1)
            .ToHashSet();
        return state with
        {
            LastPresented = thisAnswer
                .Concat(state.LastPresented.Where(p => keep.Contains(p.Handle.Turn)))
                .OrderByDescending(p => p.Handle.Turn)
                .ThenBy(p => p.Handle.Rank)
                .ToList(),
        };
    }

    /// <summary>The titles <paramref name="handles"/> name, in the order named; a handle nothing was shown under is dropped.</summary>
    public static IReadOnlyList<Guid> ResolveHandles(IEnumerable<TurnHandle> handles, IReadOnlyList<PresentedRef> presented) =>
        handles
            .Select(h => presented.FirstOrDefault(p => p.Handle == h)?.TitleId)
            .OfType<Guid>()
            .Distinct()
            .ToList();

    /// <summary>The newest answer's cards, in display order.</summary>
    public static IReadOnlyList<PresentedRef> NewestAnswer(IReadOnlyList<PresentedRef> presented)
    {
        if (presented.Count == 0)
        {
            return [];
        }

        var newest = presented.Max(p => p.Handle.Turn);
        return presented.Where(p => p.Handle.Turn == newest).OrderBy(p => p.Handle.Rank).ToList();
    }

    private static List<ConceptTerm> Merge(IEnumerable<ConceptTerm> existing, IEnumerable<ConceptTerm> added)
    {
        var merged = existing.ToList();
        foreach (var term in added)
        {
            if (!merged.Any(c => NegationTerms.EqualsAnyForm(c.UserWords, term.UserWords)
                                 || NegationTerms.EqualsAnyForm(term.UserWords, c.UserWords)))
            {
                merged.Add(term);
            }
        }

        return merged;
    }

    private static List<string> Clean(IEnumerable<string> words) =>
        words
            .Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
