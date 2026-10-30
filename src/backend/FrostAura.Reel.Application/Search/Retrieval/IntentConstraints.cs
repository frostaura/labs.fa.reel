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
/// non-action ninja films. Alone, they OR as a flavour list too, except where the user's own words are
/// a genre phrase ("romantic comedies"), which ANDs its genres. Negations are not constraints at all — they are handled as exclusions by
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
    /// <param name="message">
    /// The user's turn as typed. It is read only for a genres-only intent, which has no keyword to label
    /// it: the one genre phrase in the turn that names exactly the intent's genres ("a 90s romantic
    /// comedy" → "romantic comedy") stands for the user's words, so a romcom ANDs its genres instead of
    /// counting every comedy and every romance (HREV8-ASK-01). <paramref name="userWords"/> wins over it.
    /// </param>
    public static HardConstraints FromIntent(
        SearchIntent intent,
        IReadOnlyCollection<MediaType>? requestMediaTypes = null,
        string? userWords = null,
        string? message = null)
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
            concepts.AddRange(GenreConcepts(genres, said ?? Concepts.ConceptResolver.GenrePhraseIn(message, genres)));
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

    /// <summary>
    /// A genres-only intent. The interpreter's genres are a flavour list ("scary films" → horror,
    /// thriller) that OR together, as v1 discovery reads them (TMDB's <c>with_genres</c> pipe) — unless
    /// the user's own words are nothing but genre words naming two or more genres without an "or"
    /// ("romantic comedies"): then each named genre is a concept of its own and they AND, the reading
    /// <see cref="Concepts.ConceptResolver.GenreReading"/> gives the same words anywhere else
    /// (HREV8-ASK-01). The user's words are the caller's topic label or, failing that, the genre phrase
    /// in their turn (<see cref="Concepts.ConceptResolver.GenrePhraseIn"/>). Without either, the
    /// stand-in names the OR it counts ("romance or comedy"), never a phrase that reads as both genres
    /// at once.
    /// </summary>
    private static IEnumerable<ConceptTerm> GenreConcepts(List<string> genres, string? said)
    {
        if (said is not null
            && Concepts.ConceptResolver.GenreReading(new ConceptTerm(said, [], [])) is { Count: > 1 } together)
        {
            return together;
        }

        var standIn = genres.Count == 1
            ? genres[0]
            : $"{string.Join(", ", genres.Take(genres.Count - 1))} or {genres[^1]}";
        return [new ConceptTerm(said ?? standIn, [], genres, StandInWords: said is null)];
    }

    private static List<string> Clean(IEnumerable<string>? values) =>
        (values ?? [])
            .Select(v => v.Trim().ToLowerInvariant())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
