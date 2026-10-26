using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Search;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// v1's keyless turn (no model connected): keyword matches shown as unchecked, and a pointer with nothing on
/// screen asked back. v1 only — v2's keyless plan is <c>FallbackPlans.Keyless</c>; deleted at cutover.
/// </summary>
public sealed partial class LiveSearchExpansionService
{
    /// <summary>
    /// A keyless install's turn (R1-10): no model reads the request and none checks a card against
    /// it, so nothing here is a pick. The local lexical engine still finds titles carrying the user's
    /// words, inside every standing condition and the media the turn is about, and they are shown as
    /// what they are — unchecked — with the account's bar on every card and counted in the reply.
    /// Honest copy, the <see cref="SearchTurnDegraded.NoModel"/> flag, stage timings and no exposure:
    /// the same record a keyed turn keeps.
    ///
    /// <para>It used to stream the lexical hits straight into the grid as picks, with no reply, no
    /// flag and no timings: "ninja movies" answered American Ninja Warrior, a reality series, and the
    /// follow-up answered 24 unrelated titles, 8 of them under the founder's 6.0 bar.</para>
    /// </summary>
    private async Task<TurnOutcome> StreamKeylessAsync(
        Guid accountId, string query, AskTurnInput input, AskStageTimings timings,
        Func<string, bool, Task> reply, Emit emit, CancellationToken ct)
    {
        var media = KeylessMedia(query, input);
        var shown = input.ShownTitleIds.ToHashSet();

        // Read before anything is searched (IREV1-SYS-04). A message pointing at a card on screen
        // ("how long is the second one?") is a question about that card, which nothing here can
        // read — and its leftover words matched 14 unrelated titles. A message that only rules
        // things out ("no superheroes please") has nothing to search FOR, and searching it as
        // written answered 17 superhero titles. Neither is searched.
        var reading = AskReelIntentSanitizer.ReadKeyless(query);
        var pointer = AskReelPointer.Ordinal(query);
        // Built directly rather than through FromRequest: its vocabulary floor guards a CAPTION, and
        // a keyless card that trips a negated word is dropped, never captioned (see ReadKeyless).
        var negations = new AskReelNegations(
            [], [], [.. reading.Negated.SelectMany(SearchVocabulary.ResolveGenres).Distinct(StringComparer.Ordinal)], reading.Negated);
        var nothingToSearch = pointer is not null
            ? KeylessUnsearched.Pointer
            : reading.Positive.Count == 0 ? KeylessUnsearched.NoPositiveWords : KeylessUnsearched.None;
        List<LexicalSearchService.LexicalResult> hits = [];
        using (timings.Stage(AskStageTimings.Discover))
        {
            if (nothingToSearch == KeylessUnsearched.None)
            {
                hits = (await lexical.SearchAsync(accountId, string.Join(' ', reading.Positive), ReturnCount * 2, ct))
                    .Where(h => !shown.Contains(h.Title.Id))
                    .Where(h => MatchesMedia(h.Title, media))
                    // What the user ruled out never reaches the screen as a keyword match.
                    .Where(h => RequestedDemerit(h.Title, negations) == 0)
                    .Take(ReturnCount)
                    .ToList();
            }
        }

        decimal floor;
        IReadOnlyDictionary<Guid, decimal> scores;
        using (timings.Stage(AskStageTimings.Score))
        {
            floor = MinPredictedFloor(await db.ContentFilters.Where(f => f.AccountId == accountId).ToListAsync(ct));
            scores = hits.Count == 0
                ? new Dictionary<Guid, decimal>()
                : await scorer.ScoreAsync(accountId, hits.Select(h => h.Title.Id).ToList(), DateTime.UtcNow, ct);
        }

        // Clearing the bar first, each half in match order: the bar is still the user's own rule,
        // and a card under it never leads.
        var cards = hits
            .Select((h, index) => (
                Card: new GatedCard(
                    h.Title, Math.Round(h.MatchScore, 3), scores.TryGetValue(h.Title.Id, out var p) ? p : null,
                    Verdict: null, Blend: 0d, AskReelGate.Placement.Unjudged, Outside: false, Why: null),
                Index: index))
            .OrderBy(x => AskReelGate.Clears(x.Card.Predicted, floor) ? 0 : 1)
            .ThenBy(x => x.Index)
            .Select(x => x.Card)
            .ToList();
        var underBar = cards.Count(c => !AskReelGate.Clears(c.Predicted, floor));

        var text = nothingToSearch == KeylessUnsearched.None
            ? KeylessCopy(cards.Count, underBar, floor)
            : KeylessUnsearchedCopy(nothingToSearch, pointer);
        string[] flags = [SearchTurnDegraded.NoModel];
        await reply(text, true);
        // The bar binds a keyword match like any other card (XM-06): under it is the floor tray.
        var (held, clearing) = SplitByBar(cards, floor);
        var reason = AskReelReason.Build(
            new TurnCounts(Picks: 0, Outside: 0, Tray: held.Count, UncheckedTray: clearing.Count, HeldUnchecked: held.Count), floor);
        await emit("done", new
        {
            results = Array.Empty<object>(),
            streamed = held.Count,
            kept = 0,
            droppedByFloor = held.Count,
            belowFloor = held.Select(CardOf).ToList(),
            nearMiss = Array.Empty<object>(),
            @unchecked = clearing.Select(CardOf).ToList(),
            mode = SearchTurnMode.NoneFound,
            supply = (object?)null,
            degraded = flags,
            reason,
            timingsMs = input.Debug ? timings.Snapshot() : null,
            debug = (object?)null,
        });

        return new TurnOutcome(
            text,
            reason,
            Failed: false,
            FailureCode: null,
            Relaxation: null,
            FloorRelaxed: false,
            DiscoveredCount: hits.Count,
            IntentJson: null,
            ModelArtifactId: await ActiveArtifactAsync(accountId, ct),
            ModelId: null,
            Stored([], [], held, clearing),
            ConflictJson: null,
            SearchTurnMode.NoneFound,
            DegradedFlagsOf(flags),
            TimingsJson(timings),
            ModelIdsJson: null,
            ReasoningEffort: null);
    }

    /// <summary>
    /// The media a keyless turn is about, with no model to read it: the UI's explicit filter, else
    /// the message's own media words ("movies", "shows"), else the previous answer's single media
    /// type when the message names none (<see cref="AskReelCarry.Media"/>).
    /// </summary>
    internal static IReadOnlyList<MediaType> KeylessMedia(string message, AskTurnInput input)
    {
        if (input.MediaFilter.Count > 0)
        {
            return input.MediaFilter.Distinct().ToList();
        }

        return AskReelCarry.Media(AskReelCarry.MediaNamed(message), input.PriorIntent?.MediaTypes, message);
    }

    /// <summary>Why a keyless turn ran no search at all — or <see cref="None"/> when it did.</summary>
    internal enum KeylessUnsearched
    {
        /// <summary>The message named something to search for, and it was searched.</summary>
        None,

        /// <summary>The message points at a card on screen: a question about that card, not a search.</summary>
        Pointer,

        /// <summary>Every content word of the message is something the user ruled out, or filler.</summary>
        NoPositiveWords,
    }

    private const string KeylessLead =
        "Ask Reel has no language model connected right now, so I can’t read what you asked or check anything against it.";

    /// <summary>
    /// The keyless reply when nothing was searched (IREV1-SYS-04): it says why, and never implies a
    /// search found nothing.
    /// </summary>
    internal static string KeylessUnsearchedCopy(KeylessUnsearched why, int? pointer) => why switch
    {
        KeylessUnsearched.Pointer =>
            $"{KeylessLead} That includes a question about {PointerPhrase(pointer)}, so I haven’t searched — that title is still in my earlier answer above.",
        _ => $"{KeylessLead} A plain keyword search needs a word for what you want, and your message only names what to skip — so I haven’t searched.",
    };

    private static string PointerPhrase(int? pointer) =>
        pointer is { } position ? $"“{AskReelPointer.Named(position)}”" : "a title on screen";

    /// <summary>The keyless reply: what happened, what is below, and how many of those sit under the bar.</summary>
    internal static string KeylessCopy(int shown, int underBar, decimal floor)
    {
        const string lead = KeylessLead;
        if (shown == 0)
        {
            return $"{lead} A plain keyword search for your words found nothing new either.";
        }

        var below = FormattableString.Invariant(
            $"Below {(shown == 1 ? "is 1 title" : $"are {shown} titles")} carrying your words — a keyword match only, marked as unchecked.");
        if (underBar <= 0 || floor <= 0m)
        {
            return $"{lead} {below}";
        }

        var bar = AskReelReason.FormatFloor(floor);
        return shown == 1
            ? $"{lead} {below} It scores under your {bar} bar."
            : FormattableString.Invariant($"{lead} {below} {underBar} of them {(underBar == 1 ? "scores" : "score")} under your {bar} bar.");
    }

    /// <summary>
    /// A pointer with nothing on screen to point at (R1-04): asked back, deterministically, with no
    /// model call and no search — so nothing is guessed from the sentence's leftover words.
    /// </summary>
    private static async Task<TurnOutcome> NothingToPointAtAsync(
        int pointer, AskTurnInput input, AskStageTimings timings, Func<string, bool, Task> reply, Emit emit)
    {
        var text = AskReelPointer.NothingToPointAt(pointer, input.Presented);
        await reply(text, false);
        const string reason = "nothing on screen to point at";
        await emit("done", new
        {
            results = Array.Empty<object>(),
            streamed = 0,
            kept = 0,
            droppedByFloor = 0,
            belowFloor = Array.Empty<object>(),
            nearMiss = Array.Empty<object>(),
            @unchecked = Array.Empty<object>(),
            mode = SearchTurnMode.Clarify,
            supply = (object?)null,
            degraded = Array.Empty<string>(),
            reason,
            timingsMs = input.Debug ? timings.Snapshot() : null,
            debug = (object?)null,
        });

        return new TurnOutcome(
            text, reason, Failed: false, FailureCode: null, Relaxation: null, FloorRelaxed: false, DiscoveredCount: null,
            IntentJson: null, ModelArtifactId: null, ModelId: null, [], ConflictJson: null, SearchTurnMode.Clarify,
            DegradedFlags: null, TimingsJson(timings), ModelIdsJson: null, ReasoningEffort: null);
    }
}
