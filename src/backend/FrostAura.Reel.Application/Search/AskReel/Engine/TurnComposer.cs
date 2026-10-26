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
/// <param name="Judged">How many titles the judge was sent.</param>
/// <param name="CarriedForward">The plan failed and the previous state was searched again.</param>
/// <param name="JudgeFailed">At least one judge batch ruled on nothing.</param>
/// <param name="Person">The person a person request was about, with the role they were searched in.</param>
/// <param name="PersonSeen">How many of the person's credited titles the account has seen (FD-3: counted, never named).</param>
/// <param name="Answer">An ItemQuestion's answer text, or a tool answer composed upstream.</param>
/// <param name="AnswerTitle">The title an answer is about.</param>
/// <param name="ShownHere">Titles clearing the bar that this conversation had already shown.</param>
/// <param name="HeldUnchecked">
/// Of <paramref name="UnderBar"/>, the cards nobody judged (XM-06: the bar holds back an unchecked card
/// too). They are in the floor tray but never said to FIT, and are counted with the unchecked cards.
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
    int HeldUnchecked = 0);

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
            var what = c.Topic is { Length: > 0 } topic ? $"{topic} {Noun(c.Media, total)}" : Noun(c.Media, total);
            sentences.Add(c.UnjudgedPicks > 0
                ? Inv($"{c.Picks} {what} checked against your request, and {c.UnjudgedPicks} more I couldn't check.")
                : Inv($"{total} {what} that fit what you asked."));
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

        if (c.JudgeFailed && c.UnjudgedPicks == 0 && c.Unchecked + c.HeldUnchecked > 0)
        {
            sentences.Add(JudgeFailedNote);
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
            Unchecked: c.Unchecked + c.HeldUnchecked,
            JudgedAny: !c.JudgeFailed || c.Judged > 0,
            ShownHere: c.ShownHere,
            NearMissNames: c.NearMissNames,
            Checked: c.Judged));
    }

    private static string Lead(TurnContext c, string reply)
    {
        if (!c.CarriedForward)
        {
            return reply;
        }

        var lead = c.Topic is { Length: > 0 } topic
            ? string.Format(CultureInfo.InvariantCulture, CarriedForwardLead, topic)
            : CarriedForwardPlainLead;
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
