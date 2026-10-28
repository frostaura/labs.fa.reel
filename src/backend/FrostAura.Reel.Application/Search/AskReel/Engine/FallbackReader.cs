using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;

namespace FrostAura.Reel.Application.Search.AskReel.Engine;

/// <summary>
/// What a message says when no model read it — the deterministic reading both fallback plans
/// (<see cref="FallbackPlans"/>) are built from.
/// </summary>
/// <param name="ItemQuestion">The message asks about a card on screen ("is the first one violent?").</param>
/// <param name="Concepts">The topics it asks for, as phrases; never a comparison, a connective or a generic noun.</param>
/// <param name="Genres">Genres it asks for beside a topic ("spy thrillers": the thriller genre), or by a phrase that names one ("love stories").</param>
/// <param name="Moods">Moods it names: soft.</param>
/// <param name="Directions">Attribute steers its comparisons name ("funnier": humor, more): soft.</param>
/// <param name="Negations">The phrases it rules out, whole ("walking dead").</param>
/// <param name="ReferenceNames">Titles it asks for something like ("movies like Inception"), to be found by name.</param>
/// <param name="Years">The year window it names, or null.</param>
/// <param name="YearWords">The words that named the window.</param>
/// <param name="Media">The media its words name, or null.</param>
public sealed record FallbackReading(
    bool ItemQuestion,
    IReadOnlyList<string> Concepts,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Moods,
    IReadOnlyList<AttributeDirection> Directions,
    IReadOnlyList<string> Negations,
    IReadOnlyList<string> ReferenceNames,
    YearRange? Years,
    IReadOnlyList<string> YearWords,
    MediaScope? Media)
{
    /// <summary>True when the reading names a topic, a genre or a reference — something a new request is about.</summary>
    public bool NamesSomething => Concepts.Count > 0 || Genres.Count > 0 || ReferenceNames.Count > 0;
}

/// <summary>
/// The model-less reading of one message (HREV4-ASK-05, HREV4-RET-02). The keyless reader alone
/// turned every word it kept into a hard topic, and the fallback path is what every user gets the
/// moment the provider fails:
/// <list type="bullet">
/// <item>"is the first one violent?" searched the mood "violent" across every medium — 16 unchecked
/// titles, mostly shows, instead of an answer about the card;</item>
/// <item>"like John Wick but funnier" required "john wick" AND "funnier" as tags, "movies like
/// Inception" counted "2 inception movies", "older stuff is fine" required "older" and "fine";</item>
/// <item>"what about movies instead" searched "instead", "love stories" searched "stories", "spy
/// thrillers" required the tag "spy thriller" (32 titles) where the spy thriller shelf holds 180, and
/// "no walking dead" ruled out "walking" and "dead" as separate words.</item>
/// </list>
/// Here a question about a card is an item question; "like &lt;title&gt;" is a reference; a comparison
/// is a soft steer or nothing; a connective or a generic noun is never a topic; a genre word at the
/// end of a phrase is the genre the topic sits in; and a negation is the phrase the user wrote.
/// </summary>
public static class FallbackReader
{
    /// <summary>Words that open a question about something ("is it…", "how long…", "tell me…").</summary>
    private static readonly IReadOnlySet<string> QuestionLeads = new HashSet<string>(StringComparer.Ordinal)
    {
        "is", "was", "does", "did", "has", "are", "were", "can", "could", "would", "will", "how", "what",
        "who", "whose", "when", "where", "which", "why", "tell", "what's", "who's", "how's", "isn't", "doesn't",
    };

    /// <summary>Words that make a message a search near or away from a card, never a question about it.</summary>
    private static readonly IReadOnlySet<string> SearchCues = new HashSet<string>(StringComparer.Ordinal)
    {
        "like", "similar", "alike", "more", "less", "instead", "another", "else", "other", "others",
    };

    /// <summary>The comparisons a steer can carry, by the attribute they move (the planner's own six).</summary>
    private static readonly IReadOnlyDictionary<string, AttributeDirection> Comparisons =
        new Dictionary<string, AttributeDirection>(StringComparer.Ordinal)
        {
            ["funnier"] = new("humor", AttributeTrend.More),
            ["sillier"] = new("humor", AttributeTrend.More),
            ["goofier"] = new("humor", AttributeTrend.More),
            ["darker"] = new("darkness", AttributeTrend.More),
            ["grittier"] = new("darkness", AttributeTrend.More),
            ["bleaker"] = new("darkness", AttributeTrend.More),
            ["lighter"] = new("darkness", AttributeTrend.Less),
            ["brighter"] = new("darkness", AttributeTrend.Less),
            ["faster"] = new("pacing", AttributeTrend.More),
            ["quicker"] = new("pacing", AttributeTrend.More),
            ["pacier"] = new("pacing", AttributeTrend.More),
            ["snappier"] = new("pacing", AttributeTrend.More),
            ["slower"] = new("pacing", AttributeTrend.Less),
            ["scarier"] = new("intensity", AttributeTrend.More),
            ["tenser"] = new("intensity", AttributeTrend.More),
            ["intenser"] = new("intensity", AttributeTrend.More),
            ["gorier"] = new("intensity", AttributeTrend.More),
            ["bloodier"] = new("intensity", AttributeTrend.More),
            ["wilder"] = new("intensity", AttributeTrend.More),
            ["calmer"] = new("intensity", AttributeTrend.Less),
            ["gentler"] = new("intensity", AttributeTrend.Less),
            ["softer"] = new("intensity", AttributeTrend.Less),
            ["milder"] = new("intensity", AttributeTrend.Less),
            ["cosier"] = new("intensity", AttributeTrend.Less),
            ["cozier"] = new("intensity", AttributeTrend.Less),
            ["smarter"] = new("complexity", AttributeTrend.More),
            ["cleverer"] = new("complexity", AttributeTrend.More),
            ["deeper"] = new("complexity", AttributeTrend.More),
            ["trickier"] = new("complexity", AttributeTrend.More),
            ["simpler"] = new("complexity", AttributeTrend.Less),
            ["easier"] = new("complexity", AttributeTrend.Less),
            ["happier"] = new("optimism", AttributeTrend.More),
            ["cheerier"] = new("optimism", AttributeTrend.More),
            ["sweeter"] = new("optimism", AttributeTrend.More),
            ["sadder"] = new("optimism", AttributeTrend.Less),
            ["gloomier"] = new("optimism", AttributeTrend.Less),
        };

    /// <summary>
    /// Words that compare, concede or connect and name no topic: "older stuff is fine", "what about
    /// movies instead". The year and runtime comparisons are read where they belong (the year window,
    /// the conflict frame) and never as a tag a title must carry.
    /// </summary>
    private static readonly IReadOnlySet<string> NeverATopic = new HashSet<string>(StringComparer.Ordinal)
    {
        "older", "newer", "earlier", "later", "recent", "longer", "shorter", "better", "worse", "bigger", "smaller",
        "weirder", "fine", "okay", "alright", "sure", "cool", "instead", "else", "rather", "either", "whatever",
        "different", "another", "similar", "otherwise", "story", "stories", "tale", "tales", "flick", "flicks",
        "picture", "pictures", "options", "ideas", "recommendations", "suggestions", "content", "watching",
    };

    /// <summary>Phrases that name a genre outright but are not genre words on their own ("love" is a floor word).</summary>
    private static readonly IReadOnlyDictionary<string, string> GenrePhrases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["love story"] = "romance",
        ["love stories"] = "romance",
    };

    /// <summary>Words before "like" that make it "something like &lt;title&gt;" rather than the verb ("I'd like").</summary>
    private static readonly IReadOnlySet<string> LikeHeads = new HashSet<string>(StringComparer.Ordinal)
    {
        "movies", "movie", "films", "film", "shows", "show", "series", "something", "anything", "stuff", "more",
        "titles", "ones", "things", "else", "just", "kinda", "sort", "kind", "much",
    };

    /// <summary>Words that end a title named after "like": a new clause, a condition, a pointer.</summary>
    private static readonly IReadOnlySet<string> NameEnds = new HashSet<string>(StringComparer.Ordinal)
    {
        "but", "and", "with", "without", "except", "or", "only", "please", "though", "although", "yet", "that",
        "which", "who", "if", "for", "from", "in", "on", "minus", "no", "not", "nothing", "plus", "set",
    };

    /// <summary>Words that make "like …" a pointer at what is on screen, never a title to find.</summary>
    private static readonly IReadOnlySet<string> NotAName = new HashSet<string>(StringComparer.Ordinal)
    {
        "that", "this", "those", "these", "it", "them", "the", "one", "ones", "first", "second", "third", "fourth",
        "fifth", "last", "previous", "above", "before", "earlier", "a", "an", "some", "any", "what", "you",
    };

    /// <summary>Words after "the" that point at what is on screen or in the list rather than start a title.</summary>
    private static readonly IReadOnlySet<string> NotATheName = new HashSet<string>(StringComparer.Ordinal)
    {
        "rest", "others", "other", "same", "latter", "former", "next", "lot", "ones", "sort", "kind", "type",
        "kinds", "types", "sorts", "way", "vibe", "vibes", "feel", "tone", "style", "mood", "result", "results",
        "below", "more", "less", "movie", "movies", "film", "films", "show", "shows", "series", "title", "titles",
        "stuff", "things", "list", "pick", "picks", "suggestion", "suggestions", "option", "options", "card", "cards",
    };

    /// <summary>
    /// The reference name without a leading "the" — the words a name the catalogue does not hold is
    /// searched by ("movies like the zxqwv" searches "zxqwv").
    /// </summary>
    public static string WithoutArticle(string name) =>
        name.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? name[4..].TrimStart() : name;

    /// <summary>The media words, which end a title's name and never form a topic.</summary>
    private static readonly IReadOnlySet<string> MediaWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "movie", "movies", "film", "films", "show", "shows", "series", "tv", "title", "titles",
    };

    /// <summary>The message read with no model.</summary>
    public static FallbackReading Read(string? message, ConversationState state, int currentYear)
    {
        ArgumentNullException.ThrowIfNull(state);
        var request = AskReelIntentSanitizer.ReadKeylessRequest(message, currentYear);
        var negations = AskReelIntentSanitizer.KeylessNegatedPhrases(message);
        var media = PlanSanitizer.UserMedia(null, message);

        if (IsItemQuestion(message, state))
        {
            return new FallbackReading(true, [], [], [], [], [], [], null, [], null);
        }

        var words = AskReelIntentSanitizer.Words(message);
        var reference = ReferenceName(message);
        var referenceWords = reference is null ? [] : AskReelIntentSanitizer.Words(reference);

        var genres = GenresOfPhrases(words).ToList();

        var directions = words
            .Where(Comparisons.ContainsKey)
            .Select(w => Comparisons[w])
            .DistinctBy(d => d.Attribute)
            .ToList();

        var concepts = new List<string>();
        foreach (var phrase in request.Phrases)
        {
            var phraseWords = AskReelIntentSanitizer.Words(phrase);
            if (referenceWords.Count > 0 && phraseWords.All(referenceWords.Contains))
            {
                continue;
            }

            foreach (var run in Runs(phraseWords.Where(w => !referenceWords.Contains(w)).ToList()))
            {
                var (topic, runGenres) = SplitGenres(run);
                foreach (var g in runGenres.Where(g => !genres.Contains(g)))
                {
                    genres.Add(g);
                }

                if (topic is { Length: > 0 } t && !concepts.Contains(t, StringComparer.Ordinal))
                {
                    concepts.Add(t);
                }
            }
        }

        return new FallbackReading(
            false,
            concepts,
            genres,
            request.Moods,
            directions,
            negations,
            reference is null ? [] : [reference],
            request.Years,
            request.YearWords,
            media);
    }

    /// <summary>
    /// The genres <paramref name="words"/> name by a phrase that is no genre word on its own ("love
    /// story" is romance), in the order they appear. The sanitiser reads the user's words through this
    /// too, so a genre this reader found is never then dropped as a copied standing value (HREV5-ASK-02).
    /// </summary>
    internal static IReadOnlyList<string> GenresOfPhrases(IReadOnlyList<string> words)
    {
        var genres = new List<string>();
        for (var i = 0; i + 1 < words.Count; i++)
        {
            if (GenrePhrases.TryGetValue($"{words[i]} {words[i + 1]}", out var genre) && !genres.Contains(genre))
            {
                genres.Add(genre);
            }
        }

        return genres;
    }

    /// <summary>
    /// True when the message asks about a card on screen: question-shaped, pointing at a card by its
    /// place ("the first one") or naming a title the conversation showed, and not asking for more
    /// like it. "Is the first one violent?" is a question; "more like the first one" is a search.
    /// </summary>
    internal static bool IsItemQuestion(string? message, ConversationState state)
    {
        var words = AskReelIntentSanitizer.Words(message);
        if (words.Count == 0 || words.Any(SearchCues.Contains))
        {
            return false;
        }

        var question = (message ?? string.Empty).TrimEnd().EndsWith('?') || QuestionLeads.Contains(words[0]);
        if (!question || (words.Count > 1 && words[0] is "what" or "how" && words[1] == "about"))
        {
            return false;
        }

        return AskReelPointer.Ordinal(message) is not null
            || state.LastPresented.Any(p => PlanSanitizer.NamesTitle(message ?? string.Empty, p.Name));
    }

    /// <summary>
    /// The title a message asks for something like — "movies like Inception", "like John Wick but
    /// funnier", "something similar to Heat" — or null. "Like" counts at the start of the message or
    /// after a word that makes it a comparison ("movies like", "something like"), never as the verb
    /// ("I'd like a comedy"); the name runs to the next clause, condition or media word, and a pointer
    /// ("more like the second one") is not a name.
    /// </summary>
    internal static string? ReferenceName(string? message)
    {
        var tokens = Tokens(message);
        for (var i = 0; i < tokens.Count; i++)
        {
            var start = -1;
            if (tokens[i].Word == "like" && !tokens[i].Closes && (i == 0 || LikeHeads.Contains(tokens[i - 1].Word)))
            {
                start = i + 1;
            }
            else if (tokens[i].Word == "similar" && i + 1 < tokens.Count && tokens[i + 1].Word == "to" && !tokens[i + 1].Closes)
            {
                start = i + 2;
            }

            if (start < 0 || start >= tokens.Count)
            {
                continue;
            }

            // "the" opens a title as often as a pointer: "like the matrix" is a name, "like the second
            // one" is not. It joins the name when a name follows it — never a pointer or filler word
            // ("the other one", "the same", "the rest") — and a name the catalogue does not hold is
            // searched without it (HREV5-RET-02).
            if (tokens[start].Word == "the" && !tokens[start].Closes && start + 1 < tokens.Count
                && !NotAName.Contains(tokens[start + 1].Word) && !NotATheName.Contains(tokens[start + 1].Word))
            {
                // The article stays in the name; the loop below reads the rest.
            }
            else if (NotAName.Contains(tokens[start].Word))
            {
                continue;
            }

            var name = new List<string>();
            for (var j = start; j < tokens.Count && name.Count < 6; j++)
            {
                var word = tokens[j].Word;
                if (NameEnds.Contains(word) || MediaWords.Contains(word) || Comparisons.ContainsKey(word))
                {
                    break;
                }

                name.Add(word);
                if (tokens[j].Closes)
                {
                    break;
                }
            }

            // A name after "the" may be made of words that are filler elsewhere: "The Thing".
            if ((name.Count > 1 && name[0] == "the") || (name.Count > 0 && name[0] != "the" && name.Any(w => !AskReelIntentSanitizer.IsFiller(w))))
            {
                return string.Join(' ', name);
            }
        }

        return null;
    }

    /// <summary>The phrase's words split wherever a word names no topic, each run kept whole.</summary>
    private static IEnumerable<List<string>> Runs(IReadOnlyList<string> words)
    {
        var run = new List<string>();
        foreach (var word in words)
        {
            if (NeverATopic.Contains(word) || Comparisons.ContainsKey(word))
            {
                if (run.Count > 0)
                {
                    yield return run;
                }

                run = [];
                continue;
            }

            run.Add(word);
        }

        if (run.Count > 0)
        {
            yield return run;
        }
    }

    /// <summary>
    /// "spy thrillers" is the topic "spy" inside the thriller genre: the genre word that ENDS a phrase
    /// is its genre, split off once ("true crime documentaries" is "true crime" in documentary). A
    /// phrase made only of genre words stays whole — "romantic comedies" is already read as its genres
    /// downstream — and so does a curated concept phrase ("second world war").
    /// </summary>
    private static (string? Topic, IReadOnlyList<string> Genres) SplitGenres(IReadOnlyList<string> run)
    {
        var joined = string.Join(' ', run);
        // A curated concept phrase stays whole too: "second world war" is the World War II concept,
        // never "second world" in the war genre.
        if (run.Count < 2 || ConceptSynonyms.GenresNamedBy(joined) is { Count: > 0 }
            || ConceptSynonyms.GroupOfAnyForm(joined).Skip(1).Any()
            || ConceptSynonyms.GenreSlugsOf(run[^1]) is not { Count: > 0 } slugs)
        {
            return (joined, []);
        }

        return (string.Join(' ', run.Take(run.Count - 1)), slugs.Distinct(StringComparer.Ordinal).ToList());
    }

    private static List<(string Word, bool Closes)> Tokens(string? message)
    {
        var tokens = new List<(string Word, bool Closes)>();
        foreach (var token in (message ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var closes = token.EndsWith(',') || token.EndsWith('.') || token.EndsWith(';') || token.EndsWith('?') || token.EndsWith('!');
            var tokenWords = AskReelIntentSanitizer.Words(token);
            for (var i = 0; i < tokenWords.Count; i++)
            {
                tokens.Add((tokenWords[i], closes && i == tokenWords.Count - 1));
            }
        }

        return tokens;
    }
}
