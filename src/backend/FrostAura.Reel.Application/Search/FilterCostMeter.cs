using System.Linq.Expressions;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Application.Search;

/// <summary>What one rule costs: the titles it alone is keeping out of the pool.</summary>
/// <param name="Kind">The <c>FilterKind</c> name, so the editor can find the control it belongs to.</param>
/// <param name="Value">The value the user would tap off — the genre, the threshold, the allow-list.</param>
/// <param name="Marginal">
/// Eligible titles with this ONE rule relaxed, minus eligible titles as configured. Marginals do not
/// sum: two rules can be keeping the same title out, so relaxing both adds less than the two numbers
/// together. Reported per rule for exactly that reason — a single "your filters cost you 4,898"
/// would be arithmetic nobody can act on.
/// </param>
public readonly record struct FilterCostRule(string Kind, string Value, int Marginal);

/// <summary>A rule worth offering to relax, because relaxing it actually returns titles.</summary>
public readonly record struct FilterCostSuggestion(string Kind, string Value, int Gain);

/// <summary>
/// The cost of a rule set, as the conditions editor shows it: how many titles survive, and what each
/// rule is individually costing.
/// </summary>
/// <param name="Total">Titles in the catalogue — the denominator in "421 of 5,319".</param>
/// <param name="Eligible">
/// Titles that survive both the rules AND the locked eligibility rule (zero interaction footprint).
/// This is the real supply behind tonight's picks, not a filter-only count: a title already watched
/// is not coming back whatever the user does to a chip.
/// </param>
/// <param name="EligibleUnderShrunkFloor">
/// What <paramref name="Eligible"/> would be if the crowd floor compared the vote-shrunk badge score
/// instead of the raw average (founder decision FD-7, pending) — null when the rule set has no crowd
/// floor. The badge already shows the shrunk score; this is the pool change the switch would make,
/// shown before anyone makes it.
/// </param>
public readonly record struct FilterCostReport(
    int Total,
    int Eligible,
    IReadOnlyList<FilterCostRule> ByRule,
    IReadOnlyList<FilterCostSuggestion> Suggestions,
    int? EligibleUnderShrunkFloor = null);

/// <summary>
/// Costs a draft rule set against the real catalogue, so the conditions editor can say what a chip
/// is worth BEFORE it is saved, and Ask Reel's <c>conditions()</c> tool can name what each standing
/// condition is costing.
///
/// Why it exists: on live data the founder's rules — English only, eight excluded genres, crowd
/// floor 6.0, year ≥ 2000 — leave roughly 421 of 5,319 titles eligible, which is under eight
/// feed-fulls, and retrieval already pulls three times the whole surviving universe. "The picks are
/// getting worse every time I queue" is that pool being consumed, and the editor offered no feedback
/// whatsoever — so the one screen where the trade is made was the one screen that never named it.
///
/// <para><b>One pass (H-6).</b> The meter used to count the pool, then re-count it once per rule with
/// that rule relaxed: 13 sequential scans for the founder's rules, 7–53 s per draft on live. It now
/// reads the catalogue ONCE: every title is projected to the set of rules it breaks (a bitmask, one
/// bit per rule, from <see cref="FilterBreaches"/>) plus whether it carries the account's footprint,
/// and Postgres groups by that shape. Everything the editor shows falls out of the groups: the
/// eligible pool is the footprint-free titles that break nothing, and a rule's marginal is the
/// footprint-free titles that break THAT rule and no other — exactly what relaxing it alone would
/// add. The language rule relaxes to every language and one of several allow-listed genres can only
/// narrow the pool, so both read the same way the subtractive counts did; the parity test pins the
/// two methods equal title for title.</para>
///
/// It never writes, never persists a draft, and never widens what is served — a costed rule set is a
/// question about the catalogue, not permission to show anything.
/// </summary>
public sealed class FilterCostMeter(IReelDbContext db, EligibilityQueryBuilder eligibility, IAccountContext accountContext, IMemoryCache cache)
{
    /// <summary>
    /// How long a report stays warm. The editor re-costs on every debounced keystroke and chip tap,
    /// and a draft is re-visited constantly while a user fiddles — but the catalogue moves under it
    /// on every sync, so this is a burst absorber, not a cache of record.
    /// </summary>
    public static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The most rules a single report costs individually: one bit each across two 63-bit masks. Every
    /// rule beyond the cap still binds the eligible count (they share the last bit) but is reported
    /// with a marginal of 0 rather than dropped, so nothing silently disappears from the editor.
    /// </summary>
    public const int MaxCostedRules = FilterBreaches.MaxOwnBits;

    /// <summary>How many relaxations to offer. Three fits the editor and forces a real ranking.</summary>
    public const int SuggestionCount = 3;

    private const int BitsPerMask = FilterBreaches.BitsPerMask;

    /// <summary>
    /// Costs <paramref name="rules"/> for the account this unit of work is scoped to.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The context is unscoped. The eligible count is per-account by construction (it subtracts that
    /// account's watch history, ratings and reactions), so an unscoped call could only ever return a
    /// number belonging to nobody — and the global query filters would let it through silently.
    /// </exception>
    public Task<FilterCostReport> MeasureAsync(FilterRuleSet rules, CancellationToken ct = default)
    {
        var accountId = accountContext.AccountId
            ?? throw new InvalidOperationException(
                "FilterCostMeter requires an account-scoped context — the eligible count is per-account.");
        return MeasureAsync(accountId, rules, ct);
    }

    /// <summary>
    /// Costs <paramref name="rules"/> for <paramref name="accountId"/> — the entry point for callers
    /// that hold the account explicitly (Ask Reel's <c>conditions()</c> tool). The footprint is read
    /// by account id, so the answer is the same whether or not the context is scoped.
    /// </summary>
    public async Task<FilterCostReport> MeasureAsync(Guid accountId, FilterRuleSet rules, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var key = $"filtercost:{accountId}:{rules.CacheKey()}";
        if (cache.TryGetValue<FilterCostReport>(key, out var cached))
        {
            return cached;
        }

        var all = rules.Rules();
        var groups = await CountShapesAsync(accountId, rules, all, ct);

        var total = groups.Sum(g => g.Count);
        var free = groups.Where(g => !g.Footprint).ToList();
        var eligible = free.Where(g => g.M0 == 0 && g.M1 == 0).Sum(g => g.Count);

        var byRule = new List<FilterCostRule>(all.Count);
        for (var i = 0; i < all.Count; i++)
        {
            var rule = all[i];
            byRule.Add(new FilterCostRule(rule.Kind.ToString(), rule.Value, MarginalOf(rules, all, i, free)));
        }

        var suggestions = byRule
            .Where(r => r.Marginal > 0)
            .OrderByDescending(r => r.Marginal)
            .Take(SuggestionCount)
            .Select(r => new FilterCostSuggestion(r.Kind, r.Value, r.Marginal))
            .ToList();

        // The FD-7 counterfactual: the crowd-floor bit swapped for the shrunk reading. A title is in
        // that pool when it breaks nothing but (perhaps) the raw floor, and clears the shrunk one.
        int? underShrunkFloor = null;
        if (rules.MinCrowdRating is not null)
        {
            var crowdBit = FilterBreaches.BitOf(all, all.ToList().FindIndex(r => r.Kind == FilterKind.MinCrowdRating));
            underShrunkFloor = free
                .Where(g => !g.ShrunkBreach && WithoutBit(g, crowdBit) is (0, 0))
                .Sum(g => g.Count);
        }

        var report = new FilterCostReport(total, eligible, byRule, suggestions, underShrunkFloor);
        cache.Set(key, report, CacheWindow);
        return report;
    }

    /// <summary>
    /// The marginal of rule <paramref name="index"/>: the footprint-free titles that relaxing it ALONE
    /// would add, read off the groups. Clamped at zero by construction — relaxing a rule can only
    /// widen the pool, except an allow-listed genre among several, which narrows it and reports 0.
    /// </summary>
    private static int MarginalOf(FilterRuleSet rules, IReadOnlyList<FilterRule> all, int index, IReadOnlyList<ShapeCount> free)
    {
        if (index >= MaxCostedRules)
        {
            return 0;
        }

        var rule = all[index];
        if (rule.Kind == FilterKind.IncludeGenre && rules.IncludeGenres.Count > 1)
        {
            // Dropping one of several allow-listed genres leaves an allow-list that admits a subset
            // of what it admitted before: the pool can only shrink, so the honest gain is zero.
            return 0;
        }

        var bit = FilterBreaches.BitOf(all, index);
        return free.Where(g => FilterBreaches.Only(g.M0, g.M1, bit)).Sum(g => g.Count);
    }

    /// <summary>
    /// The single statement: every title grouped by (footprint, the rules it breaks, whether it
    /// breaks the shrunk crowd floor). The allow-list rules share one bit per list, so every
    /// IncludeGenre value maps to the first IncludeGenre's bit (and likewise Language, which has one).
    /// </summary>
    private async Task<List<ShapeCount>> CountShapesAsync(
        Guid accountId, FilterRuleSet rules, IReadOnlyList<FilterRule> all, CancellationToken ct)
    {
        var t = Expression.Parameter(typeof(Title), "t");
        var (m0, m1) = FilterBreaches.Masks(rules, all, t);

        var shrunk = FilterBreaches.BodyOn(FilterBreaches.UnderCrowdFloor(rules.MinCrowdRating, CrowdFloorMeasure.Shrunk), t);
        var footprint = FilterBreaches.BodyOn(eligibility.Footprint(accountId), t);

        var projection = Expression.Lambda<Func<Title, Shape>>(
            Expression.MemberInit(
                Expression.New(typeof(Shape)),
                Expression.Bind(typeof(Shape).GetProperty(nameof(Shape.Footprint))!, footprint),
                Expression.Bind(typeof(Shape).GetProperty(nameof(Shape.M0))!, m0),
                Expression.Bind(typeof(Shape).GetProperty(nameof(Shape.M1))!, m1),
                Expression.Bind(typeof(Shape).GetProperty(nameof(Shape.ShrunkBreach))!, shrunk)),
            t);

        return await db.Titles
            .Select(projection)
            .GroupBy(s => new { s.Footprint, s.M0, s.M1, s.ShrunkBreach })
            .Select(g => new ShapeCount(g.Key.Footprint, g.Key.M0, g.Key.M1, g.Key.ShrunkBreach, g.Count()))
            .ToListAsync(ct);
    }

    private static (long, long) WithoutBit(ShapeCount g, int bit) =>
        bit < BitsPerMask
            ? (g.M0 & ~(1L << bit), g.M1)
            : (g.M0, g.M1 & ~(1L << (bit - BitsPerMask)));

    /// <summary>One title's shape, as Postgres computes it.</summary>
    private sealed class Shape
    {
        public bool Footprint { get; init; }

        public long M0 { get; init; }

        public long M1 { get; init; }

        public bool ShrunkBreach { get; init; }
    }

    /// <summary>How many titles share one shape.</summary>
    private sealed record ShapeCount(bool Footprint, long M0, long M1, bool ShrunkBreach, int Count);
}
