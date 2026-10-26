using System.Diagnostics;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>
/// Ask Reel's retriever (program §4.7, C-1, C-5): four arms, every one inside the account's
/// eligibility, fused by reciprocal rank (k = 60).
///
/// <list type="number">
/// <item><b>Facet</b> — the resolved concepts (<see cref="ConceptResolver"/>) as an indexed overlap on
/// exact vocabulary labels, whole-word name matches and genre tags (with TV stand-ins), best
/// vote-shrunk crowd score first.</item>
/// <item><b>Full text</b> — <see cref="FullTextArm"/> over the name, keywords, themes and overview.</item>
/// <item><b>Semantic</b> — kNN on the positive concept text (moods included, rejected words never),
/// blended toward any reference titles. The embedding gets <see cref="SemanticBudget"/>; an uncached
/// text that misses it is fused without, and the arm is reported in
/// <see cref="RetrievalResult.SkippedArms"/>. <c>hnsw.iterative_scan</c> is on for every connection
/// (<c>HnswFilteredSearchInterceptor</c>), so a filtered search returns the full k.</item>
/// <item><b>TMDB</b> — <see cref="TmdbDiscoveryArm"/>, the standing conditions sent with the fetch,
/// its own 1.5 s deadline (retrieval waits at most <see cref="TmdbWait"/>), run on its own database
/// context so it overlaps the catalogue arms. Titles
/// not yet in the catalogue come back as <see cref="RetrievalResult.Provisional"/>.</item>
/// </list>
///
/// <para><b>Facets act as filters (§4.7).</b> When the request names a concept, a candidate needs
/// facet or full-text evidence before it is returned at all: the semantic and TMDB arms can only
/// re-rank titles the catalogue evidence already admits. TMDB keyword tags are noisy (Batman Begins
/// carries "ninja"), and a vector arm always fills — the two reasons a ninja request used to show 24
/// non-ninja cards. Without a concept (a mood, a reference, a person, a decade) every arm draws from
/// the eligible titles that satisfy the remaining hard constraints.</para>
///
/// <para><b>Supply</b> is <see cref="ISupplyReporter"/>'s over the RESOLVED constraints, so the
/// counts a reply quotes are counted with the same labels the cards were found with.</para>
/// </summary>
public sealed class HybridRetriever(
    IReelDbContext db,
    EligibilityQueryBuilder eligibility,
    ISupplyReporter supply,
    IConceptResolver resolver,
    FullTextArm fullText,
    ConceptEmbeddings embeddings,
    IServiceScopeFactory scopes,
    ILogger<HybridRetriever> logger) : IAskReelRetriever
{
    /// <summary>Reciprocal-rank fusion constant shared by every arm.</summary>
    public const int RrfK = FacetRetriever.RrfK;

    /// <summary>The most candidates one call returns.</summary>
    public const int MaxWant = FacetRetriever.MaxWant;

    /// <summary>How deep each catalogue arm reads before fusion.</summary>
    public const int ArmDepth = 120;

    /// <summary>Neighbours the semantic arm asks for.</summary>
    public const int SemanticK = 60;

    /// <summary>
    /// How long, from the start of the call, retrieval waits for the TMDB arm before fusing without it.
    /// The arm has its own 1.5 s deadline, but the retrieve stage's budget is 1.0 s at p95 (§4.11), and
    /// for a heavy watcher most of what TMDB returns is already seen; a late arm finishes in the
    /// background and its keyword ids are cached for the next turn.
    /// </summary>
    public static readonly TimeSpan TmdbWait = TimeSpan.FromMilliseconds(900);

    /// <summary>How long the semantic arm waits for its embedding (§4.7: 1.0 s, then fuse without it).</summary>
    public static readonly TimeSpan SemanticBudget = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public async Task<RetrievalResult> RetrieveAsync(RetrievalQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(q);
        var timings = new Dictionary<string, long>();
        var skipped = new List<string>();
        var clock = Stopwatch.StartNew();
        var started = Stopwatch.StartNew();

        var hard = await resolver.ResolveAsync(q.Hard, ct);
        timings["resolve"] = clock.ElapsedMilliseconds;

        // Network work starts first and overlaps everything the database does below.
        var semanticText = q.Arms.HasFlag(RetrievalArms.Semantic) ? SemanticText(hard, q.Soft) : string.Empty;
        var semanticClock = Stopwatch.StartNew();
        var embedding = semanticText.Length > 0
            ? embeddings.EmbedAsync(semanticText, SemanticBudget, ct)
            : Task.FromResult<float[]?>(null);
        var tmdb = q.Arms.HasFlag(RetrievalArms.Tmdb) && (hard.HasConcepts || q.Message is not null)
            ? TmdbAsync(q, hard, ct)
            : Task.FromResult<TmdbDiscoveryResult?>(null);

        // Supply runs on its own context, beside the catalogue arms: it is several counts over the
        // concept's population (300-650 ms for the founder), and nothing below waits on it.
        var supplyTask = StartScopedSupply(q, hard, ct);

        var want = Math.Clamp(q.Want, 0, MaxWant);
        if (want == 0)
        {
            await DrainAsync(embedding, tmdb);
            var only = await FinishSupplyAsync(supplyTask, q, hard, timings, ct);
            return new RetrievalResult([], only, new StageTimings(timings)) { SkippedArms = skipped };
        }

        var exclude = q.Exclude.Distinct().ToList();
        var eligible = await EligibleBaseAsync(q, ct);
        var withoutConcepts = hard with { Concepts = [] };
        var basePool = HardConstraintFilter.Apply(eligible, db, withoutConcepts).Where(t => !exclude.Contains(t.Id));
        var conceptPool = HardConstraintFilter.Apply(eligible, db, hard).Where(t => !exclude.Contains(t.Id));
        var lists = new Dictionary<string, List<Guid>>();
        var evidence = new Dictionary<Guid, List<string>>();
        var similarity = new Dictionary<Guid, double>();

        if (q.Arms.HasFlag(RetrievalArms.Facet))
        {
            clock.Restart();
            // Best vote-shrunk crowd score first (task 4, D-12): a 10.0 on 2 votes ranks below a 7.0
            // on 5,000, so the facet arm's head - what the judge sees first - is the concept's
            // best-regarded titles, not merely its most-voted or its luckiest few-vote outliers.
            lists["facet"] = await CrowdVotes.BestShrunkFirst(conceptPool).Select(t => t.Id).Take(ArmDepth).ToListAsync(ct);
            timings["facet"] = clock.ElapsedMilliseconds;
        }

        if (q.Arms.HasFlag(RetrievalArms.FullText) && hard.HasConcepts)
        {
            clock.Restart();
            try
            {
                var hits = await fullText.SearchAsync(basePool, hard.Concepts, ArmDepth, ct);
                lists["fulltext"] = [.. hits.Select(h => h.TitleId)];
                foreach (var hit in hits)
                {
                    Add(evidence, hit.TitleId, hit.Evidence());
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Hybrid retrieval: the full-text arm failed; fusing without it.");
                skipped.Add("fulltext");
            }

            timings["fulltext"] = clock.ElapsedMilliseconds;
        }

        // With a concept, only titles carrying catalogue evidence may be returned (§4.7).
        HashSet<Guid>? admitted = hard.HasConcepts
            ? [.. lists.GetValueOrDefault("facet", []), .. lists.GetValueOrDefault("fulltext", [])]
            : null;

        if (q.Arms.HasFlag(RetrievalArms.Semantic))
        {
            var vector = await embedding;
            timings["semantic-embed"] = semanticClock.ElapsedMilliseconds;
            var anchor = await AnchorAsync(vector, q.Soft.ReferenceTitleIds, ct);
            if (anchor is null)
            {
                if (semanticText.Length > 0 || q.Soft.ReferenceTitleIds.Count > 0)
                {
                    skipped.Add("semantic");
                }
            }
            else
            {
                clock.Restart();
                var neighbours = await NeighboursAsync(anchor, admitted, basePool, ct);
                lists["semantic"] = [.. neighbours.Select(n => n.TitleId)];
                foreach (var (id, distance) in neighbours)
                {
                    similarity[id] = 1d - distance;
                }

                timings["semantic"] = clock.ElapsedMilliseconds;
            }
        }

        var provisional = new List<ProvisionalTitle>();
        if (q.Arms.HasFlag(RetrievalArms.Tmdb))
        {
            clock.Restart();
            var remaining = TmdbWait - started.Elapsed;
            if (!tmdb.IsCompleted && remaining > TimeSpan.Zero)
            {
                await Task.WhenAny(tmdb, Task.Delay(remaining, ct));
                ct.ThrowIfCancellationRequested();
            }

            var discovered = tmdb.IsCompletedSuccessfully ? tmdb.Result : null;
            timings["tmdb-wait"] = clock.ElapsedMilliseconds;
            if (discovered is null)
            {
                if ((hard.HasConcepts || q.Message is not null) && !tmdb.IsCompleted)
                {
                    // Still running: it finishes on its own scope (and warms TMDB's keyword-id cache
                    // for the next turn), but this turn does not wait past the retrieve budget.
                    skipped.Add("tmdb-late");
                }
                else if (hard.HasConcepts || q.Message is not null)
                {
                    skipped.Add("tmdb");
                }
            }
            else
            {
                timings["tmdb"] = discovered.ElapsedMs;
                if (discovered.TimedOut)
                {
                    skipped.Add("tmdb-partial");
                }

                var ids = discovered.EligibleItems.Select(i => i.CatalogueTitleId).OfType<Guid>().Distinct().ToList();
                var kept = admitted is not null
                    ? ids.Where(admitted.Contains).ToList()
                    : await basePool.Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct);
                var keptSet = kept.ToHashSet();
                lists["tmdb"] = [.. ids.Where(keptSet.Contains)];
                provisional.AddRange(discovered.ProvisionalItems.Select(i => new ProvisionalTitle(
                    i.Item.Id, i.Item.IsMovie ? MediaType.Movie : MediaType.Show, i.Item.Name, i.Item.ReleasedAt?.Year)));
            }
        }

        clock.Restart();
        var fused = Fuse(lists, admitted);
        var ceiling = await eligibility.MaturityCeilingAsync(q.AccountId, ct);
        var head = fused.Take((want * 2) + 20).Select(f => f.TitleId).ToList();
        var titles = await db.Titles.Where(t => head.Contains(t.Id)).AsNoTracking().ToListAsync(ct);
        var byId = EligibilityQueryBuilder.WithinMaturity(titles, ceiling).ToDictionary(t => t.Id);
        var candidates = fused
            .Where(f => byId.ContainsKey(f.TitleId))
            .Take(want)
            .Select(f => new Candidate(
                f.TitleId,
                f.Score,
                similarity.TryGetValue(f.TitleId, out var s) ? s : null,
                [.. HardConstraintFilter.EvidenceFor(byId[f.TitleId], hard)
                    .Concat(evidence.GetValueOrDefault(f.TitleId, []))
                    .Distinct(StringComparer.Ordinal)]))
            .ToList();
        timings["fuse"] = clock.ElapsedMilliseconds;

        var report = await FinishSupplyAsync(supplyTask, q, hard, timings, ct);
        return new RetrievalResult(candidates, report, new StageTimings(timings))
        {
            Provisional = provisional,
            SkippedArms = skipped,
        };
    }

    /// <summary>
    /// The positive text the semantic arm embeds: each concept's own words (never a rejected one —
    /// rejections never enter a concept) and the moods. Empty when there is nothing positive.
    /// </summary>
    internal static string SemanticText(HardConstraints hard, SoftPreferences soft)
    {
        var parts = hard.Concepts
            .Select(c => ConceptResolver.Normalise(c.UserWords))
            .Concat(soft.Moods.Select(ConceptResolver.Normalise))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Reciprocal-rank fusion over the arms' ranked lists: score = Σ 1 / (k + rank). With a concept,
    /// only <paramref name="admitted"/> titles are scored.
    /// </summary>
    internal static IReadOnlyList<(Guid TitleId, double Score)> Fuse(
        IReadOnlyDictionary<string, List<Guid>> lists, IReadOnlySet<Guid>? admitted)
    {
        var scores = new Dictionary<Guid, double>();
        foreach (var list in lists.Values)
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var id = list[rank];
                if (admitted is not null && !admitted.Contains(id))
                {
                    continue;
                }

                scores[id] = scores.GetValueOrDefault(id) + (1d / (RrfK + rank + 1));
            }
        }

        return [.. scores.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value))];
    }

    /// <summary>The standing conditions, plus this turn's own rejections when there are any.</summary>
    private async Task<IQueryable<Title>> EligibleBaseAsync(RetrievalQuery q, CancellationToken ct)
    {
        if (q.ExcludeGenres.Count == 0 && q.ExcludeKeywords.Count == 0)
        {
            return eligibility.EligibleTitles(q.AccountId);
        }

        var rows = await db.ContentFilters.IgnoreQueryFilters().Where(f => f.AccountId == q.AccountId).ToListAsync(ct);
        var rules = FilterRuleSet.FromRows(rows).WithExclusions(q.ExcludeGenres, q.ExcludeKeywords);
        return eligibility.EligibleTitles(q.AccountId, rules);
    }

    /// <summary>
    /// The query vector: the concept embedding, blended toward the reference titles' embeddings (the
    /// mean of the two halves), or the references alone when there is no text. Null when neither is
    /// available.
    /// </summary>
    private async Task<float[]?> AnchorAsync(float[]? concept, IReadOnlyList<Guid> references, CancellationToken ct)
    {
        List<float[]> refs = [];
        if (references.Count > 0)
        {
            var ids = references.ToList();
            refs = (await db.TitleEmbeddings.Where(e => ids.Contains(e.TitleId)).Select(e => e.Embedding).ToListAsync(ct))
                .Select(v => v.ToArray())
                .ToList();
        }

        if (concept is null && refs.Count == 0)
        {
            return null;
        }

        var dims = concept?.Length ?? refs[0].Length;
        var anchor = new float[dims];
        var halves = 0;
        if (concept is not null)
        {
            Accumulate(anchor, concept, 1f);
            halves++;
        }

        if (refs.Count > 0)
        {
            foreach (var r in refs.Where(r => r.Length == dims))
            {
                Accumulate(anchor, r, 1f / refs.Count);
            }

            halves++;
        }

        for (var i = 0; i < dims; i++)
        {
            anchor[i] /= halves;
        }

        return anchor;
    }

    private static void Accumulate(float[] into, float[] value, float weight)
    {
        for (var i = 0; i < into.Length; i++)
        {
            into[i] += value[i] * weight;
        }
    }

    /// <summary>
    /// The nearest titles to <paramref name="anchor"/>: among the admitted titles when the request
    /// names a concept (the semantic arm then only ranks), else among the pool.
    /// </summary>
    private async Task<List<(Guid TitleId, double Distance)>> NeighboursAsync(
        float[] anchor, IReadOnlySet<Guid>? admitted, IQueryable<Title> pool, CancellationToken ct)
    {
        var vector = new Vector(anchor);
        IQueryable<Guid> ids = admitted is not null
            ? db.Titles.Where(t => admitted.Contains(t.Id)).Select(t => t.Id)
            : pool.Select(t => t.Id);
        var rows = await db.TitleEmbeddings
            .Where(e => ids.Contains(e.TitleId))
            .OrderBy(e => e.Embedding.CosineDistance(vector))
            .Take(SemanticK)
            .Select(e => new { e.TitleId, Distance = e.Embedding.CosineDistance(vector) })
            .ToListAsync(ct);
        return [.. rows.Select(r => (r.TitleId, r.Distance))];
    }

    /// <summary>
    /// The supply report on its own scope (its own context), so it overlaps the arms; null when the
    /// container cannot give one (a hand-built retriever in a test), and the caller then counts it on
    /// this context after the arms — never concurrently with them.
    /// </summary>
    private Task<(SupplyReport Report, long ElapsedMs)>? StartScopedSupply(RetrievalQuery q, HardConstraints hard, CancellationToken ct)
    {
        var scope = scopes.CreateScope();
        if (scope.ServiceProvider.GetService<ISupplyReporter>() is not { } scoped)
        {
            scope.Dispose();
            return null;
        }

        return RunAsync();

        async Task<(SupplyReport Report, long ElapsedMs)> RunAsync()
        {
            using (scope)
            {
                var clock = Stopwatch.StartNew();
                var report = await scoped.ReportAsync(q.AccountId, hard, q.Exclude, ct);
                return (report, clock.ElapsedMilliseconds);
            }
        }
    }

    /// <summary>The supply report — the overlapped one when it was started, else counted now — with its own time recorded.</summary>
    private async Task<SupplyReport> FinishSupplyAsync(
        Task<(SupplyReport Report, long ElapsedMs)>? started, RetrievalQuery q, HardConstraints hard,
        Dictionary<string, long> timings, CancellationToken ct)
    {
        if (started is not null)
        {
            var (report, elapsed) = await started;
            timings["supply"] = elapsed;
            return report;
        }

        var clock = Stopwatch.StartNew();
        var counted = await supply.ReportAsync(q.AccountId, hard, q.Exclude, ct);
        timings["supply"] = clock.ElapsedMilliseconds;
        return counted;
    }

    /// <summary>The TMDB arm on its own scope (its own database context), so it overlaps the catalogue arms.</summary>
    private async Task<TmdbDiscoveryResult?> TmdbAsync(RetrievalQuery q, HardConstraints hard, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var arm = scope.ServiceProvider.GetRequiredService<TmdbDiscoveryArm>();
            var concepts = hard.Concepts.Select(c => ConceptResolver.Normalise(c.UserWords)).Where(w => w.Length > 0).Distinct().ToList();
            var genres = hard.Concepts.SelectMany(c => c.Genres).Distinct().ToList();
            return await arm.DiscoverAsync(
                new TmdbDiscoveryRequest(
                    q.AccountId,
                    genres,
                    concepts,
                    q.ExcludeGenres,
                    q.ExcludeKeywords,
                    hard.Media,
                    hard.Years,
                    hard.MaxRuntimeMinutes,
                    q.Message,
                    q.Region,
                    Math.Clamp(q.Want, 1, MaxWant)),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Hybrid retrieval: the TMDB arm failed; fusing without it.");
            return null;
        }
    }

    private static void Add(Dictionary<Guid, List<string>> evidence, Guid id, IEnumerable<string> items)
    {
        if (!evidence.TryGetValue(id, out var list))
        {
            evidence[id] = list = [];
        }

        list.AddRange(items);
    }

    private static async Task DrainAsync(Task<float[]?> embedding, Task<TmdbDiscoveryResult?> tmdb)
    {
        try
        {
            await Task.WhenAll(embedding, tmdb);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Both arms log their own failures; nothing was going to use the result.
        }
    }
}
