using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.CrossReference;

/// <summary>
/// The k nearest titles the viewer has ACTUALLY RATED, per candidate — the evidence the whole
/// cross-reference design rests on, and the one thing in the prompt the model cannot confabulate.
///
/// <para><b>One LATERAL query per batch, not per candidate.</b> Sixty round-trips per batch would
/// dominate a 20–35 second provider call; one query with a <c>CROSS JOIN LATERAL</c> lets Postgres
/// run sixty nearest-neighbour scans inside one plan. Raw SQL through
/// <c>db.Database.SqlQuery&lt;T&gt;($"…")</c> — the interpolated form, so every value is a real
/// parameter and none of this is string concatenation — the same shape
/// <c>JobRunnerService.ClaimNextJobAsync</c> uses.</para>
///
/// <para><b>The AccountId predicate is written out, deliberately.</b> Raw SQL bypasses EF's global
/// query filters entirely, so the tenancy guarantee that holds everywhere else in this codebase by
/// construction holds HERE only because the predicate is in the text. Never remove it, and never
/// let a caller pass an account id it did not get from the session or the job.</para>
///
/// <para><b>The rated set is MATERIALIZED, and that is what forces the scan to be exact.</b> The
/// inner set is the account's own rated titles — ~6% of the catalogue — and an approximate index
/// over the whole catalogue cannot answer a nearest-neighbour question restricted to a small
/// filtered subset. Left to itself Postgres will happily use the HNSW index and apply the rating
/// join as a FILTER, which is faster and silently wrong at the margin: pgvector's index scan yields
/// a bounded number of tuples and then ends, so a candidate whose nearest rated titles all fall
/// outside that window comes back with fewer neighbours than asked for — or none — and nothing
/// anywhere reports it. Materialising the rated rows puts them out of the index's reach and makes
/// the LIMIT mean what it says.</para>
///
/// <para><b>Measured on the founder's dev database, 2026-09-10</b> (2,040 rated-and-embedded
/// titles, 35,704 titles, 5,208 embedded), one 60-candidate batch at k=3: <b>665 ms exact</b>
/// against 76 ms for the index-scan plan this replaces. 665 ms sits against a 20–35 second provider
/// call, which is why the plan's in-process <c>System.Numerics.Vector&lt;float&gt;</c> fallback —
/// gated on "&gt; 2 s" — is deliberately NOT implemented: it would be a second copy of this maths
/// carrying a third of a second of benefit. Re-measure before assuming it stays true at ten times
/// the rated history; the cost is linear in the rated set.</para>
///
/// <para><b><see cref="Options.AsOf"/> is the leakage axis.</b> When it is set, only ratings whose
/// immutable <c>FirstRatedAt</c> is at or before the split may be neighbours — otherwise a backtest
/// hands the judge the very ratings it is about to be scored against. NULL when serving, where
/// every rating the account has made is fair evidence.</para>
/// </summary>
public sealed class NeighbourFinder(IReelDbContext db)
{
    /// <summary>Neighbours shown per candidate in round 1. Enough to triangulate, few enough to read.</summary>
    public const int DefaultK = 3;

    /// <summary>
    /// Round 2's rank offset. The second pass shows ranks 4–6 rather than 1–3 so that a "second
    /// keep" is a second look at different evidence and not the same prompt asked twice.
    /// </summary>
    public const int SecondRoundSkip = 3;

    /// <summary>A neighbour must be rated at or below this to count as a DISLIKED neighbour.</summary>
    public const short DislikedCeiling = 4;

    /// <summary>
    /// And it must be at least this similar. Below 0.60 "the nearest thing you disliked" is a
    /// coincidence rather than a comparison, and the prompt would be handed a reason to say no that
    /// has nothing to do with the candidate.
    /// </summary>
    public const double DislikedMinimumSimilarity = 0.60d;

    /// <summary>How a batch's neighbours are chosen.</summary>
    /// <param name="K">How many neighbours to return per candidate, after <paramref name="Skip"/>.</param>
    /// <param name="Skip">Ranks to discard from the front — 0 in round 1, 3 in round 2.</param>
    /// <param name="AsOf">
    /// Backtest split point. Only ratings with <c>FirstRatedAt &lt;= AsOf</c> may be neighbours.
    /// NULL when serving.
    /// </param>
    /// <param name="IncludeNearestDisliked">
    /// Whether to append low-rated neighbours after the ranked ones — round 2's extra evidence, and
    /// prompt v2's in round 1. Absent where nothing qualifies, never substituted.
    /// </param>
    /// <param name="DislikedTake">
    /// How many low-rated neighbours to append. 1 in round 2; 2 under prompt v2, whose whole
    /// premise is that the judge could not see a disliked neighbour often enough to use one.
    /// </param>
    /// <param name="DislikedRatingCeiling">
    /// The rating at or below which a neighbour counts as low-rated. <see cref="DislikedCeiling"/>
    /// (4) for round 2, which wants an unambiguous dislike; prompt v2 widens it to 5 so that an
    /// account whose lowest scores cluster at 5 still shows the judge its negative evidence rather
    /// than showing it nothing.
    /// </param>
    /// <param name="DislikedSimilarityFloor">
    /// And how near it has to be to mean anything. Round 2 uses 0.60; prompt v2 uses 0.55, the
    /// same trade in the same direction — a slightly weaker comparison beats no comparison, and
    /// the similarity is printed beside it so the judge can discount it itself.
    /// </param>
    public sealed record Options(
        int K = DefaultK,
        int Skip = 0,
        DateTime? AsOf = null,
        bool IncludeNearestDisliked = false,
        int DislikedTake = 1,
        short DislikedRatingCeiling = DislikedCeiling,
        double DislikedSimilarityFloor = DislikedMinimumSimilarity);

    /// <summary>Prompt v2 shows two low-rated neighbours, not one.</summary>
    public const int SecondVersionDislikedTake = 2;

    /// <summary>
    /// And counts a 5 as low. Prompt v2's whole premise is that a negative comparison has to be
    /// VISIBLE whenever one exists — on an account whose lowest scores cluster at 5, a ceiling of 4
    /// shows the judge nothing at all, and nothing is what the 2026-09-10 run measured.
    /// </summary>
    public const short SecondVersionDislikedCeiling = 5;

    /// <summary>
    /// At a slightly looser similarity floor than round 2's, for the same reason: a weaker
    /// comparison the judge can discount — the similarity is printed beside it — beats no
    /// comparison at all.
    /// </summary>
    public const double SecondVersionDislikedSimilarity = 0.55d;

    /// <summary>
    /// The evidence a prompt generation is shown, in ONE place. The mapping from "which prompt" to
    /// "which neighbours" is part of what a stored verdict means, so the backtest and the serving
    /// job have to read it from the same line or a measured v2 result would not describe the v2
    /// that later ships.
    ///
    /// <para>v1: the three nearest rated titles, whatever they are rated. v2: the same three plus up
    /// to two nearest rated ≤ 5 at similarity ≥ 0.55 — five neighbours, at least two of which are
    /// negative evidence where the account has any.</para>
    /// </summary>
    public static Options ForPromptVersion(int promptVersion, DateTime? asOf) =>
        promptVersion >= Domain.CrossReference.CrossReferencePromptVersions.Second
        ? new Options(
            DefaultK,
            Skip: 0,
            AsOf: asOf,
            IncludeNearestDisliked: true,
            DislikedTake: SecondVersionDislikedTake,
            DislikedRatingCeiling: SecondVersionDislikedCeiling,
            DislikedSimilarityFloor: SecondVersionDislikedSimilarity)
        : new Options(DefaultK, Skip: 0, AsOf: asOf, IncludeNearestDisliked: false);

    /// <summary>The raw projection the query returns. Internal shape; callers get the grouped map.</summary>
    private sealed record NeighbourRow(
        Guid CandidateId, Guid TitleId, string Name, int? Year, short Rating, double Similarity);

    /// <summary>
    /// Neighbours per candidate, in descending similarity. Candidates with no embedding — or with
    /// no rated title anywhere near them — are simply absent from the map, which the prompt builder
    /// reads as "no neighbour clause" rather than as an error.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CrossReferenceNeighbour>>> FindAsync(
        Guid accountId, IReadOnlyList<Guid> candidateTitleIds, Options options, CancellationToken ct)
    {
        if (candidateTitleIds.Count == 0 || options.K <= 0)
        {
            return new Dictionary<Guid, IReadOnlyList<CrossReferenceNeighbour>>();
        }

        var ids = candidateTitleIds.Distinct().ToArray();
        var take = options.K + options.Skip;
        var asOf = options.AsOf;

        // `{asOf}::timestamptz IS NULL OR …` rather than two query texts: one plan, one place for
        // the predicate, and the serving path and the backtest cannot drift apart.
        var rows = await db.Database.SqlQuery<NeighbourRow>(
            $"""
             WITH rated AS MATERIALIZED (
                 SELECT r."TitleId" AS "TitleId",
                        r."Rating"  AS "Rating",
                        t."Name"    AS "Name",
                        t."Year"    AS "Year",
                        te."Embedding" AS "Embedding"
                 FROM "UserRatings" r
                 JOIN "TitleEmbeddings" te ON te."TitleId" = r."TitleId"
                 JOIN "Titles" t ON t."Id" = r."TitleId"
                 WHERE r."AccountId" = {accountId}
                   AND r."SubjectType" IN ('Movie', 'Show')
                   AND ({asOf}::timestamptz IS NULL OR r."FirstRatedAt" <= {asOf}::timestamptz)
             )
             SELECT c."CandidateId" AS "CandidateId",
                    n."TitleId"     AS "TitleId",
                    n."Name"        AS "Name",
                    n."Year"        AS "Year",
                    n."Rating"      AS "Rating",
                    n."Similarity"  AS "Similarity"
             FROM unnest({ids}) AS c("CandidateId")
             JOIN "TitleEmbeddings" ce ON ce."TitleId" = c."CandidateId"
             CROSS JOIN LATERAL (
                 SELECT rt."TitleId" AS "TitleId",
                        rt."Name"    AS "Name",
                        rt."Year"    AS "Year",
                        rt."Rating"  AS "Rating",
                        (1 - (rt."Embedding" <=> ce."Embedding"))::double precision AS "Similarity"
                 FROM rated rt
                 WHERE rt."TitleId" <> c."CandidateId"
                 ORDER BY rt."Embedding" <=> ce."Embedding"
                 LIMIT {take}
             ) n
             """).ToListAsync(ct);

        var ranked = rows
            .GroupBy(r => r.CandidateId)
            .ToDictionary(
                g => g.Key,
                IReadOnlyList<CrossReferenceNeighbour> (g) => g
                    .OrderByDescending(r => r.Similarity)
                    // Lowest id last, so a similarity tie resolves the same way on every run and a
                    // golden prompt test is not at the mercy of Postgres row order.
                    .ThenBy(r => r.TitleId)
                    // Skip/Take in memory rather than an OFFSET in the LATERAL: the inner LIMIT
                    // already bounds the scan, and an OFFSET would make the query text depend on
                    // the round for no plan benefit.
                    .Skip(options.Skip)
                    .Take(options.K)
                    .Select(r => new CrossReferenceNeighbour(r.Name, r.Year, r.Rating, r.Similarity, r.TitleId))
                    .ToList());

        if (!options.IncludeNearestDisliked)
        {
            return ranked;
        }

        if (options.DislikedTake <= 0)
        {
            return ranked;
        }

        // The extra clause, and it needs its OWN scan. Filtering the ranked rows would only ever
        // find a low-rated neighbour that already made the top-k — which is precisely the case
        // where the prompt least needs the extra evidence. This asks the question directly: of the
        // titles this viewer rated at or below the ceiling, which are nearest, and are they near
        // enough to mean anything.
        var dislikedTake = options.DislikedTake;
        var dislikedCeiling = options.DislikedRatingCeiling;
        var similarityFloor = options.DislikedSimilarityFloor;
        var dislikedRows = await db.Database.SqlQuery<NeighbourRow>(
            $"""
             WITH rated AS MATERIALIZED (
                 SELECT r."TitleId" AS "TitleId",
                        r."Rating"  AS "Rating",
                        t."Name"    AS "Name",
                        t."Year"    AS "Year",
                        te."Embedding" AS "Embedding"
                 FROM "UserRatings" r
                 JOIN "TitleEmbeddings" te ON te."TitleId" = r."TitleId"
                 JOIN "Titles" t ON t."Id" = r."TitleId"
                 WHERE r."AccountId" = {accountId}
                   AND r."SubjectType" IN ('Movie', 'Show')
                   AND r."Rating" <= {dislikedCeiling}
                   AND ({asOf}::timestamptz IS NULL OR r."FirstRatedAt" <= {asOf}::timestamptz)
             )
             SELECT c."CandidateId" AS "CandidateId",
                    n."TitleId"     AS "TitleId",
                    n."Name"        AS "Name",
                    n."Year"        AS "Year",
                    n."Rating"      AS "Rating",
                    n."Similarity"  AS "Similarity"
             FROM unnest({ids}) AS c("CandidateId")
             JOIN "TitleEmbeddings" ce ON ce."TitleId" = c."CandidateId"
             CROSS JOIN LATERAL (
                 SELECT rt."TitleId" AS "TitleId",
                        rt."Name"    AS "Name",
                        rt."Year"    AS "Year",
                        rt."Rating"  AS "Rating",
                        (1 - (rt."Embedding" <=> ce."Embedding"))::double precision AS "Similarity"
                 FROM rated rt
                 WHERE rt."TitleId" <> c."CandidateId"
                 ORDER BY rt."Embedding" <=> ce."Embedding"
                 LIMIT {dislikedTake}
             ) n
             WHERE n."Similarity" >= {similarityFloor}
             """).ToListAsync(ct);

        var nearestDisliked = dislikedRows
            .GroupBy(r => r.CandidateId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.Similarity).ThenBy(r => r.TitleId).ToList());

        var withDisliked = new Dictionary<Guid, IReadOnlyList<CrossReferenceNeighbour>>();
        foreach (var candidateId in ids)
        {
            var shown = ranked.TryGetValue(candidateId, out var list) ? list : [];
            // Deduplicated against the ranked set rather than merged with it: a low-rated title that
            // already made the top-k is the same evidence, and printing it twice would read to the
            // judge as two independent negatives.
            var extra = (nearestDisliked.GetValueOrDefault(candidateId) ?? [])
                .Where(r => shown.All(n => n.TitleId != r.TitleId))
                .Take(dislikedTake)
                .Select(r => new CrossReferenceNeighbour(r.Name, r.Year, r.Rating, r.Similarity, r.TitleId))
                .ToList();

            if (shown.Count + extra.Count > 0)
            {
                withDisliked[candidateId] = [.. shown, .. extra];
            }
        }

        return withDisliked;
    }
}
