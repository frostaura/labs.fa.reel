using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>
/// Reads a v1 <see cref="SearchIntent"/> as <see cref="HardConstraints"/>, so the current pipeline can
/// ask for a <see cref="SupplyReport"/> before the v2 planner exists.
///
/// <para>The mapping keeps the v1 meaning of the intent rather than inventing a stricter one. v1
/// keywords are alternative pulls for ONE topic ("ninja", "shinobi"), so they become one concept whose
/// labels OR together; AND-ing them would turn a synonym list into an impossible conjunction. Genres
/// become that concept only when there are no keywords: next to a keyword they are the interpreter's
/// guess at a flavour ("action" beside "ninja"), and requiring them would quietly drop the
/// non-action ninja films. Negations are not constraints at all — they are handled as exclusions by
/// the chat layer — so <see cref="SearchIntent.ExcludeGenres"/> and
/// <see cref="SearchIntent.ExcludeKeywords"/> are deliberately not read here.</para>
/// </summary>
public static class IntentConstraints
{
    /// <summary>The hard constraints a v1 intent expresses.</summary>
    /// <param name="intent">The interpreted intent.</param>
    /// <param name="requestMediaTypes">
    /// The request's own media filter (<see cref="AskMediaTypes.Resolve"/>); it wins over the intent's
    /// guess whenever it names anything, because it is what the user selected.
    /// </param>
    /// <param name="userWords">
    /// The user's own words for the topic, when the caller has them. Without them the concept is named
    /// by the keywords (or genres) joined, marked <see cref="ConceptTerm.StandInWords"/>, so the name arm
    /// never reads a curated synonym among them as something the user said (HREV3-RET-A). A caller
    /// holding only a label it built from the keywords passes null, not that label.
    /// </param>
    public static HardConstraints FromIntent(
        SearchIntent intent, IReadOnlyCollection<MediaType>? requestMediaTypes = null, string? userWords = null)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var keywords = Clean(intent.Keywords);
        var genres = Clean(intent.Genres);
        var said = string.IsNullOrWhiteSpace(userWords) ? null : userWords;
        var concepts = new List<ConceptTerm>();
        if (keywords.Count > 0)
        {
            concepts.Add(new ConceptTerm(said ?? string.Join(" ", keywords), keywords, [], StandInWords: said is null));
        }
        else if (genres.Count > 0)
        {
            concepts.Add(new ConceptTerm(said ?? string.Join(" ", genres), [], genres, StandInWords: said is null));
        }

        var people = (intent.People ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new PersonConstraint(p.Name.Trim(), null, p.Role))
            .ToList();

        var years = intent.MinYear is null && intent.MaxYear is null
            ? null
            : new YearRange(intent.MinYear, intent.MaxYear);

        var media = requestMediaTypes is { Count: > 0 }
            ? MediaScopes.From(requestMediaTypes)
            : MediaScopes.From(intent.MediaTypes);

        return new HardConstraints(concepts, people, years, intent.MaxRuntimeMinutes, media);
    }

    private static List<string> Clean(IEnumerable<string>? values) =>
        (values ?? [])
            .Select(v => v.Trim().ToLowerInvariant())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
