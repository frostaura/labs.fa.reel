using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FrostAura.Reel.Domain.Lens;

/// <summary>
/// The closed vocabularies the lens may answer with. Closed for the same reason the tone and era
/// taxonomies are: free text lets the model mint one value per title until the field stops being
/// filterable and starts being noise. These two ARE filterable — the tonight-mood chips re-rank
/// over <see cref="Moods"/> — so an invented value is a silently dead card, not merely untidy.
/// </summary>
public static class LensVocabulary
{
    /// <summary>How the title feels to sit with. Drives the tonight-mood chips.</summary>
    public static readonly string[] Moods =
    [
        "light", "heavy", "tense", "cosy", "cerebral", "background",
    ];

    /// <summary>The watching occasion it suits.</summary>
    public static readonly string[] WhenToWatch =
    [
        "tonight-alone", "with-someone", "needs-full-attention", "fine-as-background", "weekend",
    ];

    private static readonly HashSet<string> MoodSet = new(Moods, StringComparer.Ordinal);

    private static readonly HashSet<string> WhenSet = new(WhenToWatch, StringComparer.Ordinal);

    public static bool IsMood(string? value) => value is not null && MoodSet.Contains(value);

    public static bool IsWhenToWatch(string? value) => value is not null && WhenSet.Contains(value);
}

/// <summary>
/// One anchor: a title from the user's OWN library, with a one-clause statement of how the
/// candidate relates to it. The name must match a title the account has actually rated — an
/// invented comparison is the most convincing kind of wrong, because it sounds like memory.
/// </summary>
public sealed record LensAnchor(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("relation")] string Relation);

/// <summary>
/// The lens object, as generated and as stored in <see cref="TitleLensCopy.LensJson"/>.
///
/// <para><b>No numbers in the prose, ever.</b> The model is given the calibrated prediction and its
/// error bar as CONTEXT — so it can pitch a 8.4 differently from a 6.1 — and is explicitly forbidden
/// from restating either. That is what keeps the model artifact out of the cache key and the cache
/// warm across a retrain.</para>
///
/// <para><b>First act only.</b> Every plot fact must come from the supplied overview and keywords,
/// and nothing past the first act may be described. A deterministic lexicon enforces it after the
/// fact, because the model's own promise that it did not spoil anything is worth nothing.</para>
/// </summary>
public sealed record LensNote(
    /// <summary>≤420 chars. The premise, retold in this viewer's own taste vocabulary, second person.</summary>
    [property: JsonPropertyName("lens")] string Lens,
    /// <summary>≤90 chars. The one reason to press play tonight — the card and push headline.</summary>
    [property: JsonPropertyName("hook")] string Hook,
    /// <summary>≤120 chars, or null. The honest reservation. The one field allowed to argue against the pick.</summary>
    [property: JsonPropertyName("caution")] string? Caution,
    /// <summary>Up to 3 titles from the user's own ratings, loved or hated, each with its relation.</summary>
    [property: JsonPropertyName("anchors")] IReadOnlyList<LensAnchor> Anchors,
    /// <summary>One of <see cref="LensVocabulary.Moods"/>.</summary>
    [property: JsonPropertyName("mood")] string Mood,
    /// <summary>One of <see cref="LensVocabulary.WhenToWatch"/>.</summary>
    [property: JsonPropertyName("whenToWatch")] string WhenToWatch)
{
    public const int MaxLensChars = 420;
    public const int MaxHookChars = 90;
    public const int MaxCautionChars = 120;
    public const int MaxAnchors = 3;

    /// <summary>
    /// What the PROMPT and the response schema ask for — roughly a tenth under the hard ceilings
    /// above, which stay where they are as the parse and storage limit.
    ///
    /// <para><b>Why the two numbers differ.</b> A structured-output <c>maxLength</c> is enforced by
    /// constrained decoding: the string stops at exactly that character, mid-word if that is where
    /// the budget ran out. Live copy came back reading "…a crowd-pleasing sw" and "…marriage
    /// strain,." for precisely that reason. Asking for less than the ceiling gives the model room
    /// to land its own sentence before the hard stop, which is a better fix than tidying up after
    /// one — <c>LensGuards.Tidy</c> is the backstop for the times it still overruns, not the
    /// policy.</para>
    ///
    /// <para>Keeping <see cref="MaxLensChars"/> and friends at their old values matters: every note
    /// already persisted was written against them, and narrowing the parse ceiling would make
    /// perfectly good stored copy unreadable.</para>
    /// </summary>
    public const int RequestLensChars = 378;

    /// <inheritdoc cref="RequestLensChars"/>
    public const int RequestHookChars = 81;

    /// <inheritdoc cref="RequestLensChars"/>
    public const int RequestCautionChars = 108;

    /// <summary>
    /// The first clause of <see cref="Lens"/> — what a rail card's two-line why and the hero's
    /// second line render — or null when the lens opens on nothing a pick card may carry.
    ///
    /// <para><b>A complete clause, never a severed one.</b> The old rule took the first sentence
    /// when it fitted and otherwise cut at the last space before the limit and added "…" — and a
    /// lens opens on a sentence of about 200 characters (median over the 316 stored bodies; 249 of
    /// them run past 160), so most cards read as sentences broken off mid-thought. Nothing here
    /// ever ends in "…". In order, the first that fits wins:</para>
    /// <list type="number">
    /// <item>the first sentence, read with abbreviations (S.W.A.T., Mr., U.S.) left whole;</item>
    /// <item>its leading clauses, split only where a clause genuinely ends — a semicolon, a colon,
    /// a dash, or a comma that opens a "though", "but", "while", "where", "which" or "as"
    /// clause;</item>
    /// <item>the longest prefix that ends on a comma or before "where", "while", "then" and their
    /// kin, once the main clause is under way — "Ginger Snaps gives you two outcast sisters in
    /// Bailey Downs." out of a 215-character sentence. Never inside an opening "If you like…"
    /// clause, and never when the main clause opens on a list ("Heists, double-crosses…"), where
    /// the first comma would leave a bare noun. A cut never falls inside a closed apposition
    /// ("Liam, an ex-con, back in…"), between two stacked adjectives ("two cold, calculating
    /// men"), or before an "after" a participle needs ("sent after"); a list cut short is
    /// re-closed with its own "and" ("uses intuition and dreams.");</item>
    /// <item>the same two steps on the main clause alone, with its "If you like…" opener
    /// dropped;</item>
    /// <item>null — the card shows its own line. A missing caption is honest; a severed one is
    /// the defect.</item>
    /// </list>
    /// Every cut closes with a full stop.
    ///
    /// <para><b>Never a hedge.</b> A lens may weigh the pick in its body, but its first clause
    /// captions the card, and a caption that argues against its own pick ("…though this is a much
    /// slower drama than you often prefer") is the defect the round-1 review rendered on Home.
    /// No candidate that hedges (<see cref="Ports.SecondPersonVoice.SoundsHedged"/>) is taken;
    /// when the opening clause itself hedges there is nothing to show and the result is null.</para>
    /// </summary>
    public string? FirstClause(int maxChars = 160)
    {
        var text = Lens.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var sentence = LensClauses.FirstSentence(text);
        if (LensClauses.IsOnlyTaste(sentence))
        {
            // "You favor brisk thrillers and sci-fi." is about the viewer; the title starts next.
            var rest = text[sentence.Length..].TrimStart();
            if (rest.Length == 0)
            {
                return null;
            }

            sentence = LensClauses.FirstSentence(rest);
        }

        if (sentence.Length <= maxChars && !Ports.SecondPersonVoice.SoundsHedged(sentence))
        {
            return sentence;
        }

        // A lens that OPENS on a hedge ("Though it is slower than you usually like, …") has
        // framed the pick against itself; showing what follows would spin that framing positive.
        var mainStart = LensClauses.MainClauseStart(sentence);
        if (mainStart < 0 || Ports.SecondPersonVoice.SoundsHedged(sentence[..mainStart]))
        {
            return null;
        }

        var fitted = LensClauses.Fit(sentence, mainStart, maxChars);
        if (fitted is not null || mainStart == 0)
        {
            return fitted;
        }

        var main = LensClauses.Capitalise(sentence[mainStart..]);
        return main.Length <= maxChars && !Ports.SecondPersonVoice.SoundsHedged(main)
            ? main
            : LensClauses.Fit(main, 0, maxChars);
    }
}

/// <summary>
/// The sentence and clause boundaries <see cref="LensNote.FirstClause"/> cuts on. Separate and
/// internal-facing so each rule is testable on its own, and so the abbreviation list has one home.
/// </summary>
public static partial class LensClauses
{
    /// <summary>One clause and the separator that came before it (empty for the first).</summary>
    public sealed record Clause(string Separator, string Text);

    /// <summary>
    /// Words that end in a full stop without ending a sentence. Matched case-insensitively against
    /// the token before the stop. Initialisms with internal stops (S.W.A.T., U.S., L.A.) are caught
    /// separately, by shape.
    /// </summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "st", "jr", "sr", "prof", "vs", "vol", "mt", "ft", "lt", "sgt", "capt", "col", "gen", "rev",
    };

    /// <summary>
    /// The words that open a subordinate or contrasting clause after a comma. A comma before any
    /// other word is a list or an apposition and is never a cut point.
    /// </summary>
    private static readonly string[] ClauseOpeners =
    [
        "though", "although", "even though", "even if", "but", "yet", "while", "whereas", "where", "which", "as", "until", "before", "after",
        "then", "and then",
    ];

    /// <summary>
    /// The first sentence: up to and including the first '.', '!' or '?' that is followed by the end
    /// of the text or by whitespace, and that is not the stop of an abbreviation or an initialism.
    /// The whole text when no such boundary exists.
    /// </summary>
    public static string FirstSentence(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is not ('.' or '!' or '?'))
            {
                continue;
            }

            // A run of stops ("?!", "...") ends together.
            var end = i;
            while (end + 1 < text.Length && text[end + 1] is '.' or '!' or '?')
            {
                end++;
            }

            var atEnd = end + 1 >= text.Length;
            if (!atEnd && !char.IsWhiteSpace(text[end + 1]) && text[end + 1] is not ('"' or '\'' or '\u201D' or '\u2019' or ')'))
            {
                // "S.W.A.T", "3.5", "U.S.-made": a stop inside a token.
                i = end;
                continue;
            }

            if (c == '.' && end == i && IsAbbreviation(text, i))
            {
                continue;
            }

            // Keep a closing quote or bracket with its sentence.
            var close = end + 1;
            while (close < text.Length && text[close] is '"' or '\'' or '\u201D' or '\u2019' or ')')
            {
                close++;
            }

            return text[..close].TrimEnd();
        }

        return text;
    }

    /// <summary>
    /// <paramref name="sentence"/> split into clauses at semicolons, colons, spaced dashes, and
    /// commas that open a clause (see <see cref="ClauseOpeners"/>). The first clause has an empty
    /// separator; joining every separator and text gives the sentence back.
    /// </summary>
    public static IReadOnlyList<Clause> Split(string sentence)
    {
        var clauses = new List<Clause>();
        var separator = string.Empty;
        var from = 0;
        var i = 0;
        while (i < sentence.Length)
        {
            var cut = CutAt(sentence, i);
            if (cut > 0)
            {
                clauses.Add(new Clause(separator, sentence[from..i]));
                separator = sentence[i..(i + cut)];
                from = i + cut;
                i = from;
                continue;
            }

            i++;
        }

        clauses.Add(new Clause(separator, sentence[from..]));
        return clauses;
    }

    /// <summary>Ends a clause cut from a longer sentence with a full stop, unless it already ends a sentence.</summary>
    public static string Close(string clause)
    {
        var trimmed = clause.TrimEnd(',', ';', ':', ' ', '\u2014', '\u2013', '-');
        return trimmed.Length > 0 && trimmed[^1] is '.' or '!' or '?' ? trimmed : trimmed + ".";
    }

    /// <summary>
    /// The words that open a lens's introductory clause — "If you like…", "Since you lean…", "For
    /// your…", "With your taste…". A sentence that opens on one has its main clause after a later
    /// comma, and nothing before that point is a sentence on its own.
    /// </summary>
    private static readonly HashSet<string> Openers = new(StringComparer.Ordinal)
    {
        "If", "Since", "For", "Because", "When", "While", "Although", "Though", "As", "After", "Before", "Given",
        "With", "Whether", "Unlike", "Despite", "Once", "Until", "Whenever", "Even",
    };

    /// <summary>
    /// A sentence about the viewer rather than the title — "You usually reach for brisk
    /// thrillers, but this…", "You tend to favour action…". It frames the pick and captions
    /// nothing; the pick starts after its comma.
    /// </summary>
    [GeneratedRegex(@"^You (?:usually|often|tend to|typically|normally|mostly|generally|lean|favou?r|prefer|rate|reach|want|like|love|gravitate)\b")]
    private static partial Regex TasteOpener();

    /// <summary>Whether <paramref name="sentence"/> is about the viewer and never reaches a main clause about the title.</summary>
    public static bool IsOnlyTaste(string sentence) => TasteOpener().IsMatch(sentence) && MainClauseStart(sentence) < 0;

    /// <summary>Intensifiers that leave a phrase open whatever follows: "more intimate," is half of "more intimate, slower drama".</summary>
    private static readonly HashSet<string> Intensifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "more", "less", "so", "very", "too", "quite", "rather", "most", "fairly", "pretty",
    };

    /// <summary>
    /// Words that may follow a comma without continuing the adjective before it: a determiner or a
    /// name opens the next list item ("a boy, a dog"), a conjunction or relative opens a clause.
    /// </summary>
    private static readonly HashSet<string> NewPhrase = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "then", "with", "plus", "while", "where", "who", "which", "as", "so", "before", "after",
        "until", "his", "her", "their", "its", "your", "this", "that", "these", "those", "some", "one", "two", "three", "all", "both",
        "now", "only", "each", "every", "from", "into", "in", "on", "at", "for", "to",
    };

    /// <summary>Subjects that start a main clause after an opener's comma ("…, this gives you").</summary>
    private static readonly HashSet<string> PronounSubjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "this", "that", "these", "those", "it", "you", "you're", "you\u2019re", "he", "she", "they", "we", "here", "there",
    };

    /// <summary>Verbs that carry a lens's main clause without the usual third-person "-s".</summary>
    private static readonly HashSet<string> BareVerbs = new(StringComparer.Ordinal)
    {
        "get", "keep", "have", "are", "were", "was", "can", "will", "must", "make", "take", "move", "find", "land", "meet",
    };

    /// <summary>
    /// Words a cut may not leave at the end of a caption: a conjunction, an article, a preposition
    /// or a determiner promises a word that a cut there would remove.
    /// </summary>
    private static readonly HashSet<string> Dangling = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "but", "nor", "yet", "so", "with", "without", "of", "the", "a", "an", "to", "in", "into", "on", "onto", "for", "from",
        "by", "at", "as", "such", "than", "plus", "his", "her", "their", "its", "your", "my", "our", "this", "that", "who", "which",
        "very", "just", "more", "most", "then", "both", "either", "each", "every", "over", "under", "between", "about", "like",
    };

    /// <summary>Where, besides a comma, a caption may end inside a main clause: before a subordinate "where", "while", "then"…</summary>
    private static readonly string[] CutWords =
    [
        "where ", "while ", "when ", "whose ", "who ", "until ", "then ", "and then ", "before ", "after ",
    ];

    /// <summary>
    /// Where the main clause of <paramref name="sentence"/> begins: 0 when the sentence opens on
    /// it, the index after an introductory clause's comma when it opens on one of
    /// <see cref="Openers"/>, and -1 when it opens on one and no main clause can be found.
    ///
    /// <para>The intro ends at the first comma followed by a pronoun subject ("…, this gives you")
    /// or by a capitalised word whose run up to the next comma carries a verb ("…, Ginger Snaps
    /// gives you"). The verb test is what keeps "If you liked The Bone Collector, Fracture, and
    /// Source Code, this…" from taking "Fracture" for the main clause, and an adjective list ("If
    /// you like your picks dark, brisk, and odd, …") never qualifies because it is lower case.</para>
    /// </summary>
    public static int MainClauseStart(string sentence)
    {
        var taste = TasteOpener().IsMatch(sentence);
        if (!taste && !Openers.Contains(FirstWord(sentence, 0)))
        {
            return 0;
        }

        for (var i = sentence.IndexOf(',', StringComparison.Ordinal); i >= 0; i = sentence.IndexOf(',', i + 1))
        {
            var at = i + 1 + Spaces(sentence, i + 1);
            var word = FirstWord(sentence, at);

            // "You usually reach for thrillers, but this keeps…": the taste statement hands over
            // to the pick through a conjunction.
            if (taste && word is "but" or "so" or "yet" or "and")
            {
                at += word.Length + Spaces(sentence, at + word.Length);
                word = FirstWord(sentence, at);
            }

            if (word.Length == 0)
            {
                continue;
            }

            if (PronounSubjects.Contains(word))
            {
                return at;
            }

            if (char.IsUpper(word[0]) && word is not ("And" or "Or" or "But" or "Nor") && CarriesVerb(sentence, at))
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>
    /// The longest complete caption of at most <paramref name="maxChars"/> characters that
    /// <paramref name="sentence"/> yields once its main clause (from <paramref name="mainStart"/>)
    /// is under way: leading clauses first, then a cut at a comma or before a subordinate word.
    /// Closed with a full stop; null when no such cut exists or every one hedges.
    /// </summary>
    public static string? Fit(string sentence, int mainStart, int maxChars)
    {
        var room = maxChars - 1;

        // Leading clauses, joined while they fit and none of them hedges.
        var clauses = Split(sentence);
        string? joined = null;
        var prefix = string.Empty;
        foreach (var clause in clauses)
        {
            var candidate = prefix + clause.Separator + clause.Text;
            if (candidate.Length > room || Ports.SecondPersonVoice.SoundsHedged(candidate))
            {
                break;
            }

            prefix = candidate;
            joined = candidate;
        }

        if (joined is not null && KeepsMainClause(joined, mainStart, 3))
        {
            return Close(joined);
        }

        // A cut inside the main clause. Not when it opens on a list ("Heists, double-crosses…"):
        // every cut would leave a bare noun or a fragment of nouns. An apposition that closes
        // ("this puts Liam, an ex-con, back in London…") is let through, but no cut may fall
        // inside it.
        var from = mainStart + 1;
        var firstComma = sentence.IndexOf(',', mainStart);
        if (firstComma >= 0 && MainWords(sentence[..firstComma], mainStart) < 4)
        {
            var closes = ClosedApposition(sentence, firstComma);
            if (closes < 0)
            {
                return null;
            }

            from = closes + 1;
        }

        string? best = null;
        for (var p = from; p < sentence.Length && p <= room; p++)
        {
            if (!IsCutPoint(sentence, p) || SplitsModifiers(sentence, p))
            {
                continue;
            }

            var cut = sentence[..p].TrimEnd();
            if (!KeepsMainClause(cut, mainStart, 4) || Dangling.Contains(LastWord(cut)) || OpensPhrase(sentence, cut) || !Balanced(cut))
            {
                continue;
            }

            // A comma inside a list whose closing "and" is still ahead: the kept items are
            // re-closed with their own "and" ("uses intuition, dreams, and chats…" → "uses
            // intuition and dreams."), so the caption never reads as a list cut off mid-way.
            if (sentence[p] == ',' && ListContinues(sentence, p))
            {
                cut = CloseList(cut, Math.Max(mainStart, from - 1));
            }

            // The cut must not stop just short of a hedge it belongs to: "this is a gentler," is
            // the front of "a gentler, dreamlike detour".
            var ahead = sentence.IndexOfAny([',', ';', ':', '\u2014', '\u2013'], p + 1);
            if (Ports.SecondPersonVoice.SoundsHedged(ahead < 0 ? sentence : sentence[..ahead]))
            {
                continue;
            }

            var closed = Close(cut);
            if (closed.Length <= maxChars && !Ports.SecondPersonVoice.SoundsHedged(closed))
            {
                best = closed;
            }
        }

        return best;
    }

    /// <summary>
    /// Words that open a new phrase after a comma rather than another item of a list: "…, with
    /// Josh caught…", "…, then sends him north", "…, who works…". A participle ("…, trying to win
    /// back…", "…, dodging booby traps") opens one too and is checked by shape.
    /// </summary>
    private static readonly HashSet<string> PhraseOpeners = new(StringComparer.OrdinalIgnoreCase)
    {
        "with", "without", "then", "where", "while", "when", "who", "whose", "which", "so", "but", "yet", "as", "until", "before",
        "after", "plus", "all", "each", "both", "now", "only", "even", "though", "although", "because", "since", "if",
    };

    /// <summary>
    /// Adjectives a lens stacks in front of a noun ("two cold, calculating young men", "intense,
    /// confusing messages"). A comma after one of these, before another modifier, sits inside a
    /// noun phrase, and a cut there strands the adjective. Suffix-shaped adjectives (-ous, -ive,
    /// -ful, -less, -ish) are caught by shape.
    /// </summary>
    private static readonly HashSet<string> StackedAdjectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "cold", "dark", "bleak", "brisk", "tense", "intense", "grim", "quiet", "small", "big", "old", "young", "long", "short",
        "slow", "fast", "hard", "soft", "warm", "odd", "strange", "raw", "gritty", "lean", "mean", "tight", "sharp", "bright",
        "light", "heavy", "rough", "wild", "bruised", "sour", "brutal", "violent", "bloody", "moody", "eerie", "creepy", "funny",
        "sad", "sweet", "bitter", "messy", "noisy", "lonely", "weird", "slick", "glossy", "stylish", "brooding", "sprawling",
        "cramped", "remote", "isolated", "rich", "poor", "desperate", "earnest", "playful", "whimsical", "chilling", "haunting",
        "gripping", "unsettling", "harrowing", "sinister", "savage", "fierce", "gentle", "tender", "intimate", "epic", "classic",
        "modern", "ancient", "rural", "urban", "suburban", "gruesome", "grimy", "seedy", "sleazy", "smart", "clever",
    };

    /// <summary>
    /// Whether the comma at <paramref name="p"/> separates two modifiers of one noun: the word
    /// before it is an adjective and the word after it is a lower-case word that opens nothing new.
    /// </summary>
    private static bool SplitsModifiers(string s, int p)
    {
        if (s[p] != ',')
        {
            return false;
        }

        var before = LastWord(s[..p]);
        var at = p + 1 + Spaces(s, p + 1);
        var after = FirstWord(s, at);
        if (after.Length == 0 || !char.IsLower(after[0]) || NewPhrase.Contains(after) || PhraseOpeners.Contains(after))
        {
            return false;
        }

        return StackedAdjectives.Contains(before)
            || (before.Length > 4 && (before.EndsWith("ous", StringComparison.OrdinalIgnoreCase)
                || before.EndsWith("ive", StringComparison.OrdinalIgnoreCase)
                || before.EndsWith("ful", StringComparison.OrdinalIgnoreCase)
                || before.EndsWith("less", StringComparison.OrdinalIgnoreCase)
                || before.EndsWith("ish", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Whether a comma is followed by a new phrase rather than another list item.</summary>
    private static bool OpensNewPhrase(string s, int comma)
    {
        var at = comma + 1 + Spaces(s, comma + 1);
        var word = FirstWord(s, at);
        return word.Length > 0
            && (PhraseOpeners.Contains(word)
                || (word.Length > 4 && char.IsLower(word[0]) && word.EndsWith("ing", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Whether the comma at <paramref name="comma"/> sits inside a list that closes later: it is
    /// not followed by a new phrase, and an "and" or "or" comes before the list's run ends — at a
    /// clause boundary, at a comma that opens a new phrase, or at the end of the sentence.
    /// </summary>
    private static bool ListContinues(string s, int comma)
    {
        if (OpensNewPhrase(s, comma))
        {
            return false;
        }

        var end = s.Length;
        for (var i = comma + 1; i < s.Length; i++)
        {
            if (s[i] is ';' or ':' or '—' or '–' || (s[i] == ',' && OpensNewPhrase(s, i)))
            {
                end = i;
                break;
            }
        }

        return ListCloser().IsMatch(s, comma + 1) && ListCloser().Match(s, comma + 1).Index < end;
    }

    [GeneratedRegex(@"\b(?:and|or)\b")]
    private static partial Regex ListCloser();

    /// <summary>
    /// Re-closes the list a cut ended in: the last comma of the kept run (after
    /// <paramref name="floor"/>) becomes " and ". Left alone when the kept run is a single item,
    /// when that comma opens a new phrase, or when it is an apposition after a name ("Liam, an
    /// ex-con").
    /// </summary>
    private static string CloseList(string cut, int floor)
    {
        var last = cut.LastIndexOf(',');
        if (last <= floor || OpensNewPhrase(cut, last))
        {
            return cut;
        }

        var item = cut[(last + 1)..].TrimStart();
        if (ListCloser().IsMatch(item))
        {
            return cut;
        }

        var before = LastWord(cut[..last]);
        var first = FirstWord(cut, last + 1 + Spaces(cut, last + 1));
        if (before.Length > 0 && char.IsUpper(before[0]) && first is "a" or "an" or "the")
        {
            return cut;
        }

        return cut[..last] + " and " + item;
    }

    /// <summary>
    /// When the comma at <paramref name="comma"/> opens an apposition ("Liam, an ex-con, back
    /// in…"), the index of the comma that closes it; otherwise -1.
    /// </summary>
    private static int ClosedApposition(string s, int comma)
    {
        var at = comma + 1 + Spaces(s, comma + 1);
        var first = FirstWord(s, at);
        if (first is not ("a" or "an" or "the"))
        {
            return -1;
        }

        var close = s.IndexOf(',', at);
        return close > 0 && s[at..close].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 6 ? close : -1;
    }

    /// <summary>The main clause on its own, as a sentence: its first letter capitalised.</summary>
    public static string Capitalise(string clause) =>
        clause.Length == 0 ? clause : char.ToUpperInvariant(clause[0]) + clause[1..];

    private static bool IsCutPoint(string s, int p)
    {
        if (s[p] == ',')
        {
            return true;
        }

        if (s[p] != ' ')
        {
            return false;
        }

        foreach (var word in CutWords)
        {
            if (string.Compare(s, p + 1, word, 0, word.Length, StringComparison.Ordinal) == 0)
            {
                // "a new Terminator sent after the future leader": after a participle, "after",
                // "before" and "until" are prepositions the verb needs, not a clause it can drop.
                return word is not ("before " or "after " or "until ") || !EndsOnParticiple(s[..p]);
            }
        }

        return false;
    }

    /// <summary>Participles that take their "after"/"before" with them: "sent after", "left before".</summary>
    private static readonly HashSet<string> IrregularParticiples = new(StringComparer.OrdinalIgnoreCase)
    {
        "sent", "left", "held", "kept", "set", "built", "put", "brought", "thrown", "lost", "born", "shot", "caught", "taken",
        "given", "driven", "hidden", "stolen", "found", "led", "run", "come", "gone", "named",
    };

    private static bool EndsOnParticiple(string head)
    {
        var word = LastWord(head);
        return IrregularParticiples.Contains(word) || (word.Length > 3 && word.EndsWith("ed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether the run at <paramref name="at"/>, up to the next comma or semicolon, carries a verb.
    /// Not up to a stop or a colon: an initialism ("L.I.E. drops you") and a title's colon ("Santa
    /// Paws 2: The Santa Pups gives you") sit inside the subject.
    /// </summary>
    private static bool CarriesVerb(string s, int at)
    {
        var end = s.IndexOfAny([',', ';'], at);
        return HasVerb(s[at..(end < 0 ? s.Length : end)]);
    }

    /// <summary>
    /// Whether <paramref name="run"/> has, after its first word, a lower-case word shaped like a
    /// lens's main verb: third-person "-s" ("gives", "puts", "is") or one of <see cref="BareVerbs"/>.
    /// A heuristic, and only ever a guard — it decides whether a capitalised word after an opener's
    /// comma starts the main clause, and whether a cut kept any of it.
    /// </summary>
    private static bool HasVerb(string run)
    {
        var words = run.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < words.Length; i++)
        {
            var w = words[i].Trim('"', '\'', '\u201C', '\u201D', '(', ')');
            if (w.Length < 2 || !char.IsLower(w[0]) || !w.All(c => char.IsLetter(c) || c == '-'))
            {
                continue;
            }

            if (BareVerbs.Contains(w) || (w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) && !w.EndsWith("us", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a caption keeps at least <paramref name="minWords"/> words of the main clause — and,
    /// after an opener, its verb: "If you like it bright, Santa Paws 2." keeps a title and no
    /// sentence.
    /// </summary>
    private static bool KeepsMainClause(string caption, int mainStart, int minWords) =>
        MainWords(caption, mainStart) >= minWords && (mainStart == 0 || HasVerb(caption[mainStart..]));

    private static int MainWords(string prefix, int mainStart) =>
        prefix.Length <= mainStart ? 0 : prefix[mainStart..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string FirstWord(string s, int at)
    {
        var end = at;
        while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] is '\'' or '\u2019' or '-' or '/'))
        {
            end++;
        }

        return s[at..end];
    }

    /// <summary>
    /// Whether <paramref name="cut"/> stops inside a noun phrase: after an intensifier ("more
    /// intimate,"), or after an article and one word when the comma is followed by another
    /// modifier rather than a new item — "this is a gentler, dreamlike detour" cut to "this is a
    /// gentler." — while "a boy, a dog" and "his trail, so" stay cuttable.
    /// </summary>
    private static bool OpensPhrase(string sentence, string cut)
    {
        var words = cut.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return false;
        }

        if (Intensifiers.Contains(words[^2]))
        {
            return true;
        }

        if (words[^2] is not ("a" or "an" or "the" or "A" or "An" or "The"))
        {
            return false;
        }

        var after = cut.Length + 1;
        after += Spaces(sentence, Math.Min(after, sentence.Length));
        var next = after < sentence.Length ? FirstWord(sentence, after) : string.Empty;
        return next.Length > 0 && char.IsLower(next[0]) && !NewPhrase.Contains(next);
    }

    private static string LastWord(string s)
    {
        var start = s.Length;
        while (start > 0 && !char.IsWhiteSpace(s[start - 1]))
        {
            start--;
        }

        return s[start..].Trim(',', ';', ':', '"', '\u201D', ')');
    }

    /// <summary>Whether every bracket and double quote opened in <paramref name="s"/> is closed in it.</summary>
    private static bool Balanced(string s) =>
        s.Count(c => c == '(') == s.Count(c => c == ')')
        && s.Count(c => c == '"') % 2 == 0
        && s.Count(c => c == '\u201C') == s.Count(c => c == '\u201D');

    /// <summary>The length of the separator starting at <paramref name="i"/>, or 0 when no clause ends there.</summary>
    private static int CutAt(string s, int i)
    {
        var c = s[i];
        if (c is ';' or ':')
        {
            return 1 + Spaces(s, i + 1);
        }

        // A dash between clauses — spaced (" — ", " - ") or closed up ("dread—and") — never a
        // hyphen inside a word ("small-town").
        if (c is '\u2014' or '\u2013' && (i == 0 || s[i - 1] != ' '))
        {
            return 1 + Spaces(s, i + 1);
        }

        if (c == ' ' && i + 1 < s.Length
            && (s[i + 1] is '\u2014' or '\u2013' || (s[i + 1] == '-' && i + 2 < s.Length && s[i + 2] == ' ')))
        {
            return 2 + Spaces(s, i + 2);
        }

        if (c == ',')
        {
            var after = i + 1 + Spaces(s, i + 1);
            foreach (var opener in ClauseOpeners)
            {
                if (string.Compare(s, after, opener, 0, opener.Length, StringComparison.OrdinalIgnoreCase) == 0
                    && (after + opener.Length >= s.Length || !char.IsLetter(s[after + opener.Length])))
                {
                    return after - i;
                }
            }
        }

        return 0;
    }

    private static int Spaces(string s, int from)
    {
        var n = 0;
        while (from + n < s.Length && s[from + n] == ' ')
        {
            n++;
        }

        return n;
    }

    /// <summary>Whether the '.' at <paramref name="dot"/> closes an abbreviation or an initialism.</summary>
    private static bool IsAbbreviation(string text, int dot)
    {
        var start = dot;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] is not ('(' or '"' or '\u201C'))
        {
            start--;
        }

        var token = text[start..dot];
        if (token.Length == 0)
        {
            return false;
        }

        // An initialism: single letters separated by stops ("S.W.A.T", "U.S", "L.A"), or one capital.
        if (token.Length == 1 && char.IsUpper(token[0]))
        {
            return true;
        }

        if (token.Contains('.') && token.Split('.').All(part => part.Length == 1 && char.IsLetter(part[0])))
        {
            return true;
        }

        return Abbreviations.Contains(token);
    }
}
