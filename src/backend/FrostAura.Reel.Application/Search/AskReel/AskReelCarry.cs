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

        return AskReelIntentSanitizer.Words(WithoutShowVerb(message)).Any(MediaWords.Contains) ? current : carried;
    }

    /// <summary>
    /// The media a message names in its own words, for a turn no model read: movies when it says
    /// only movie words, shows when it says only show words, nothing when it says both or neither.
    /// The verb in "show me" / "show us" names nothing (<see cref="WithoutShowVerb"/>).
    /// </summary>
    public static IReadOnlyList<MediaType> MediaNamed(string? message) =>
        MediaIn(AskReelIntentSanitizer.Words(WithoutShowVerb(message)));

    /// <summary>
    /// The media a list of words names, read word by word: callers that have already taken the verb
    /// "show" out (<see cref="WithoutShowVerb"/>) and then selected words — the asked-for or the
    /// ruled-out ones — read them here, so the selection cannot re-create a "show me".
    /// </summary>
    public static IReadOnlyList<MediaType> MediaIn(IEnumerable<string> words)
    {
        var said = words.ToList();
        var movie = said.Any(MovieWords.Contains);
        var show = said.Any(ShowWords.Contains);
        return movie == show ? [] : movie ? [MediaType.Movie] : [MediaType.Show];
    }

    /// <summary>
    /// The message with every VERB "show" replaced by a word that names no medium (HREV1-ASK-02
    /// follow-on). "Show me samurai films" asks for films, and "show me something feel-good" names no
    /// medium at all; read as a media word, "show" made the first name both media (so the planner's
    /// "any" stood and shows came back) and the second name TV. "Show" before "me" or "us" is the
    /// verb unless a determiner in its own clause, before any other media word, owns it as a noun:
    /// "a cosy show me and my wife can watch", "any show us kids would like".
    /// </summary>
    public static string WithoutShowVerb(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        return ShowBeforeObject.Replace(message, match => IsNoun(message[..match.Index]) ? match.Value : "find");

        static bool IsNoun(string before)
        {
            var clause = before[(before.LastIndexOfAny(ClauseBreaks) + 1)..];
            var words = AskReelIntentSanitizer.Words(clause);
            for (var i = words.Count - 1; i >= Math.Max(0, words.Count - NounReach); i--)
            {
                if (ShowDeterminers.Contains(words[i]))
                {
                    return true;
                }

                if (MovieWords.Contains(words[i]) || ShowWords.Contains(words[i]))
                {
                    return false;
                }
            }

            return false;
        }
    }

    /// <summary>"show" directly before "me" or "us", as a whole word in any case.</summary>
    private static readonly System.Text.RegularExpressions.Regex ShowBeforeObject = new(
        @"\bshow(?=\s+(?:me|us)\b)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly char[] ClauseBreaks = [',', '.', ';', ':', '!', '?'];

    /// <summary>How far back a determiner may stand and still own "show" ("a really cosy show me…").</summary>
    private const int NounReach = 3;

    /// <summary>Words that make the "show" after them a noun ("a show", "any show", "this show").</summary>
    private static readonly IReadOnlySet<string> ShowDeterminers = new HashSet<string>(StringComparer.Ordinal)
    {
        "a", "an", "the", "any", "another", "one", "this", "that", "every", "each", "some",
    };

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
