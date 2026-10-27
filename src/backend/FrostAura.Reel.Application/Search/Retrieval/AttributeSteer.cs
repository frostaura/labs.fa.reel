using System.Linq.Expressions;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>
/// Attribute steers ("funnier", "less dark", a mood's implied lean) over the extractor's 0–1 scalars
/// on <see cref="TitleAttributes"/>. A steer ORDERS; it never excludes a title from the request — the
/// steer list only says which titles lean the asked way, and fusion does the rest (program §4.3:
/// <c>AttributeDirection</c> "can reorder, never exclude").
///
/// <para><b>Relative to what.</b> With reference titles ("like John Wick but funnier", "less dark than
/// T2.1") the pivot is the references' centroid on that attribute (§4.6: critiques shift relative to
/// the critiqued titles); without, it is the scale's midpoint, 0.5. A title leans the asked way when
/// every explicit steer is on its side of the pivot.</para>
/// </summary>
public static class AttributeSteer
{
    /// <summary>The scale's midpoint, the pivot when there are no reference titles.</summary>
    public const decimal Midpoint = 0.5m;

    /// <summary>The attribute names the planner may send (its closed set), each with its column.</summary>
    private static readonly IReadOnlyDictionary<string, Expression<Func<TitleAttributes, decimal>>> Columns =
        new Dictionary<string, Expression<Func<TitleAttributes, decimal>>>(StringComparer.Ordinal)
        {
            ["darkness"] = a => a.Darkness,
            ["pacing"] = a => a.Pacing,
            ["complexity"] = a => a.Complexity,
            ["intensity"] = a => a.EmotionalIntensity,
            ["humor"] = a => a.Humor,
            ["optimism"] = a => a.Optimism,
        };

    private static readonly IReadOnlyDictionary<string, Func<TitleAttributes, decimal>> Readers =
        Columns.ToDictionary(kv => kv.Key, kv => kv.Value.Compile(), StringComparer.Ordinal);

    /// <summary>
    /// The attribute's canonical name ("humour" and "funny" read as "humor", "emotional intensity" as
    /// "intensity"), or null for an attribute the extractor does not measure — which is then ignored,
    /// never guessed at.
    /// </summary>
    public static string? Canonical(string? attribute)
    {
        var name = attribute?.Trim().ToLowerInvariant().Replace('_', ' ');
        return name switch
        {
            "humour" or "funny" or "funnier" or "humor" => "humor",
            "dark" or "darker" or "darkness" => "darkness",
            "pace" or "pacing" or "speed" => "pacing",
            "complexity" or "complex" => "complexity",
            "intensity" or "emotional intensity" or "emotionalintensity" or "intense" => "intensity",
            "optimism" or "optimistic" or "hope" or "hopeful" => "optimism",
            _ => null,
        };
    }

    /// <summary>
    /// The steers to apply: the request's explicit ones first (canonical, unknown attributes dropped),
    /// then implied ones (a mood's lean) only for attributes the request did not steer itself.
    /// </summary>
    public static IReadOnlyList<(string Attribute, AttributeTrend Trend, bool Explicit)> Merge(
        IReadOnlyList<AttributeDirection> explicitSteers, IReadOnlyList<AttributeDirection> implied)
    {
        var merged = new List<(string Attribute, AttributeTrend Trend, bool Explicit)>();
        foreach (var (steers, isExplicit) in new[] { (explicitSteers, true), (implied, false) })
        {
            foreach (var steer in steers)
            {
                if (Canonical(steer.Attribute) is { } name && merged.All(m => m.Attribute != name))
                {
                    merged.Add((name, steer.Trend, isExplicit));
                }
            }
        }

        return merged;
    }

    /// <summary>One attribute's value on a materialised row.</summary>
    public static decimal Value(TitleAttributes attributes, string attribute) => Readers[attribute](attributes);

    /// <summary>
    /// How far <paramref name="attributes"/> leans the asked way: Σ over the steers of
    /// (value − pivot), sign-flipped for "less". Positive leans the asked way.
    /// </summary>
    public static decimal Fit(
        TitleAttributes attributes,
        IReadOnlyList<(string Attribute, AttributeTrend Trend, bool Explicit)> steers,
        IReadOnlyDictionary<string, decimal> pivots) =>
        steers.Sum(s => Sign(s.Trend) * (Value(attributes, s.Attribute) - PivotOf(pivots, s.Attribute)));

    /// <summary>True when every EXPLICIT steer is on its side of its pivot (implied ones only order).</summary>
    public static bool Leans(
        TitleAttributes attributes,
        IReadOnlyList<(string Attribute, AttributeTrend Trend, bool Explicit)> steers,
        IReadOnlyDictionary<string, decimal> pivots) =>
        steers.Where(s => s.Explicit).All(s => Sign(s.Trend) * (Value(attributes, s.Attribute) - PivotOf(pivots, s.Attribute)) > 0m);

    /// <summary><see cref="Fit"/> as SQL, for ordering a whole pool's attributes.</summary>
    public static Expression<Func<TitleAttributes, decimal>> FitExpression(
        IReadOnlyList<(string Attribute, AttributeTrend Trend, bool Explicit)> steers,
        IReadOnlyDictionary<string, decimal> pivots)
    {
        var parameter = Expression.Parameter(typeof(TitleAttributes), "a");
        Expression body = Expression.Constant(0m);
        foreach (var steer in steers)
        {
            var column = new Rebind(Columns[steer.Attribute].Parameters[0], parameter).Visit(Columns[steer.Attribute].Body);
            var lean = Expression.Subtract(column, Expression.Constant(PivotOf(pivots, steer.Attribute)));
            body = Expression.Add(body, steer.Trend == AttributeTrend.More ? lean : Expression.Negate(lean));
        }

        return Expression.Lambda<Func<TitleAttributes, decimal>>(body, parameter);
    }

    /// <summary><see cref="Leans"/> as SQL.</summary>
    public static Expression<Func<TitleAttributes, bool>> LeansExpression(
        IReadOnlyList<(string Attribute, AttributeTrend Trend, bool Explicit)> steers,
        IReadOnlyDictionary<string, decimal> pivots)
    {
        var parameter = Expression.Parameter(typeof(TitleAttributes), "a");
        Expression body = Expression.Constant(true);
        foreach (var steer in steers.Where(s => s.Explicit))
        {
            var column = new Rebind(Columns[steer.Attribute].Parameters[0], parameter).Visit(Columns[steer.Attribute].Body);
            var pivot = Expression.Constant(PivotOf(pivots, steer.Attribute));
            body = Expression.AndAlso(
                body,
                steer.Trend == AttributeTrend.More ? Expression.GreaterThan(column, pivot) : Expression.LessThan(column, pivot));
        }

        return Expression.Lambda<Func<TitleAttributes, bool>>(body, parameter);
    }

    /// <summary>The evidence string a steer leaves on a title that leans its way: <c>steer:humor+</c>, <c>steer:darkness-</c>.</summary>
    public static string Evidence(string attribute, AttributeTrend trend) =>
        $"steer:{attribute}{(trend == AttributeTrend.More ? "+" : "-")}";

    private static decimal Sign(AttributeTrend trend) => trend == AttributeTrend.More ? 1m : -1m;

    private static decimal PivotOf(IReadOnlyDictionary<string, decimal> pivots, string attribute) =>
        pivots.TryGetValue(attribute, out var pivot) ? pivot : Midpoint;

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
