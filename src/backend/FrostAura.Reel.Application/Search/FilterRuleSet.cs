using FrostAura.Reel.Domain.Filters;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// One relaxable content rule, addressed the way a user sees it: a kind plus the single value they
/// would tap off. <c>ExcludeGenre/"comedy"</c> is one rule; the eight excluded genres are eight
/// rules, not one. The scalar kinds (<see cref="FilterKind.MinCrowdRating"/>,
/// <see cref="FilterKind.MinYear"/>, <see cref="FilterKind.MaxYear"/>) carry their threshold as the
/// value, and <see cref="FilterKind.Language"/> is reported once for the whole allow-list because
/// there is exactly one control for it.
/// </summary>
public readonly record struct FilterRule(FilterKind Kind, string Value);

/// <summary>
/// A content-preference rule set held in memory rather than read from <c>ContentFilters</c> — the
/// DRAFT the conditions editor is showing before anything is saved.
///
/// It exists for the filter cost meter: the founder's rules cut a 5,319-title catalogue to ~421
/// eligible titles and the editor gave no feedback whatsoever, so "why is my feed getting worse"
/// and "what did that chip cost me" were unanswerable in the one place they are asked. Costing a
/// rule set means counting the pool under rules the account has NOT saved, and then re-counting it
/// with one rule dropped — which the persisted path structurally cannot do, since it reads the
/// rules back out of the database inside the same SQL statement.
///
/// The normalisation here is deliberately identical to what <c>FilterEndpoints</c> writes: trimmed,
/// lower-cased, de-duplicated, and an empty language list meaning the English-only default. A draft
/// costed under different normalisation than the save would store is a meter that lies about the
/// thing it is measuring.
/// </summary>
public sealed record FilterRuleSet
{
    /// <summary>The language sentinel meaning "every language" — the same one the rows use.</summary>
    public const string AllLanguages = "*";

    /// <summary>The language the account falls back to when no language rule is configured.</summary>
    public const string DefaultLanguage = "en";

    private FilterRuleSet(
        IReadOnlyList<string> excludeGenres,
        IReadOnlyList<string> includeGenres,
        IReadOnlyList<string> excludeKeywords,
        IReadOnlyList<string> languages,
        decimal? minCrowdRating,
        int? minYear,
        int? maxYear)
    {
        ExcludeGenres = excludeGenres;
        IncludeGenres = includeGenres;
        ExcludeKeywords = excludeKeywords;
        Languages = languages;
        MinCrowdRating = minCrowdRating;
        MinYear = minYear;
        MaxYear = maxYear;
    }

    public IReadOnlyList<string> ExcludeGenres { get; private init; }

    public IReadOnlyList<string> IncludeGenres { get; private init; }

    public IReadOnlyList<string> ExcludeKeywords { get; private init; }

    /// <summary>
    /// The language allow-list AS STORED: empty means "no rows", which the eligibility rule reads as
    /// English-only. Use <see cref="EffectiveLanguages"/> for the list to actually filter on.
    /// </summary>
    public IReadOnlyList<string> Languages { get; private init; }

    public decimal? MinCrowdRating { get; private init; }

    public int? MinYear { get; private init; }

    public int? MaxYear { get; private init; }

    /// <summary>
    /// The allow-list the predicate uses. Empty ⇒ <see cref="DefaultLanguage"/>, mirroring the
    /// persisted arm's "no rows ⇒ English only" (founder ask 2026-06-30) rather than "no rows ⇒ no
    /// titles". Containing <see cref="AllLanguages"/> means the language rule is off entirely.
    /// </summary>
    public IReadOnlyList<string> EffectiveLanguages =>
        Languages.Count > 0 ? Languages : [DefaultLanguage];

    /// <summary>True when the language rule constrains nothing.</summary>
    public bool AllLanguagesAllowed => EffectiveLanguages.Contains(AllLanguages);

    /// <summary>
    /// Builds a normalised rule set from raw editor input. Values are trimmed, lower-cased and
    /// de-duplicated exactly as a save would store them; the year window is swapped when inverted
    /// and rejected out of range by <see cref="ContentFilterRules"/> before it reaches here, so an
    /// out-of-range bound arrives already handled. A genre on both lists is unsatisfiable, so the
    /// exclusion wins — the same reading the editor applies.
    /// </summary>
    public static FilterRuleSet Create(
        IEnumerable<string>? excludeGenres,
        IEnumerable<string>? includeGenres,
        IEnumerable<string>? excludeKeywords,
        IEnumerable<string>? languages,
        decimal? minCrowdRating,
        int? minYear,
        int? maxYear)
    {
        var excluded = Clean(excludeGenres);
        var included = Clean(includeGenres).Where(g => !excluded.Contains(g)).ToList();
        return new FilterRuleSet(
            excluded,
            included,
            Clean(excludeKeywords),
            Clean(languages),
            ContentFilterRules.NormalizeCrowdFloor(minCrowdRating),
            minYear,
            maxYear);
    }

    /// <summary>
    /// The rule set an account has actually saved, rebuilt from its <c>ContentFilters</c> rows. Used
    /// to cost the persisted conditions when the caller has no draft, and by the parity test that
    /// pins this predicate to the persisted one.
    /// </summary>
    public static FilterRuleSet FromRows(IEnumerable<ContentFilter> rows)
    {
        var list = rows.ToList();
        string[] Values(FilterKind kind) => list.Where(f => f.Kind == kind).Select(f => f.Value).ToArray();
        return Create(
            Values(FilterKind.ExcludeGenre),
            Values(FilterKind.IncludeGenre),
            Values(FilterKind.ExcludeKeyword),
            Values(FilterKind.Language),
            ContentFilterRules.ParseRating(list.FirstOrDefault(f => f.Kind == FilterKind.MinCrowdRating)?.Value),
            ContentFilterRules.ParseYear(list.FirstOrDefault(f => f.Kind == FilterKind.MinYear)?.Value),
            ContentFilterRules.ParseYear(list.FirstOrDefault(f => f.Kind == FilterKind.MaxYear)?.Value));
    }

    /// <summary>
    /// Every rule that is actually ON, in the order the meter reports them. A rule set with nothing
    /// configured yields nothing — there is no cost to attribute, and no relaxation to suggest.
    /// </summary>
    public IReadOnlyList<FilterRule> Rules()
    {
        var rules = new List<FilterRule>();
        foreach (var genre in ExcludeGenres)
        {
            rules.Add(new FilterRule(FilterKind.ExcludeGenre, genre));
        }

        foreach (var genre in IncludeGenres)
        {
            rules.Add(new FilterRule(FilterKind.IncludeGenre, genre));
        }

        // One rule for the whole allow-list: the editor has ONE language control, and reporting
        // "dropping English adds 640" per code would invite a tap that leaves the list empty, which
        // means English-only again rather than the wider pool the number promised.
        if (!AllLanguagesAllowed)
        {
            rules.Add(new FilterRule(FilterKind.Language, string.Join(",", EffectiveLanguages)));
        }

        if (MinCrowdRating is { } crowd)
        {
            rules.Add(new FilterRule(FilterKind.MinCrowdRating, ContentFilterRules.FormatRating(crowd)));
        }

        if (MinYear is { } min)
        {
            rules.Add(new FilterRule(FilterKind.MinYear, ContentFilterRules.FormatYear(min)));
        }

        if (MaxYear is { } max)
        {
            rules.Add(new FilterRule(FilterKind.MaxYear, ContentFilterRules.FormatYear(max)));
        }

        foreach (var keyword in ExcludeKeywords)
        {
            rules.Add(new FilterRule(FilterKind.ExcludeKeyword, keyword));
        }

        return rules;
    }

    /// <summary>
    /// This rule set with one rule relaxed — the counterfactual the marginal count is measured
    /// against. "Relaxed" is the WIDEST honest reading of tapping that control off: dropping the
    /// only language row would silently re-assert the English-only default, so the language rule
    /// relaxes to the all-languages sentinel instead of to an empty list.
    /// </summary>
    public FilterRuleSet Without(FilterRule rule) => rule.Kind switch
    {
        FilterKind.ExcludeGenre => this with { ExcludeGenres = Except(ExcludeGenres, rule.Value) },
        FilterKind.IncludeGenre => this with { IncludeGenres = Except(IncludeGenres, rule.Value) },
        FilterKind.ExcludeKeyword => this with { ExcludeKeywords = Except(ExcludeKeywords, rule.Value) },
        FilterKind.Language => this with { Languages = [AllLanguages] },
        FilterKind.MinCrowdRating => this with { MinCrowdRating = null },
        FilterKind.MinYear => this with { MinYear = null },
        FilterKind.MaxYear => this with { MaxYear = null },
        _ => this,
    };

    /// <summary>
    /// This rule set with one turn's own rejections added ("no fantasy", "no zombies") as further
    /// ExcludeGenre and ExcludeKeyword rules — the conditions one Ask Reel turn is judged by. It can
    /// only ever narrow: values are normalised as a save would store them, and unlike
    /// <see cref="Create"/> the IncludeGenre allow-list is left exactly as it is, because dropping an
    /// allow-listed genre the user just rejected could empty the allow-list, and an empty allow-list
    /// means "every genre" — a widening.
    /// </summary>
    public FilterRuleSet WithExclusions(IEnumerable<string>? genres, IEnumerable<string>? keywords) =>
        this with
        {
            ExcludeGenres = Clean(ExcludeGenres.Concat(genres ?? [])),
            ExcludeKeywords = Clean(ExcludeKeywords.Concat(keywords ?? [])),
        };

    /// <summary>
    /// A stable key for this rule set, for the meter's short cache. Order-insensitive per list, so
    /// two drafts that differ only in the order chips were tapped share one cache entry.
    /// </summary>
    public string CacheKey()
    {
        static string Join(IReadOnlyList<string> values) => string.Join(",", values.Order(StringComparer.Ordinal));
        return string.Join(
            "|",
            Join(ExcludeGenres),
            Join(IncludeGenres),
            Join(ExcludeKeywords),
            Join(EffectiveLanguages),
            MinCrowdRating?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            MinYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            MaxYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-");
    }

    private static List<string> Clean(IEnumerable<string>? values) =>
        (values ?? [])
            .Select(v => v.Trim().ToLowerInvariant())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static List<string> Except(IReadOnlyList<string> values, string value) =>
        values.Where(v => !string.Equals(v, value, StringComparison.Ordinal)).ToList();
}
