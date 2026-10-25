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

    /// <summary>How many near-miss titles the sentence names.</summary>
    public const int MaxNearMissNamed = 3;

    /// <summary>The facts a none-found reply is built from.</summary>
    /// <param name="Supply">The supply report for the request's hard constraints, or null when the request named no concept.</param>
    /// <param name="ConceptWords">The user's words for the topic ("ninja"), or null.</param>
    /// <param name="Media">Which media the request was about.</param>
    /// <param name="UnderBar">Names of judged matches the account's For-you bar put in the tray, in tray order.</param>
    /// <param name="NearMisses">How many partial matches went to the near-miss tray.</param>
    /// <param name="Floor">The account's For-you bar; 0 when none is set.</param>
    /// <param name="Unchecked">How many cards the judge never ruled on, now in the "not checked" tray.</param>
    /// <param name="JudgedAny">Whether the judge ruled on at least one card this turn.</param>
    /// <param name="ShownHere">
    /// How many of the titles the supply counts as clearing the bar this conversation has already
    /// shown. They are excluded from the turn, so "you have 6 unseen ninja movies … none is a match"
    /// about six cards nobody judged was false (D4); they are named as shown instead.
    /// </param>
    /// <param name="UnscoredHeld">
    /// Names of judged matches the bar held back because the account's model has no score for them —
    /// not "under" the bar, so not named as under it.
    /// </param>
    /// <param name="NearMissNames">
    /// Names of the partial matches in the near-miss tray, in tray order. On a turn that found no
    /// match they are the closest thing it did find, so the reply names them rather than counting
    /// them (R1-01: "Blade of the 47 Ronin" sat in the tray of a samurai turn whose reply never said so).
    /// </param>
    public sealed record Facts(
        SupplyReport? Supply,
        string? ConceptWords,
        MediaScope Media,
        IReadOnlyList<string> UnderBar,
        int NearMisses,
        decimal Floor,
        int Unchecked = 0,
        bool JudgedAny = true,
        int ShownHere = 0,
        IReadOnlyList<string>? UnscoredHeld = null,
        IReadOnlyList<string>? NearMissNames = null);

    /// <summary>The lead a reply gets when it would otherwise repeat the previous answer word for word.</summary>
    public const string AgainLead = "Looking again turned up nothing new.";

    /// <summary>
    /// The reply, led by <see cref="AgainLead"/> when the conversation's previous answer said exactly
    /// the same thing — a follow-up ("none of those are ninja films") deserves to hear that it was
    /// heard, not the identical paragraph again.
    /// </summary>
    public static string Again(string reply, string? previousReply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var previous = previousReply?.Trim();
        return previous is { Length: > 0 }
            && (string.Equals(previous, reply, StringComparison.Ordinal)
                || string.Equals(previous, $"{AgainLead} {reply}", StringComparison.Ordinal))
                ? $"{AgainLead} {reply}"
                : reply;
    }

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
            // back as if they named a shelf that came up empty. With nothing judged, "nothing
            // matches" would be a verdict nobody reached.
            sentences.Add(facts.JudgedAny || facts.Unchecked == 0
                ? "Nothing I found matches what you asked closely enough to show."
                : "I couldn't check what I found against what you asked this time.");
        }
        else if (supply.ClearingFloor > 0)
        {
            // The catalogue tags some unseen titles with the topic. The ones this conversation has
            // already shown were excluded before anything was judged, so they are named as shown;
            // what is left is the part the judge actually saw.
            var shown = Math.Clamp(facts.ShownHere, 0, supply.ClearingFloor);
            var fresh = supply.ClearingFloor - shown;
            if (shown > 0 && fresh == 0)
            {
                sentences.Add(FormattableString.Invariant(
                    $"{(shown == 1 ? "The one unseen" : $"All {shown} unseen")} {Noun(topic, shown)} inside your conditions {(shown == 1 ? "was" : "were")} already shown in this conversation."));
            }
            else
            {
                if (shown > 0)
                {
                    sentences.Add(FormattableString.Invariant(
                        $"{shown} of your unseen {Noun(topic, supply.ClearingFloor)} {(shown == 1 ? "was" : "were")} already shown in this conversation."));
                }

                var other = shown > 0 ? " other" : string.Empty;
                sentences.Add(facts.JudgedAny
                    ? FormattableString.Invariant(
                        $"You have {fresh}{other} unseen {Noun(topic, fresh)} tagged that way, but none of the ones I checked is really a match.")
                    : FormattableString.Invariant(
                        $"You have {fresh}{other} unseen {Noun(topic, fresh)} tagged that way, but I couldn't check them against what you asked this time."));
            }
        }
        else
        {
            // Nothing clears the bar. When titles sit under it (or have no score while it is set),
            // "none left" would be false — they are there, and the tray shows them.
            sentences.Add(supply.PredictedFloor is { } bar && supply.BelowFloor + supply.Unscored > 0
                ? FormattableString.Invariant(
                    $"None of your unseen {topic ?? "matches"} clears your {AskReelReason.FormatFloor(bar)} bar.")
                : $"There are no {topic ?? "matches"} left for you.");
            sentences.Add(Partition(supply, topic));
        }

        if (facts.UnderBar.Count > 0)
        {
            var named = facts.UnderBar.Take(MaxUnderBarNamed).ToList();
            var list = JoinedWithMore(named, facts.UnderBar.Count - named.Count);
            sentences.Add(FormattableString.Invariant(
                $"Under your {AskReelReason.FormatFloor(facts.Floor)} bar: {list} — shown below."));
        }
        else if (supply is { BelowFloor: > 0 } s && s.PredictedFloor is { } bar)
        {
            sentences.Add(FormattableString.Invariant(
                $"{s.BelowFloor} {Are(s.BelowFloor)} under your {AskReelReason.FormatFloor(bar)} bar."));
        }

        if (facts.UnscoredHeld is { Count: > 0 } held)
        {
            var named = held.Take(MaxUnderBarNamed).ToList();
            var list = JoinedWithMore(named, held.Count - named.Count);
            sentences.Add(FormattableString.Invariant(
                $"{list} {(held.Count == 1 ? "has" : "have")} no score from your model yet, so your {AskReelReason.FormatFloor(facts.Floor)} bar holds {(held.Count == 1 ? "it" : "them")} back — shown below."));
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

        if (facts.Unchecked > 0)
        {
            sentences.Add(FormattableString.Invariant(
                $"{facts.Unchecked} {(facts.Unchecked == 1 ? "title" : "titles")} I couldn't check against your request {Are(facts.Unchecked)} below, marked as unchecked."));
        }

        if (facts.NearMissNames is { Count: > 0 } close)
        {
            var named = close.Take(MaxNearMissNamed).ToList();
            var list = JoinedWithMore(named, Math.Max(facts.NearMisses, close.Count) - named.Count);
            sentences.Add($"Close, but not quite: {list} — tucked away below.");
        }
        else if (facts.NearMisses > 0)
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

    /// <summary>"A, B, C and 2 more" — never "A, B and C and 2 more"; just <see cref="Joined"/> when none are left over.</summary>
    private static string JoinedWithMore(IReadOnlyList<string> named, int more) =>
        more > 0
            ? FormattableString.Invariant($"{string.Join(", ", named)} and {more} more")
            : Joined(named);

    private static string Joined(IReadOnlyList<string> values) =>
        values.Count switch
        {
            0 => string.Empty,
            1 => values[0],
            2 => $"{values[0]} and {values[1]}",
            _ => $"{string.Join(", ", values.Take(values.Count - 1))} and {values[^1]}",
        };
}
