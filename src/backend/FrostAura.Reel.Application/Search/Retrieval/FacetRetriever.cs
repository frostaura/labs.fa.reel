using System.Diagnostics;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>
/// The facet arm of Ask Reel retrieval, standing alone as the first <see cref="IAskReelRetriever"/>
/// until the full-text, semantic and TMDB arms join it in the hybrid retriever.
///
/// <para>What it returns is real and grounded: eligible titles (the one builder, plus the maturity
/// ceiling) that satisfy every hard constraint, never one in <see cref="RetrievalQuery.Exclude"/>,
/// most-voted first, each scored by reciprocal-rank fusion over this single arm (k=60) so the scores
/// stay comparable once other arms fuse in. A request with concepts therefore only ever gets titles
/// carrying keyword or genre evidence for every concept.</para>
///
/// <para>What it cannot do yet, by construction rather than by omission: find a title the facets do
/// not tag (that is the full-text arm), use moods or reference titles (the semantic arm), or reach
/// titles not yet in the catalogue (the TMDB arm). Soft preferences are accepted and do not narrow
/// anything; ordering them is the judge's and the ranker's job.</para>
/// </summary>
public sealed class FacetRetriever(IReelDbContext db, EligibilityQueryBuilder eligibility, ISupplyReporter supply)
    : IAskReelRetriever
{
    /// <summary>Reciprocal-rank fusion constant shared by every arm.</summary>
    public const int RrfK = 60;

    /// <summary>The most candidates one call returns, whatever the caller asks for.</summary>
    public const int MaxWant = 200;

    /// <inheritdoc />
    public async Task<RetrievalResult> RetrieveAsync(RetrievalQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(q);
        var timings = new Dictionary<string, long>();

        var clock = Stopwatch.StartNew();
        var report = await supply.ReportAsync(q.AccountId, q.Hard, ct);
        timings["supply"] = clock.ElapsedMilliseconds;

        var want = Math.Clamp(q.Want, 0, MaxWant);
        if (want == 0)
        {
            return new RetrievalResult([], report, new StageTimings(timings));
        }

        clock.Restart();
        var exclude = q.Exclude.Distinct().ToList();
        var ceiling = await eligibility.MaturityCeilingAsync(q.AccountId, ct);
        var pool = HardConstraintFilter.Apply(eligibility.EligibleTitles(q.AccountId), db, q.Hard)
            .Where(t => !exclude.Contains(t.Id));

        // Over-fetched because the maturity ceiling can only be applied in memory; a second, wider
        // read covers a restricted profile whose first page was mostly over the ceiling.
        var titles = new List<Title>(want);
        foreach (var fetch in new[] { (want * 2) + 20, want * 10 })
        {
            var page = await CrowdVotes.MostVotedFirst(pool).Take(fetch).AsNoTracking().ToListAsync(ct);
            titles = EligibilityQueryBuilder.WithinMaturity(page, ceiling).Take(want).ToList();
            if (titles.Count >= want || page.Count < fetch)
            {
                break;
            }
        }

        var candidates = titles
            .Select((t, index) => new Candidate(
                t.Id,
                FusedScore: 1d / (RrfK + index + 1),
                Similarity: null,
                Evidence: HardConstraintFilter.EvidenceFor(t, q.Hard)))
            .ToList();
        timings["facet"] = clock.ElapsedMilliseconds;

        return new RetrievalResult(candidates, report, new StageTimings(timings));
    }
}
