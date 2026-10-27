using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// v1's honest none-found turn and the supply wire shape. <see cref="SupplyWire"/> is shared with v2 and moves
/// with the contracts at cutover; the rest is v1 only (§4.13).
/// </summary>
public sealed partial class LiveSearchExpansionService
{
    /// <summary>
    /// The none-found answer: the deterministic account of the supply as the reply, the trays the
    /// judge filled (the under-bar matches, the near misses), and a terminal frame that says
    /// <c>mode: noneFound</c> with the supply report behind it.
    /// </summary>
    private async Task<TurnOutcome> NoneFoundAsync(
        Guid accountId,
        Retrieval.HardConstraints hard,
        Retrieval.RetrievalResult? facet,
        SearchIntent intent,
        int relaxation,
        int eligibleCount,
        int eligiblePoolSize,
        int discoveredCount,
        IReadOnlyList<string> underBar,
        IReadOnlyList<GatedCard> nearMiss,
        IReadOnlyList<GatedCard> belowFloor,
        decimal floor,
        string? planDegradedReply,
        Func<string, Task> replyAsync,
        IReadOnlyList<string> degraded,
        AskStageTimings timings,
        AskTurnInput input,
        Emit emit,
        string? conflictJson,
        string? conflictMessage,
        int judged,
        AskReelJudge.Summary? judge,
        string? effort,
        CancellationToken ct,
        IReadOnlyList<GatedCard>? uncheckedTray = null,
        IReadOnlyList<string>? unscoredHeld = null,
        IReadOnlyCollection<Guid>? judgedIds = null,
        bool scoredThisTurn = false,
        int heldUnchecked = 0)
    {
        uncheckedTray ??= [];
        judgedIds ??= [];
        Retrieval.SupplyReport? supply = facet?.Supply;

        // The facet arm counted the supply at DISCOVERY, before this turn scored anything. Its split
        // of the eligible titles by score is stale the moment the turn scores one of its unscored
        // titles: the dinosaur turn said "4 have no score from your model yet" above a tray showing
        // all four at 6.06–6.53 against a 6.0 bar (IREV3-SYS-01). So a supply with unscored titles is
        // counted again, through the same reporter, once the turn's scores are stored.
        var stale = scoredThisTurn && supply is { Unscored: > 0 };
        if ((supply is null || stale) && supplyReporter is not null && (hard.HasConcepts || hard.People.Count > 0))
        {
            try
            {
                supply = await supplyReporter.ReportAsync(accountId, hard, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The counts are the explanation, not the answer: without them the reply still says
                // nothing matched, it just cannot say how many were seen. A stale count is not kept
                // in their place — it is the false sentence this re-count exists to prevent.
                logger.LogWarning(ex, "Ask Reel: the supply report failed; the none-found reply carries no counts.");
                supply = null;
            }
        }
        else if (stale)
        {
            // No reporter to count again with: no counts rather than counts the turn has outdated.
            supply = null;
        }

        // The same resolved topic the supply counted (R1-02): the words are the ones the predicate used.
        var concept = hard.Concepts.Count > 0 ? hard.Concepts[0].UserWords : null;
        var shownHere = supply is { ClearingFloor: > 0 }
            ? await ClearingInSupplyAsync(accountId, hard, supply, input.ShownTitleIds, floor, ct) ?? 0
            : 0;
        // How many of the tagged titles that clear the bar the judge actually ruled on (IREV2-SYS-01):
        // the supply counts the whole catalogue, the judge at most MaxJudged a turn.
        var checkedOfTagged = supply is { ClearingFloor: > 0 }
            ? await ClearingInSupplyAsync(accountId, hard, supply, judgedIds, floor, ct)
            : 0;
        var text = NoneFoundCopy.Compose(new NoneFoundCopy.Facts(
            supply, concept, hard.Media, underBar, nearMiss.Count, floor,
            // Every card nobody judged, in either tray: the bar holds some of them back (XM-06), and
            // they are still cards the reply owes the "couldn't check" sentence for.
            Unchecked: uncheckedTray.Count + heldUnchecked,
            JudgedAny: judge?.JudgedAny ?? true,
            ShownHere: shownHere,
            UnscoredHeld: unscoredHeld,
            NearMissNames: [.. nearMiss.Select(c => c.Title.Name)],
            Checked: judgedIds.Count,
            CheckedOfTagged: checkedOfTagged));

        // A follow-up that changes nothing hears that it was heard, not the same paragraph again.
        text = NoneFoundCopy.Again(text, PreviousReply(input.History));

        // A collision with the account's own conditions is the likeliest reason nothing is left,
        // so it leads: "Your conditions exclude comedy and romance…" before the counts.
        if (conflictMessage is { Length: > 0 })
        {
            text = $"{conflictMessage} {text}";
        }

        // A degraded plan leads with the reply for its cause (IREV2-SYS-02), not the transient
        // "in a moment" sentence: a spent allowance or a refused key does not recover by waiting.
        if (planDegradedReply is { Length: > 0 })
        {
            text = $"{planDegradedReply} {text}";
        }

        await replyAsync(text);
        var reason = AskReelReason.Build(
            new TurnCounts(
                Picks: 0, Outside: 0, Tray: belowFloor.Count, NearMiss: nearMiss.Count, UncheckedTray: uncheckedTray.Count,
                HeldUnchecked: heldUnchecked),
            floor);
        await emit("done", new
        {
            results = Array.Empty<object>(),
            streamed = belowFloor.Count,
            kept = 0,
            droppedByFloor = belowFloor.Count,
            belowFloor = belowFloor.Select(CardOf).ToList(),
            nearMiss = nearMiss.Select(CardOf).ToList(),
            @unchecked = uncheckedTray.Select(CardOf).ToList(),
            mode = SearchTurnMode.NoneFound,
            supply = SupplyWire(supply),
            degraded = degraded.ToArray(),
            reason,
            timingsMs = input.Debug ? timings.Snapshot() : null,
            debug = input.Debug
                ? (object?)new
                {
                    intent,
                    relaxation,
                    eligibleCount,
                    eligiblePoolSize,
                    judged,
                    judgeBatches = judge?.Batches ?? 0,
                    judgeFailedBatches = judge?.FailedBatches ?? 0,
                    judgeMissing = judge?.Missing ?? 0,
                    topic = concept,
                    streamed = belowFloor.Count,
                    kept = 0,
                    droppedByFloor = belowFloor.Count,
                    nearMiss = nearMiss.Count,
                    timingsMs = timings.Snapshot(),
                }
                : null,
        });

        return new TurnOutcome(
            text,
            reason,
            Failed: false,
            FailureCode: null,
            relaxation,
            FloorRelaxed: false,
            discoveredCount,
            System.Text.Json.JsonSerializer.Serialize(intent),
            await ActiveArtifactAsync(accountId, ct),
            agent.ModelId,
            Stored([], nearMiss, belowFloor, uncheckedTray),
            conflictJson,
            SearchTurnMode.NoneFound,
            DegradedFlagsOf(degraded),
            TimingsJson(timings),
            ModelIdsJson(),
            effort);
    }

    /// <summary>
    /// The supply report as the wire carries it: every enum as its NAME. The report's
    /// <c>ConditionKnockout.Kind</c> is a <c>FilterKind</c>, and the stream's serializer writes enums
    /// as numbers, so the client — which reads <c>kind</c> as a string — rejected every <c>done</c>
    /// frame that carried a relaxation offer and left the turn spinning (acceptance D1). Projected
    /// here rather than by a global converter, so no other frame's shape moves.
    /// </summary>
    internal static object? SupplyWire(Retrieval.SupplyReport? supply) =>
        supply is null
            ? null
            : new
            {
                supply.MatchingInCatalogue,
                supply.AlreadySeen,
                supply.SavedOrDismissed,
                supply.ExcludedByConditions,
                supply.Eligible,
                supply.BelowFloor,
                supply.Unscored,
                supply.ClearingFloor,
                supply.LowVoteMatches,
                supply.MinVotes,
                supply.PredictedFloor,
                supply.KnockoutsMeasured,
                Knockouts = supply.Knockouts.Select(k => new
                {
                    Kind = k.Kind.ToString(),
                    k.Value,
                    k.ContentFilterId,
                    k.Label,
                    k.WouldUnlock,
                    Sample = k.Sample.Select(t => new { t.TitleId, t.Name, t.Year, Media = t.Media.ToString() }).ToList(),
                }).ToList(),
            };

    /// <summary>
    /// How many of <paramref name="titleIds"/> the supply counts as clearing the bar — counted through
    /// the same eligibility and topic predicates, then the same bar. Asked of the titles this
    /// conversation has already shown, which were excluded before anything was judged, so the reply
    /// names them as shown rather than as rejected (D4); and of the titles the judge ruled on, so the
    /// reply never says it checked tagged titles it never sent to the judge (IREV2-SYS-01). A failure
    /// costs the sentence, never the turn: it answers null.
    /// </summary>
    private async Task<int?> ClearingInSupplyAsync(
        Guid accountId, Retrieval.HardConstraints hard, Retrieval.SupplyReport supply,
        IReadOnlyCollection<Guid> titleIds, decimal floor, CancellationToken ct)
    {
        if (titleIds.Count == 0)
        {
            return 0;
        }

        try
        {
            var ids = titleIds.ToList();
            var ceiling = await eligibility.MaturityCeilingAsync(accountId, ct);
            var topical = (await Retrieval.HardConstraintFilter.Apply(eligibility.EligibleTitles(accountId), db, hard)
                    .Where(t => ids.Contains(t.Id))
                    .ToListAsync(ct))
                .Where(t => Retrieval.CrowdVotes.Of(t) >= supply.MinVotes)
                .Where(t => EligibilityQueryBuilder.PassesMaturity(t, ceiling))
                .Select(t => t.Id)
                .ToList();
            if (topical.Count == 0)
            {
                return 0;
            }

            var artifactId = await ActiveArtifactAsync(accountId, ct);
            if (artifactId is not { } activeId)
            {
                return 0;
            }

            var predicted = await db.TitleScores
                .Where(x => x.AccountId == accountId && x.ModelArtifactId == activeId && topical.Contains(x.TitleId))
                .Select(x => new { x.TitleId, x.PredictedRating })
                .ToListAsync(ct);
            return predicted
                .GroupBy(x => x.TitleId)
                .Count(g => AskReelGate.Clears(g.First().PredictedRating, floor));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ask Reel: counting titles inside the supply failed; the reply omits the count.");
            return null;
        }
    }

    /// <summary>The last thing Reel said in this conversation, without the card handles history appends.</summary>
    private static string? PreviousReply(IReadOnlyList<ChatTurn> history)
    {
        var last = history.LastOrDefault(t => t.Role == "assistant")?.Text;
        if (last is null)
        {
            return null;
        }

        var handles = last.IndexOf(AskReelHandles.HistoryMarker, StringComparison.Ordinal);
        return (handles >= 0 ? last[..handles] : last).TrimEnd();
    }
}
