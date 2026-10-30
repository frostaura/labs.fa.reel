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
    /// it points at nothing: the first of its <see cref="Pointers"/>. An ordinal counts only when a card
    /// noun follows it ("the second one", "the 3rd film", "the first one's"): "first time round" and
    /// "my first horror film ever" name no card. "#2" and "number 2" count on their own.
    /// </summary>
    public static int? Ordinal(string? message) =>
        Pointers(message) is [var first, ..] ? first.Position : null;

    /// <summary>
    /// The position of the first card <paramref name="message"/> REJECTS — "not the first one", "I
    /// didn't like the second one", "anything but the third one", "the second one is terrible", "the
    /// first one? nah" — or null when it rejects none (<see cref="Pointers"/>).
    /// </summary>
    public static int? Rejected(string? message) =>
        Pointers(message).FirstOrDefault(p => p.Rejected) is { Rejected: true } rejected ? rejected.Position : null;

    /// <summary>
    /// Every card <paramref name="message"/> points at, in the order it names them, each with whether
    /// the message rejects it.
    ///
    /// <para><b>Why (HREV6-ASK-02).</b> A pointer was only ever a reference to search NEAR. "Not the
    /// first one" put Heat in the soft references on every path, the reference arm re-ranked towards
    /// Heat's labels and the judge read "Points at: Heat" and "Like: Heat"; with no model, "I didn't
    /// like the second one" searched the topic "didn't" and threw the conversation's topic away.</para>
    ///
    /// <para><b>Why each pointer is judged on its own (HREV6-ASK-02-R1).</b> "Not the first one, more
    /// like the second one" rejects one card and asks for more like another. Read as one verdict on the
    /// whole message, it barred both cards and searched near neither.</para>
    ///
    /// <para>A pointer is rejected by, in its own clause (a clause ends at punctuation, and at a "but"
    /// that is not "anything but"): a cue before it, with nothing between but the words that carry one
    /// to the card ("not a fan of the", "no to the", "nah the"); or a verdict after it ("is terrible",
    /// "'s boring", "isn't for me"). Or, when the pointer is its clause, by the clause after it being
    /// only a refusal ("the first one? nah", "first one, no thanks", "the second one, not for me"). A
    /// question about the card ("isn't the second one terrible?", "is the first one not too violent?")
    /// rejects nothing.</para>
    /// </summary>
    public static IReadOnlyList<PointerMention> Pointers(string? message) =>
        Read(Tokens(message)).Select(p => new PointerMention(p.Position, p.Rejected)).ToList();

    /// <summary>
    /// <paramref name="message"/> without the clauses that only reject a card ("I didn't like the second
    /// one, something darker" → "something darker"), so a reading with no model never takes the
    /// rejection's own words — "didn't", "terrible", "fan" — as a topic or a negation (HREV6-ASK-02).
    /// A refusal clause that rejects the card ("the first one? nah") and an interjection that opens the
    /// rejection ("nope, not the first one") go with it: kept, "nope" and "ugh" became the new topic
    /// and replaced the conversation's (HREV6-ASK-02-R3).
    /// </summary>
    public static string WithoutRejectedClause(string message)
    {
        var tokens = Tokens(message);
        var drop = new bool[tokens.Count];
        foreach (var pointer in Read(tokens).Where(p => p.Rejected))
        {
            var (start, end) = Clause(tokens, pointer.Start);
            Mark(drop, start, end);
            if (pointer.RefusedAfter is { } refusal)
            {
                Mark(drop, refusal.Start, refusal.End);
            }

            // An interjection that opens the message right before it: "nope, not the first one". One
            // later in the message refuses the clause before IT ("superhero movies? nah. not the first
            // one"), and is the keyless reader's to read.
            if (start > 0 && Clause(tokens, start - 1) is { Start: 0 } before && IsRefusal(tokens, before.Start, before.End))
            {
                Mark(drop, before.Start, before.End);
            }
        }

        // The message's own text, less every raw token whose words were all dropped.
        var kept = tokens
            .Select((t, i) => (t.Raw, Dropped: drop[i]))
            .GroupBy(t => t.Raw)
            .Where(g => g.Any(t => !t.Dropped))
            .Select(g => g.Key.Text);
        return string.Join(' ', kept).Trim();

        static void Mark(bool[] drop, int start, int end)
        {
            for (var k = start; k <= end; k++)
            {
                drop[k] = true;
            }
        }
    }

    /// <summary>A pointer read off a message: where it stands, what it points at, and whether it is rejected.</summary>
    private sealed record PointerRead(int Start, int End, int Position, bool Rejected, (int Start, int End)? RefusedAfter);

    /// <summary>One word of a message, with the raw token it came from and whether punctuation closes its clause after it.</summary>
    private sealed record Token(string Word, bool Closes, RawToken Raw);

    /// <summary>A space-separated token of the message as written, by its place (so equal texts stay apart).</summary>
    private sealed record RawToken(int Index, string Text);

    private static List<PointerRead> Read(IReadOnlyList<Token> tokens)
    {
        var pointers = new List<PointerRead>();
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (PointerAt(tokens, i) is not { } position)
            {
                continue;
            }

            pointers.Add(Judge(tokens, i, position));
            i++;
        }

        return pointers;
    }

    /// <summary>The position a pointer starting at <paramref name="i"/> names ("the SECOND one", "NUMBER 2"), or null.</summary>
    private static int? PointerAt(IReadOnlyList<Token> tokens, int i)
    {
        if (i + 1 >= tokens.Count || tokens[i].Closes)
        {
            return null;
        }

        if (Ordinals.TryGetValue(tokens[i].Word, out var position)
            && CardNouns.Contains(tokens[i + 1].Word)
            && (i == 0 || tokens[i - 1].Word is "the" or "that" or "this"))
        {
            return position;
        }

        return tokens[i].Word == "number" ? SmallNumber(tokens[i + 1].Word) : null;
    }

    /// <summary>Whether the message rejects the pointer at <paramref name="at"/> (its card noun or number follows it).</summary>
    private static PointerRead Judge(IReadOnlyList<Token> tokens, int at, int position)
    {
        var end = at + 1;
        var (clauseStart, clauseEnd) = Clause(tokens, at);
        PointerRead Verdict(bool rejected, (int, int)? refusal = null) => new(at, end, position, rejected, refusal);

        // A question about the card rejects nothing: "isn't the second one terrible?".
        if (QuestionLeads.Contains(tokens[clauseStart].Word))
        {
            return Verdict(false);
        }

        // Before it, in its clause: a cue, past only the words that carry one to the card.
        var onlyArticles = true;
        for (var j = at - 1; j >= clauseStart; j--)
        {
            var word = tokens[j].Word;
            if (AskReelIntentSanitizer.IsNegationCue(word) || RejectVerbs.Contains(word) || RefusalWords.Contains(word)
                || (word == "but" && j > 0 && ExceptHeads.Contains(tokens[j - 1].Word)))
            {
                return Verdict(true);
            }

            onlyArticles &= word is "the" or "that" or "this" or "and" or "ok" or "okay" or "well" or "so" or "hmm" or "as" or "for";
            if (!CueBridges.Contains(word))
            {
                break;
            }
        }

        // After it, in its clause: a verdict ("is terrible"), or a negated "for me"/"good"/"my thing".
        var question = tokens[clauseEnd].Raw.Text.EndsWith('?');
        for (var j = end + 1; !question && !tokens[end].Closes && j <= clauseEnd && j <= end + 5; j++)
        {
            var word = tokens[j].Word;
            if (Verdicts.Contains(word)
                || (AskReelIntentSanitizer.IsNegationCue(word) && j + 1 <= clauseEnd && !tokens[j].Closes && VerdictObjects.Contains(tokens[j + 1].Word)))
            {
                return Verdict(true);
            }

            if (tokens[j].Closes || !VerdictBridges.Contains(word))
            {
                break;
            }
        }

        // The pointer is its whole clause, and the clause after it only refuses: "the first one? nah".
        if (onlyArticles && clauseEnd == end && end + 1 < tokens.Count)
        {
            var (nextStart, nextEnd) = Clause(tokens, end + 1);
            if (IsRefusal(tokens, nextStart, nextEnd))
            {
                return Verdict(true, (nextStart, nextEnd));
            }
        }

        return Verdict(false);
    }

    /// <summary>
    /// The clause around <paramref name="index"/>: from just after the last closing punctuation (or
    /// at a "but" that is not "anything but") to the next closing punctuation, or the next such "but".
    /// "More like the first one but not the second one" is two clauses.
    /// </summary>
    private static (int Start, int End) Clause(IReadOnlyList<Token> tokens, int index)
    {
        var start = index;
        while (start > 0 && !tokens[start - 1].Closes && !OpensClause(tokens, start))
        {
            start--;
        }

        var end = index;
        while (end < tokens.Count - 1 && !tokens[end].Closes && !OpensClause(tokens, end + 1))
        {
            end++;
        }

        return (start, end);
    }

    /// <summary>True when a new clause starts at <paramref name="index"/>: a "but" that is not "anything but".</summary>
    private static bool OpensClause(IReadOnlyList<Token> tokens, int index) =>
        tokens[index].Word == "but" && index > 0 && !ExceptHeads.Contains(tokens[index - 1].Word);

    /// <summary>True when the words from <paramref name="start"/> to <paramref name="end"/> only refuse: "nah", "no thanks", "not for me", "ugh".</summary>
    private static bool IsRefusal(IReadOnlyList<Token> tokens, int start, int end)
    {
        var words = tokens.Skip(start).Take(end - start + 1).Select(t => t.Word).ToList();
        return words.Count > 0 && words.All(RefusalClauseWords.Contains) && words.Any(RefusalHeads.Contains);
    }

    /// <summary>
    /// The message's words, each with whether punctuation closes its clause after it and the raw token
    /// it came from. A card noun's "'s" is its own word ("one's" → "one", "'s"), and "#2" reads as
    /// "number 2", so "the first one's terrible" and "not #1" point exactly as their spelt-out forms do.
    /// </summary>
    private static List<Token> Tokens(string? message)
    {
        var tokens = new List<Token>();
        var index = 0;
        foreach (var text in (message ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var raw = new RawToken(index++, text);
            var closes = text.EndsWith(',') || text.EndsWith('.') || text.EndsWith(';') || text.EndsWith('?') || text.EndsWith('!');
            var words = new List<string>();
            if (text.TrimStart('(').StartsWith('#'))
            {
                words.Add("number");
            }

            foreach (var word in AskReelIntentSanitizer.Words(text))
            {
                if (word.EndsWith("'s", StringComparison.Ordinal) && CardNouns.Contains(word[..^2]))
                {
                    words.Add(word[..^2]);
                    words.Add("'s");
                }
                else
                {
                    words.Add(word);
                }
            }

            for (var i = 0; i < words.Count; i++)
            {
                tokens.Add(new Token(words[i], closes && i == words.Count - 1, raw));
            }
        }

        return tokens;
    }

    /// <summary>Words that open a question about a card rather than a verdict on it.</summary>
    private static readonly IReadOnlySet<string> QuestionLeads = new HashSet<string>(StringComparer.Ordinal)
    {
        "is", "isn't", "isnt", "was", "wasn't", "wasnt", "does", "doesn't", "doesnt", "are", "aren't", "can", "could",
        "would", "will", "how", "what", "what's", "who", "who's", "which", "why", "where", "when", "has", "have",
    };

    /// <summary>The words before a "but" that make it "except" ("anything but the third one"), never a clause break.</summary>
    private static readonly IReadOnlySet<string> ExceptHeads = new HashSet<string>(StringComparer.Ordinal)
    {
        "anything", "everything", "whatever",
    };

    /// <summary>Verbs that reject the card they govern ("skip the first one", "I hated the second one").</summary>
    private static readonly IReadOnlySet<string> RejectVerbs = new HashSet<string>(StringComparer.Ordinal)
    {
        "skip", "drop", "ditch", "remove", "lose", "scrap", "nix", "hated", "dislike", "disliked", "loathe",
        "loathed", "ignore", "excluding", "except",
    };

    /// <summary>Refusals that reject the card right after them: "nah the first one", "nope, the second one".</summary>
    private static readonly IReadOnlySet<string> RefusalWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "nah", "nope", "naw",
    };

    /// <summary>The words a clause that only refuses is made of: "nah", "no thanks", "not for me", "hard pass", "ugh".</summary>
    private static readonly IReadOnlySet<string> RefusalClauseWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "no", "nah", "nope", "naw", "not", "thanks", "thank", "you", "thx", "for", "me", "pass", "hard", "meh", "ugh",
        "eh", "interested", "way", "that", "one", "really", "please", "definitely", "god", "oh", "yuck", "ew", "eww",
    };

    /// <summary>The words that make a clause of <see cref="RefusalClauseWords"/> a refusal rather than filler.</summary>
    private static readonly IReadOnlySet<string> RefusalHeads = new HashSet<string>(StringComparer.Ordinal)
    {
        "no", "nah", "nope", "naw", "not", "pass", "meh", "ugh", "yuck", "ew", "eww",
    };

    /// <summary>The words that may stand between a cue and the card it rejects ("not a fan of the", "no to the").</summary>
    private static readonly IReadOnlySet<string> CueBridges = new HashSet<string>(StringComparer.Ordinal)
    {
        "the", "that", "this", "a", "an", "of", "i", "i'm", "i've", "i'd", "we", "me", "us", "really", "much", "big",
        "huge", "fan", "like", "liked", "love", "loved", "want", "wanted", "see", "seeing", "watch", "watching",
        "show", "showing", "give", "care", "for", "keen", "on", "into", "interested", "in", "sold", "so", "too",
        "at", "all", "anything", "everything", "more", "again", "do", "did", "about", "feeling", "feel", "to",
        "and", "ok", "okay", "well", "hmm", "as",
    };

    /// <summary>Words that pass a verdict on a card ("the second one is terrible").</summary>
    private static readonly IReadOnlySet<string> Verdicts = new HashSet<string>(StringComparer.Ordinal)
    {
        "terrible", "awful", "bad", "boring", "rubbish", "crap", "crappy", "trash", "garbage", "meh", "lame", "dull",
        "horrible", "dreadful", "overrated", "sucks", "sucked", "stinks", "stank", "naff", "worst", "weak", "poor",
        "tedious", "dire", "pants", "nah", "nope",
    };

    /// <summary>What a negated verdict is about: "isn't for me", "wasn't good", "not my thing", "no thanks".</summary>
    private static readonly IReadOnlySet<string> VerdictObjects = new HashSet<string>(StringComparer.Ordinal)
    {
        "for", "good", "great", "my", "interesting", "fun", "it", "thanks", "thank", "way",
    };

    /// <summary>The words that may stand between the card and a verdict on it ("is kinda", "'s really").</summary>
    private static readonly IReadOnlySet<string> VerdictBridges = new HashSet<string>(StringComparer.Ordinal)
    {
        "is", "was", "'s", "looks", "looked", "seems", "seemed", "sounds", "sounded", "felt", "feels", "kinda",
        "kind", "of", "sort", "really", "so", "pretty", "just", "too", "a", "bit", "totally", "very", "honestly",
        "absolutely", "quite", "rather", "way", "seriously",
    };

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

/// <summary>One pointer at a card in a message: its place on screen, and whether the message rejects that card.</summary>
/// <param name="Position">1-based, or <see cref="AskReelPointer.Last"/>.</param>
public readonly record struct PointerMention(int Position, bool Rejected);
