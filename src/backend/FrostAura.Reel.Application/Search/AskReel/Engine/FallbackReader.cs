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
/// <param name="Chitchat">The message asks what Ask Reel is or does ("what can you do?"), or greets it: no search.</param>
/// <param name="QuestionName">For an item question that names its title ("what is Better Call Saul about?"), that name.</param>
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
    MediaScope? Media,
    bool Chitchat = false,
    string? QuestionName = null)
{
    /// <summary>A question about one title, named or pointed at.</summary>
    public static FallbackReading Question(string? name) => new(true, [], [], [], [], [], [], null, [], null, QuestionName: name);

    /// <summary>Small talk about Ask Reel itself.</summary>
    public static FallbackReading Talk { get; } = new(false, [], [], [], [], [], [], null, [], null, Chitchat: true);

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
        "actually", "honestly",
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

    /// <summary>
    /// The message read with no model. Before any topic is read: a question about a card or a title
    /// is an item question, a question about Ask Reel itself is small talk, and a hyphenated genre word
    /// ("sci-fi") is kept whole. A word a negation cue governs ("nothing with superheroes", "without
    /// aliens") is a negation and never also a topic (HREV5-ASK-01).
    /// </summary>
    public static FallbackReading Read(string? message, ConversationState state, int currentYear)
    {
        ArgumentNullException.ThrowIfNull(state);
        var (text, compounds) = JoinGenreCompounds(message);
        if (IsItemQuestion(text, state))
        {
            return FallbackReading.Question(null);
        }

        if (IsChitchat(text))
        {
            return FallbackReading.Talk;
        }

        if (AboutQuestion(text) is { } about)
        {
            return FallbackReading.Question(about.Length == 0 ? null : about);
        }

        var request = AskReelIntentSanitizer.ReadKeylessRequest(text, currentYear);
        var negations = AskReelIntentSanitizer.KeylessNegatedPhrases(text)
            .Select(n => string.Join(' ', n.Split(' ').Select(w => compounds.TryGetValue(w, out var c) ? c.Written : w)))
            .ToList();
        var media = PlanSanitizer.UserMedia(null, text);

        var words = AskReelIntentSanitizer.Words(text);
        var reference = ReferenceName(text);
        var referenceWords = reference is null ? [] : AskReelIntentSanitizer.Words(reference);

        var genres = GenresOfPhrases(words).ToList();
        void AddGenres(IEnumerable<string> slugs)
        {
            foreach (var g in slugs.Where(g => !genres.Contains(g)))
            {
                genres.Add(g);
            }
        }

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
                // A genre written another way, alone: "sci-fi" (kept whole), "sitcoms", "docs".
                if (run.Count == 1 && WrittenGenre(run[0], compounds) is { Count: > 0 } named)
                {
                    AddGenres(named);
                    continue;
                }

                var (topic, runGenres) = SplitGenres(run, compounds);
                AddGenres(runGenres);
                if (topic is { Length: > 0 } t && !concepts.Contains(t, StringComparer.Ordinal))
                {
                    concepts.Add(t);
                }
            }
        }

        // What the user ruled out is never also what they asked for.
        concepts.RemoveAll(c => negations.Any(n => NegationTerms.EqualsAnyForm(c, n) || NegationTerms.EqualsAnyForm(n, c)));

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
    /// The message with each hyphenated genre word joined into one ("sci-fi" → "scifi", "rom-com" →
    /// "romcom"), and what each joined word was written as and names. The tokenizer splits on hyphens,
    /// so "sci-fi without aliens" was read as the topic "sci" — "None of your unseen sci movies…", over
    /// a shelf of 19 where the catalogue holds 4,518 science fiction titles (HREV5-ASK-01). A hyphenated
    /// word that names no genre ("feel-good", "action-packed") is left as it was.
    /// </summary>
    internal static (string Text, IReadOnlyDictionary<string, (string Written, IReadOnlyList<string> Genres)> Compounds) JoinGenreCompounds(string? message)
    {
        var compounds = new Dictionary<string, (string Written, IReadOnlyList<string> Genres)>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(message) || !message.Contains('-', StringComparison.Ordinal))
        {
            return (message ?? string.Empty, compounds);
        }

        var tokens = message.Split(' ');
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var start = 0;
            var end = token.Length;
            while (start < end && !char.IsLetterOrDigit(token[start]))
            {
                start++;
            }

            while (end > start && !char.IsLetterOrDigit(token[end - 1]))
            {
                end--;
            }

            var core = token[start..end].ToLowerInvariant();
            if (!core.Contains('-', StringComparison.Ordinal))
            {
                continue;
            }

            var joined = core.Replace("-", string.Empty, StringComparison.Ordinal);
            var slugs = ConceptSynonyms.GenresNamedBy(core) is { Count: > 0 } named
                ? named
                : SearchVocabulary.ResolveGenres(joined);
            if (slugs.Count == 0)
            {
                continue;
            }

            compounds[joined] = (core, slugs);
            tokens[i] = string.Concat(token.AsSpan(0, start), joined, token.AsSpan(end));
        }

        return (string.Join(' ', tokens), compounds);
    }

    /// <summary>
    /// The genres one word names when the concept vocabulary would not read it as one: a joined
    /// hyphenated genre ("scifi" from "sci-fi"), or a word the conflict detector reads as a genre and
    /// the concept synonyms do not ("sitcoms", "docs", "sci"). A word the concept synonyms already
    /// name ("horror", "romantic comedies") stays a topic, read as its genres downstream.
    /// </summary>
    private static IReadOnlyList<string>? WrittenGenre(
        string word, IReadOnlyDictionary<string, (string Written, IReadOnlyList<string> Genres)> compounds)
    {
        if (compounds.TryGetValue(word, out var compound))
        {
            return compound.Genres;
        }

        return ConceptSynonyms.GenreSlugsOf(word) is null && SearchVocabulary.ResolveGenres(word) is { Count: > 0 } aliased
            ? aliased
            : null;
    }

    /// <summary>
    /// Questions about Ask Reel itself, and greetings, written as their words alone: "what can you
    /// do?" searched sixteen random titles and showed them unchecked (HREV5-ASK-01).
    /// </summary>
    private static readonly IReadOnlySet<string> TalkPhrases = new HashSet<string>(StringComparer.Ordinal)
    {
        "what can you do", "what else can you do", "what do you do", "what can i ask", "what can i ask you",
        "what can i ask for", "what should i ask", "how does this work", "how does it work", "how do you work",
        "how do i use this", "how do i use you", "how do i use it", "what are you", "who are you", "help", "help me",
        "hi", "hello", "hey", "hiya", "thanks", "thank you", "thank you so much", "cheers", "good morning",
        "good evening", "good afternoon",
    };

    /// <summary>Words that pad small talk without changing it ("hey reel, what can you do please?").</summary>
    private static readonly IReadOnlySet<string> TalkPadding = new HashSet<string>(StringComparer.Ordinal)
    {
        "reel", "please", "so", "ok", "okay", "um", "hmm", "exactly", "actually",
    };

    /// <summary>True when the whole message is small talk about Ask Reel or a greeting.</summary>
    internal static bool IsChitchat(string? message)
    {
        var words = AskReelIntentSanitizer.Words(message).Where(w => !TalkPadding.Contains(w)).ToList();
        if (words.Count > 1 && words[0] is "hi" or "hey" or "hello" or "hiya")
        {
            var rest = string.Join(' ', words.Skip(1));
            return TalkPhrases.Contains(rest);
        }

        return words.Count > 0 && TalkPhrases.Contains(string.Join(' ', words));
    }

    /// <summary>
    /// The title a "what is &lt;title&gt; about?" question names — "what's Heat about", "what was The
    /// Wire about" — or the empty string when the words between name nothing ("what is that about?"),
    /// or null when the message is not that question. Searched, "what is Better Call Saul about?"
    /// counted "9 unseen call saul titles" (HREV5-ASK-01).
    /// </summary>
    internal static string? AboutQuestion(string? message)
    {
        var words = AskReelIntentSanitizer.Words(message);
        if (words.Count < 3 || words[^1] != "about")
        {
            return null;
        }

        int start;
        if (words[0] is "what's" or "whats")
        {
            start = 1;
        }
        else if (words[0] == "what" && words[1] is "is" or "was" or "s")
        {
            start = 2;
        }
        else
        {
            return null;
        }

        var name = words.Skip(start).Take(words.Count - 1 - start).ToList();
        if (name.Count == 0)
        {
            return null;
        }

        return name.All(AskReelIntentSanitizer.IsFiller) ? string.Empty : string.Join(' ', name);
    }

    /// <summary>
    /// The genres <paramref name="words"/> name by a phrase that is no genre word on its own ("love
    /// story" is romance), in the order they appear. The sanitiser reads the user's words through this
    /// too, so a genre this reader found is never then dropped as a copied standing value (HREV5-ASK-02).
    /// </summary>
    internal static IReadOnlyList<string> GenresOfPhrases(IReadOnlyList<string> words) => SearchVocabulary.GenresOfPhrases(words);

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
    private static (string? Topic, IReadOnlyList<string> Genres) SplitGenres(
        IReadOnlyList<string> run, IReadOnlyDictionary<string, (string Written, IReadOnlyList<string> Genres)> compounds)
    {
        var joined = string.Join(' ', run);
        // A curated concept phrase stays whole too: "second world war" is the World War II concept,
        // never "second world" in the war genre.
        if (run.Count < 2 || ConceptSynonyms.GenresNamedBy(joined) is { Count: > 0 }
            || ConceptSynonyms.GroupOfAnyForm(joined).Skip(1).Any()
            || (ConceptSynonyms.GenreSlugsOf(run[^1]) ?? WrittenGenre(run[^1], compounds)) is not { Count: > 0 } slugs)
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
