using System.Linq.Expressions;
using System.Text.RegularExpressions;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>
/// Each standing rule of a <see cref="FilterRuleSet"/> as a SQL predicate that is TRUE when a title
/// BREAKS that one rule — the per-rule half of the single-pass cost meter and of every "dropping this
/// rule would unlock N" count.
///
/// <para><b>Why a second phrasing is safe.</b> Every predicate here is the logical negation of the
/// clause <see cref="EligibilityQueryBuilder.ContentFilteredTitles(FilterRuleSet, CrowdFloorMeasure)"/>
/// applies for the same rule, built from the same pieces (<see cref="GenreStandIns"/>,
/// <see cref="CrowdVotes"/>), so "breaks no rule" selects exactly the draft path's pool. The parity is
/// pinned title for title by <c>FilterCostMeterTests.Single_pass_marginals_equal_the_subtractive_counts</c>
/// on real Postgres; change one side and that test fails.</para>
///
/// <para><b>The allow-lists are list-level.</b> IncludeGenre and Language each have ONE breach
/// predicate for the whole list: a title breaks the IncludeGenre rule when the allow-list is
/// non-empty and it carries none of the listed genres, and the Language rule when its language is not
/// on the effective allow-list. How a list-level breach turns into a per-value marginal is the
/// meter's job (relaxing one of several allow-listed genres can only narrow the pool).</para>
/// </summary>
public static class FilterBreaches
{
    /// <summary>
    /// True when the title breaks <paramref name="rule"/> under <paramref name="rules"/>. For
    /// <see cref="FilterKind.IncludeGenre"/> and <see cref="FilterKind.Language"/> the predicate is the
    /// whole list's, whichever value <paramref name="rule"/> names.
    /// </summary>
    public static Expression<Func<Title, bool>> Breaks(
        FilterRuleSet rules, FilterRule rule, CrowdFloorMeasure measure = CrowdFloorMeasure.Raw)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rule.Kind switch
        {
            FilterKind.ExcludeGenre => AnyOf([ExcludedGenre(rule.Value), Untagged]),
            FilterKind.IncludeGenre => OutsideAllowList(rules.IncludeGenres),
            FilterKind.ExcludeKeyword => CarriesKeyword(rule.Value),
            FilterKind.Language => OutsideLanguages(rules),
            FilterKind.MinCrowdRating => UnderCrowdFloor(rules.MinCrowdRating, measure),
            FilterKind.MinYear => BeforeYear(rules.MinYear),
            FilterKind.MaxYear => AfterYear(rules.MaxYear),
            _ => t => false,
        };
    }

    /// <summary>
    /// A title the catalogue holds no genre for at all. Under ANY ExcludeGenre rule it is unproven, and
    /// it breaks every such rule: nothing shows it clears the exclusion, so it fails closed, as an
    /// unknown language fails "English only" and an unrated title fails a crowd floor. Before this a
    /// 5-minute making-of documentary with no genres was the only "More like this" for John Wick to an
    /// account excluding documentary, and a "wickedly funny comedy" with no genres was eligible to one
    /// excluding comedy (HREV2-RET-04); the global typeahead already hid such rows. It is a breach of
    /// the RULE, never of the genre: <see cref="ExcludedGenre"/> stays the genre's own reading, so a
    /// "romance" concept never matches a title merely because it has no genres.
    /// </summary>
    public static readonly Expression<Func<Title, bool>> Untagged = t => t.Genres.Length == 0;

    /// <summary>
    /// An ExcludeGenre of <paramref name="slug"/>: the genre itself, an alias genre (Trakt's
    /// <c>suspense</c> for thriller), a composite rule (anime is animation in Japanese), or a stand-in
    /// keyword on a medium whose taxonomy cannot tag the genre.
    /// </summary>
    public static Expression<Func<Title, bool>> ExcludedGenre(string slug)
    {
        var value = slug;
        var arms = new List<Expression<Func<Title, bool>>> { t => t.Genres.Contains(value) };
        foreach (var alias in GenreStandIns.AliasesOf(value))
        {
            var aliasValue = alias;
            arms.Add(t => t.Genres.Contains(aliasValue));
        }

        if (GenreStandIns.CompositeOf(value) is { } composite)
        {
            arms.Add(composite);
        }

        if (GenreStandIns.Patterns.ContainsKey(value))
        {
            arms.Add(GenreStandIns.CarriesStandIn(value));
        }

        return AnyOf(arms);
    }

    /// <summary>
    /// An ExcludeKeyword: the value in the name, the overview or any TMDB keyword (containment, as
    /// stored). The name and overview are matched case-insensitively by a LITERAL pattern
    /// (<see cref="LiteralPattern"/>, Postgres <c>~*</c>) rather than <c>lower(…) LIKE</c>: the same
    /// titles, but <c>lower()</c> over every overview was the single most expensive term in the cost
    /// meter's one pass — about 130 ms of a 38k-title scan per excluded keyword against 60 ms for the
    /// pattern (R1-20). The keyword arm stays a case-sensitive containment (the value arrives
    /// lower-cased from <see cref="FilterRuleSet"/>, as TMDB keywords are stored), read over the labels
    /// joined by newlines — one string search per title instead of a subplan unnesting every label,
    /// which halved that arm. A value that itself spans a newline could match across two labels, so
    /// such a value keeps the per-label form.
    /// </summary>
    public static Expression<Func<Title, bool>> CarriesKeyword(string keyword)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        var value = keyword;
        var pattern = LiteralPattern(keyword);
        if (value.Contains('\n', StringComparison.Ordinal))
        {
            return t => Regex.IsMatch(t.Name, pattern, RegexOptions.IgnoreCase)
                || (t.Overview != null && Regex.IsMatch(t.Overview, pattern, RegexOptions.IgnoreCase))
                || t.Keywords.Any(k => k.Contains(value));
        }

        return t => Regex.IsMatch(t.Name, pattern, RegexOptions.IgnoreCase)
            || (t.Overview != null && Regex.IsMatch(t.Overview, pattern, RegexOptions.IgnoreCase))
            || string.Join("\n", t.Keywords).Contains(value);
    }

    /// <summary>
    /// A Postgres regular expression (ARE) that matches <paramref name="value"/> literally: every
    /// character that is not a letter or digit is backslash-escaped, which an ARE always reads as that
    /// character itself ("sci.fi" never matches "sci-fi", "c++" is not a quantifier).
    /// </summary>
    public static string LiteralPattern(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var pattern = new System.Text.StringBuilder(value.Length * 2);
        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c))
            {
                pattern.Append('\\');
            }

            pattern.Append(c);
        }

        return pattern.ToString();
    }

    /// <summary>The IncludeGenre allow-list: non-empty and the title carries none of it.</summary>
    public static Expression<Func<Title, bool>> OutsideAllowList(IReadOnlyList<string> includeGenres)
    {
        if (includeGenres.Count == 0)
        {
            return t => false;
        }

        var allowed = includeGenres.ToList();
        return t => !t.Genres.Any(g => allowed.Contains(g));
    }

    /// <summary>The language allow-list: on, and the title's language is unknown or not on it.</summary>
    public static Expression<Func<Title, bool>> OutsideLanguages(FilterRuleSet rules)
    {
        if (rules.AllLanguagesAllowed)
        {
            return t => false;
        }

        var languages = rules.EffectiveLanguages.ToList();
        return t => !(t.Language != null && languages.Contains(t.Language.ToLower()));
    }

    /// <summary>
    /// The crowd floor: a title with no crowd rating breaks it, as it does on the serving path. Read on
    /// the raw TMDB-then-Trakt average, or on the vote-shrunk score (FD-7's pending reading).
    /// </summary>
    public static Expression<Func<Title, bool>> UnderCrowdFloor(decimal? floor, CrowdFloorMeasure measure)
    {
        if (floor is not { } value)
        {
            return t => false;
        }

        if (measure == CrowdFloorMeasure.Shrunk)
        {
            return Not(CrowdVotes.ShrunkAtLeast(value));
        }

        // Spelled as two-valued tests on purpose (R1-06). The resolved reading is NULL in SQL for a
        // title with no rating, and NOT (NULL >= floor) is NULL, not true: a breach bit built from
        // it stayed clear, so the meter counted every unrated title as clearing the floor. Each
        // conjunct here is false (never NULL) when its rating is missing, so the breach is true.
        return t => !((t.TmdbVoteAverage != null && t.TmdbVoteAverage > 0m && t.TmdbVoteAverage >= value)
            || (!(t.TmdbVoteAverage != null && t.TmdbVoteAverage > 0m)
                && t.TraktRating != null && t.TraktRating > 0m && t.TraktRating >= value));
    }

    /// <summary>The MinYear bound: a dated title released before it. An undated title passes.</summary>
    public static Expression<Func<Title, bool>> BeforeYear(int? minYear)
    {
        if (minYear is not { } min)
        {
            return t => false;
        }

        return t => !(t.Year == null && t.ReleasedAt == null && t.FirstAiredAt == null)
            && (t.Year ?? (t.ReleasedAt != null ? t.ReleasedAt.Value.Year : t.FirstAiredAt!.Value.Year)) < min;
    }

    /// <summary>The MaxYear bound: a dated title released after it. An undated title passes.</summary>
    public static Expression<Func<Title, bool>> AfterYear(int? maxYear)
    {
        if (maxYear is not { } max)
        {
            return t => false;
        }

        return t => !(t.Year == null && t.ReleasedAt == null && t.FirstAiredAt == null)
            && (t.Year ?? (t.ReleasedAt != null ? t.ReleasedAt.Value.Year : t.FirstAiredAt!.Value.Year)) > max;
    }

    /// <summary>Bits per mask column (the sign bit is never used).</summary>
    public const int BitsPerMask = 63;

    /// <summary>The most rules that get a bit of their own across the two mask columns; later rules share the last bit.</summary>
    public const int MaxOwnBits = (2 * BitsPerMask) - 1;

    /// <summary>
    /// The bit rule <paramref name="index"/> of <paramref name="all"/> owns in a breach mask. Every
    /// IncludeGenre value shares the first IncludeGenre's bit (the allow-list breaches as a whole);
    /// rules past <see cref="MaxOwnBits"/> share the last bit, so they still bind without a bit of
    /// their own.
    /// </summary>
    public static int BitOf(IReadOnlyList<FilterRule> all, int index)
    {
        if (all[index].Kind == FilterKind.IncludeGenre)
        {
            for (var i = 0; i < all.Count; i++)
            {
                if (all[i].Kind == FilterKind.IncludeGenre)
                {
                    index = i;
                    break;
                }
            }
        }

        return Math.Min(index, MaxOwnBits);
    }

    /// <summary>
    /// The two bigint mask expressions over <paramref name="parameter"/>: bit <see cref="BitOf"/> of a
    /// rule is set when the title breaks it. Computed once per title in SQL, so every count that
    /// relaxes one rule can be read off the masks instead of re-scanning.
    ///
    /// <para>Two or more keyword exclusions are tested behind one <see cref="AnyKeyword"/> gate: a
    /// title the gate rejects breaks none of them, so its keyword bits are 0 without running each
    /// keyword's own pattern over its overview. The per-keyword patterns then run only for the few
    /// titles the gate admits. Every bit is unchanged, because each keyword's predicate implies the
    /// gate; the saving is one overview scan per keyword after the first (two keywords: 81 → 56 ms
    /// over 38k titles, R1-20).</para>
    /// </summary>
    public static (Expression M0, Expression M1) Masks(
        FilterRuleSet rules, IReadOnlyList<FilterRule> all, ParameterExpression parameter, CrowdFloorMeasure measure = CrowdFloorMeasure.Raw)
    {
        ArgumentNullException.ThrowIfNull(all);
        Expression m0 = Expression.Constant(0L);
        Expression m1 = Expression.Constant(0L);
        Expression? gated0 = null;
        Expression? gated1 = null;
        bool Gateable(FilterRule rule) => rule.Kind == FilterKind.ExcludeKeyword && !rule.Value.Contains('\n', StringComparison.Ordinal);
        var gate = AnyKeyword(all.Where(Gateable).Select(r => r.Value).ToList());
        var includeSeen = false;
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Kind == FilterKind.IncludeGenre)
            {
                if (includeSeen)
                {
                    continue;
                }

                includeSeen = true;
            }

            var bit = BitOf(all, i);
            var breach = BodyOn(Breaks(rules, all[i], measure), parameter);
            var term = Expression.Condition(breach, Expression.Constant(1L << (bit % BitsPerMask)), Expression.Constant(0L));
            if (gate is not null && Gateable(all[i]))
            {
                if (bit < BitsPerMask)
                {
                    gated0 = gated0 is null ? term : Expression.Or(gated0, term);
                }
                else
                {
                    gated1 = gated1 is null ? term : Expression.Or(gated1, term);
                }
            }
            else if (bit < BitsPerMask)
            {
                m0 = Expression.Or(m0, term);
            }
            else
            {
                m1 = Expression.Or(m1, term);
            }
        }

        // Each mask carries the gate only when it holds a gated bit, so the gate is never evaluated
        // for a mask it cannot change.
        if (gate is not null && gated0 is not null)
        {
            m0 = Expression.Or(m0, Expression.Condition(BodyOn(gate, parameter), gated0, Expression.Constant(0L)));
        }

        if (gate is not null && gated1 is not null)
        {
            m1 = Expression.Or(m1, Expression.Condition(BodyOn(gate, parameter), gated1, Expression.Constant(0L)));
        }

        return (m0, m1);
    }

    /// <summary>
    /// TRUE when a title MAY carry any of <paramref name="keywords"/>, FALSE when it carries none: the
    /// name and overview tested once against one alternation of every keyword's
    /// <see cref="LiteralPattern"/>, plus each keyword's own label containment. Each keyword's
    /// <see cref="CarriesKeyword"/> implies it, which is what makes it a safe gate. Null for fewer than
    /// two keywords, where a gate would only add a scan. Values spanning a newline are the caller's to
    /// leave out: their label arm is per-label, not the joined containment repeated here.
    /// </summary>
    public static Expression<Func<Title, bool>>? AnyKeyword(IReadOnlyList<string> keywords)
    {
        ArgumentNullException.ThrowIfNull(keywords);
        var values = keywords.Distinct(StringComparer.Ordinal).ToList();
        if (values.Count < 2)
        {
            return null;
        }

        var pattern = string.Join("|", values.Select(LiteralPattern));
        Expression<Func<Title, bool>> text = t => Regex.IsMatch(t.Name, pattern, RegexOptions.IgnoreCase)
            || (t.Overview != null && Regex.IsMatch(t.Overview, pattern, RegexOptions.IgnoreCase));
        var parameter = text.Parameters[0];
        var body = text.Body;
        foreach (var keyword in values)
        {
            var value = keyword;
            Expression<Func<Title, bool>> label = t => string.Join("\n", t.Keywords).Contains(value);
            body = Expression.OrElse(body, BodyOn(label, parameter));
        }

        return Expression.Lambda<Func<Title, bool>>(body, parameter);
    }

    /// <summary>True when bit <paramref name="bit"/> is set in the masks.</summary>
    public static bool Has(long m0, long m1, int bit) =>
        bit < BitsPerMask ? (m0 & (1L << bit)) != 0 : (m1 & (1L << (bit - BitsPerMask))) != 0;

    /// <summary>True when bit <paramref name="bit"/> is the ONLY bit set — the title breaks that rule and no other.</summary>
    public static bool Only(long m0, long m1, int bit) =>
        bit < BitsPerMask
            ? m0 == (1L << bit) && m1 == 0
            : m0 == 0 && m1 == (1L << (bit - BitsPerMask));

    /// <summary>The OR of <paramref name="arms"/> over one shared parameter; false for none.</summary>
    public static Expression<Func<Title, bool>> AnyOf(IReadOnlyList<Expression<Func<Title, bool>>> arms)
    {
        if (arms.Count == 0)
        {
            return t => false;
        }

        if (arms.Count == 1)
        {
            return arms[0];
        }

        var parameter = Expression.Parameter(typeof(Title), "t");
        var body = arms
            .Select(arm => new Rebind(arm.Parameters[0], parameter).Visit(arm.Body))
            .Aggregate(Expression.OrElse);
        return Expression.Lambda<Func<Title, bool>>(body, parameter);
    }

    /// <summary>The negation of <paramref name="predicate"/>.</summary>
    public static Expression<Func<Title, bool>> Not(Expression<Func<Title, bool>> predicate) =>
        Expression.Lambda<Func<Title, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    /// <summary><paramref name="predicate"/>'s body with its parameter replaced by <paramref name="parameter"/>.</summary>
    public static Expression BodyOn(Expression<Func<Title, bool>> predicate, ParameterExpression parameter) =>
        new Rebind(predicate.Parameters[0], parameter).Visit(predicate.Body);

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
