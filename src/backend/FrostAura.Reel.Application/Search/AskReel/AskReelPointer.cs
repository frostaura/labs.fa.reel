using System.Globalization;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// "The second one", "the last one", "#3" — a message that points at a card by its place on screen,
/// and the deterministic answer when there is nothing there to point at.
///
/// <para><b>Why it exists (R1-04).</b> "more like the second one you showed me first time round" was
/// asked in a conversation whose answers had put no picks on screen. With nothing to resolve the
/// pointer against, the model filled the request with the sentence's leftovers — keywords
/// <c>["you", "showed", "round"]</c> — and the reply told the founder "You have 9 unseen you movies
/// tagged that way". A pointer with no target is not a search; it is a question to ask back.</para>
///
/// <para><b>When it intervenes, and when it does not.</b> Only when the pointer CANNOT resolve: no
/// answer in the conversation shows anything, or none shows as many cards as the ordinal names. Which
/// answer the user means ("first time round", "the one before") is the model's to read from the
/// handles; this class never second-guesses a pointer that some answer could satisfy.</para>
/// </summary>
public static class AskReelPointer
{
    /// <summary>The ordinal the message points at with <see cref="Last"/>, rather than a number.</summary>
    public const int Last = -1;

    private static readonly IReadOnlyDictionary<string, int> Ordinals = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["first"] = 1,
        ["1st"] = 1,
        ["second"] = 2,
        ["2nd"] = 2,
        ["third"] = 3,
        ["3rd"] = 3,
        ["fourth"] = 4,
        ["4th"] = 4,
        ["fifth"] = 5,
        ["5th"] = 5,
        ["sixth"] = 6,
        ["6th"] = 6,
        ["seventh"] = 7,
        ["7th"] = 7,
        ["eighth"] = 8,
        ["8th"] = 8,
        ["ninth"] = 9,
        ["9th"] = 9,
        ["tenth"] = 10,
        ["10th"] = 10,
        ["last"] = Last,
    };

    /// <summary>Nouns that make an ordinal a pointer at a card: "the second ONE", "the third FILM".</summary>
    private static readonly IReadOnlySet<string> CardNouns = new HashSet<string>(StringComparer.Ordinal)
    {
        "one", "ones", "movie", "film", "show", "series", "title", "pick", "option", "card", "suggestion",
    };

    /// <summary>True when <paramref name="word"/> makes an ordinal before it a pointer at a card ("one", "film", "pick").</summary>
    public static bool IsCardNoun(string word) => CardNouns.Contains(word);

    /// <summary>
    /// The position <paramref name="message"/> points at — 1-based, or <see cref="Last"/> — or null when
    /// it points at nothing. An ordinal counts only when a card noun follows it ("the second one",
    /// "the 3rd film"): "first time round" and "my first horror film ever" name no card.
    /// "#2" and "number 2" count on their own.
    /// </summary>
    public static int? Ordinal(string? message)
    {
        var words = AskReelIntentSanitizer.Words(message);
        for (var i = 0; i < words.Count; i++)
        {
            if (Ordinals.TryGetValue(words[i], out var position)
                && i + 1 < words.Count
                && CardNouns.Contains(words[i + 1])
                && (i == 0 || words[i - 1] is "the" or "that" or "this"))
            {
                return position;
            }

            if (words[i] == "number" && i + 1 < words.Count && SmallNumber(words[i + 1]) is { } n)
            {
                return n;
            }
        }

        // "#2" — the hash is not a word character, so it is read off the raw text.
        var raw = message ?? string.Empty;
        for (var i = raw.IndexOf('#', StringComparison.Ordinal); i >= 0; i = raw.IndexOf('#', i + 1))
        {
            var digits = new string(raw.Skip(i + 1).TakeWhile(char.IsAsciiDigit).ToArray());
            if (SmallNumber(digits) is { } n)
            {
                return n;
            }
        }

        return null;
    }

    /// <summary>
    /// The position <paramref name="message"/> points at when it REJECTS that card — "not the first
    /// one", "I didn't like the second one", "anything but the third one", "skip the first one", "the
    /// second one is terrible" — or null when it points at nothing, or at a card it asks for or asks
    /// about. Only the card <see cref="Ordinal"/> reads is judged, so the rejection and the handle the
    /// sanitiser resolves are always the same card.
    ///
    /// <para><b>Why (HREV6-ASK-02).</b> A pointer was only ever a reference to search NEAR. "Not the
    /// first one" put Heat in the soft references on every path, the reference arm re-ranked towards
    /// Heat's labels and the judge read "Points at: Heat" and "Like: Heat"; with no model, "I didn't
    /// like the second one" searched the topic "didn't" and threw the conversation's topic away.</para>
    ///
    /// <para>A rejection is a cue in the pointer's own clause before it, with nothing between but the
    /// words that carry it ("not a fan of the", "don't show me the", "nothing like the"), or a verdict
    /// after it ("is terrible", "was kinda boring", "isn't for me"). A question about the card ("isn't
    /// the second one terrible?", "is the first one not too violent?") rejects nothing.</para>
    /// </summary>
    public static int? Rejected(string? message)
    {
        if (Ordinal(message) is not { } position)
        {
            return null;
        }

        var tokens = Tokens(message);
        if (tokens.Count == 0 || QuestionLeads.Contains(tokens[0].Word))
        {
            return null;
        }

        var at = PointerIndex(tokens);
        if (at < 0 || Ordinals[tokens[at].Word] != position)
        {
            return null;
        }

        // Before it, in its clause: a cue, past only the words that carry one to the card.
        for (var j = at - 1; j >= 0 && !tokens[j].Closes; j--)
        {
            var word = tokens[j].Word;
            if (AskReelIntentSanitizer.IsNegationCue(word) || RejectVerbs.Contains(word)
                || (word == "but" && j > 0 && tokens[j - 1].Word is "anything" or "everything" or "whatever"))
            {
                return position;
            }

            if (!CueBridges.Contains(word))
            {
                break;
            }
        }

        // After it, in its clause: a verdict ("is terrible"), or a negated "for me"/"good"/"my thing".
        var question = (message ?? string.Empty).TrimEnd().EndsWith('?');
        var cardNoun = at + 1;
        for (var j = cardNoun + 1; !question && !tokens[cardNoun].Closes && j < tokens.Count && j <= cardNoun + 5; j++)
        {
            var word = tokens[j].Word;
            if (Verdicts.Contains(word)
                || (AskReelIntentSanitizer.IsNegationCue(word) && j + 1 < tokens.Count && !tokens[j].Closes && VerdictObjects.Contains(tokens[j + 1].Word)))
            {
                return position;
            }

            if (tokens[j].Closes || !VerdictBridges.Contains(word))
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="message"/> without the clause that rejects a card ("I didn't like the second
    /// one, something darker" → "something darker"), so a reading with no model never takes the
    /// rejection's own words — "didn't", "terrible", "fan" — as a topic or a negation (HREV6-ASK-02).
    /// </summary>
    public static string WithoutRejectedClause(string message)
    {
        var clauses = new List<string>();
        var clause = new List<string>();
        foreach (var token in (message ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            clause.Add(token);
            if (token[^1] is ',' or '.' or ';' or '?' or '!')
            {
                clauses.Add(string.Join(' ', clause));
                clause.Clear();
            }
        }

        if (clause.Count > 0)
        {
            clauses.Add(string.Join(' ', clause));
        }

        return string.Join(' ', clauses.Where(c => Rejected(c) is null)).Trim();
    }

    /// <summary>Words that open a question about a card rather than a verdict on it.</summary>
    private static readonly IReadOnlySet<string> QuestionLeads = new HashSet<string>(StringComparer.Ordinal)
    {
        "is", "isn't", "isnt", "was", "wasn't", "wasnt", "does", "doesn't", "doesnt", "are", "aren't", "can", "could",
        "would", "will", "how", "what", "what's", "who", "who's", "which", "why", "where", "when", "has", "have",
    };

    /// <summary>Verbs that reject the card they govern ("skip the first one", "I hated the second one").</summary>
    private static readonly IReadOnlySet<string> RejectVerbs = new HashSet<string>(StringComparer.Ordinal)
    {
        "skip", "drop", "ditch", "remove", "lose", "scrap", "nix", "hated", "dislike", "disliked", "loathe",
        "loathed", "ignore", "excluding", "except",
    };

    /// <summary>The words that may stand between a cue and the card it rejects ("not a fan of the", "don't show me the").</summary>
    private static readonly IReadOnlySet<string> CueBridges = new HashSet<string>(StringComparer.Ordinal)
    {
        "the", "that", "this", "a", "an", "of", "i", "i'm", "i've", "i'd", "we", "me", "us", "really", "much", "big",
        "huge", "fan", "like", "liked", "love", "loved", "want", "wanted", "see", "seeing", "watch", "watching",
        "show", "showing", "give", "care", "for", "keen", "on", "into", "interested", "in", "sold", "so", "too",
        "at", "all", "anything", "everything", "more", "again", "do", "did", "about", "feeling", "feel",
    };

    /// <summary>Words that pass a verdict on a card ("the second one is terrible").</summary>
    private static readonly IReadOnlySet<string> Verdicts = new HashSet<string>(StringComparer.Ordinal)
    {
        "terrible", "awful", "bad", "boring", "rubbish", "crap", "crappy", "trash", "garbage", "meh", "lame", "dull",
        "horrible", "dreadful", "overrated", "sucks", "sucked", "stinks", "stank", "naff", "worst", "weak", "poor",
        "tedious", "dire", "pants",
    };

    /// <summary>What a negated verdict is about: "isn't for me", "wasn't good", "not my thing".</summary>
    private static readonly IReadOnlySet<string> VerdictObjects = new HashSet<string>(StringComparer.Ordinal)
    {
        "for", "good", "great", "my", "interesting", "fun", "it",
    };

    /// <summary>The words that may stand between the card and a verdict on it ("is kinda", "looks really").</summary>
    private static readonly IReadOnlySet<string> VerdictBridges = new HashSet<string>(StringComparer.Ordinal)
    {
        "is", "was", "'s", "looks", "looked", "seems", "seemed", "sounds", "sounded", "felt", "feels", "kinda",
        "kind", "of", "sort", "really", "so", "pretty", "just", "too", "a", "bit", "totally", "very", "honestly",
        "absolutely", "quite", "rather", "way", "seriously",
    };

    /// <summary>The index of the ordinal <see cref="Ordinal"/> reads (its card noun follows it), or -1.</summary>
    private static int PointerIndex(IReadOnlyList<(string Word, bool Closes)> tokens)
    {
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (Ordinals.ContainsKey(tokens[i].Word) && !tokens[i].Closes && CardNouns.Contains(tokens[i + 1].Word)
                && (i == 0 || tokens[i - 1].Word is "the" or "that" or "this"))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The message's words, each with whether punctuation closes its clause after it.</summary>
    private static List<(string Word, bool Closes)> Tokens(string? message)
    {
        var tokens = new List<(string Word, bool Closes)>();
        foreach (var token in (message ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var closes = token.EndsWith(',') || token.EndsWith('.') || token.EndsWith(';') || token.EndsWith('?') || token.EndsWith('!');
            var words = AskReelIntentSanitizer.Words(token);
            for (var i = 0; i < words.Count; i++)
            {
                tokens.Add((words[i], closes && i == words.Count - 1));
            }
        }

        return tokens;
    }

    /// <summary>
    /// Whether a pointer at <paramref name="position"/> can resolve against the cards on screen:
    /// some answer shows at least that many (any card at all, for <see cref="Last"/>).
    /// </summary>
    public static bool CanResolve(int position, IReadOnlyList<PresentedCard> presented)
    {
        if (presented.Count == 0)
        {
            return false;
        }

        var most = presented.GroupBy(p => p.Turn).Max(g => g.Count());
        return position == Last ? most > 0 : most >= position;
    }

    /// <summary>
    /// The reply when a pointer has nothing to resolve against — plain, deterministic, and never a
    /// search: it says what is on screen and asks what the user meant.
    /// </summary>
    public static string NothingToPointAt(int position, IReadOnlyList<PresentedCard> presented)
    {
        var named = Named(position);
        if (presented.Count == 0)
        {
            return $"There’s nothing on screen yet for “{named}” to point at — I haven’t shown you any titles in this conversation. Tell me what you’re in the mood for and I’ll look.";
        }

        var most = presented.GroupBy(p => p.Turn).Max(g => g.Count());
        return string.Create(
            CultureInfo.InvariantCulture,
            $"My answers here have shown at most {most} {(most == 1 ? "title" : "titles")} at a time, so there’s no {Place(position)} to point at. Which title did you mean?");
    }

    /// <summary>
    /// The reply when a pointer COULD resolve against some answer on screen, but nothing settled
    /// which card it means — the plan named none, and the newest answer has no card at that place, or
    /// the message points at an earlier answer without saying which (HREV1-ASK-03). Asked back, never
    /// searched: a search would run on the topic again and call the result "like the third one".
    /// </summary>
    public static string Unresolved(int position) =>
        $"I couldn’t tell which title “{Named(position)}” means here. Which one did you mean? Name it, or tell me which answer it was in.";

    /// <summary>The pointer as the user would say it: "the second one", "the last one", "the title #12".</summary>
    public static string Named(int position) => position == Last ? "the last one" : $"the {Place(position)}";

    private static int? SmallNumber(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 40 ? n : null;

    /// <summary>"second one", "title #12".</summary>
    private static string Place(int position) =>
        position is >= 1 and <= 10
            ? $"{Word(position)} one"
            : string.Create(CultureInfo.InvariantCulture, $"title #{position}");

    private static string Word(int position) => position switch
    {
        1 => "first",
        2 => "second",
        3 => "third",
        4 => "fourth",
        5 => "fifth",
        6 => "sixth",
        7 => "seventh",
        8 => "eighth",
        9 => "ninth",
        _ => "tenth",
    };
}
