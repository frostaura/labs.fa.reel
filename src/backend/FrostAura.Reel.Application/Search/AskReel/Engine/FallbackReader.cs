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
        "actually", "honestly", "others", "rest",
        // Interjections: with no model, "nope, not the first one" searched the new request "nope" and
        // threw the conversation's topic away (HREV6-ASK-02-R3).
        "nope", "nah", "naw", "ugh", "meh", "eh", "yuck", "ew", "eww", "hmm", "hmmm", "bleh",
        // The pointer "number 2" (AskReelPointer), never the topic "number".
        "number",
        // The credit a person is named for ("starring", "directed by") — read as the role
        // (AskReelEngine.FallbackRole), never as a topic beside the person (HREV5-RET-02, round 7).
        "starring", "starred", "featuring", "featured", "directed", "director", "directors", "directing",
        "acting", "actor", "actors", "actress", "actresses",
    };

    /// <summary>
    /// True when <paramref name="word"/> names the credit a person is asked for by ("starring",
    /// "directed") — never a topic, and never a word left beside a person grounded from the same phrase.
    /// </summary>
    internal static bool IsRoleWord(string word) =>
        word is "starring" or "starred" or "featuring" or "featured" or "directed" or "director" or "directors"
            or "directing" or "acting" or "actor" or "actors" or "actress" or "actresses";

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
        "excluding", "sans", "barring", "apart", "aside",
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

        if (TitleQuestion(text) is { } about)
        {
            return FallbackReading.Question(about.Length == 0 ? null : about);
        }

        // A capitalised name is read whole from here on ("Will Smith", "Samuel L. Jackson"): split
        // into words, its stop words, initials and short words were dropped one by one.
        var (joinedText, names) = JoinProperNames(text);
        string Spoken(string phrase) => string.Join(' ', phrase.Split(' ').Select(w =>
            names.TryGetValue(w, out var name) ? name : compounds.TryGetValue(w, out var c) ? c.Written : w));

        var request = AskReelIntentSanitizer.ReadKeylessRequest(joinedText, currentYear);
        var negations = AskReelIntentSanitizer.KeylessNegatedPhrases(joinedText).Select(Spoken).ToList();
        var media = PlanSanitizer.UserMedia(null, text);

        var words = AskReelIntentSanitizer.Words(text);
        var joinedReference = ReferenceName(joinedText);
        var reference = joinedReference is null ? null : Spoken(joinedReference);
        var referenceWords = joinedReference is null ? [] : AskReelIntentSanitizer.Words(joinedReference);

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

                var (joinedTopic, runGenres) = SplitGenres(run, compounds);
                AddGenres(runGenres);
                if (joinedTopic is { Length: > 0 } && Spoken(joinedTopic) is var t && !concepts.Contains(t, StringComparer.Ordinal))
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

    /// <summary>
    /// The words a question about Ask Reel itself is made of — what it is, does or can be asked —
    /// and nothing that could name a topic. A message made only of these, asking about Ask Reel
    /// (<see cref="TalkSubjects"/>) by one of <see cref="TalkAnchors"/>, is small talk however it is
    /// phrased: the exact-phrase list alone left "what can you help me with?" searching "help" and
    /// "what can you do for me?" searching the whole catalogue (HREV5-ASK-01, round 7).
    /// </summary>
    private static readonly IReadOnlySet<string> TalkWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "what", "what's", "whats", "how", "who", "can", "could", "would", "should", "do", "does", "are", "is",
        "you", "your", "yourself", "i", "me", "we", "us", "this", "it", "for", "with", "of", "to", "about", "the",
        "a", "an", "all", "else", "exactly", "kind", "kinds", "sort", "sorts", "type", "types", "thing", "things",
        "help", "capable", "able", "ask", "work", "works", "use", "features", "feature", "abilities", "ability",
        "offer", "tell", "questions", "question", "good", "at", "really",
    };

    /// <summary>The words that make a message made of <see cref="TalkWords"/> a question about what Ask Reel does.</summary>
    private static readonly IReadOnlySet<string> TalkAnchors = new HashSet<string>(StringComparer.Ordinal)
    {
        "help", "capable", "able", "ask", "do", "work", "works", "use", "features", "feature", "abilities",
        "ability", "offer", "yourself",
    };

    /// <summary>The words that make it about Ask Reel or about using it.</summary>
    private static readonly IReadOnlySet<string> TalkSubjects = new HashSet<string>(StringComparer.Ordinal)
    {
        "you", "your", "yourself", "i", "we", "this", "it",
    };

    /// <summary>True when the whole message is small talk about Ask Reel or a greeting.</summary>
    internal static bool IsChitchat(string? message)
    {
        var words = AskReelIntentSanitizer.Words(message).Where(w => !TalkPadding.Contains(w)).ToList();
        if (words.Count > 1 && words[0] is "hi" or "hey" or "hello" or "hiya")
        {
            words = words.Skip(1).ToList();
        }

        if (words.Count == 0)
        {
            return false;
        }

        return TalkPhrases.Contains(string.Join(' ', words))
            || (words.All(TalkWords.Contains) && words.Any(TalkAnchors.Contains) && words.Any(TalkSubjects.Contains));
    }

    /// <summary>
    /// The title a question about one title names — "what is Better Call Saul about?", "tell me about
    /// Heat", "is The Wire any good?", "what's the plot of Dune?", "who's in Heat?" — or the empty
    /// string when it points at what is on screen instead ("what is that about?", "is it any good?"),
    /// or null when the message is not such a question. Searched, "what is Better Call Saul about?"
    /// counted "9 unseen call saul titles" (HREV5-ASK-01), and in round 6 only that one phrasing was
    /// read: "tell me about…", "is … any good?" and "what's the plot of…" still searched "call saul".
    /// </summary>
    internal static string? TitleQuestion(string? message)
    {
        var words = AskReelIntentSanitizer.Words(message).ToList();
        while (words.Count > 0 && words[^1] is "please" or "then" or "again")
        {
            words.RemoveAt(words.Count - 1);
        }

        if (words.Count >= 3 && words[^1] == "about" && AboutQuestion(words) is { } about)
        {
            return about;
        }

        foreach (var (lead, tail) in TitleQuestionShapes)
        {
            if (words.Count > lead.Length + tail.Length
                && words.Take(lead.Length).SequenceEqual(lead, StringComparer.Ordinal)
                && words.Skip(words.Count - tail.Length).SequenceEqual(tail, StringComparer.Ordinal))
            {
                return QuestionTitle(words.Skip(lead.Length).Take(words.Count - lead.Length - tail.Length).ToList());
            }
        }

        return null;
    }

    /// <summary>
    /// The shapes of a question about one title, as the words before and after its name. The first
    /// shape that fits decides; a name that cannot be a title (<see cref="QuestionTitle"/>) leaves the
    /// message a search.
    /// </summary>
    private static readonly IReadOnlyList<(string[] Lead, string[] Tail)> TitleQuestionShapes =
    [
        (["tell", "me", "more", "about"], []),
        (["tell", "me", "about"], []),
        (["tell", "us", "about"], []),
        (["what's", "the", "plot", "of"], []),
        (["whats", "the", "plot", "of"], []),
        (["what", "is", "the", "plot", "of"], []),
        (["what's", "the", "story", "of"], []),
        (["what", "is", "the", "story", "of"], []),
        (["what's", "the", "premise", "of"], []),
        (["what", "is", "the", "premise", "of"], []),
        (["what", "happens", "in"], []),
        (["who's", "in"], []),
        (["who", "is", "in"], []),
        (["who", "stars", "in"], []),
        (["who", "starred", "in"], []),
        (["who", "directed"], []),
        (["how", "long", "is"], []),
        (["is"], ["any", "good"]),
        (["was"], ["any", "good"]),
        (["are"], ["any", "good"]),
        (["is"], ["good"]),
        (["is"], ["worth", "watching"]),
        (["is"], ["worth", "it"]),
        (["is"], ["worth", "a", "watch"]),
        (["is"], ["worth", "seeing"]),
        (["is"], ["worth", "my", "time"]),
        (["should", "i", "watch"], []),
    ];

    /// <summary>Words that, as the whole of a question's subject, point at what is on screen.</summary>
    private static readonly IReadOnlySet<string> OnScreenSubjects = new HashSet<string>(StringComparer.Ordinal)
    {
        "it", "that", "this", "one", "them", "those", "these",
    };

    /// <summary>Words a title in a question never starts with: a request ("a comedy", "something") or "there".</summary>
    private static readonly IReadOnlySet<string> NotATitleStart = new HashSet<string>(StringComparer.Ordinal)
    {
        "a", "an", "some", "any", "something", "anything", "more", "other", "another", "there", "you", "your", "my",
        "good", "great",
    };

    /// <summary>
    /// The subject of a question about one title as that title's name; the empty string when it only
    /// points at what is on screen; null when it reads as a request ("tell me about some heist movies",
    /// "is there anything good") — a media word, a genre, a negation or a comparison makes it one.
    /// </summary>
    private static string? QuestionTitle(IReadOnlyList<string> subject)
    {
        if (subject.Count == 0 || subject.Count > 6)
        {
            return null;
        }

        if (subject.All(OnScreenSubjects.Contains))
        {
            return string.Empty;
        }

        if (NotATitleStart.Contains(subject[0])
            || subject.All(AskReelIntentSanitizer.IsFiller)
            || subject.Any(w => MediaWords.Contains(w) || SearchCues.Contains(w) || Comparisons.ContainsKey(w)
                || w is "no" or "not" or "without" or "nothing" or "except")
            || subject.All(w => ConceptSynonyms.GenreSlugsOf(w) is not null || SearchVocabulary.ResolveGenres(w).Count > 0))
        {
            return null;
        }

        return string.Join(' ', subject);
    }

    /// <summary>"what is &lt;title&gt; about" read off its words (the last is "about"), as <see cref="TitleQuestion"/> returns it.</summary>
    private static string? AboutQuestion(IReadOnlyList<string> words)
    {
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

    /// <summary>Lower-case words that join two capitalised parts of one name ("Robert de Niro", "Ludwig van Beethoven").</summary>
    private static readonly IReadOnlySet<string> NameParticles = new HashSet<string>(StringComparer.Ordinal)
    {
        "de", "da", "di", "del", "della", "van", "von", "der", "den", "le", "la", "du", "dos", "das", "bin", "ben",
    };

    /// <summary>The suffixes that may end a name ("Robert Downey Jr.").</summary>
    private static readonly IReadOnlySet<string> NameSuffixes = new HashSet<string>(StringComparer.Ordinal)
    {
        "jr", "sr", "ii", "iii", "iv",
    };

    /// <summary>Stop words that are also common first names, so a capitalised one may open a name even at the start ("Will Smith movies").</summary>
    private static readonly IReadOnlySet<string> FirstNameStopWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "will", "may",
    };

    /// <summary>
    /// The message with each capitalised name of two or more words joined into one word ("Will
    /// Smith" → "willsmith", "Samuel L. Jackson" → "samuelljackson"), and the words each joined word
    /// stands for ("will smith", "samuel l jackson"). The reader split a name into words and then
    /// dropped the ones that are stop words, two letters or shorter, or punctuated, so "Will Smith"
    /// was the topic "smith", "Al Pacino" "pacino", "Robert De Niro" the two topics "robert" and
    /// "niro", and "Robert Downey Jr." "robert downey" — none of which grounds as the person
    /// (HREV5-RET-02, round 7). Whole, the grounder finds them.
    ///
    /// <para>Only the user's capitals mark a name, so a message in one case throughout (all lower or
    /// all upper) is left as it was; and a capital that only starts a sentence, or a word that is a
    /// genre, a medium, a mood, praise, a comparison or a negation, never starts one: "Tense Heist
    /// Movies" is still a mood and a topic.</para>
    /// </summary>
    internal static (string Text, IReadOnlyDictionary<string, string> Names) JoinProperNames(string text)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLower) || !text.Any(char.IsUpper))
        {
            return (text, names);
        }

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(NameToken.Of).ToList();
        var output = new List<string>(tokens.Count);
        var i = 0;
        while (i < tokens.Count)
        {
            var run = NameRunAt(tokens, i);
            if (run is not { } r)
            {
                output.Add(tokens[i].Raw);
                i++;
                continue;
            }

            for (var k = i; k < r.Start; k++)
            {
                output.Add(tokens[k].Raw);
            }

            var parts = tokens.Skip(r.Start).Take(r.Length).SelectMany(t => AskReelIntentSanitizer.Words(t.Core)).ToList();
            var joined = string.Concat(parts);
            names[joined] = string.Join(' ', parts);
            var last = tokens[r.Start + r.Length - 1];
            output.Add(string.Concat(tokens[r.Start].Lead, joined, last.Abbreviated ? last.Trail.TrimStart('.') : last.Trail));
            i = r.Start + r.Length;
        }

        return (string.Join(' ', output), names);
    }

    /// <summary>The name that starts at or just after <paramref name="start"/> (after a dropped lead), or null.</summary>
    private static (int Start, int Length)? NameRunAt(IReadOnlyList<NameToken> tokens, int start)
    {
        if (!IsNameWord(tokens, start))
        {
            return null;
        }

        var end = start;
        while (true)
        {
            if (tokens[end].Breaks || end + 1 >= tokens.Count)
            {
                break;
            }

            var next = end + 1;
            if (IsNameWord(tokens, next) || (NameSuffixes.Contains(tokens[next].Lower) && tokens[next].Capitalised))
            {
                end = next;
                continue;
            }

            // A particle joins two capitalised parts: "Robert de Niro".
            if (NameParticles.Contains(tokens[next].Lower) && !tokens[next].Breaks && next + 1 < tokens.Count && IsNameWord(tokens, next + 1))
            {
                end = next + 1;
                continue;
            }

            break;
        }

        // A stop word neither opens a name ("With Will Smith", "Like The Dark Knight") nor ends one —
        // except "the", which opens a title, and a stop word that is also a first name ("Will").
        var first = start;
        var lastIndex = end;
        while (first < lastIndex && AskReelIntentSanitizer.IsFiller(tokens[first].Lower)
               && tokens[first].Lower != "the" && !FirstNameStopWords.Contains(tokens[first].Lower))
        {
            first++;
        }

        while (lastIndex > first && AskReelIntentSanitizer.IsFiller(tokens[lastIndex].Lower) && !NameSuffixes.Contains(tokens[lastIndex].Lower))
        {
            lastIndex--;
        }

        var length = lastIndex - first + 1;
        var proper = Enumerable.Range(first, length).Count(k =>
            !AskReelIntentSanitizer.IsFiller(tokens[k].Lower) && !NameParticles.Contains(tokens[k].Lower)
            && !NameSuffixes.Contains(tokens[k].Lower) && !tokens[k].Initial);
        if (length < 2 || proper < 1)
        {
            return null;
        }

        // A run that holds a two-word mood ("Slow Burn") is the mood, not a name.
        for (var k = first; k < first + length - 1; k++)
        {
            if (AskReelIntentSanitizer.IsMoodPhrase($"{tokens[k].Lower} {tokens[k + 1].Lower}"))
            {
                return null;
            }
        }

        return (first, length);
    }

    /// <summary>
    /// True when the token at <paramref name="index"/> can be part of a capitalised name: capitalised
    /// (or an initial), not "I", and not a word that only starts the sentence or names something
    /// else (a genre, a medium, a mood, praise, a comparison, a negation, a greeting).
    /// </summary>
    private static bool IsNameWord(IReadOnlyList<NameToken> tokens, int index)
    {
        var token = tokens[index];
        if (!token.Capitalised || token.Lower.Length == 0)
        {
            return false;
        }

        var lower = token.Lower;
        if (lower is "i" or "a" || lower.StartsWith("i'", StringComparison.Ordinal))
        {
            return false;
        }

        if (token.Initial)
        {
            return index > 0 && !tokens[index - 1].EndsSentence;
        }

        if (MediaWords.Contains(lower) || NeverATopic.Contains(lower) || Comparisons.ContainsKey(lower)
            || TalkPadding.Contains(lower) || lower is "hi" or "hey" or "hello" or "hiya" || AskReelIntentSanitizer.IsNegationCue(lower)
            || AskReelIntentSanitizer.IsMoodPhrase(lower) || AskReelIntentSanitizer.IsPraise(lower)
            || ConceptSynonyms.GenreSlugsOf(lower) is not null || SearchVocabulary.ResolveGenres(lower).Count > 0)
        {
            return false;
        }

        var sentenceStart = index == 0 || (tokens[index - 1].EndsSentence && !tokens[index - 1].Abbreviated);
        return !sentenceStart
            || FirstNameStopWords.Contains(lower)
            || !(AskReelIntentSanitizer.IsFiller(lower) || QuestionLeads.Contains(lower) || LikeHeads.Contains(lower));
    }

    /// <summary>One space-separated token of a message, read for <see cref="JoinProperNames"/>.</summary>
    private sealed record NameToken(string Raw, string Lead, string Core, string Trail)
    {
        public string Lower { get; } = Core.ToLowerInvariant();

        /// <summary>Starts with a capital letter.</summary>
        public bool Capitalised => Core.Length > 0 && char.IsUpper(Core[0]);

        /// <summary>One capital letter, as in "Samuel L. Jackson".</summary>
        public bool Initial => Core.Length == 1 && char.IsUpper(Core[0]);

        /// <summary>A full stop that abbreviates ("L.", "Jr.") rather than ends a sentence.</summary>
        public bool Abbreviated => Trail.StartsWith('.') && (Initial || NameSuffixes.Contains(Lower));

        /// <summary>Ends its sentence: ".", "?" or "!" that is not an abbreviation's.</summary>
        public bool EndsSentence => Trail.Length > 0 && Trail[0] is '.' or '?' or '!';

        /// <summary>Ends a name: any punctuation after it but an abbreviation's full stop.</summary>
        public bool Breaks => Trail.Length > 0 && !Abbreviated;

        public static NameToken Of(string raw)
        {
            var start = 0;
            var end = raw.Length;
            while (start < end && !char.IsLetterOrDigit(raw[start]))
            {
                start++;
            }

            while (end > start && !char.IsLetterOrDigit(raw[end - 1]))
            {
                end--;
            }

            return new NameToken(raw, raw[..start], raw[start..end], raw[end..]);
        }
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
            else if (NotAName.Contains(tokens[start].Word)
                || (tokens[start].Word == "number" && start + 1 < tokens.Count && int.TryParse(tokens[start + 1].Word, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 40))
            {
                // "More like #2" and "more like number 2" point at a card (AskReelPointer), never a title
                // named "2": read as one, the turn was a new request with no topic and no pointer.
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
            var tokenWords = AskReelIntentSanitizer.Words(token).ToList();
            if (token.StartsWith('#') && tokenWords.Count > 0)
            {
                // "#2" reads as "number 2", the pointer it is.
                tokenWords.Insert(0, "number");
            }

            for (var i = 0; i < tokenWords.Count; i++)
            {
                tokens.Add((tokenWords[i], closes && i == tokenWords.Count - 1));
            }
        }

        return tokens;
    }
}
