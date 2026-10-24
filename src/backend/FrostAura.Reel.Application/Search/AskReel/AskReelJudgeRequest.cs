using System.Globalization;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// The request the judge rules on: the user's message read IN ITS CONVERSATION, never the bare
/// follow-up.
///
/// <para><b>Why.</b> The judge's <c>satisfies</c> verdict is the gate, and it was asked about the raw
/// latest message. After "feel-good movie", "no superheroes please" asked whether a title satisfies
/// "no superheroes please" — every grief drama was a yes, and "feel-good" never reached the gate
/// (D2 / acceptance D4). The same text in another conversation ("more please" after ninja films, and
/// after romcoms) reused one verdict per title for six hours, because the cache key was the message
/// alone. Composing the request from the message, the conversation's earlier asks and the resolved
/// reading — and keying the cache on the composed text — fixes both at once: the judge sees what is
/// actually being asked, and two conversations share a verdict only when they ask the same thing.</para>
/// </summary>
public static class AskReelJudgeRequest
{
    /// <summary>How many earlier user messages the request carries.</summary>
    public const int EarlierAsks = 2;

    /// <summary>The longest an earlier message is quoted, in characters.</summary>
    public const int EarlierAskChars = 160;

    /// <summary>The composed request text, one fact per line.</summary>
    public static string Compose(
        string message, IReadOnlyList<ChatTurn> history, SearchIntent intent, IReadOnlyList<string> referenceNames)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(referenceNames);

        var lines = new List<string>(4) { message.Trim() };
        if (referenceNames.Count > 0)
        {
            lines.Add($"The user wants something like: {string.Join(", ", referenceNames)}");
        }

        var earlier = history
            .Where(t => t.Role == "user" && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => Clip(t.Text.Trim()))
            .TakeLast(EarlierAsks)
            .ToList();
        if (earlier.Count > 0)
        {
            lines.Add($"Earlier in this conversation the user asked: {string.Join("; ", earlier.Select(e => $"“{e}”"))}");
        }

        var reading = Reading(intent);
        if (reading.Count > 0)
        {
            lines.Add($"Read in context, they want {string.Join("; ", reading)}.");
        }

        return string.Join('\n', lines);
    }

    /// <summary>The resolved reading as short clauses: media, genres, topic, years, exclusions.</summary>
    private static List<string> Reading(SearchIntent intent)
    {
        var clauses = new List<string>(5);
        var media = intent.MediaTypes.Distinct().ToList();
        clauses.Add(media.Count == 1
            ? media[0] == MediaType.Movie ? "a movie" : "a TV show"
            : "a movie or a TV show");

        if (intent.Genres.Count > 0)
        {
            clauses.Add($"genres: {string.Join(", ", intent.Genres)}");
        }

        if (intent.Keywords.Count > 0)
        {
            clauses.Add($"about: {string.Join(", ", intent.Keywords)}");
        }

        if (intent.MinYear is { } min)
        {
            clauses.Add(string.Create(CultureInfo.InvariantCulture, $"released {min} or later"));
        }

        if (intent.MaxYear is { } max)
        {
            clauses.Add(string.Create(CultureInfo.InvariantCulture, $"released {max} or earlier"));
        }

        var not = (intent.ExcludeGenres ?? []).Concat(intent.ExcludeKeywords ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (not.Count > 0)
        {
            clauses.Add($"nothing that is: {string.Join(", ", not)}");
        }

        return clauses;
    }

    private static string Clip(string text) =>
        text.Length <= EarlierAskChars ? text : string.Concat(text.AsSpan(0, EarlierAskChars - 1), "…");
}
