using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking.Interleaving;
using FrostAura.Reel.Domain.Feed;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Experiments;

/// <summary>What one policy earned across the experiment.</summary>
/// <param name="PolicyId">The ranker.</param>
/// <param name="Description">Its one-line description, or null when it is no longer a known policy.</param>
/// <param name="Team">"A", "B", or null when it is neither of the currently configured arms.</param>
/// <param name="Slates">Slates it contributed at least one card to.</param>
/// <param name="Cards">Cards it contributed.</param>
/// <param name="Credits">Outcome credits its cards earned.</param>
/// <param name="SlateWins">Slates where it out-earned the other arm.</param>
public sealed record PolicyStanding(
    string PolicyId,
    string? Description,
    string? Team,
    int Slates,
    int Cards,
    int Credits,
    int SlateWins);

/// <summary>
/// The founder-facing reading of the experiment. Every field is computed on read from the
/// append-only exposure log — there is no summary table, because a stored rollup of a number the
/// founder is invited to peek at continuously would only ever be a staleness class.
/// </summary>
/// <param name="ComputedAt">When this reading was taken.</param>
/// <param name="Enabled">Whether new slates are currently being drafted.</param>
/// <param name="PolicyA">The configured team-A ranker.</param>
/// <param name="PolicyB">The configured team-B ranker.</param>
/// <param name="Tau">The mSPRT mixing standard deviation the p-value and CI were computed with.</param>
/// <param name="Alpha">The always-valid type-I ceiling.</param>
/// <param name="Slates">Slates drafted between exactly the two configured arms.</param>
/// <param name="DecidedSlates">Slates where one arm out-earned the other. The test's sample size.</param>
/// <param name="Ties">Slates with equal credit — including the (usual) zero-zero. Not observations.</param>
/// <param name="WinsA">Decided slates won by team A.</param>
/// <param name="WinsB">Decided slates won by team B.</param>
/// <param name="AlwaysValidP">The mSPRT p-value. Safe to read at any moment, as often as you like.</param>
/// <param name="Significant">Whether it has crossed <paramref name="Alpha"/>.</param>
/// <param name="PreferenceRateA">A's observed share of decided slates, or null with none.</param>
/// <param name="CiLow">Lower end of the always-valid interval on A's preference rate.</param>
/// <param name="CiHigh">Upper end of the always-valid interval.</param>
/// <param name="SignTestP">The fixed-sample sanity companion. Never the decision.</param>
/// <param name="FirstSlateAt">When the experiment's first slate was served.</param>
/// <param name="LastSlateAt">When its most recent slate was served.</param>
/// <param name="DecidedSlatesPerDay">The observed rate — what the time estimate is built on.</param>
/// <param name="SlatesToDecisionAt65">Decided slates a 65/35 preference needs to cross α.</param>
/// <param name="EstimatedDaysToDecision">Days remaining at the observed rate, or null when unknowable.</param>
/// <param name="Standings">Per-policy detail, including any arm no longer configured.</param>
public sealed record InterleaveSummary(
    DateTime ComputedAt,
    bool Enabled,
    string PolicyA,
    string PolicyB,
    double Tau,
    double Alpha,
    int Slates,
    int DecidedSlates,
    int Ties,
    int WinsA,
    int WinsB,
    double AlwaysValidP,
    bool Significant,
    double? PreferenceRateA,
    double CiLow,
    double CiHigh,
    double SignTestP,
    DateTime? FirstSlateAt,
    DateTime? LastSlateAt,
    double? DecidedSlatesPerDay,
    int SlatesToDecisionAt65,
    double? EstimatedDaysToDecision,
    IReadOnlyList<PolicyStanding> Standings);

/// <summary>
/// Reads the interleaving experiment out of the exposure log.
///
/// <para><b>Outcome credit.</b> A card earns its ranker one credit when the user SAVED it, WATCHED
/// it, or RATED IT 8 OR HIGHER after it was served — the same three signals the
/// <c>ExposureOutcomes</c> view already computes, with the causal time guards already in it. A
/// rating below 8 is deliberately not a credit: the product's claim is "you will love this", so a
/// tolerated film is not a win for the ranker that surfaced it.</para>
///
/// <para><b>The unit is the slate, not the card.</b> Preference is measured within a slate because
/// that is the only place the two rankers competed for the same attention at the same moment.
/// Comparing card totals across slates would let a ranker win by being on screen more often, which
/// team-draft's balance is specifically designed to prevent.</para>
///
/// <para><b>Slates are matched to the CURRENT arms.</b> Only slates whose contributing policies are
/// exactly the two configured ones enter the test; anything else is reported in
/// <see cref="InterleaveSummary.Standings"/> instead of being quietly folded in. Reconfiguring the
/// arms therefore restarts the comparison honestly rather than mixing two experiments into one
/// p-value.</para>
/// </summary>
public sealed class InterleaveSummaryQuery(IReelDbContext db, InterleaveOptions options)
{
    /// <summary>Computes the reading for one account.</summary>
    public async Task<InterleaveSummary> ComputeAsync(Guid accountId, DateTime now, CancellationToken ct)
    {
        // The experiment's window (INTERLEAVE_SINCE): slates served before the ranker and pool were
        // frozen belong to another experiment.
        var since = options.Since ?? DateTime.MinValue;
        var exposures = await db.ExposureEvents
            .Where(e => e.AccountId == accountId && e.InterleaveGroup != null && e.PolicyId != null && e.OccurredAt >= since)
            .Select(e => new { e.Id, e.InterleaveGroup, e.PolicyId, e.OccurredAt })
            .ToListAsync(ct);

        if (exposures.Count == 0)
        {
            return Empty(now);
        }

        var credited = (await db.ExposureOutcomes
                .Where(o => o.AccountId == accountId
                    && (o.SavedAt != null || o.WatchedAt != null || o.ActualRating >= 8))
                .Select(o => o.ExposureId)
                .ToListAsync(ct))
            .ToHashSet();

        // One row per (slate, policy): how many cards it contributed and how many earned credit.
        var slates = exposures
            .GroupBy(e => e.InterleaveGroup!.Value)
            .Select(g => new
            {
                Group = g.Key,
                At = g.Min(e => e.OccurredAt),
                ByPolicy = g.GroupBy(e => e.PolicyId!)
                    .ToDictionary(
                        p => p.Key,
                        p => (Cards: p.Count(), Credits: p.Count(e => credited.Contains(e.Id))),
                        StringComparer.Ordinal),
            })
            .OrderBy(s => s.At)
            .ToList();

        var matched = slates
            .Where(s => s.ByPolicy.ContainsKey(options.PolicyA)
                && s.ByPolicy.ContainsKey(options.PolicyB)
                && s.ByPolicy.Count == 2)
            .ToList();

        var preferences = new List<bool>(matched.Count);
        var ties = 0;
        foreach (var slate in matched)
        {
            var creditA = slate.ByPolicy[options.PolicyA].Credits;
            var creditB = slate.ByPolicy[options.PolicyB].Credits;
            if (creditA == creditB)
            {
                ties++;
                continue;
            }

            preferences.Add(creditA > creditB);
        }

        var decided = preferences.Count;
        var winsA = preferences.Count(x => x);
        var winsB = decided - winsA;
        var p = Msprt.AlwaysValidP(preferences, options.Tau);
        var rate = decided == 0 ? (double?)null : (double)winsA / decided;
        var half = Msprt.CiHalfWidth(decided, options.Tau, options.Alpha);
        var centre = rate ?? 0.5d;

        var firstAt = slates.Count == 0 ? (DateTime?)null : slates[0].At;
        var lastAt = slates.Count == 0 ? (DateTime?)null : slates[^1].At;

        // Slates per day from the OBSERVED span, not from a hoped-for cadence. A span shorter than a
        // day cannot support a rate: reporting one from a single afternoon would produce a
        // time-to-decision estimate off by an order of magnitude in the flattering direction.
        var spanDays = firstAt is null || lastAt is null ? 0d : (lastAt.Value - firstAt.Value).TotalDays;
        var perDay = spanDays >= 1d && decided > 0 ? decided / spanDays : (double?)null;
        var needed = Msprt.SlatesToDecision(0.65d, options.Tau, options.Alpha);
        var remaining = Math.Max(0, needed - decided);
        var days = perDay is > 0d ? remaining / perDay.Value : (double?)null;

        var standings = slates
            .SelectMany(s => s.ByPolicy.Select(kv => new { s.Group, Policy = kv.Key, kv.Value.Cards, kv.Value.Credits }))
            .GroupBy(x => x.Policy, StringComparer.Ordinal)
            .Select(g => new PolicyStanding(
                g.Key,
                RankingPolicyCatalog.Find(g.Key)?.Description,
                TeamOf(g.Key),
                g.Select(x => x.Group).Distinct().Count(),
                g.Sum(x => x.Cards),
                g.Sum(x => x.Credits),
                SlateWinsFor(g.Key)))
            .OrderByDescending(x => x.SlateWins)
            .ThenBy(x => x.PolicyId, StringComparer.Ordinal)
            .ToList();

        return new InterleaveSummary(
            now,
            options.Enabled,
            options.PolicyA,
            options.PolicyB,
            options.Tau,
            options.Alpha,
            matched.Count,
            decided,
            ties,
            winsA,
            winsB,
            p,
            p <= options.Alpha,
            rate,
            Math.Clamp(centre - half, 0d, 1d),
            Math.Clamp(centre + half, 0d, 1d),
            SignTest.TwoSidedP(decided, winsA),
            firstAt,
            lastAt,
            perDay,
            needed,
            days,
            standings);

        string? TeamOf(string policyId) =>
            string.Equals(policyId, options.PolicyA, StringComparison.Ordinal) ? "A"
            : string.Equals(policyId, options.PolicyB, StringComparison.Ordinal) ? "B"
            : null;

        int SlateWinsFor(string policyId) => slates.Count(s =>
            s.ByPolicy.TryGetValue(policyId, out var mine)
            && s.ByPolicy.Where(kv => !string.Equals(kv.Key, policyId, StringComparison.Ordinal))
                .All(kv => mine.Credits > kv.Value.Credits)
            && s.ByPolicy.Count > 1);
    }

    /// <summary>The reading before a single slate exists — every count zero, nothing implied.</summary>
    private InterleaveSummary Empty(DateTime now) => new(
        now,
        options.Enabled,
        options.PolicyA,
        options.PolicyB,
        options.Tau,
        options.Alpha,
        0, 0, 0, 0, 0,
        1d,
        false,
        null,
        0d,
        1d,
        1d,
        null,
        null,
        null,
        Msprt.SlatesToDecision(0.65d, options.Tau, options.Alpha),
        null,
        []);
}
