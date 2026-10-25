using System.Text.RegularExpressions;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>
/// Decides whether a request is shaped like a TITLE — "Inception", "the dark knight",
/// <c>"Heat"</c> — and so deserves a TMDB title search, or shaped like a description, which does not.
///
/// <para>Why it exists: Ask Reel sent every message to TMDB's title search, including "none of the
/// above are ninja movies. I want ninja movies" (A-10). A sentence searched as a title returns
/// whatever titles happen to share its words, by popularity — noise that then had to be filtered
/// back out, and that crowded the 40-candidate cut. A description is answered by discovery; only a
/// name is answered by a title search.</para>
///
/// <para>The rule is deliberately conservative — a missed title search costs one arm of recall that
/// the other arms (and the lookup tool) still cover, while a false one costs a page of noise on
/// every turn:</para>
/// <list type="bullet">
/// <item>Text in double quotes is a title, whatever surrounds it: <c>something like "Heat"</c>.</item>
/// <item>A message that exactly names a catalogue title is a title, unless it is nothing but the
/// request's own topic words.</item>
/// <item>Otherwise the whole message must be short (at most <see cref="MaxWords"/> words), carry no
/// sentence punctuation, and contain none of the words that make a request a description
/// ("movies", "something", "but", "instead", a question word, …).</item>
/// </list>
/// </summary>
public static partial class TitleShape
{
    /// <summary>The longest unquoted message read as a title. Most titles are one to four words.</summary>
    public const int MaxWords = 6;

    /// <summary>
    /// Words that only ever appear in a request ABOUT titles, never as most of a title. Lower-case,
    /// compared whole-word. Words that are common inside real titles are absent on purpose — "with"
    /// (Dances with Wolves), "about" (About Time), "like" (Like Crazy), "no" (No Country for Old Men),
    /// "me" (Despicable Me), "dark" (The Dark Knight) — so a title is never mistaken for a request.
    /// </summary>
    private static readonly HashSet<string> DescriptionWords = new(StringComparer.Ordinal)
    {
        "movie", "movies", "film", "films", "shows", "series", "tv", "documentaries",
        "something", "anything", "similar", "but", "instead", "than", "except",
        "want", "need", "looking", "recommend", "recommendation", "recommendations", "suggest", "suggestions",
        "please", "what", "which", "why", "how", "funnier", "lighter", "darker", "older", "newer",
        "60s", "70s", "80s", "90s", "2000s", "2010s", "2020s",
    };

    /// <summary>
    /// The title to search for, or null when <paramref name="message"/> is a description. A quoted
    /// span wins; otherwise the trimmed message itself when it passes every rule.
    /// </summary>
    public static string? Extract(string? message) => Extract(message, [], namesCatalogueTitle: false);

    /// <summary>
    /// <see cref="Extract(string?)"/> for a request that also resolved to topic words (its concepts
    /// and genres), when the message is not known to name a catalogue title.
    /// </summary>
    public static string? Extract(string? message, IReadOnlyCollection<string> topicWords) =>
        Extract(message, topicWords, namesCatalogueTitle: false);

    /// <summary>
    /// The full rule, in this order:
    /// <list type="number">
    /// <item>a quoted span is a title, whatever surrounds it;</item>
    /// <item>a message that is nothing but its topic words ("ninja", "time loop") is a topic, even if
    /// some film happens to be called "Ninja" — discovery answers it, and a title search would add
    /// whatever is named that, by popularity;</item>
    /// <item>a message that exactly names a catalogue title (<paramref name="namesCatalogueTitle"/>,
    /// asked of the whole trimmed message, see <see cref="LookupText"/>) is a title, BEFORE the shape
    /// rules: "How to Train Your Dragon", "Scary Movie", "Monsters, Inc." and "What We Do in the
    /// Shadows" carry description words or punctuation and would otherwise never be searched;</item>
    /// <item>otherwise the shape rules (<see cref="Candidate"/>) must pass, and a message whose words
    /// are at least <see cref="TopicShare"/> topic words ("haunted house horror", "detective hunting
    /// a serial killer") is a topic.</item>
    /// </list>
    /// </summary>
    public static string? Extract(string? message, IReadOnlyCollection<string> topicWords, bool namesCatalogueTitle)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var quoted = QuotedSpan().Match(message);
        if (quoted.Success && quoted.Groups["title"].Value.Trim() is { Length: > 0 } inQuotes)
        {
            return inQuotes;
        }

        var text = message.Trim();
        if (TopicCoverage(text, topicWords) >= 1d)
        {
            return null;
        }

        if (namesCatalogueTitle && text.Length <= MaxLookupLength)
        {
            return text;
        }

        if (Candidate(message) is not { } candidate)
        {
            return null;
        }

        var coverage = TopicCoverage(candidate.Text, topicWords);
        if (coverage >= 1d)
        {
            return null;
        }

        return namesCatalogueTitle || coverage < TopicShare ? candidate.Text : null;
    }

    /// <summary>The longest message worth asking the catalogue about: longer than any title it holds.</summary>
    public const int MaxLookupLength = 120;

    /// <summary>
    /// The text to ask the catalogue about ("is this exactly a title's name?") — the whole trimmed
    /// message — or null when no answer could change <see cref="Extract(string?, IReadOnlyCollection{string}, bool)"/>:
    /// a blank or over-long message, or a quoted span, which is a title regardless.
    /// </summary>
    public static string? LookupText(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || QuotedSpan().IsMatch(message))
        {
            return null;
        }

        var text = message.Trim();
        return text.Length <= MaxLookupLength ? text : null;
    }

    /// <summary>
    /// The message after the shape rules alone — quoted span, punctuation, length, description words —
    /// or null. <see cref="TitleCandidate.Quoted"/> marks a span the user put in quotes, which no topic
    /// rule overrides.
    /// </summary>
    public static TitleCandidate? Candidate(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var quoted = QuotedSpan().Match(message);
        if (quoted.Success && quoted.Groups["title"].Value.Trim() is { Length: > 0 } inQuotes)
        {
            return new TitleCandidate(inQuotes, Quoted: true);
        }

        var text = message.Trim();
        if (SentencePunctuation().IsMatch(text))
        {
            return null;
        }

        var words = Words(text);
        if (words.Count == 0 || words.Count > MaxWords || words.Any(DescriptionWords.Contains))
        {
            return null;
        }

        return new TitleCandidate(text, Quoted: false);
    }

    /// <summary>
    /// The share of a message's content words that are topic words, comparing singular forms
    /// ("dinosaurs" is the topic "dinosaur"). Articles and joiners carry no weight either way.
    /// </summary>
    public static double TopicCoverage(string text, IReadOnlyCollection<string> topicWords)
    {
        var topic = topicWords
            .SelectMany(Words)
            .Select(Singular)
            .ToHashSet(StringComparer.Ordinal);
        var content = Words(text).Where(w => !Joiners.Contains(w)).Select(Singular).ToList();
        if (topic.Count == 0 || content.Count == 0)
        {
            return 0d;
        }

        return (double)content.Count(topic.Contains) / content.Count;
    }

    /// <summary>The topic-word share at or above which a message that names no catalogue title is a topic.</summary>
    public const double TopicShare = 0.4;

    /// <summary>Articles and joiners: neither title nor topic evidence.</summary>
    private static readonly HashSet<string> Joiners = new(StringComparer.Ordinal) { "a", "an", "the", "and", "of", "in", "on", "at", "to", "movie", "film", "show" };

    private static List<string> Words(string text) =>
        [.. WordSplit().Split(text.ToLowerInvariant()).Where(w => w.Length > 0)];

    private static string Singular(string word) =>
        word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) ? word[..^1] : word;

    /// <summary>True when <paramref name="message"/> is shaped like a title.</summary>
    public static bool IsTitleShaped(string? message) => Extract(message) is not null;

    [GeneratedRegex("[\"“”](?<title>[^\"“”]{1,120})[\"“”]")]
    private static partial Regex QuotedSpan();

    // A sentence break or a question: "x. y", "x? ", "x!" — titles rarely carry one, requests often do.
    // A colon and an apostrophe are kept: "Mission: Impossible", "Ocean's Eleven".
    [GeneratedRegex(@"[.?!;](\s|$)|,")]
    private static partial Regex SentencePunctuation();

    [GeneratedRegex(@"[\s\-–—/]+")]
    private static partial Regex WordSplit();
}

/// <summary>A message that passed the shape rules: the text to search, and whether the user quoted it.</summary>
public sealed record TitleCandidate(string Text, bool Quoted);
