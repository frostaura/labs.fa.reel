using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>
/// A user's rejection read as wide as the same word asked for (HREV5-ASK-04). "How many mafia titles
/// are left?" counts every title tagged with any stem of the mafia group (mafia, gangster, mob,
/// mobster, organized crime), yet "crime titles, no mafia" still showed The Wire and Sons of Anarchy
/// (organized crime), Snowfall (gangster) and Godfather of Harlem (mobster): the rejection matched
/// only the word "mafia". A negation must exclude everything the positive concept would include.
///
/// <para><b>What widens.</b> Only the curated <see cref="ConceptSynonyms.Groups"/> — a group is by
/// definition one concept — and only as keyword LABELS, the way a positive concept's synonyms match
/// (<see cref="HardConstraintFilter.NegatedConceptPredicate"/>): a synonym is a claim about TMDB's tags,
/// never about prose, so "no mafia" does not drop a film whose overview mentions "an angry mob". The
/// rejected word itself keeps its existing, wider reading (name, overview or tag;
/// <see cref="FilterBreaches.CarriesKeyword"/>). A genre word is not widened here: it is rejected as a
/// genre, with its aliases and stand-ins. Nothing reaches for embeddings: a rejection is airtight, and
/// "nearest by similarity" is not a definition anyone can check.</para>
///
/// <para><b>The user's own positive words win.</b> "Gangster films, no mafia" asks for gangsters and
/// rejects the mafia — one group, two sides. Widening the rejection over the group would answer
/// with nothing, so a group the request's own concepts name is not widened; the rejected word still
/// rules out what it says itself.</para>
/// </summary>
public static class NegatedConcepts
{
    /// <summary>Trailing words that add nothing to what is rejected: "no mafia stuff" rejects mafia.</summary>
    private static readonly HashSet<string> TrailingFiller = new(StringComparer.Ordinal)
    {
        "stuff", "things", "thing", "content", "related", "themes", "theme", "type", "types", "kind", "kinds", "plots", "plot",
    };

    /// <summary>
    /// The synonym stems the rejected words reach: every member of the curated group a rejected word
    /// (or its singular, or the word without a trailing filler) belongs to. Empty when no rejected word
    /// is in a group, so a rejection outside the table is read exactly as before. A group any of
    /// <paramref name="asked"/>'s own words belong to is left out (see the class remarks).
    /// </summary>
    public static IReadOnlyList<string> Stems(IEnumerable<string>? rejectedKeywords, IEnumerable<ConceptTerm>? asked = null)
    {
        var askedGroups = (asked ?? [])
            .SelectMany(c => Readings(ConceptResolver.Normalise(c.UserWords)))
            .Select(GroupOf)
            .OfType<string[]>()
            .ToList();
        var stems = new List<string>();
        foreach (var raw in rejectedKeywords ?? [])
        {
            foreach (var form in Readings(ConceptResolver.Normalise(raw)))
            {
                if (GroupOf(form) is { } group && !askedGroups.Any(g => ReferenceEquals(g, group)))
                {
                    stems.AddRange(group);
                }
            }
        }

        return stems.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The in-memory twin of <see cref="HardConstraintFilter.NegatedConceptPredicate"/>, for a caller
    /// that filters titles it has already loaded: true when one of <paramref name="title"/>'s keyword
    /// tags is reached by one of <paramref name="stems"/> (<see cref="Stems"/>), by the very rule a
    /// positive concept's tag is matched with (<see cref="HardConstraintFilter.KeywordMatches"/>). The
    /// name and the overview are never read: a synonym is a claim about the tags. A check of the
    /// rejected word alone misses its group — "crime dramas, no mafia stuff" would still top up a
    /// title tagged only "gangster" into a keyless turn's unchecked tray.
    /// </summary>
    public static bool Reaches(Title title, IReadOnlyList<string> stems)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(stems);
        return stems.Count > 0
            && title.Keywords.Any(keyword => stems.Any(stem => HardConstraintFilter.KeywordMatches(keyword?.ToLowerInvariant(), stem)));
    }

    /// <summary>The curated group <paramref name="form"/> (or its singular) belongs to; null for a genre word or no group.</summary>
    private static string[]? GroupOf(string form)
    {
        if (ConceptSynonyms.GenreSlugsOf(form) is not null)
        {
            return null;
        }

        foreach (var candidate in ConceptSynonyms.Singulars(form).Prepend(form))
        {
            foreach (var group in ConceptSynonyms.Groups)
            {
                if (group.Contains(candidate, StringComparer.Ordinal))
                {
                    return group;
                }
            }
        }

        return null;
    }

    /// <summary>The words as typed, then without any trailing filler ("mafia stuff" → "mafia").</summary>
    private static IEnumerable<string> Readings(string normal)
    {
        if (normal.Length == 0)
        {
            yield break;
        }

        yield return normal;
        var tokens = normal.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var trimmed = false;
        while (tokens.Count > 1 && TrailingFiller.Contains(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
            trimmed = true;
        }

        if (trimmed)
        {
            yield return string.Join(' ', tokens);
        }
    }
}
