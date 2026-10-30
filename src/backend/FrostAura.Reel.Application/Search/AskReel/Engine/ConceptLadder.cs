using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;

namespace FrostAura.Reel.Application.Search.AskReel.Engine;

/// <summary>
/// The relaxation ladder of a resolved request (§4.8: "before NoneFound, soft terms may widen"): the
/// request itself, then the request with its least-anchoring concept dropped, and so on down to its
/// anchor. Pure, so every rule is pinned by unit tests.
///
/// <para><b>Why it exists.</b> The planner writes every descriptive word it hears as a requirement:
/// "big action movies" became the action genre AND a keyword "big" (two unseen titles, both judged
/// no), and "space movies about isolation and silence" AND-ed three keywords into an empty set — while
/// 66 and 4 titles clearing the founder's bar sat one rung up. A word read as a requirement it never
/// was must not empty the screen; the judge, which reads the whole message, still decides what
/// satisfies it, so a rung widens recall and never precision.</para>
///
/// <para><b>What is never dropped:</b> a person, a language, the media, the years and the runtime —
/// each is a statement about the title the user made in so many words — and, when no person or language
/// anchors the request, its genres and its last concept (dropping those would be the "always fill" rung
/// §4.13 deletes; "romantic comedies" is both genres or nothing). Under a person or a language a genre
/// is a description of their work and may go: "Jason Statham action movies" runs dry at Statham.</para>
///
/// <para><b>The judge is never relaxed.</b> A relaxed rung changes what is RETRIEVED; the judge still
/// reads the whole request as the user put it, so a title from a wider rung is a pick only when it
/// satisfies what was asked.</para>
///
/// <para><b>Which concept goes first:</b> a keyword before a genre (a genre is a shelf, a keyword
/// describes something on it: "documentaries about space", "nature documentary series"), a concept the
/// user's NEWEST message does not say before one it does ("now spy movies" after "hitman movies" keeps
/// spy), and among the rest the one said last ("space movies about isolation and silence" keeps space).</para>
///
/// <para><b>Which rung an empty answer reports</b> (<see cref="ReportRung"/>): the broadest rung that
/// is itself empty — the level at which the request runs dry. "Documentaries about space" against an
/// excluded documentary genre is empty at "documentaries", so the reply counts documentaries and
/// offers the documentary relaxation (997), not the 27 the conjunction would; "zombie westerns" with
/// westerns to spare is empty only as zombie westerns, and is reported as such.</para>
/// </summary>
public static class ConceptLadder
{
    /// <summary>
    /// The rungs of <paramref name="resolved"/>, from the request itself to its anchor. A request with
    /// nothing to drop has one rung.
    /// </summary>
    public static IReadOnlyList<HardConstraints> Rungs(HardConstraints resolved, string? message)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        var rungs = new List<HardConstraints> { resolved };
        var anchored = resolved.People.Count > 0 || resolved.LanguageTerms.Count > 0;
        var kept = Priority(resolved.Concepts, message).ToList();

        // Without a person or a language to stand on, a genre is the shelf itself and is never dropped
        // ("romantic comedies" is romance AND comedy, both or nothing), and the last concept stays.
        var keep = anchored ? 0 : Math.Max(1, kept.Count(i => IsGenre(resolved.Concepts[i])));
        while (kept.Count > keep)
        {
            kept.RemoveAt(kept.Count - 1);
            var indices = kept.ToHashSet();
            rungs.Add(resolved with { Concepts = resolved.Concepts.Where((_, i) => indices.Contains(i)).ToList() });
        }

        return rungs;
    }

    /// <summary>
    /// Given whether each rung of <see cref="Rungs"/> has offerable supply, the rung to search (the
    /// first that has some, else the request itself) and the rung an empty answer reports (the last
    /// empty rung before the first that has some, else the anchor).
    /// </summary>
    public static (int Search, int Report) Choose(IReadOnlyList<bool> offerable)
    {
        ArgumentNullException.ThrowIfNull(offerable);
        for (var i = 0; i < offerable.Count; i++)
        {
            if (offerable[i])
            {
                return (i, Math.Max(0, i - 1));
            }
        }

        return (0, Math.Max(0, offerable.Count - 1));
    }

    /// <summary>The words a relaxed rung no longer requires, for the turn record.</summary>
    public static IReadOnlyList<string> Dropped(HardConstraints from, HardConstraints to) =>
        [.. from.Concepts.Where(c => !to.Concepts.Contains(c)).Select(c => c.UserWords)];

    /// <summary>The concepts' indices, most anchoring first (see the type remarks).</summary>
    internal static IEnumerable<int> Priority(IReadOnlyList<ConceptTerm> concepts, string? message)
    {
        var said = ConceptResolver.Normalise(message);
        return concepts
            .Select((concept, index) => (Index: index, Genre: IsGenre(concept), Position: PositionIn(said, concept)))
            .OrderBy(c => c.Genre ? 0 : 1)
            .ThenBy(c => c.Position < 0 ? 1 : 0)
            .ThenBy(c => c.Position < 0 ? c.Index : c.Position)
            .ThenBy(c => c.Index)
            .Select(c => c.Index);
    }

    /// <summary>A concept that is a genre and nothing else: its shelf is the genre.</summary>
    private static bool IsGenre(ConceptTerm concept) =>
        HardConstraintFilter.GenresOf(concept).Count > 0 && HardConstraintFilter.LabelsOf(concept).Count == 0;

    /// <summary>Where the concept's words first stand in the message, or -1.</summary>
    private static int PositionIn(string said, ConceptTerm concept)
    {
        var words = ConceptResolver.Normalise(concept.UserWords);
        if (said.Length == 0 || words.Length == 0)
        {
            return -1;
        }

        foreach (var token in words.Split(' ', StringSplitOptions.RemoveEmptyEntries).Prepend(words))
        {
            foreach (var form in ConceptSynonyms.Singulars(token).Prepend(token))
            {
                var at = IndexOfWord(said, form);
                if (at >= 0)
                {
                    return at;
                }
            }
        }

        return -1;
    }

    private static int IndexOfWord(string text, string word)
    {
        var from = 0;
        while (from <= text.Length - word.Length)
        {
            var at = text.IndexOf(word, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return -1;
            }

            var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            if (before)
            {
                return at;
            }

            from = at + 1;
        }

        return -1;
    }
}
