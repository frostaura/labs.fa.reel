using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// The request's topic, resolved ONCE — the keywords the supply counts and the words the reply
/// names them by, from the same list, so the two can never describe different shelves.
///
/// <para><b>Why it exists (R1-02).</b> The supply report counted the UNION of every keyword the model
/// wrote, while the reply named only the first: the ninja follow-up came back as
/// <c>["ninja", "martial arts", "ninja warrior"]</c>, the supply counted 543 titles across all three,
/// and the reply told the founder "You have 4 unseen ninja movies tagged that way" — after the turn
/// before had said, correctly, that he had none.</para>
///
/// <para><b>The user's words decide.</b> The model's keywords are alternative pulls, and it widens
/// them on its own initiative ("martial arts" beside "ninja"). Discovery may use every one of them;
/// the topic that is COUNTED and NAMED is the subset the user actually said — in this message, or,
/// for a follow-up that names no topic ("more like that"), in the conversation's earlier asks, newest
/// first. Only when the user's words anchor none of them is the model's whole list the topic, and
/// then the reply names every keyword it counted ("swordplay or warrior movies") rather than
/// pretending one of them stands for the rest.</para>
/// </summary>
public static class AskReelTopic
{
    /// <summary>A resolved topic: the keywords counted, and the words the reply calls them by.</summary>
    /// <param name="Keywords">The keywords the topic predicate counts, lower-cased; empty for no topic.</param>
    /// <param name="Label">"ninja", "ninja or shinobi", "swordplay, warrior or ronin"; null for no topic.</param>
    /// <param name="Anchored">Whether the user's own words named these keywords.</param>
    public sealed record Resolved(IReadOnlyList<string> Keywords, string? Label, bool Anchored);

    /// <summary>No topic at all.</summary>
    public static Resolved None { get; } = new([], null, false);

    /// <summary>
    /// The topic of a turn: the model's <paramref name="keywords"/> narrowed to the ones the user said,
    /// looking first at <paramref name="message"/> and then back through the user's earlier asks in
    /// <paramref name="history"/>.
    /// </summary>
    public static Resolved Resolve(IReadOnlyList<string>? keywords, string? message, IReadOnlyList<ChatTurn>? history)
    {
        var cleaned = (keywords ?? [])
            .Select(k => k.Trim().ToLowerInvariant())
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (cleaned.Count == 0)
        {
            return None;
        }

        var asks = new List<string>();
        if (!string.IsNullOrWhiteSpace(message))
        {
            asks.Add(message);
        }

        asks.AddRange((history ?? [])
            .Where(t => t.Role == "user" && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text)
            .Reverse());

        foreach (var ask in asks)
        {
            var anchored = cleaned.Where(k => NegationTerms.MatchesPhrase(ask, k)).ToList();
            if (anchored.Count > 0)
            {
                return new Resolved(anchored, Label(anchored), Anchored: true);
            }
        }

        return new Resolved(cleaned, Label(cleaned), Anchored: false);
    }

    /// <summary>"a", "a or b", "a, b or c" — every keyword counted, named.</summary>
    internal static string Label(IReadOnlyList<string> keywords) =>
        keywords.Count switch
        {
            0 => string.Empty,
            1 => keywords[0],
            _ => $"{string.Join(", ", keywords.Take(keywords.Count - 1))} or {keywords[^1]}",
        };

    /// <summary>
    /// The words that name the shelf a request's supply is counted over: the person, when one is
    /// set, then every hard concept in the order they were intersected — "romantic comedy",
    /// "Liam Neeson thriller" — or null when the request names neither.
    ///
    /// <para><b>Why every concept (HREV2-ASK-01).</b> The supply counts the AND of every hard
    /// concept and every person, and the reply named only the first concept: "romantic comedies"
    /// planned as romantic, comedy and romance, was counted as all three and described as "the 145
    /// romantic titles in the catalogue" — a shelf the count was never taken over, and a different
    /// number again when the plan ordered the concepts differently. A count and its label come from
    /// the same constraints, or the sentence is false.</para>
    ///
    /// <para><b>A concept that restates another is named once.</b> "romantic" and "romance" both name
    /// the romance genre; the intersection of the two is that genre, so the second adds no word
    /// ("romantic comedy", never "romantic comedy romance"). So does a concept whose words already
    /// stand inside a kept one ("comedy" beside "romantic comedy").</para>
    /// </summary>
    public static string? Shelf(Retrieval.HardConstraints hard)
    {
        ArgumentNullException.ThrowIfNull(hard);
        var words = new List<string>(hard.Concepts.Count);
        var genreSets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var concept in hard.Concepts)
        {
            var said = concept.UserWords?.Trim();
            if (string.IsNullOrEmpty(said))
            {
                continue;
            }

            if (words.Any(kept => NegationTerms.MatchesPhrase(kept, said) || NegationTerms.MatchesPhrase(said, kept)))
            {
                // One already standing inside the other: keep the longer phrase, in the earlier place.
                var index = words.FindIndex(kept => NegationTerms.MatchesPhrase(said, kept));
                if (index >= 0 && said.Length > words[index].Length)
                {
                    words[index] = said;
                }

                continue;
            }

            if (GenreSet(concept) is { } genres && !genreSets.Add(genres))
            {
                continue;
            }

            words.Add(said);
        }

        // A required language leads the shelf's name, in the user's words: "korean thrillers".
        var languages = hard.LanguageTerms
            .Select(l => l.UserWords.Trim())
            .Where(w => w.Length > 0 && !words.Any(kept => NegationTerms.MatchesPhrase(kept, w)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        words.InsertRange(0, languages);

        var people = hard.People.Select(p => p.Name.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var parts = new List<string>(2);
        if (people.Count > 0)
        {
            parts.Add(string.Join(" and ", people));
        }

        if (words.Count > 0)
        {
            parts.Add(string.Join(' ', words));
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>The genres a concept stands for — its own, else the ones its words name — as one key; null when none.</summary>
    private static string? GenreSet(Retrieval.ConceptTerm concept)
    {
        var genres = concept.Genres.Count > 0
            ? concept.Genres
            : Retrieval.Concepts.ConceptSynonyms.GenresNamedBy(Retrieval.Concepts.ConceptResolver.Normalise(concept.UserWords)) ?? [];
        var cleaned = genres
            .Select(g => g.Trim().ToLowerInvariant())
            .Where(g => g.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return cleaned.Count == 0 ? null : string.Join('|', cleaned);
    }
}
