using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// What a follow-up inherits from the conversation's previous reading when its own words are silent
/// about it — today, the media type.
///
/// <para><b>Why.</b> On the live "feel-good movie" → "no superheroes please" exchange the second
/// reading came back with no media type, and two series (Free Rein, The Family) reached the picks of
/// a conversation that had asked for a movie (acceptance §5.2, "the media type is preserved"). A
/// follow-up that refines an exclusion is still about the same kind of title. The carry is
/// deterministic and narrow: only when the follow-up names no media at all, only when the previous
/// reading named exactly one, and never over an explicit filter from the UI.</para>
/// </summary>
public static class AskReelCarry
{
    /// <summary>Words that make a message say something about media, so nothing is carried over it.</summary>
    private static readonly IReadOnlySet<string> MediaWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "movie", "movies", "film", "films", "flick", "flicks", "cinema", "tv", "show", "shows", "series",
        "season", "seasons", "episode", "episodes", "miniseries", "sitcom", "sitcoms", "anything", "either",
        "both",
    };

    /// <summary>
    /// The media types this turn should search: its own when it has a definite reading, otherwise the
    /// previous reading's single type when the message names no media.
    /// </summary>
    public static IReadOnlyList<MediaType> Media(
        IReadOnlyList<MediaType> current, IReadOnlyList<MediaType>? previous, string message)
    {
        ArgumentNullException.ThrowIfNull(current);
        var definite = current.Distinct().Count() == 1;
        if (definite || previous is null)
        {
            return current;
        }

        var carried = previous.Distinct().ToList();
        if (carried.Count != 1)
        {
            return current;
        }

        return AskReelIntentSanitizer.Words(message).Any(MediaWords.Contains) ? current : carried;
    }

    /// <summary>The previous reading, from the stored JSON of the last turn that had one; null when unreadable.</summary>
    public static SearchIntent? Parse(string? intentJson)
    {
        if (string.IsNullOrWhiteSpace(intentJson))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<SearchIntent>(intentJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
