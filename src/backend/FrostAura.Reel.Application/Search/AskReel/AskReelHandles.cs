using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// One card an earlier answer put on screen, with the handle the model is shown for it.
/// </summary>
/// <param name="Turn">The answer's number in the conversation, 1-based, over assistant rows in sequence order.</param>
/// <param name="Rank">The card's position on screen within that answer, 1-based — its stored rank plus one.</param>
/// <param name="TitleId">The title.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Year">Its year, when known.</param>
/// <param name="Media">Movie or show.</param>
public sealed record PresentedCard(int Turn, int Rank, Guid TitleId, string Name, int? Year, MediaType Media)
{
    /// <summary>"T2.3" — the third card of the second answer.</summary>
    public string Handle => string.Create(CultureInfo.InvariantCulture, $"T{Turn}.{Rank}");

    /// <summary>"T2.3 Ballerina (2025)" — what the model reads.</summary>
    public string Line => Year is { } year
        ? string.Create(CultureInfo.InvariantCulture, $"{Handle} {Name} ({year})")
        : string.Create(CultureInfo.InvariantCulture, $"{Handle} {Name}");
}

/// <summary>
/// The numbered handles that let "the second one" mean the second card on screen.
///
/// <para><b>Why handles at all.</b> References used to resolve against the conversation's whole
/// no-repeat set ordered by TMDB id and cut at twenty (A-4, B-8): "is the first one violent?" was
/// answered about Replicas, the fifth card on screen, because it had the lowest TMDB id. Each card the
/// model is shown now carries its answer number and its on-screen position, in display order, newest
/// answer first — the order a person scanning up the screen meets them.</para>
///
/// <para><b>Keyed by stored rank,</b> so the handle of a card never depends on a database's row order
/// and the interpret cache key built from the list is stable for one conversation state.</para>
/// </summary>
public static class AskReelHandles
{
    /// <summary>How many presented cards reach the prompt. The prompt itself caps at 20 as well.</summary>
    public const int MaxPromptLines = 20;

    /// <summary>How many cards of one answer ride its history line.</summary>
    public const int MaxHistoryHandles = 8;

    /// <summary>
    /// The presented cards in prompt order: newest answer first, and within an answer by on-screen
    /// position. A pure function of the cards, so the same conversation state is the same prompt.
    /// </summary>
    public static IReadOnlyList<PresentedCard> InPromptOrder(IEnumerable<PresentedCard> presented) =>
        presented
            .OrderByDescending(p => p.Turn)
            .ThenBy(p => p.Rank)
            .Take(MaxPromptLines)
            .ToList();

    /// <summary>The prompt lines for <paramref name="presented"/>, in <see cref="InPromptOrder"/>.</summary>
    public static IReadOnlyList<string> PromptLines(IEnumerable<PresentedCard> presented) =>
        InPromptOrder(presented).Select(p => p.Line).ToList();

    /// <summary>
    /// The suffix an assistant history line carries — " [Shown: T2.1 Ballerina (2025) · T2.2 …]" —
    /// so the transcript the model reads says what each answer actually showed, not only what it said.
    /// Empty when the answer showed nothing.
    /// </summary>
    public static string HistorySuffix(IEnumerable<PresentedCard> ofOneAnswer)
    {
        var lines = ofOneAnswer.OrderBy(p => p.Rank).Take(MaxHistoryHandles).Select(p => p.Line).ToList();
        return lines.Count == 0 ? string.Empty : $" [Shown: {string.Join(" · ", lines)}]";
    }

    /// <summary>
    /// A stable digest of what the model is shown, for the interpret cache key: every handle and its
    /// title, in prompt order. "none" for nothing shown, so the commonest key is readable in a dump.
    /// </summary>
    public static string Digest(IEnumerable<PresentedCard> presented)
    {
        var ordered = InPromptOrder(presented);
        if (ordered.Count == 0)
        {
            return "none";
        }

        var sb = new StringBuilder();
        foreach (var p in ordered)
        {
            sb.Append(p.Handle).Append('=').Append(p.TitleId.ToString("N")).Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
