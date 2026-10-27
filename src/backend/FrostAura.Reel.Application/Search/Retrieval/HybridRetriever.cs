using System.Diagnostics;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Application.Tenancy;
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
/// <item><b>Mood</b> — <see cref="MoodArm"/>: each mood ("cosy", "tense") read as the extracted tone,
/// keyword labels and genres that carry it, one ranked list per mood. Soft: it finds and orders, never
/// filters.</item>
/// <item><b>Semantic</b> — kNN on the positive concept text (moods included, rejected words never),
/// blended toward any reference titles. The embedding gets <see cref="SemanticBudget"/> (none at all
/// for a concept request, where the arm only re-ranks what the catalogue admits); an uncached
/// text that misses it falls back to the mean of the precomputed embeddings of the labels the
/// concepts and moods reached, and only with none of those is the arm fused without and reported in
/// <see cref="RetrievalResult.SkippedArms"/>. <c>hnsw.iterative_scan</c> is on for every connection
/// (<c>HnswFilteredSearchInterceptor</c>), so a filtered search returns the full k.</item>
/// <item><b>Steer</b> — <see cref="AttributeSteer"/>: the request's attribute steers ("funnier", "less
/// dark") and its moods' implied lean, relative to the reference titles' centroid when there are any.
/// It re-ranks what the other arms found, and reads the whole pool only when nothing else spoke.</item>
/// <item><b>TMDB</b> — <see cref="TmdbDiscoveryArm"/>, the standing conditions sent with the fetch,
/// its own 1.5 s deadline (retrieval waits at most <see cref="TmdbWait"/>, and not at all once the
/// catalogue's evidence holds <see cref="SufficientShare"/> of a concept request), run on its own database
/// context so it overlaps the catalogue arms. Titles
/// not yet in the catalogue come back as <see cref="RetrievalResult.Provisional"/>.</item>
/// </list>
///
/// <para><b>Facets act as filters (§4.7).</b> When the request names a concept, a candidate needs
/// facet or full-text evidence before it is returned at all: the semantic and TMDB arms can only
/// re-rank titles the catalogue evidence already admits. TMDB keyword tags are noisy (Batman Begins
/// carries "ninja"), and a vector arm always fills — the two reasons a ninja request used to show 24
/// non-ninja cards. Without a concept (a mood, a reference, a person, a decade) every arm draws from
/// the eligible titles that satisfy the remaining hard constraints, and the bare best-regarded list
/// runs only when no mood, reference or steer list found anything.</para>
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
    private readonly MoodArm moodArm = new(db, resolver);

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

    /// <summary>
    /// The share of the request the catalogue's evidence must hold before retrieval stops waiting on
    /// TMDB for a concept (R1-18): from half the want, TMDB would mostly re-rank and add provisional
    /// titles the next turn can use; below it, the titles TMDB adds are the answer.
    /// </summary>
    public const double SufficientShare = 0.5;

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

        if (q.Arms.HasFlag(RetrievalArms.Facet) && hard.HasConcepts)
        {
            await FacetAsync(conceptPool, lists, timings, clock, ct);
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

        // R1-18: with a concept the semantic arm can only re-rank titles that evidence admits, so its
        // embedding is never waited for (taken if it has arrived, else the reached labels stand in);
        // a cold concept text takes 1.2-2.7 s, so the 1.0 s wait used to buy nothing on most turns.
        // TMDB can add titles the catalogue lacks, so it is waited for only while the evidence is
        // thin — under SufficientShare of the want. Both calls finish in the background either way,
        // warming the embedding and keyword-id caches for the next turn.
        var rerankOnlySemantic = admitted is not null;
        var sufficient = admitted is not null && admitted.Count >= Math.Max(1, (int)Math.Ceiling(want * SufficientShare));

        // Moods (soft): one ranked list per mood the catalogue can read — its tone, labels and genres.
        // With a concept they only re-rank what the concept admits (fusion ignores anything else).
        var moodResult = MoodArmResult.Empty;
        if (q.Soft.Moods.Count > 0)
        {
            clock.Restart();
            moodResult = await moodArm.FindAsync(hard.HasConcepts ? conceptPool : basePool, q.Soft.Moods, ArmDepth, ct);
            foreach (var (mood, hits) in moodResult.Lists)
            {
                lists[$"mood:{mood}"] = [.. hits.Select(h => h.TitleId)];
                foreach (var hit in hits)
                {
                    Add(evidence, hit.TitleId, hit.Evidence);
                }
            }

            timings["mood"] = clock.ElapsedMilliseconds;
        }

        if (q.Arms.HasFlag(RetrievalArms.Semantic))
        {
            var waitClock = Stopwatch.StartNew();
            var late = rerankOnlySemantic && !embedding.IsCompleted;
            var vector = late ? null : await embedding;
            timings["semantic-wait"] = waitClock.ElapsedMilliseconds;
            timings["semantic-embed"] = semanticClock.ElapsedMilliseconds;
            if (vector is null && semanticText.Length > 0)
            {
                // The text missed its budget (or there is no provider): stand on the precomputed
                // embeddings of the labels the concepts and moods reached instead of dropping the arm.
                var labels = hard.Concepts.SelectMany(c => c.ExactLabels ?? []).Concat(moodResult.Labels).Distinct(StringComparer.Ordinal).ToList();
                var (centroid, used) = await LabelCentroidAsync(labels, ct);
                if (centroid is not null)
                {
                    vector = centroid;
                    timings["semantic-labels"] = used;
                }
            }

            var anchor = await AnchorAsync(vector, q.Soft.ReferenceTitleIds, ct);
            if (anchor is null)
            {
                if (late)
                {
                    // Not waited for (it could only re-rank) and no label stood in.
                    skipped.Add("semantic-late");
                }
                else if (semanticText.Length > 0 || q.Soft.ReferenceTitleIds.Count > 0)
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

        // Attribute steers ("funnier", "less dark", a mood's lean): the population leaning the asked
        // way, most first. It re-ranks what the other arms found; only when nothing else spoke does it
        // read the whole pool.
        var steers = AttributeSteer.Merge(q.Soft.Directions, moodResult.Implied);
        if (steers.Count > 0)
        {
            clock.Restart();
            IReadOnlyCollection<Guid>? population = admitted
                ?? (lists.Values.Any(l => l.Count > 0) ? [.. lists.Values.SelectMany(l => l).Distinct()] : null);
            var steered = await SteerAsync(steers, q.Soft.ReferenceTitleIds, population, basePool, ct);
            if (steered.Count > 0)
            {
                lists["steer"] = [.. steered.Select(s => s.TitleId)];
                foreach (var (id, lean) in steered)
                {
                    Add(evidence, id, lean);
                }
            }

            timings["steer"] = clock.ElapsedMilliseconds;
        }

        // No concept and no soft list to stand on (a person, a decade, a bare media switch): the
        // pool's best-regarded titles. Never beside a mood or reference list, where it was noise —
        // a "cosy" request used to be answered by the catalogue's best-rated titles of any kind.
        if (q.Arms.HasFlag(RetrievalArms.Facet) && !hard.HasConcepts && !lists.Values.Any(l => l.Count > 0))
        {
            await FacetAsync(basePool, lists, timings, clock, ct);
        }

        var provisional = new List<ProvisionalTitle>();
        if (q.Arms.HasFlag(RetrievalArms.Tmdb))
        {
            clock.Restart();
            var remaining = sufficient ? TimeSpan.Zero : TmdbWait - started.Elapsed;
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
    /// The facet list: best vote-shrunk crowd score first (task 4, D-12) — a 10.0 on 2 votes ranks
    /// below a 7.0 on 5,000, so the head the judge sees first is the pool's best-regarded titles, not
    /// merely its most-voted or its luckiest few-vote outliers.
    /// </summary>
    private static async Task FacetAsync(
        IQueryable<Title> pool, Dictionary<string, List<Guid>> lists, Dictionary<string, long> timings, Stopwatch clock, CancellationToken ct)
    {
        clock.Restart();
        lists["facet"] = await CrowdVotes.BestShrunkFirst(pool).Select(t => t.Id).Take(ArmDepth).ToListAsync(ct);
        timings["facet"] = clock.ElapsedMilliseconds;
    }

    /// <summary>
    /// The titles leaning the way <paramref name="steers"/> ask, most first, each with the
    /// <c>steer:</c> evidence it satisfies. Pivots are the reference titles' centroid when there are
    /// references, else the midpoint (<see cref="AttributeSteer"/>). Within
    /// <paramref name="population"/> when given; otherwise over the pool's titles with at least
    /// <see cref="CrowdVotes.MinRelaxVotes"/> votes, by lean in steps of 0.2 and then the crowd, so the
    /// head is well regarded rather than merely extreme. Trusted (Done) attributes only.
    /// </summary>
    private async Task<List<(Guid TitleId, IReadOnlyList<string> Evidence)>> SteerAsync(
        IReadOnlyList<(string Attribute, AttributeTrend Trend, bool Explicit)> steers,
        IReadOnlyList<Guid> references,
        IReadOnlyCollection<Guid>? population,
        IQueryable<Title> pool,
        CancellationToken ct)
    {
        var pivots = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (references.Count > 0)
        {
            var refIds = references.ToList();
            var refRows = await Scalars(db.TitleAttributes.Where(a => refIds.Contains(a.TitleId) && a.Status == AttributeExtractionStatus.Done)).ToListAsync(ct);
            if (refRows.Count > 0)
            {
                foreach (var (attribute, _, _) in steers)
                {
                    pivots[attribute] = refRows.Average(r => AttributeSteer.Value(r, attribute));
                }
            }
        }

        List<TitleAttributes> rows;
        if (population is not null)
        {
            var ids = population.ToList();
            rows = [.. (await Scalars(db.TitleAttributes.Where(a => ids.Contains(a.TitleId) && a.Status == AttributeExtractionStatus.Done)).ToListAsync(ct))
                .Where(r => AttributeSteer.Leans(r, steers, pivots) && AttributeSteer.Fit(r, steers, pivots) > 0m)
                .OrderByDescending(r => AttributeSteer.Fit(r, steers, pivots))
                .ThenBy(r => r.TitleId)
                .Take(ArmDepth)];
        }
        else
        {
            var poolIds = CrowdVotes.AtLeast(pool, CrowdVotes.MinRelaxVotes).Select(t => t.Id);
            var leaning = await Scalars(db.TitleAttributes
                    .Where(a => a.Status == AttributeExtractionStatus.Done && poolIds.Contains(a.TitleId))
                    .Where(AttributeSteer.LeansExpression(steers, pivots))
                    .OrderByDescending(AttributeSteer.FitExpression(steers, pivots))
                    .ThenBy(a => a.TitleId))
                .Take(ArmDepth * 3)
                .ToListAsync(ct);
            var leaningIds = leaning.Select(r => r.TitleId).ToList();
            var crowdRank = (await CrowdVotes.BestShrunkFirst(pool.Where(t => leaningIds.Contains(t.Id))).Select(t => t.Id).ToListAsync(ct))
                .Select((id, rank) => (id, rank))
                .ToDictionary(x => x.id, x => x.rank);
            rows = [.. leaning
                .Where(r => AttributeSteer.Fit(r, steers, pivots) > 0m && crowdRank.ContainsKey(r.TitleId))
                .OrderByDescending(r => Math.Round(AttributeSteer.Fit(r, steers, pivots) * 5m, MidpointRounding.AwayFromZero))
                .ThenBy(r => crowdRank[r.TitleId])
                .Take(ArmDepth)];
        }

        return [.. rows.Select(r => (r.TitleId, (IReadOnlyList<string>)[.. steers
            .Where(s => (s.Trend == AttributeTrend.More ? 1m : -1m) * (AttributeSteer.Value(r, s.Attribute) - (pivots.TryGetValue(s.Attribute, out var p) ? p : AttributeSteer.Midpoint)) > 0m)
            .Select(s => AttributeSteer.Evidence(s.Attribute, s.Trend))]))];
    }

    /// <summary>The six steerable scalars only — never the raw extraction JSON.</summary>
    private static IQueryable<TitleAttributes> Scalars(IQueryable<TitleAttributes> rows) =>
        rows.Select(a => new TitleAttributes
        {
            TitleId = a.TitleId,
            Darkness = a.Darkness,
            Pacing = a.Pacing,
            Complexity = a.Complexity,
            EmotionalIntensity = a.EmotionalIntensity,
            Humor = a.Humor,
            Optimism = a.Optimism,
        });

    /// <summary>
    /// The mean of the precomputed embeddings of <paramref name="labels"/> (the most-carried 24 that
    /// have one, from the current embedding model), and how many were used; null when none has one.
    /// </summary>
    private async Task<(float[]? Centroid, int Used)> LabelCentroidAsync(IReadOnlyList<string> labels, CancellationToken ct)
    {
        if (labels.Count == 0)
        {
            return (null, 0);
        }

        var wanted = labels.ToList();
        var vectors = await db.KeywordLabels
            .Where(l => wanted.Contains(l.Label) && l.Embedding != null && l.EmbeddingModel == Ml.EmbeddingText.Model)
            .OrderByDescending(l => l.TitleCount)
            .ThenBy(l => l.Label)
            .Select(l => l.Embedding!)
            .Take(24)
            .ToListAsync(ct);
        if (vectors.Count == 0)
        {
            return (null, 0);
        }

        var dims = vectors[0].ToArray().Length;
        var centroid = new float[dims];
        foreach (var v in vectors.Select(v => v.ToArray()).Where(v => v.Length == dims))
        {
            Accumulate(centroid, v, 1f / vectors.Count);
        }

        return (centroid, vectors.Count);
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
        var scope = PinnedScope(q.AccountId);
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

    /// <summary>
    /// A new DI scope pinned to the turn's account (R1-01, J-11). A fresh scope has a fresh, unpinned
    /// <see cref="IAccountContext"/>; without the pin every account-scoped read on it (the account's
    /// conditions, its seen titles) is an unscoped read — every tenant's rows under Warn, a refused
    /// query under Throw. Pinned before anything is resolved from it.
    /// </summary>
    private IServiceScope PinnedScope(Guid accountId)
    {
        var scope = scopes.CreateScope();
        scope.ServiceProvider.GetService<IAccountContext>()?.SetAccount(accountId);
        return scope;
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
            using var scope = PinnedScope(q.AccountId);
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
