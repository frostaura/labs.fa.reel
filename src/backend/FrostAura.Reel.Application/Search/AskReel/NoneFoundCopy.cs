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
    /// <param name="Unchecked">
    /// How many cards the judge never ruled on that sit in the "not checked" tray — that tray only. An
    /// unchecked card the bar held back is in the floor tray and counted in <paramref name="HeldUnchecked"/>:
    /// adding it here said "9 titles … marked as unchecked" above an unchecked tray of 5 (HREV4-ASK-03).
    /// </param>
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
    /// <param name="Checked">How many titles the judge actually ruled on this turn.</param>
    /// <param name="CheckedOfTagged">
    /// How many of the unseen titles the supply counts as clearing the bar were among those the judge
    /// ruled on. The supply counts every tagged title in the catalogue and the judge rules on at most
    /// <see cref="AskReelJudge.MaxJudged"/> a turn, so "none of the ones I checked is really a match"
    /// over tagged titles nobody judged implied a check that never happened (IREV2-SYS-01). The
    /// sentence says how many were checked instead. Null — not counted — reads as NONE checked: a
    /// caller that did not count a check may not claim one (HREV1-RET-01, where v2 never counted and
    /// every none-found reply claimed to have checked titles nobody judged).
    /// </param>
    /// <param name="Guessed">
    /// The topic is a guess at the user's words: led by <see cref="GuessedLead"/>, the counts kept as
    /// facts about the words searched, and no relaxation offered.
    /// </param>
    /// <param name="Unread">
    /// No model read the request (none is configured, or the planner failed): a reply that found
    /// nothing at all says the reading failed (<see cref="UnreadNothing"/>), never that nothing exists.
    /// </param>
    /// <param name="UnreadLed">The reply is led by a sentence that already says the request was not fully read.</param>
    /// <param name="HeldUnchecked">
    /// How many cards nobody judged sit in the floor tray because the bar held them back. They are named
    /// as held back by the bar — the tray's own caption — never as "marked as unchecked", and with any
    /// in the tray the floor sentence describes the tray, not the tagged supply's under-bar count, so
    /// the reply and the tray beneath it never give two numbers for one tray (HREV4-ASK-03).
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
        IReadOnlyList<string>? NearMissNames = null,
        int Checked = 0,
        int? CheckedOfTagged = null,
        int HeldUnchecked = 0,
        bool Guessed = false,
        bool Unread = false,
        bool UnreadLed = false);

    /// <summary>
    /// The lead of a none-found reply whose topic is a guess at the user's words (no model read them):
    /// it says nothing matched the READING, never "there are no X left", and offers no relaxation of
    /// the user's conditions for a shelf they may not have asked for (HREV4-RET-02).
    /// </summary>
    public const string GuessedLead = "Nothing unseen inside your conditions matches my reading of that.";

    /// <summary>
    /// The lead of a turn that found nothing to show and checked nothing: no verdict was reached, so
    /// "nothing I found matches" would claim one (HREV4-ASK-05: the model-less "like John Wick but
    /// funnier" said it on a turn that judged nothing).
    /// </summary>
    public const string NothingFoundUnchecked = "I didn't find anything to show for that this time.";

    /// <summary>
    /// The reply of a turn no model read that found nothing at all to show: the reading is what failed,
    /// so it says that — never that nothing exists. With no model, "movies with Liam Neeson" read the
    /// name as a keyword no title carries and replied <see cref="NothingFoundUnchecked"/> over a
    /// catalogue holding about 80 of his films (HREV5-RET-02).
    /// </summary>
    public const string UnreadNothing =
        "I couldn't read that well enough to find anything for it — try naming a genre, a topic, a person or a title.";

    /// <summary>
    /// <see cref="UnreadNothing"/> after a lead that has already said the request could not be fully
    /// read and what was searched instead ("I couldn't fully read that, so I searched for …").
    /// </summary>
    public const string UnreadNothingLed = "Nothing turned up for that reading — try naming a genre, a topic, a person or a title.";

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
        var heldBack = facts.Floor > 0m ? Math.Max(0, facts.HeldUnchecked) : 0;
        var uncheckedTray = Math.Max(0, facts.Unchecked) + (facts.Floor > 0m ? 0 : Math.Max(0, facts.HeldUnchecked));

        // Cards this reply puts on screen that may be what was asked for: a lead that says none exist
        // would be contradicted by the tray beneath it (HREV5-RET-03).
        var possible = facts.UnderBar.Count + heldBack + (facts.UnscoredHeld?.Count ?? 0) + uncheckedTray;
        var taggedNone = false;

        if (supply is null || supply.MatchingInCatalogue == 0)
        {
            // No catalogue count stands behind the topic words here — they are the model's reading
            // ("witty action playful tone"), not a tag anything carries — so they are not repeated
            // back as if they named a shelf that came up empty. With nothing judged, "nothing
            // matches" would be a verdict nobody reached; with no model having read the request
            // either, the reading is what failed (HREV5-RET-02).
            //
            // With a card on screen that may be what was asked for, no lead may say nothing was found
            // or that the reading failed: "I couldn't read that well enough to find anything for it"
            // sat above "Taken has no score from your model yet … — shown below" (HREV5-RET-02/RET-03,
            // round 7). A judged match the bar holds back was found and matched; a card nobody judged
            // was found and not checked.
            var heldMatches = facts.UnderBar.Count + (facts.UnscoredHeld?.Count ?? 0);
            sentences.Add(facts.JudgedAny
                ? heldMatches > 0 && facts.Floor > 0m
                    ? FormattableString.Invariant(
                        $"Nothing I found both matches what you asked and clears your {AskReelReason.FormatFloor(facts.Floor)} bar.")
                    : possible > 0
                        ? "Nothing I checked matches what you asked closely enough to show."
                        : "Nothing I found matches what you asked closely enough to show."
                : possible > 0
                    ? "I couldn't check what I found against what you asked this time."
                    : facts.Unread && facts.NearMisses == 0 ? facts.UnreadLed ? UnreadNothingLed : UnreadNothing
                    : NothingFoundUnchecked);
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
                    ? Tagged(fresh, other, topic, facts.Checked, facts.CheckedOfTagged)
                    : FormattableString.Invariant(
                        $"You have {fresh}{other} unseen {Noun(topic, fresh)} tagged that way, but I couldn't check {(fresh == 1 ? "it" : "them")} against what you asked this time."));
            }
        }
        else
        {
            // Nothing clears the bar. When titles sit under it (or have no score while it is set),
            // "none left" would be false — they are there, and the tray shows them. The supply counts
            // only the TAGGED shelf, so when the trays below hold a possible match another arm found
            // (the full text, the vectors), the lead says the tagged ones are gone rather than that
            // none exist: "There are no period drama shows left for you … 1 title … shown below"
            // denied the card it then showed (HREV5-RET-03).
            var underIt = supply.PredictedFloor is { } bar && supply.BelowFloor + supply.Unscored > 0;
            taggedNone = !underIt && possible > 0;
            sentences.Add(underIt
                ? FormattableString.Invariant(
                    $"None of your unseen {topic ?? "matches"} clears your {AskReelReason.FormatFloor(supply.PredictedFloor!.Value)} bar.")
                : facts.Guessed ? GuessedLead
                : taggedNone ? $"None of the tagged {topic ?? "matches"} are left for you."
                : $"There are no {topic ?? "matches"} left for you.");
            sentences.Add(Partition(supply, topic));
        }

        // Beside a lead about the tagged shelf, what the trays hold is named as OTHER possible matches.
        var others = taggedNone || (facts.Guessed && possible > 0);
        string Held(int count) => others
            ? FormattableString.Invariant($"{count} other possible {(count == 1 ? "match" : "matches")}")
            : FormattableString.Invariant($"{count} {(count == 1 ? "title" : "titles")}");

        if (facts.UnderBar.Count > 0)
        {
            var named = facts.UnderBar.Take(MaxUnderBarNamed).ToList();
            var list = JoinedWithMore(named, facts.UnderBar.Count - named.Count);
            sentences.Add(FormattableString.Invariant(
                $"Under your {AskReelReason.FormatFloor(facts.Floor)} bar: {list} — shown below."));
            if (heldBack > 0)
            {
                sentences.Add($"Your bar also holds back {Held(heldBack)} I couldn't check against your request.");
            }
        }
        else if (heldBack > 0)
        {
            // The floor tray holds only cards nobody judged: the reply counts that tray, as its caption
            // does, rather than the tagged supply's under-bar count beside it (HREV4-ASK-03).
            sentences.Add(FormattableString.Invariant(
                $"Your {AskReelReason.FormatFloor(facts.Floor)} bar holds back {Held(heldBack)} I couldn't check against your request — shown below."));
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

        if (supply is { Knockouts.Count: > 0 } k && !facts.Guessed)
        {
            var offers = k.Knockouts
                .Take(MaxKnockoutsNamed)
                .Select(o => FormattableString.Invariant($"turning off “{o.Label}” would unlock {o.WouldUnlock}"))
                .ToList();
            sentences.Add(Capitalised(string.Join("; ", offers)) + ".");
        }

        if (uncheckedTray > 0)
        {
            sentences.Add($"{Capitalised(Held(uncheckedTray))} I couldn't check against your request {Are(uncheckedTray)} below, marked as unchecked.");
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

    /// <summary>
    /// The tagged-supply sentence on a turn the judge ran: "none of the ones I checked" only when
    /// every one of them was checked, and otherwise how many were, out of how many the judge saw.
    /// An uncounted check (<paramref name="checkedOfTagged"/> null) is no check.
    /// </summary>
    private static string Tagged(int fresh, string other, string? topic, int judged, int? checkedOfTagged)
    {
        var lead = FormattableString.Invariant($"You have {fresh}{other} unseen {Noun(topic, fresh)} tagged that way");
        var ofTagged = Math.Clamp(checkedOfTagged ?? 0, 0, fresh);
        if (ofTagged >= fresh)
        {
            return $"{lead}, but none of the ones I checked is really a match.";
        }

        if (judged <= 0)
        {
            return $"{lead}, but I didn't check {(fresh == 1 ? "it" : "them")} against what you asked this time.";
        }

        var checkedTitles = FormattableString.Invariant($"the {judged} {(judged == 1 ? "title" : "titles")} I checked this time");
        if (ofTagged == 0)
        {
            return $"{lead}, but {(fresh == 1 ? "it wasn't" : "none of them was")} among {checkedTitles}"
                + $", and {(judged == 1 ? "that one isn't" : "none of those is")} really a match.";
        }

        var rest = fresh - ofTagged;
        return FormattableString.Invariant(
            $"{lead}. I checked {ofTagged} of them and {(ofTagged == 1 ? "it isn't" : "none is")} really a match; the other {rest} {(rest == 1 ? "wasn't" : "weren't")} among {checkedTitles}.");
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

    /// <summary>
    /// "ninja movies", "ninja shows", "ninja titles"; null when the request named no concept. Words
    /// that already end in a plural noun for what is counted — a medium ("horror films") or a genre
    /// ("romantic comedies") — are the topic as they stand: "romantic comedies movies" read as a
    /// stutter on the founder's own romcom turn (HREV8-ASK-01).
    /// </summary>
    private static string? Topic(string? conceptWords, MediaScope media)
    {
        var words = conceptWords?.Trim();
        if (string.IsNullOrEmpty(words))
        {
            return null;
        }

        return EndsInPluralNoun(words) ? words : $"{words} {MediaNoun(media, plural: true)}";
    }

    /// <summary>Plural nouns that already name a kind of title, keyed to their singular.</summary>
    private static readonly IReadOnlyDictionary<string, string> PluralNouns =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["movies"] = "movie",
            ["films"] = "film",
            ["shows"] = "show",
            ["titles"] = "title",
            ["series"] = "series",
            ["miniseries"] = "miniseries",
            ["sitcoms"] = "sitcom",
            ["comedies"] = "comedy",
            ["romcoms"] = "romcom",
            ["dramas"] = "drama",
            ["thrillers"] = "thriller",
            ["westerns"] = "western",
            ["documentaries"] = "documentary",
            ["docuseries"] = "docuseries",
            ["musicals"] = "musical",
            ["mysteries"] = "mystery",
            ["romances"] = "romance",
            ["cartoons"] = "cartoon",
            ["biopics"] = "biopic",
            ["slashers"] = "slasher",
            ["procedurals"] = "procedural",
        };

    private static string LastWord(string words)
    {
        var at = words.LastIndexOf(' ');
        return at < 0 ? words : words[(at + 1)..];
    }

    private static bool EndsInPluralNoun(string words) => PluralNouns.ContainsKey(LastWord(words));

    private static string Noun(string? topic, int count) =>
        topic is null
            ? count == 1 ? "title" : "titles"
            : count == 1 ? Singular(topic) : topic;

    private static string Singular(string topic)
    {
        var last = LastWord(topic);
        return PluralNouns.TryGetValue(last, out var one)
            ? string.Concat(topic.AsSpan(0, topic.Length - last.Length), one)
            : topic;
    }

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
