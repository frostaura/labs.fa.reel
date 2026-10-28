using System.Globalization;
using System.Text.RegularExpressions;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// The titles a message may NAME, read deterministically: every run of its words that could be a
/// catalogue name, for the engine to match against <c>Titles.Name</c> exactly. This is the server's
/// fallback for a question about a title that the planner left without a reference.
///
/// <para><b>Why (HREV3-ASK-A, HREV3-RET-06).</b> "Is Blade of the 47 Ronin a ninja film or a samurai
/// film?" was planned as an item question with no reference and no name, and the turn asked back
/// "which title do you mean?" in 1.4 s about a title held under that exact name. A name the user typed
/// is in their words; whether the model copied it out is not something the answer may depend on.</para>
///
/// <para><b>What counts as a candidate.</b> A run of up to <see cref="MaxSpanWords"/> whitespace
/// tokens, matched as typed (lower-cased) and with surrounding punctuation and a trailing possessive
/// trimmed ("Heat's", "Airplane!"), so it equals a stored name exactly. A run made only of filler,
/// pointers, media words or cues ("the first one", "a ninja film") is never a candidate, and a
/// single word only when the user capitalised it: "is it violent?" must not answer about It (2017),
/// while "is Heat violent?" names Heat. The engine prefers the LONGEST run that names a title, then
/// the most-voted title under that name.</para>
///
/// <para><b>The user's own signal, at any length (HREV4, refuter round 4).</b> A run of several
/// lower-cased words is a common phrase far more often than a name: "is it based on a true story?"
/// answered about Based on a True Story (2017), "does it have a happy ending?" about Happy Ending
/// (2023), "is it a love story?" about Love Story (2026) and "is the dog okay at the end?" about The
/// End? (2017) — each an item question with nothing on screen that should have been asked back. So a
/// run read from a MESSAGE names a title only when it carries the user's own signal: a word that is
/// not filler and is capitalised or digit-led as typed, or quotes around the run. A capital the
/// sentence imposes (the message's first word, a word after a full stop) is not that signal in a run
/// of several words — "Based on a true story, is it?" is asked back — but still is for a single word,
/// so "Heat, is it violent?" keeps naming Heat. A name the PLANNER wrote is read without that bar
/// (<see cref="NameSpans"/>): the model already said it is a name.</para>
/// </summary>
public static partial class AskReelTitleMention
{
    /// <summary>The longest run of words read as one name.</summary>
    public const int MaxSpanWords = 10;

    /// <summary>The most words of a message read for names (a question is short; a pasted essay is not one).</summary>
    public const int MaxMessageWords = 40;

    /// <summary>One run of the message's words that may be a stored name.</summary>
    /// <param name="Text">The run, lower-cased, exactly as it would equal a lower-cased stored name.</param>
    /// <param name="Words">How many words it spans: the longer run wins.</param>
    public readonly record struct Span(string Text, int Words);

    /// <summary>Every candidate run in the user's <paramref name="message"/>, longest first, each text once.</summary>
    public static IReadOnlyList<Span> Spans(string? message) => Read(message, userTyped: true);

    /// <summary>
    /// Every candidate run in a name the planner wrote, longest first: the same runs without the bar on
    /// the user's own signal, since the planner already called the text a name (a single word still
    /// needs its capital, as before).
    /// </summary>
    public static IReadOnlyList<Span> NameSpans(string? name) => Read(name, userTyped: false);

    private static IReadOnlyList<Span> Read(string? message, bool userTyped)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return [];
        }

        var tokens = message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(MaxMessageWords).ToArray();
        var spans = new List<Span>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var length = Math.Min(MaxSpanWords, tokens.Length); length >= 1; length--)
        {
            for (var start = 0; start + length <= tokens.Length; start++)
            {
                var run = tokens.AsSpan(start, length).ToArray();
                if (!Plausible(run) || (userTyped && length > 1 && !Signalled(tokens, start, length)))
                {
                    continue;
                }

                foreach (var text in Variants(string.Join(' ', run)))
                {
                    if (seen.Add(text))
                    {
                        spans.Add(new Span(text, length));
                    }
                }
            }
        }

        return spans;
    }

    /// <summary>
    /// A name as the planner (or a user) wrote it, ready to equal a lower-cased stored name: trimmed of
    /// quotes and surrounding punctuation, and of a trailing bracketed year ("Heat (1995)").
    /// </summary>
    public static string Normalise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = TrailingYear().Replace(name.Trim(), string.Empty);
        return trimmed.Trim().Trim(Quotes).Trim().ToLowerInvariant();
    }

    /// <summary>The bracketed year a name carries at its end ("Heat (1995)" → 1995), or null.</summary>
    public static int? YearOf(string? name) =>
        name is not null && TrailingYear().Match(name.Trim()) is { Success: true } m
            ? int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>The four-digit years a message mentions (1900–2099), for preferring one title of a shared name.</summary>
    public static IReadOnlySet<int> YearsIn(string? message) =>
        message is null
            ? new HashSet<int>()
            : YearToken().Matches(message).Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToHashSet();

    /// <summary>
    /// Whether a run can be a name: not only filler, and — one word — capitalised as typed. A run
    /// that starts or ends inside a clause's punctuation is still a run; <see cref="Variants"/> trims it.
    /// </summary>
    private static bool Plausible(string[] run)
    {
        var words = run.SelectMany(AskReelIntentSanitizer.Words).ToList();
        if (words.Count == 0 || words.All(AskReelIntentSanitizer.IsFiller))
        {
            return false;
        }

        if (run.Length > 1)
        {
            return true;
        }

        var first = run[0].TrimStart(Edges).FirstOrDefault();
        return char.IsUpper(first) || char.IsDigit(first);
    }

    /// <summary>
    /// Whether the user marked a run of several words as a name: quotes around it, or a word in it that
    /// is not filler and is capitalised or digit-led as typed — a capital counting only where the
    /// sentence did not impose it (the message's first word, or a word after a full stop, a "!" or a "?").
    /// </summary>
    private static bool Signalled(string[] tokens, int start, int length)
    {
        var first = tokens[start];
        var last = tokens[start + length - 1].TrimEnd(SentencePunctuation);
        if (first.Length > 0 && Quotes.Contains(first[0]) && last.Length > 1 && Quotes.Contains(last[^1]))
        {
            return true;
        }

        for (var i = start; i < start + length; i++)
        {
            var token = tokens[i];
            if (AskReelIntentSanitizer.Words(token).All(AskReelIntentSanitizer.IsFiller))
            {
                continue;
            }

            var lead = token.TrimStart(Edges).FirstOrDefault();
            if (char.IsDigit(lead) || (char.IsUpper(lead) && !SentenceInitial(tokens, i)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the token at <paramref name="index"/> opens a sentence, so its capital is the sentence's, not the user's.</summary>
    private static bool SentenceInitial(string[] tokens, int index)
    {
        if (index == 0)
        {
            return true;
        }

        var previous = tokens[index - 1].TrimEnd(Closers);
        return previous.Length > 0 && previous[^1] is '.' or '!' or '?';
    }

    /// <summary>The run as typed, then unquoted, then trimmed of surrounding punctuation, then of a trailing possessive.</summary>
    private static IEnumerable<string> Variants(string run)
    {
        var lower = run.ToLowerInvariant();
        yield return lower;

        // Quotes only first, so a name that ends in its own punctuation survives ("“Airplane!”").
        var unquoted = lower.Trim(Quotes);
        if (unquoted.Length > 0)
        {
            yield return unquoted;
        }

        var trimmed = lower.Trim(Edges);
        if (trimmed.Length > 0)
        {
            yield return trimmed;
            foreach (var possessive in new[] { "'s", "’s" })
            {
                if (trimmed.EndsWith(possessive, StringComparison.Ordinal) && trimmed.Length > possessive.Length)
                {
                    yield return trimmed[..^possessive.Length].TrimEnd(Edges);
                }
            }
        }
    }

    /// <summary>Punctuation that bounds a name in a sentence and is never part of the stored name's edge.</summary>
    private static readonly char[] Edges = ['?', '!', ',', '.', ';', ':', '"', '\'', '“', '”', '‘', '’', '(', ')', '[', ']', '*', '_'];

    /// <summary>Punctuation a sentence puts after a closing quote ("does it have a “happy ending”?").</summary>
    private static readonly char[] SentencePunctuation = ['?', '!', ',', '.', ';', ':'];

    /// <summary>What may close a sentence-ending token after its full stop ('…the end." Is…').</summary>
    private static readonly char[] Closers = ['"', '\'', '”', '’', ')', ']', '»', '*', '_'];

    private static readonly char[] Quotes = ['"', '\'', '“', '”', '‘', '’', '«', '»', '*', '_'];

    [GeneratedRegex(@"\s*\((?<year>(19|20)\d{2})\)\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex TrailingYear();

    [GeneratedRegex(@"(?<![0-9])(19|20)\d{2}(?![0-9])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex YearToken();
}
