using FrostAura.Reel.Application.Persistence;
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

    /// <summary>
    /// The media a message names in its own words, for a turn no model read: movies when it says
    /// only movie words, shows when it says only show words, nothing when it says both or neither.
    /// </summary>
    public static IReadOnlyList<MediaType> MediaNamed(string? message)
    {
        var words = AskReelIntentSanitizer.Words(message);
        var movie = words.Any(MovieWords.Contains);
        var show = words.Any(ShowWords.Contains);
        return movie == show ? [] : movie ? [MediaType.Movie] : [MediaType.Show];
    }

    private static readonly IReadOnlySet<string> MovieWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "movie", "movies", "film", "films", "flick", "flicks", "cinema",
    };

    private static readonly IReadOnlySet<string> ShowWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "tv", "show", "shows", "series", "season", "seasons", "episode", "episodes", "miniseries", "sitcom", "sitcoms",
    };

    private static readonly System.Text.Json.JsonSerializerOptions IntentReadOptions = CreateIntentReadOptions();

    private static System.Text.Json.JsonSerializerOptions CreateIntentReadOptions()
    {
        var options = new System.Text.Json.JsonSerializerOptions(PersistedJson.Options);
        options.Converters.Insert(0, new UnrecognisedJsonEnumConverter<MediaType>());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
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
            // Read as a newer build may have written it: unknown roles fall back, and media types
            // this build cannot name are dropped rather than pinning the next turn to a filter.
            return System.Text.Json.JsonSerializer.Deserialize<SearchIntent>(intentJson, IntentReadOptions) is { } intent
                ? intent with { MediaTypes = intent.MediaTypes.Where(Enum.IsDefined).ToList() }
                : null;
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
