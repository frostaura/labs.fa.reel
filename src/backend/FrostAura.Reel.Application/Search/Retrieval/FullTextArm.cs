using System.Text;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>One full-text hit: the title, its rank, and which fields matched.</summary>
public sealed class FullTextHit
{
    public Guid TitleId { get; init; }

    public double Rank { get; init; }

    public bool InName { get; init; }

    public bool InKeywords { get; init; }

    public bool InThemes { get; init; }

    public bool InOverview { get; init; }

    /// <summary>The evidence strings this hit carries (<c>fts:name</c>, <c>fts:keywords</c>, <c>fts:themes</c>, <c>fts:overview</c>).</summary>
    public IReadOnlyList<string> Evidence()
    {
        var evidence = new List<string>(4);
        if (InName)
        {
            evidence.Add("fts:name");
        }

        if (InKeywords)
        {
            evidence.Add("fts:keywords");
        }

        if (InThemes)
        {
            evidence.Add("fts:themes");
        }

        if (InOverview)
        {
            evidence.Add("fts:overview");
        }

        return evidence;
    }
}

/// <summary>
/// The full-text arm (program §4.7 arm 2, C-5, M-10): Postgres full text over the generated
/// <c>Titles.SearchDocument</c> (name and keywords weighted A, overview C) and
/// <c>TitleAttributes.ThemesDocument</c> (extracted themes, B), both GIN-indexed (migration M2). It
/// finds what the facets cannot: a heist film whose TMDB tags never say "heist" but whose overview
/// does, a "time loop" the extractor named as a theme.
///
/// <para><b>The query.</b> Each concept is the OR of its stems as phrases (<c>phraseto_tsquery</c>,
/// so "survival at sea" keeps its word order and "ninjas" stems to "ninja"); concepts AND together,
/// exactly as the facet predicate composes them. Stems ride in as parameters; nothing the user typed
/// is spliced into the SQL.</para>
///
/// <para><b>Composed, not standalone.</b> The raw statement only finds and ranks; the caller's pool
/// (eligibility, the hard constraints other than concepts, the exclusions) is composed onto it in
/// LINQ, so this arm can never return a title the account may not see. The generated columns are
/// deliberately unmapped (no entity or write path changes), which is why this is raw SQL.</para>
/// </summary>
public sealed class FullTextArm(IReelDbContext db)
{
    /// <summary>The text-search configuration the generated columns were built with.</summary>
    public const string Configuration = "english";

    /// <summary>
    /// The pool's titles matching every concept, best-ranked first, at most <paramref name="take"/>.
    /// Empty when there are no concepts or no stems.
    /// </summary>
    public async Task<IReadOnlyList<FullTextHit>> SearchAsync(
        IQueryable<Title> pool, IReadOnlyList<ConceptTerm> concepts, int take, CancellationToken ct)
    {
        if (BuildQuery(concepts) is not { } built || take <= 0)
        {
            return [];
        }

        // Hits first, then eligibility by id. Composing the pool INTO the ranked query made Postgres
        // evaluate the whole eligibility predicate (~230 ms for the founder) before ranking ~150
        // hits; checking the ranked hits by primary key costs a few milliseconds.
        var hits = await db.Database
            .SqlQueryRaw<FullTextHit>(built.Sql, built.Parameters)
            .OrderByDescending(h => h.Rank)
            .ThenBy(h => h.TitleId)
            .Take(ReadDepth)
            .ToListAsync(ct);
        if (hits.Count == 0)
        {
            return [];
        }

        var hitIds = hits.Select(h => h.TitleId).ToList();
        var allowed = (await pool.Where(t => hitIds.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        return [.. hits.Where(h => allowed.Contains(h.TitleId)).Take(take)];
    }

    /// <summary>
    /// How many ranked hits are read before eligibility: generous, because a heavy watcher has seen
    /// most of the famous matches and they rank highest.
    /// </summary>
    public const int ReadDepth = 600;

    /// <summary>
    /// The raw statement and its parameters for <paramref name="concepts"/>, or null when there is
    /// nothing to search. Internal so a unit test can pin the shape without a database.
    /// </summary>
    internal static (string Sql, object[] Parameters)? BuildQuery(IReadOnlyList<ConceptTerm> concepts)
    {
        var parameters = new List<object>();
        var conceptClauses = new List<string>();
        foreach (var concept in concepts)
        {
            var stems = HardConstraintFilter.LabelsOf(concept);
            if (stems.Count == 0)
            {
                continue;
            }

            var phrases = new List<string>(stems.Count);
            foreach (var stem in stems)
            {
                phrases.Add($"phraseto_tsquery('{Configuration}', {{{parameters.Count}}})");
                parameters.Add(stem);
            }

            conceptClauses.Add("(" + string.Join(" || ", phrases) + ")");
        }

        if (conceptClauses.Count == 0)
        {
            return null;
        }

        var tsquery = string.Join(" && ", conceptClauses);
        var sql = new StringBuilder()
            .Append("SELECT t.\"Id\" AS \"TitleId\", ")
            .Append("ts_rank(t.\"SearchDocument\" || coalesce(a.\"ThemesDocument\", ''::tsvector), q.q)::double precision AS \"Rank\", ")
            .Append($"(to_tsvector('{Configuration}', coalesce(t.\"Name\", '')) @@ q.q) AS \"InName\", ")
            .Append($"(to_tsvector('{Configuration}', reel_labels_text(t.\"Keywords\")) @@ q.q) AS \"InKeywords\", ")
            .Append("(coalesce(a.\"ThemesDocument\", ''::tsvector) @@ q.q) AS \"InThemes\", ")
            .Append($"(to_tsvector('{Configuration}', coalesce(t.\"Overview\", '')) @@ q.q) AS \"InOverview\" ")
            .Append($"FROM (SELECT ({tsquery}) AS q) AS q ")
            .Append("JOIN LATERAL (")
            .Append("SELECT t2.\"Id\" FROM \"Titles\" AS t2 WHERE t2.\"SearchDocument\" @@ q.q ")
            .Append("UNION SELECT a2.\"TitleId\" FROM \"TitleAttributes\" AS a2 WHERE a2.\"ThemesDocument\" @@ q.q")
            .Append(") AS h ON TRUE ")
            .Append("JOIN \"Titles\" AS t ON t.\"Id\" = h.\"Id\" ")
            .Append("LEFT JOIN \"TitleAttributes\" AS a ON a.\"TitleId\" = h.\"Id\"")
            .ToString();
        return (sql, parameters.ToArray());
    }
}
