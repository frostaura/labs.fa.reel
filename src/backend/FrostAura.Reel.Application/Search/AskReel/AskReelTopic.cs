using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// The request's topic, resolved ONCE — the keywords the supply counts and the words the reply
/// names them by, from the same list, so the two can never describe different shelves.
///
/// <para><b>Why it exists (R1-02).</b> The supply report counted the UNION of every keyword the model
/// wrote, while the reply named only the first: the ninja follow-up came back as
/// <c>["ninja", "martial arts", "ninja warrior"]</c>, the supply counted 543 titles across all three,
/// and the reply told the founder "You have 4 unseen ninja movies tagged that way" — after the turn
/// before had said, correctly, that he had none.</para>
///
/// <para><b>The user's words decide.</b> The model's keywords are alternative pulls, and it widens
/// them on its own initiative ("martial arts" beside "ninja"). Discovery may use every one of them;
/// the topic that is COUNTED and NAMED is the subset the user actually said — in this message, or,
/// for a follow-up that names no topic ("more like that"), in the conversation's earlier asks, newest
/// first. Only when the user's words anchor none of them is the model's whole list the topic, and
/// then the reply names every keyword it counted ("swordplay or warrior movies") rather than
/// pretending one of them stands for the rest.</para>
/// </summary>
public static class AskReelTopic
{
    /// <summary>A resolved topic: the keywords counted, and the words the reply calls them by.</summary>
    /// <param name="Keywords">The keywords the topic predicate counts, lower-cased; empty for no topic.</param>
    /// <param name="Label">"ninja", "ninja or shinobi", "swordplay, warrior or ronin"; null for no topic.</param>
    /// <param name="Anchored">Whether the user's own words named these keywords.</param>
    public sealed record Resolved(IReadOnlyList<string> Keywords, string? Label, bool Anchored);

    /// <summary>No topic at all.</summary>
    public static Resolved None { get; } = new([], null, false);

    /// <summary>
    /// The topic of a turn: the model's <paramref name="keywords"/> narrowed to the ones the user said,
    /// looking first at <paramref name="message"/> and then back through the user's earlier asks in
    /// <paramref name="history"/>.
    /// </summary>
    public static Resolved Resolve(IReadOnlyList<string>? keywords, string? message, IReadOnlyList<ChatTurn>? history)
    {
        var cleaned = (keywords ?? [])
            .Select(k => k.Trim().ToLowerInvariant())
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (cleaned.Count == 0)
        {
            return None;
        }

        var asks = new List<string>();
        if (!string.IsNullOrWhiteSpace(message))
        {
            asks.Add(message);
        }

        asks.AddRange((history ?? [])
            .Where(t => t.Role == "user" && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text)
            .Reverse());

        foreach (var ask in asks)
        {
            var anchored = cleaned.Where(k => NegationTerms.MatchesPhrase(ask, k)).ToList();
            if (anchored.Count > 0)
            {
                return new Resolved(anchored, Label(anchored), Anchored: true);
            }
        }

        return new Resolved(cleaned, Label(cleaned), Anchored: false);
    }

    /// <summary>"a", "a or b", "a, b or c" — every keyword counted, named.</summary>
    internal static string Label(IReadOnlyList<string> keywords) =>
        keywords.Count switch
        {
            0 => string.Empty,
            1 => keywords[0],
            _ => $"{string.Join(", ", keywords.Take(keywords.Count - 1))} or {keywords[^1]}",
        };
}
