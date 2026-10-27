using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// v1's judge order and the negation partition (<c>PartitionByNegation</c> is a §4.13 deletion), with the
/// ranking and concept texts. v1 only; deleted at cutover.
/// </summary>
public sealed partial class LiveSearchExpansionService
{
    /// <summary>
    /// The order the judge sees the ranked pool in: the partition's order (catalogue evidence first,
    /// then similarity), except that when more tagged titles are ranked than the judge rules on, the
    /// tagged titles the account's bar lets through go ahead of the tagged titles it holds back.
    ///
    /// <para><b>Why (IREV2-SYS-01).</b> The facet arm returns up to 24 tagged titles and the judge
    /// rules on 16. Similarity says nothing about which of them can become a pick, so a heist turn
    /// judged 16 tagged titles by cosine, never sent four of the five that cleared the bar, and
    /// answered none-found. A tagged title under the bar can only ever reach the floor tray. Scored
    /// only in that case: below the judge's cap every tagged title is judged anyway.</para>
    /// </summary>
    private async Task<List<(Title Title, double Sim)>> JudgeOrderAsync(
        Guid accountId, List<(Title Title, double Sim)> ranked, Func<Guid, bool> hasEvidence, decimal floor,
        DateTime now, FeatureVectorBuilder.TasteState taste, AskStageTimings timings, CancellationToken ct)
    {
        var tagged = ranked.Where(x => hasEvidence(x.Title.Id)).Select(x => x.Title.Id).ToList();
        if (floor <= 0m || tagged.Count <= AskReelJudge.MaxJudged)
        {
            return ranked;
        }

        IReadOnlyDictionary<Guid, decimal> predicted;
        using (timings.Stage(AskStageTimings.Score))
        {
            predicted = await scorer.ScoreAsync(accountId, tagged, now, ct, taste);
        }

        return OrderForJudge(ranked, hasEvidence, id => AskReelGate.Clears(predicted.TryGetValue(id, out var p) ? p : null, floor));
    }

    /// <summary>
    /// Tagged titles the bar lets through, then the other tagged titles, then the rest — stably, so
    /// equal footing keeps the partition's order.
    /// </summary>
    internal static List<(Title Title, double Sim)> OrderForJudge(
        IReadOnlyList<(Title Title, double Sim)> ranked, Func<Guid, bool> hasEvidence, Func<Guid, bool> clearsBar) =>
        [.. ranked
            .Select((x, index) => (x, index))
            .OrderBy(p => !hasEvidence(p.x.Title.Id) ? 2 : clearsBar(p.x.Title.Id) ? 0 : 1)
            .ThenBy(p => p.index)
            .Select(p => p.x)];

    /// <summary>
    /// Splits the similarity-ordered pool into the picks, the flagged "outside your request"
    /// reserve — and, before either, drops everything the account permanently excludes.
    ///
    /// <para><b>Two rules, not one, because the two sources are not the same kind of fact.</b></para>
    ///
    /// <para><b>A standing exclusion is airtight.</b> A title tripping one is removed from the pool
    /// outright and can appear in neither channel. That is the account's own permanent rule, set
    /// behind a PIN, and the product says so in as many words: "Your conditions permanently exclude
    /// comedy, so nothing you asked for there can appear." It was not true. Standing exclusions
    /// reached this method as ordinary soft negations — the interpret prompt is TOLD about them and
    /// the model echoes them back on <c>intent.ExcludeGenres</c> — so the very same turn that
    /// rendered that notice also rendered three comedy cards in the reserve beneath it, each
    /// captioned by the product as a comedy title. A notice that a filter is absolute, printed above
    /// cards the filter did not stop, is worse than no notice.</para>
    ///
    /// <para><b>A requested negation is soft</b>, and that is the behaviour this method was written
    /// for: "no horror please" drops contradicting titles whenever the clean set reaches
    /// <see cref="MinResults"/>, and otherwise tops up only to that floor — flagged, ordered after
    /// every clean pick, never silently mixed in. An empty screen is not a better answer than a
    /// labelled near-miss when the user's constraint was a preference they expressed in passing.</para>
    ///
    /// <para>The cull can leave a turn with less to show, and that is the correct outcome: it
    /// removes exactly the titles the account has said it never wants to see.</para>
    /// </summary>
    internal static (List<(Title Title, double Sim)> Ranked, List<(Title Title, double Sim)> Outside) PartitionByNegation(
        IReadOnlyList<(Title Title, double Sim)> scoredPool, AskReelNegations negations, Func<Guid, bool>? hasEvidence = null)
    {
        // Titles carrying catalogue evidence for the concept go ahead of every similarity-only title
        // BEFORE either cut, stably, so equal footing keeps the cosine order (IREV1-SYS-01).
        IEnumerable<(Title Title, double Sim)> ordered = hasEvidence is null
            ? scoredPool
            : scoredPool.Select((x, index) => (x, index))
                .OrderBy(p => hasEvidence(p.x.Title.Id) ? 0 : 1)
                .ThenBy(p => p.index)
                .Select(p => p.x);
        var permitted = negations.HasStanding
            ? ordered.Where(x => !TripsStandingExclusion(x.Title, negations)).ToList()
            : [.. ordered];
        var contradicts = permitted.Where(x => RequestedDemerit(x.Title, negations) > 0).ToList();
        var clean = permitted.Where(x => RequestedDemerit(x.Title, negations) == 0).ToList();
        var ranked = clean.Take(ReturnCount).ToList();
        var outside = ranked.Count < MinResults && contradicts.Count > 0
            ? contradicts.Take(MinResults - ranked.Count).ToList()
            : [];
        return (ranked, outside);
    }

    /// <summary>
    /// The exclusions a title actually trips, phrased for the outside-request caption ("a horror
    /// title", "one that matches ‘ghosts’") and tagged with where each rule came from. Mirrors
    /// <see cref="RequestedDemerit"/> and <see cref="TripsStandingExclusion"/> exactly, so a title
    /// in either channel always has at least one match to name.
    ///
    /// <para>Standing matches come first, because they are the stronger claim about why the card is
    /// where it is — and in practice they are unreachable from the outside channel, since
    /// <see cref="PartitionByNegation"/> culls those titles before the partition. They are still
    /// computed, because a caption that could silently fall back to "you asked to skip" for a rule
    /// the user never typed is exactly the defect this distinction closes.</para>
    /// </summary>
    internal static IReadOnlyList<NegationMatch> NegationMatches(Title title, AskReelNegations negations)
    {
        var matches = new List<NegationMatch>();

        void Collect(IReadOnlyList<string> genres, IReadOnlyList<string> keywords, bool standing)
        {
            matches.AddRange(genres
                .Where(bad => TripsExcludedGenre(title, bad))
                .Select(bad => new NegationMatch($"a {bad.ToLowerInvariant()} title", standing)));
            matches.AddRange(keywords
                .Where(w => w.Length > 2 && TripsKeyword(title, w))
                .Select(w => new NegationMatch($"one that matches ‘{w}’", standing)));
        }

        Collect(negations.StandingGenres, negations.StandingKeywords, standing: true);
        Collect(negations.RequestedGenres, negations.RequestedKeywords, standing: false);
        return matches;
    }

    /// <summary>
    /// The honest deterministic caption for an outside-request card. Replaces the LLM why — which
    /// cheerfully admitted violations ("exactly what they want to avoid") — with a plain statement
    /// of the first exclusion the title trips.
    ///
    /// <para><b>The tail names the right authority.</b> "Which you asked to skip" is a quotation of
    /// the user, and it was being printed for exclusions the user never made: on one measured
    /// screen, seven of eleven outside cards told a user who had asked for a 90s romantic comedy
    /// that they had asked to skip horror — an inference of the model's, phrased as their own
    /// words. A standing rule gets "which your conditions exclude", which is the other true
    /// sentence and is also actionable, because Conditions is a screen the user can open.</para>
    /// </summary>
    internal static string OutsideWhy(IReadOnlyList<NegationMatch> matches)
    {
        if (matches.Count == 0)
        {
            return "Closest match we could find, but it's outside what you asked for — which you asked to skip.";
        }

        var first = matches[0];
        var tail = first.Standing ? "which your conditions exclude" : "which you asked to skip";
        return $"Closest match we could find, but it's {first.Phrase} — {tail}.";
    }

    /// <summary>
    /// The text the ranking query vector is built from — the interpreted POSITIVE signal only
    /// (free text, keywords, genres), never the raw sentence and never the agent's prose reply.
    /// An embedding model has no notion of "not": "a thriller, no ghosts" and "a thriller with
    /// ghosts" land close together in vector space, so ranking against the raw turn scores the
    /// rejected concept HIGHEST on the similarity used to pick what to show. Falls back to the raw
    /// text only when the intent carries no positive signal at all, because a bad seed still beats
    /// an empty one.
    /// </summary>
    internal static string RankingSeedText(AgentReply agentReply, string rawQuery, AskReelNegations negations)
    {
        var intent = agentReply.Intent;

        // Keywords and genres are the only fields guaranteed to hold the positive reading: the
        // interpreter strips rejected terms out of them by contract, and AskReelIntentSanitizer
        // removes whatever slipped through. FreeText is deliberately NOT used when they are
        // available — the heuristic path sets it to the raw query verbatim, negations and all.
        var parts = new List<string>();
        if (intent.Keywords.Count > 0)
        {
            parts.Add(string.Join(", ", intent.Keywords));
        }

        if (intent.Genres.Count > 0)
        {
            parts.Add(string.Join(", ", intent.Genres));
        }

        if (parts.Count > 0)
        {
            return string.Join(". ", parts);
        }

        // Nothing structured survived. The fallback is the POSITIVE half of the free text or the raw
        // sentence — every negation cue, every word it governs and every word the user ruled out
        // removed — so "no superheroes please" seeds nothing rather than seeding superheroes.
        var negated = negations.RequestedGenres.Concat(negations.RequestedKeywords)
            .Concat(intent.ExcludeGenres ?? []).Concat(intent.ExcludeKeywords ?? []);
        var source = !string.IsNullOrWhiteSpace(intent.FreeText) ? intent.FreeText.Trim() : rawQuery;
        return AskReelIntentSanitizer.PositiveText(source, negated);
    }

    /// <summary>
    /// The text the semantic arm embeds — the SAME positive reading as the ranking seed (A-9, C-6).
    /// It used to prepend the model's reply and the raw free text, so the retrieval vector carried
    /// "…while avoiding comedy and romance" and, on a failed read, the canned sentence itself.
    /// </summary>
    internal static string ConceptText(AgentReply agentReply, string rawQuery, AskReelNegations negations) =>
        RankingSeedText(agentReply, rawQuery, negations);
}
