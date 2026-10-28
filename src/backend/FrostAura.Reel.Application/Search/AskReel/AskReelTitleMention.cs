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

    /// <summary>Every candidate run in <paramref name="message"/>, longest first, each text once.</summary>
    public static IReadOnlyList<Span> Spans(string? message)
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
                if (!Plausible(run))
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

    private static readonly char[] Quotes = ['"', '\'', '“', '”', '‘', '’', '«', '»', '*', '_'];

    [GeneratedRegex(@"\s*\((?<year>(19|20)\d{2})\)\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex TrailingYear();

    [GeneratedRegex(@"(?<![0-9])(19|20)\d{2}(?![0-9])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex YearToken();
}
