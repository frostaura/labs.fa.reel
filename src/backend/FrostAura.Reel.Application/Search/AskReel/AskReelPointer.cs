using System.Globalization;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// "The second one", "the last one", "#3" — a message that points at a card by its place on screen,
/// and the deterministic answer when there is nothing there to point at.
///
/// <para><b>Why it exists (R1-04).</b> "more like the second one you showed me first time round" was
/// asked in a conversation whose answers had put no picks on screen. With nothing to resolve the
/// pointer against, the model filled the request with the sentence's leftovers — keywords
/// <c>["you", "showed", "round"]</c> — and the reply told the founder "You have 9 unseen you movies
/// tagged that way". A pointer with no target is not a search; it is a question to ask back.</para>
///
/// <para><b>When it intervenes, and when it does not.</b> Only when the pointer CANNOT resolve: no
/// answer in the conversation shows anything, or none shows as many cards as the ordinal names. Which
/// answer the user means ("first time round", "the one before") is the model's to read from the
/// handles; this class never second-guesses a pointer that some answer could satisfy.</para>
/// </summary>
public static class AskReelPointer
{
    /// <summary>The ordinal the message points at with <see cref="Last"/>, rather than a number.</summary>
    public const int Last = -1;

    private static readonly IReadOnlyDictionary<string, int> Ordinals = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["first"] = 1,
        ["1st"] = 1,
        ["second"] = 2,
        ["2nd"] = 2,
        ["third"] = 3,
        ["3rd"] = 3,
        ["fourth"] = 4,
        ["4th"] = 4,
        ["fifth"] = 5,
        ["5th"] = 5,
        ["sixth"] = 6,
        ["6th"] = 6,
        ["seventh"] = 7,
        ["7th"] = 7,
        ["eighth"] = 8,
        ["8th"] = 8,
        ["ninth"] = 9,
        ["9th"] = 9,
        ["tenth"] = 10,
        ["10th"] = 10,
        ["last"] = Last,
    };

    /// <summary>Nouns that make an ordinal a pointer at a card: "the second ONE", "the third FILM".</summary>
    private static readonly IReadOnlySet<string> CardNouns = new HashSet<string>(StringComparer.Ordinal)
    {
        "one", "ones", "movie", "film", "show", "series", "title", "pick", "option", "card", "suggestion",
    };

    /// <summary>
    /// The position <paramref name="message"/> points at — 1-based, or <see cref="Last"/> — or null when
    /// it points at nothing. An ordinal counts only when a card noun follows it ("the second one",
    /// "the 3rd film"): "first time round" and "my first horror film ever" name no card.
    /// "#2" and "number 2" count on their own.
    /// </summary>
    public static int? Ordinal(string? message)
    {
        var words = AskReelIntentSanitizer.Words(message);
        for (var i = 0; i < words.Count; i++)
        {
            if (Ordinals.TryGetValue(words[i], out var position)
                && i + 1 < words.Count
                && CardNouns.Contains(words[i + 1])
                && (i == 0 || words[i - 1] is "the" or "that" or "this"))
            {
                return position;
            }

            if (words[i] == "number" && i + 1 < words.Count && SmallNumber(words[i + 1]) is { } n)
            {
                return n;
            }
        }

        // "#2" — the hash is not a word character, so it is read off the raw text.
        var raw = message ?? string.Empty;
        for (var i = raw.IndexOf('#', StringComparison.Ordinal); i >= 0; i = raw.IndexOf('#', i + 1))
        {
            var digits = new string(raw.Skip(i + 1).TakeWhile(char.IsAsciiDigit).ToArray());
            if (SmallNumber(digits) is { } n)
            {
                return n;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a pointer at <paramref name="position"/> can resolve against the cards on screen:
    /// some answer shows at least that many (any card at all, for <see cref="Last"/>).
    /// </summary>
    public static bool CanResolve(int position, IReadOnlyList<PresentedCard> presented)
    {
        if (presented.Count == 0)
        {
            return false;
        }

        var most = presented.GroupBy(p => p.Turn).Max(g => g.Count());
        return position == Last ? most > 0 : most >= position;
    }

    /// <summary>
    /// The reply when a pointer has nothing to resolve against — plain, deterministic, and never a
    /// search: it says what is on screen and asks what the user meant.
    /// </summary>
    public static string NothingToPointAt(int position, IReadOnlyList<PresentedCard> presented)
    {
        var named = Named(position);
        if (presented.Count == 0)
        {
            return $"There’s nothing on screen yet for “{named}” to point at — I haven’t shown you any titles in this conversation. Tell me what you’re in the mood for and I’ll look.";
        }

        var most = presented.GroupBy(p => p.Turn).Max(g => g.Count());
        return string.Create(
            CultureInfo.InvariantCulture,
            $"My answers here have shown at most {most} {(most == 1 ? "title" : "titles")} at a time, so there’s no {Place(position)} to point at. Which title did you mean?");
    }

    /// <summary>The pointer as the user would say it: "the second one", "the last one", "the title #12".</summary>
    public static string Named(int position) => position == Last ? "the last one" : $"the {Place(position)}";

    private static int? SmallNumber(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 40 ? n : null;

    /// <summary>"second one", "title #12".</summary>
    private static string Place(int position) =>
        position is >= 1 and <= 10
            ? $"{Word(position)} one"
            : string.Create(CultureInfo.InvariantCulture, $"title #{position}");

    private static string Word(int position) => position switch
    {
        1 => "first",
        2 => "second",
        3 => "third",
        4 => "fourth",
        5 => "fifth",
        6 => "sixth",
        7 => "seventh",
        8 => "eighth",
        9 => "ninth",
        _ => "tenth",
    };
}
