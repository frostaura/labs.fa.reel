using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
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
public readonly record struct FilterCostReport(
    int Total,
    int Eligible,
    IReadOnlyList<FilterCostRule> ByRule,
    IReadOnlyList<FilterCostSuggestion> Suggestions);

/// <summary>
/// Costs a draft rule set against the real catalogue, so the conditions editor can say what a chip
/// is worth BEFORE it is saved.
///
/// Why it exists: on live data the founder's rules — English only, eight excluded genres, crowd
/// floor 6.0, year ≥ 2000 — leave roughly 421 of 5,319 titles eligible, which is under eight
/// feed-fulls, and retrieval already pulls three times the whole surviving universe. "The picks are
/// getting worse every time I queue" is that pool being consumed, and the editor offered no feedback
/// whatsoever — so the one screen where the trade is made was the one screen that never named it.
///
/// The method is subtractive, and it is the same shape <see cref="Ranking.FeedStarvationDiagnostic"/>
/// already uses on the empty-feed path: count the pool, then re-count it with one rule relaxed at a
/// time; the difference is what that rule costs. The diagnostic drops a whole <c>FilterKind</c>
/// because it is naming a culprit; the meter drops one VALUE, because the user taps chips, not kinds.
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
    /// The most rules a single report will cost individually. Each one is its own COUNT, so an
    /// account with two hundred excluded keywords would otherwise turn one debounced keystroke into
    /// two hundred queries. Beyond the cap the rules are reported without marginals rather than
    /// dropped, so nothing silently disappears from the editor.
    /// </summary>
    public const int MaxCostedRules = 30;

    /// <summary>How many relaxations to offer. Three fits the editor and forces a real ranking.</summary>
    public const int SuggestionCount = 3;

    /// <summary>
    /// Costs <paramref name="rules"/> for the account this unit of work is scoped to.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The context is unscoped. The eligible count is per-account by construction (it subtracts that
    /// account's watch history, ratings and reactions), so an unscoped call could only ever return a
    /// number belonging to nobody — and the global query filters would let it through silently.
    /// </exception>
    public async Task<FilterCostReport> MeasureAsync(FilterRuleSet rules, CancellationToken ct = default)
    {
        var accountId = accountContext.AccountId
            ?? throw new InvalidOperationException(
                "FilterCostMeter requires an account-scoped context — the eligible count is per-account.");

        var key = $"filtercost:{accountId}:{rules.CacheKey()}";
        if (cache.TryGetValue<FilterCostReport>(key, out var cached))
        {
            return cached;
        }

        var total = await db.Titles.CountAsync(ct);
        var eligible = await eligibility.EligibleTitles(accountId, rules).CountAsync(ct);

        var byRule = new List<FilterCostRule>();
        var costed = 0;
        foreach (var rule in rules.Rules())
        {
            if (costed >= MaxCostedRules)
            {
                byRule.Add(new FilterCostRule(rule.Kind.ToString(), rule.Value, 0));
                continue;
            }

            costed++;
            var without = await eligibility.EligibleTitles(accountId, rules.Without(rule)).CountAsync(ct);
            // Clamped at zero: relaxing a rule can only ever widen the pool, so a negative here would
            // be a predicate defect, and quoting it to the user would read as "allowing comedy costs
            // you 12 titles" — a sentence that cannot be true.
            byRule.Add(new FilterCostRule(rule.Kind.ToString(), rule.Value, Math.Max(0, without - eligible)));
        }

        var suggestions = byRule
            .Where(r => r.Marginal > 0)
            .OrderByDescending(r => r.Marginal)
            .Take(SuggestionCount)
            .Select(r => new FilterCostSuggestion(r.Kind, r.Value, r.Marginal))
            .ToList();

        var report = new FilterCostReport(total, eligible, byRule, suggestions);
        cache.Set(key, report, CacheWindow);
        return report;
    }
}
