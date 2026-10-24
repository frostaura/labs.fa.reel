using FrostAura.Reel.Application.Search.Retrieval;

namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// The reply a turn gives when nothing it found matches the request — composed deterministically,
/// every number taken from the <see cref="SupplyReport"/>, nothing from a model.
///
/// <para><b>Why the reply is not the model's.</b> The interpret model writes its sentence before a
/// single candidate exists, and promised "newer, well-rated ninja action films" above 24 cards with
/// no ninja in them (B-1). A "none left" answer is a statement of fact about the account's supply,
/// and the only honest source for it is the count that was actually made.</para>
///
/// <para><b>What it never says.</b> It names no seen title — prose gives counts only, the FD-3
/// default — and it never offers "already seen" as something to relax. It does name the titles it
/// found under the account's own bar, because those are on screen in the tray beneath it.</para>
///
/// <para>Copy is pinned by unit tests; every number is formatted invariantly.</para>
/// </summary>
public static class NoneFoundCopy
{
    /// <summary>How many relaxation offers the sentence names. The rest are chips.</summary>
    public const int MaxKnockoutsNamed = 2;

    /// <summary>How many under-bar titles the sentence names.</summary>
    public const int MaxUnderBarNamed = 3;

    /// <summary>The facts a none-found reply is built from.</summary>
    /// <param name="Supply">The supply report for the request's hard constraints, or null when the request named no concept.</param>
    /// <param name="ConceptWords">The user's words for the topic ("ninja"), or null.</param>
    /// <param name="Media">Which media the request was about.</param>
    /// <param name="UnderBar">Names of judged matches the account's For-you bar put in the tray, in tray order.</param>
    /// <param name="NearMisses">How many partial matches went to the near-miss tray.</param>
    /// <param name="Floor">The account's For-you bar; 0 when none is set.</param>
    public sealed record Facts(
        SupplyReport? Supply,
        string? ConceptWords,
        MediaScope Media,
        IReadOnlyList<string> UnderBar,
        int NearMisses,
        decimal Floor);

    /// <summary>The reply, one to four short sentences, plain text.</summary>
    public static string Compose(Facts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var sentences = new List<string>(4);
        var topic = Topic(facts.ConceptWords, facts.Media);
        var supply = facts.Supply;

        if (supply is null || supply.MatchingInCatalogue == 0)
        {
            // No catalogue count stands behind the topic words here — they are the model's reading
            // ("witty action playful tone"), not a tag anything carries — so they are not repeated
            // back as if they named a shelf that came up empty.
            sentences.Add("Nothing I found matches what you asked closely enough to show.");
        }
        else if (supply.ClearingFloor > 0)
        {
            // The catalogue tags some unseen titles with the topic, but the judge did not accept any
            // of the ones it saw. Say exactly that rather than "none left", which would be false.
            sentences.Add(FormattableString.Invariant(
                $"You have {supply.ClearingFloor} unseen {Noun(topic, supply.ClearingFloor)} tagged that way, but none of the ones I checked is really a match."));
        }
        else
        {
            sentences.Add($"There are no {topic ?? "matches"} left for you.");
            sentences.Add(Partition(supply, topic));
        }

        if (facts.UnderBar.Count > 0)
        {
            var named = facts.UnderBar.Take(MaxUnderBarNamed).ToList();
            var more = facts.UnderBar.Count - named.Count;
            var list = Joined(named) + (more > 0 ? FormattableString.Invariant($" and {more} more") : string.Empty);
            sentences.Add(FormattableString.Invariant(
                $"Under your {AskReelReason.FormatFloor(facts.Floor)} bar: {list} — shown below."));
        }
        else if (supply is { BelowFloor: > 0 } s && s.PredictedFloor is { } bar)
        {
            sentences.Add(FormattableString.Invariant(
                $"{s.BelowFloor} more {Are(s.BelowFloor)} under your {AskReelReason.FormatFloor(bar)} bar."));
        }

        if (supply is { Unscored: > 0 } unscored)
        {
            sentences.Add(FormattableString.Invariant(
                $"{unscored.Unscored} {Have(unscored.Unscored)} no score from your model yet."));
        }

        if (supply is { Knockouts.Count: > 0 } k)
        {
            var offers = k.Knockouts
                .Take(MaxKnockoutsNamed)
                .Select(o => FormattableString.Invariant($"turning off “{o.Label}” would unlock {o.WouldUnlock}"))
                .ToList();
            sentences.Add(Capitalised(string.Join("; ", offers)) + ".");
        }

        if (facts.NearMisses > 0)
        {
            sentences.Add(FormattableString.Invariant(
                $"{facts.NearMisses} close-but-not-quite {(facts.NearMisses == 1 ? "title is" : "titles are")} tucked away below."));
        }

        return string.Join(" ", sentences);
    }

    /// <summary>"Of the 12 in the catalogue, you've seen 6, 2 are saved or dismissed and your conditions hold out 4."</summary>
    private static string Partition(SupplyReport supply, string? topic)
    {
        var parts = new List<string>(3);
        if (supply.AlreadySeen > 0)
        {
            parts.Add(FormattableString.Invariant($"you've seen {supply.AlreadySeen}"));
        }

        if (supply.SavedOrDismissed > 0)
        {
            parts.Add(FormattableString.Invariant($"{supply.SavedOrDismissed} {Are(supply.SavedOrDismissed)} saved or dismissed"));
        }

        if (supply.ExcludedByConditions > 0)
        {
            parts.Add(FormattableString.Invariant($"your conditions hold out {supply.ExcludedByConditions}"));
        }

        var head = FormattableString.Invariant(
            $"Of the {supply.MatchingInCatalogue} {Noun(topic, supply.MatchingInCatalogue)} in the catalogue with at least {supply.MinVotes} votes");
        return parts.Count == 0
            ? head + ", none is unseen and inside your conditions."
            : $"{head}, {Joined(parts)}.";
    }

    /// <summary>"ninja movies", "ninja shows", "ninja titles"; null when the request named no concept.</summary>
    private static string? Topic(string? conceptWords, MediaScope media)
    {
        var words = conceptWords?.Trim();
        if (string.IsNullOrEmpty(words))
        {
            return null;
        }

        return $"{words} {MediaNoun(media, plural: true)}";
    }

    private static string Noun(string? topic, int count) =>
        topic is null
            ? count == 1 ? "title" : "titles"
            : count == 1 ? Singular(topic) : topic;

    private static string Singular(string topic) =>
        topic.EndsWith("movies", StringComparison.Ordinal) ? topic[..^1]
        : topic.EndsWith("shows", StringComparison.Ordinal) || topic.EndsWith("titles", StringComparison.Ordinal) ? topic[..^1]
        : topic;

    private static string MediaNoun(MediaScope media, bool plural) => media switch
    {
        MediaScope.Movies => plural ? "movies" : "movie",
        MediaScope.Shows => plural ? "shows" : "show",
        _ => plural ? "titles" : "title",
    };

    private static string Are(int count) => count == 1 ? "is" : "are";

    private static string Have(int count) => count == 1 ? "has" : "have";

    private static string Capitalised(string s) =>
        s.Length == 0 ? s : string.Concat(char.ToUpperInvariant(s[0]).ToString(), s.AsSpan(1));

    private static string Joined(IReadOnlyList<string> values) =>
        values.Count switch
        {
            0 => string.Empty,
            1 => values[0],
            2 => $"{values[0]} and {values[1]}",
            _ => $"{string.Join(", ", values.Take(values.Count - 1))} and {values[^1]}",
        };
}
