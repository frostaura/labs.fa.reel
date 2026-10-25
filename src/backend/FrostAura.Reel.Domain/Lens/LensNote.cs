using System.Text.Json.Serialization;

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
    /// lens is 378 characters of two or three long sentences, so 6 of 8 cards on Home read as
    /// sentences broken off mid-thought (round-1 surface review). Now: the first sentence, read with
    /// abbreviations (S.W.A.T., Mr., U.S.) left whole; if it is too long, its leading clauses up to
    /// the limit, split only where a clause genuinely ends — a semicolon, a dash, or a comma that
    /// opens a "though", "but", "while", "where", "which" or "as" clause, never a comma inside a
    /// list — closed with a full stop. The word-boundary trim with "…" is kept only for an opening
    /// clause that is itself longer than the limit.</para>
    ///
    /// <para><b>Never a hedge.</b> A lens may weigh the pick in its body, but its first clause
    /// captions the card, and a caption that argues against its own pick ("…though this is a much
    /// slower drama than you often prefer") is the defect the round-1 review rendered on Home.
    /// Clauses are taken only up to the first one that hedges
    /// (<see cref="Ports.SecondPersonVoice.SoundsHedged"/>); when the opening clause itself hedges
    /// there is nothing to show and the result is null, so the card falls back to its own line.</para>
    /// </summary>
    public string? FirstClause(int maxChars = 160)
    {
        var text = Lens.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var sentence = LensClauses.FirstSentence(text);
        if (sentence.Length <= maxChars && !Ports.SecondPersonVoice.SoundsHedged(sentence))
        {
            return sentence;
        }

        var clauses = LensClauses.Split(sentence);
        var kept = clauses[0].Text;
        if (Ports.SecondPersonVoice.SoundsHedged(kept))
        {
            return null;
        }

        // Room for the full stop that closes a clause cut out of a longer sentence.
        var room = maxChars - 1;
        for (var i = 1; i < clauses.Count; i++)
        {
            var candidate = kept + clauses[i].Separator + clauses[i].Text;
            if (candidate.Length > room || Ports.SecondPersonVoice.SoundsHedged(candidate))
            {
                break;
            }

            kept = candidate;
        }

        if (kept.Length <= room)
        {
            return LensClauses.Close(kept);
        }

        // One opening clause longer than the whole limit: the word-boundary trim is the only way
        // left to show something, and the ellipsis says honestly that it was cut.
        var cut = kept.LastIndexOf(' ', Math.Min(maxChars, kept.Length - 1));
        return (cut > 0 ? kept[..cut] : kept[..maxChars]).TrimEnd(',', ';', ':', ' ', '\u2014', '\u2013', '-') + "\u2026";
    }
}

/// <summary>
/// The sentence and clause boundaries <see cref="LensNote.FirstClause"/> cuts on. Separate and
/// internal-facing so each rule is testable on its own, and so the abbreviation list has one home.
/// </summary>
public static class LensClauses
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
