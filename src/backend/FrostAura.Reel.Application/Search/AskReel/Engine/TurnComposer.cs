using System.Globalization;
using FrostAura.Reel.Application.Search.Retrieval;

namespace FrostAura.Reel.Application.Search.AskReel.Engine;

/// <summary>
/// Everything a reply may be written from (§4.5 Composer): counts the server holds, the names of the
/// cards it showed, the facts it looked up. Nothing the model wrote, except an ItemQuestion's
/// answer (itself grounded in a fact sheet) and a clarifying question.
/// </summary>
/// <param name="Topic">The user's words for what was searched ("ninja"), or null for a mood or person request.</param>
/// <param name="Picks">How many judged picks reached the grid.</param>
/// <param name="UnjudgedPicks">Picks no judge ruled on (a failed batch on a turn that has judged picks).</param>
/// <param name="NearMissNames">The near-miss tray, in display order.</param>
/// <param name="UnderBar">The floor tray's size.</param>
/// <param name="Unchecked">The unchecked tray's size.</param>
/// <param name="Judged">
/// How many titles the judge actually ruled on — verdicts returned, not titles sent: 0 for a person's
/// credits, a turn with no model, and a judge that failed outright (HREV1-ASK-01).
/// </param>
/// <param name="CarriedForward">The plan failed and the previous state was searched again.</param>
/// <param name="JudgeFailed">At least one judge batch ruled on nothing.</param>
/// <param name="Person">The person a person request was about, with the role they were searched in.</param>
/// <param name="PersonSeen">How many of the person's credited titles the account has seen (FD-3: counted, never named).</param>
/// <param name="Answer">An ItemQuestion's answer text, or a tool answer composed upstream.</param>
/// <param name="AnswerTitle">The title an answer is about.</param>
/// <param name="ShownHere">Titles clearing the bar that this conversation had already shown (the supply's <c>AlreadyShown</c>).</param>
/// <param name="CheckedOfTagged">
/// Of the tagged titles clearing the bar and not yet shown, how many the judge ruled on — never a
/// count of titles sent. Null claims no check (<see cref="NoneFoundCopy.Facts.CheckedOfTagged"/>).
/// </param>
/// <param name="FirstTurn">
/// The conversation held no search before this turn — none at all, or only small talk, a question
/// about a title or a clarifying question: a carried-forward plan has nothing "before" to carry, so
/// its lead says it searched the user's own words (HREV1-ASK-06, HREV7-ASK-01).
/// </param>
/// <param name="Guessed">
/// No model read the message and the topic searched is a guess at it (a title it asked for something
/// like that the catalogue holds no title by): the reply leads with what was searched and never
/// states the counts as the user's own shelf (HREV4-RET-02).
/// </param>
/// <param name="Unread">
/// No model read the message — the plan is the keyless one or a carried-forward one — so a turn that
/// found nothing at all owns up to the reading rather than claiming nothing exists (HREV5-RET-02).
/// </param>
/// <param name="HeldUnchecked">
/// Of <paramref name="UnderBar"/>, the cards nobody judged (XM-06: the bar holds back an unchecked card
/// too). They are in the floor tray but never said to FIT, and are named as held back by the bar —
/// never counted with the unchecked tray's cards (HREV4-ASK-03).
/// </param>
public sealed record TurnContext(
    TurnAct Act,
    FinalMode Mode,
    string? Topic,
    MediaScope Media,
    int Picks,
    int UnjudgedPicks,
    IReadOnlyList<string> NearMissNames,
    int UnderBar,
    IReadOnlyList<string> UnderBarNames,
    int Unchecked,
    int Judged,
    decimal Floor,
    SupplyReport? Supply,
    bool CarriedForward,
    bool JudgeFailed,
    PersonConstraint? Person = null,
    int? PersonSeen = null,
    string? Answer = null,
    string? AnswerTitle = null,
    string? ClarifyQuestion = null,
    int ShownHere = 0,
    int HeldUnchecked = 0,
    int? CheckedOfTagged = null,
    bool FirstTurn = false,
    bool Guessed = false,
    bool Unread = false);

/// <summary>The reply and the one-line reason, as composed.</summary>
public sealed record ComposedTurn(string Reply, string? Reason);

/// <summary>The composer (§4.3): deterministic, copy pinned by tests.</summary>
public interface ITurnComposer
{
    ComposedTurn Compose(TurnContext context);
}

/// <summary>
/// The v2 reply templates. Plain text, no markdown, no "pulled", no pipeline counts, never the
/// user's message quoted back — a reply built only from these sentences cannot echo anything.
/// </summary>
public sealed class TurnComposer : ITurnComposer
{
    /// <summary>The §4.10 lead for a plan that failed and was carried forward.</summary>
    public const string CarriedForwardLead = "I couldn't fully read that — searching for {0} as before.";

    /// <summary>The same lead when the previous state named no topic to search for.</summary>
    public const string CarriedForwardPlainLead = "I couldn't fully read that, so I searched the way I did last time.";

    /// <summary>The lead for a plan that failed on a conversation's FIRST turn: nothing came before it.</summary>
    public const string FirstTurnFallbackLead = "I couldn't fully read that, so I searched for {0}.";

    /// <summary>The first-turn lead when the user's words left nothing to search for by name.</summary>
    public const string FirstTurnFallbackPlainLead = "I couldn't fully read that, so this is a broad search.";

    /// <summary>Small talk: what Ask Reel can do, in one line.</summary>
    public const string ChitchatReply =
        "I find films and shows for you — tell me a mood, a topic, a person or a title you liked.";

    /// <summary>Said beside picks when some batch of the judge failed.</summary>
    public const string JudgeFailedNote = "I couldn't check every card against your request this time — those are marked.";

    public ComposedTurn Compose(TurnContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Mode switch
        {
            FinalMode.Answer => new ComposedTurn(AnswerReply(context), null),
            FinalMode.Clarify => new ComposedTurn(
                context.ClarifyQuestion is { Length: > 0 } q ? AskReelText.StripMarkdown(q) ?? q : "Which did you mean?", null),
            FinalMode.NoneFound => new ComposedTurn(Lead(context, NoneFoundReply(context)), Reason(context)),
            _ => new ComposedTurn(Lead(context, PicksReply(context)), Reason(context)),
        };
    }

    private static string AnswerReply(TurnContext context) =>
        context.Act == TurnAct.Chitchat && context.Answer is null
            ? ChitchatReply
            : context.Answer ?? ChitchatReply;

    private static string PicksReply(TurnContext c)
    {
        var sentences = new List<string>(4);
        var total = c.Picks + c.UnjudgedPicks;
        if (c.Person is { } person)
        {
            var seen = c.PersonSeen ?? 0;
            sentences.Add(seen > 0
                ? Inv($"You've seen {seen} {Noun(c.Media, seen)} {Credit(person)}; here {(total == 1 ? "is one" : $"are {total}")} you haven't.")
                : Inv($"Here {(total == 1 ? "is one" : $"are {total}")} {Credit(person)} you haven't seen."));
        }
        else
        {
            // The noun agrees with the number in front of it: "1 heist movie checked …", never "1 heist movies".
            string What(int count) => c.Topic is { Length: > 0 } topic ? $"{topic} {Noun(c.Media, count)}" : Noun(c.Media, count);
            sentences.Add(c.UnjudgedPicks > 0
                ? Inv($"{c.Picks} {What(c.Picks)} checked against your request, and {c.UnjudgedPicks} more I couldn't check.")
                : Inv($"{total} {What(total)} that {(total == 1 ? "fits" : "fit")} what you asked."));
        }

        if (c.NearMissNames.Count > 0)
        {
            sentences.Add(Inv($"{c.NearMissNames.Count} more {(c.NearMissNames.Count == 1 ? "is" : "are")} close but not quite — tucked away below."));
        }

        // Only judged matches "fit"; an unchecked card the bar held back is counted as unchecked (XM-06).
        var fitting = c.UnderBar - c.HeldUnchecked;
        if (fitting > 0)
        {
            sentences.Add(Inv($"{fitting} more {(fitting == 1 ? "fits" : "fit")} but {(fitting == 1 ? "sits" : "sit")} under your {AskReelReason.FormatFloor(c.Floor)} bar."));
        }

        if (c.JudgeFailed && c.UnjudgedPicks == 0 && c.Unchecked > 0)
        {
            sentences.Add(JudgeFailedNote);
        }

        // An unjudged card the bar held back sits in the floor tray, which marks nothing: it is named
        // as held back there, never as "marked" (HREV4-ASK-03).
        if (c.HeldUnchecked > 0)
        {
            sentences.Add(Inv($"Your bar also holds back {c.HeldUnchecked} {(c.HeldUnchecked == 1 ? "title" : "titles")} I couldn't check against your request."));
        }

        return string.Join(' ', sentences);
    }

    private static string NoneFoundReply(TurnContext c)
    {
        if (c.Person is { } person && c.Supply is null)
        {
            var seen = c.PersonSeen ?? 0;
            return seen > 0
                ? Inv($"You've seen all {seen} {Noun(c.Media, seen)} {Credit(person)} that I know of — there are none left for you.")
                : Inv($"I don't know of any {Noun(c.Media, 2)} {Credit(person)} that you haven't seen.");
        }

        // A person request's supply is counted over their credits, so the person names the shelf
        // ("Christopher Nolan movies") the way a topic does.
        return NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            c.Supply,
            c.Topic ?? c.Person?.Name,
            c.Media,
            c.UnderBarNames,
            c.NearMissNames.Count,
            c.Floor,
            // Each unjudged card in the tray it sits in (HREV4-ASK-03): the unchecked tray's are
            // "marked as unchecked", the floor tray's are held back by the bar.
            Unchecked: c.Unchecked,
            HeldUnchecked: c.HeldUnchecked,
            // Only a verdict that came back is a check: a judge sent sixteen titles that answered
            // none, a person's credits and a turn with no model all judged nothing (HREV1-ASK-01).
            JudgedAny: c.Judged > 0,
            ShownHere: c.ShownHere,
            NearMissNames: c.NearMissNames,
            Checked: c.Judged,
            CheckedOfTagged: c.CheckedOfTagged,
            Guessed: c.Guessed,
            Unread: c.Unread,
            UnreadLed: c.CarriedForward || c.Guessed));
    }

    private static string Lead(TurnContext c, string reply)
    {
        if (!c.CarriedForward && !c.Guessed)
        {
            return reply;
        }

        // A guessed reading of a new request searched the user's words, not anything "before".
        var fresh = c.FirstTurn || (c.Guessed && c.Act == TurnAct.NewRequest);
        var lead = (c.Topic, fresh) switch
        {
            ({ Length: > 0 } topic, true) => string.Format(CultureInfo.InvariantCulture, FirstTurnFallbackLead, topic),
            (_, true) => FirstTurnFallbackPlainLead,
            ({ Length: > 0 } topic, false) => string.Format(CultureInfo.InvariantCulture, CarriedForwardLead, topic),
            _ => CarriedForwardPlainLead,
        };
        return $"{lead} {reply}";
    }

    private static string Reason(TurnContext c) =>
        AskReelReason.Build(
            new TurnCounts(
                Picks: c.Picks + c.UnjudgedPicks,
                Outside: 0,
                Tray: c.UnderBar,
                NearMiss: c.NearMissNames.Count,
                Unchecked: c.UnjudgedPicks,
                UncheckedTray: c.Unchecked,
                HeldUnchecked: c.HeldUnchecked),
            c.Floor);

    private static string Credit(PersonConstraint person) => person.Role switch
    {
        Domain.Ports.PersonRole.Director => $"directed by {person.Name}",
        Domain.Ports.PersonRole.Cast => $"starring {person.Name}",
        _ => $"with {person.Name}",
    };

    private static string Noun(MediaScope media, int count) => media switch
    {
        MediaScope.Movies => count == 1 ? "movie" : "movies",
        MediaScope.Shows => count == 1 ? "show" : "shows",
        _ => count == 1 ? "title" : "titles",
    };

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}
