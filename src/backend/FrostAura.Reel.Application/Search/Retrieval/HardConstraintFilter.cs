using System.Linq.Expressions;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>
/// The request's hard constraints as one SQL predicate over <see cref="Title"/> — the topic half of
/// every supply count and every facet retrieval. It narrows; it never decides eligibility, which
/// stays in <see cref="EligibilityQueryBuilder"/> and is composed with this by the caller.
///
/// <para>The rules, each chosen so a count quoted to the user is defensible:</para>
/// <list type="bullet">
/// <item>A concept matches by TMDB keyword CONTAINMENT (<c>%ninja%</c>) or by genre slug. Keywords
/// are stored lower-cased by ingest, so the labels are lower-cased here and the column is not.</item>
/// <item>Concepts AND together; the labels and genres inside one concept OR together.</item>
/// <item>A person matches by credit, by resolved id or else by exact case-insensitive name.</item>
/// <item>The year window is inclusive and resolves <c>Year → ReleasedAt → FirstAiredAt</c>; an
/// undated title passes, exactly as the standing year rule treats it.</item>
/// <item>A runtime ceiling needs a KNOWN runtime: "under 90 minutes" is a statement about the title,
/// and a title with no runtime cannot be shown to satisfy it.</item>
/// </list>
/// </summary>
public static class HardConstraintFilter
{
    /// <summary>The concept labels actually matched for <paramref name="concept"/>: its catalogue labels, or its own words.</summary>
    public static IReadOnlyList<string> LabelsOf(ConceptTerm concept)
    {
        var labels = concept.CatalogueLabels.Count > 0 ? concept.CatalogueLabels : [concept.UserWords];
        return Clean(labels);
    }

    /// <summary>The genre slugs that satisfy <paramref name="concept"/>, cleaned.</summary>
    public static IReadOnlyList<string> GenresOf(ConceptTerm concept) => Clean(concept.Genres);

    /// <summary><paramref name="titles"/> narrowed to those satisfying every hard constraint.</summary>
    public static IQueryable<Title> Apply(IQueryable<Title> titles, IReelDbContext db, HardConstraints hard)
    {
        var query = hard.Media switch
        {
            MediaScope.Movies => titles.Where(t => t.MediaType == MediaType.Movie),
            MediaScope.Shows => titles.Where(t => t.MediaType == MediaType.Show),
            _ => titles,
        };

        foreach (var concept in hard.Concepts)
        {
            query = query.Where(ConceptPredicate(concept));
        }

        foreach (var person in hard.People)
        {
            query = query.Where(PersonPredicate(db, person));
        }

        if (hard.Years is { } years && (years.From is not null || years.To is not null))
        {
            var from = years.From ?? int.MinValue;
            var to = years.To ?? int.MaxValue;
            query = query.Where(t =>
                (t.Year == null && t.ReleasedAt == null && t.FirstAiredAt == null)
                || ((t.Year ?? (t.ReleasedAt != null ? t.ReleasedAt.Value.Year : t.FirstAiredAt!.Value.Year)) >= from
                    && (t.Year ?? (t.ReleasedAt != null ? t.ReleasedAt.Value.Year : t.FirstAiredAt!.Value.Year)) <= to));
        }

        if (hard.MaxRuntimeMinutes is { } ceiling)
        {
            query = query.Where(t => t.RuntimeMinutes != null && t.RuntimeMinutes <= ceiling);
        }

        return query;
    }

    /// <summary>
    /// The evidence strings a title that passed <see cref="Apply"/> carries for the request's concepts
    /// and people (<c>keyword:&lt;title keyword&gt;</c>, <c>genre:&lt;slug&gt;</c>,
    /// <c>person:&lt;name&gt;/&lt;role&gt;</c>), computed in memory from the materialised title so the
    /// strings name the actual tag that matched.
    /// </summary>
    public static IReadOnlyList<string> EvidenceFor(Title title, HardConstraints hard)
    {
        var evidence = new List<string>();
        foreach (var concept in hard.Concepts)
        {
            var labels = LabelsOf(concept);
            foreach (var keyword in title.Keywords)
            {
                if (labels.Any(l => keyword.Contains(l, StringComparison.Ordinal)))
                {
                    evidence.Add($"keyword:{keyword}");
                }
            }

            foreach (var genre in GenresOf(concept))
            {
                if (title.Genres.Contains(genre, StringComparer.Ordinal))
                {
                    evidence.Add($"genre:{genre}");
                }
            }
        }

        // Only titles that passed the person predicate are ever passed in, so each credited person is
        // evidence by construction; the role is the one the request asked about.
        foreach (var person in hard.People)
        {
            evidence.Add($"person:{person.Name.Trim()}/{person.Role.ToString().ToLowerInvariant()}");
        }

        return evidence.Distinct(StringComparer.Ordinal).ToList();
    }

    private static Expression<Func<Title, bool>> ConceptPredicate(ConceptTerm concept)
    {
        var arms = new List<Expression<Func<Title, bool>>>();
        foreach (var label in LabelsOf(concept))
        {
            var value = label;
            arms.Add(t => t.Keywords.Any(k => k.Contains(value)));
        }

        foreach (var genre in GenresOf(concept))
        {
            var value = genre;
            arms.Add(t => t.Genres.Contains(value));
        }

        // A concept with nothing to match on matches nothing: an empty concept is a resolver defect,
        // and reading it as "anything" would answer a topical request with the whole catalogue.
        if (arms.Count == 0)
        {
            return t => false;
        }

        var parameter = Expression.Parameter(typeof(Title), "t");
        var body = arms
            .Select(arm => new ParameterSwap(arm.Parameters[0], parameter).Visit(arm.Body))
            .Aggregate(Expression.OrElse);
        return Expression.Lambda<Func<Title, bool>>(body, parameter);
    }

    private static Expression<Func<Title, bool>> PersonPredicate(IReelDbContext db, PersonConstraint person)
    {
        var anyRole = person.Role == PersonRole.Any;
        var role = person.Role == PersonRole.Director ? CreditRole.Director : CreditRole.Actor;
        if (person.PersonId is { } personId)
        {
            return t => db.TitleCredits.Any(c =>
                c.TitleId == t.Id && c.PersonId == personId && (anyRole || c.Role == role));
        }

        var name = person.Name.Trim().ToLowerInvariant();
        return t => db.TitleCredits.Any(c =>
            c.TitleId == t.Id
            && (anyRole || c.Role == role)
            && db.Persons.Any(p => p.Id == c.PersonId && p.Name.ToLower() == name));
    }

    private static List<string> Clean(IEnumerable<string> values) =>
        values
            .Select(v => v.Trim().ToLowerInvariant())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Rebinds one lambda's parameter onto another so OR-ed arms share a single <c>t</c>.</summary>
    private sealed class ParameterSwap(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
