using System.Globalization;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// v1's discovery arms: the facet arm through WS-3's retriever, TMDB discovery, a person's credits, the
/// semantic neighbours and the reference titles. v1 only — v2 retrieves through <c>IAskReelRetriever</c>;
/// deleted at cutover (§4.13).
/// </summary>
public sealed partial class LiveSearchExpansionService
{
    /// <summary>
    /// The facet arm: eligible titles carrying catalogue evidence for every concept the request
    /// names, through WS-3's retrieval contract, plus the supply report behind them. A failure costs
    /// the arm, never the turn.
    /// </summary>
    private async Task<Retrieval.RetrievalResult?> FacetArmAsync(
        Guid accountId, Retrieval.HardConstraints hard, IReadOnlySet<Guid> shown, IReadOnlySet<Guid> references,
        CancellationToken ct)
    {
        if (retriever is null || !hard.HasConcepts)
        {
            return null;
        }

        try
        {
            return await retriever.RetrieveAsync(
                new Retrieval.RetrievalQuery(accountId, hard, Retrieval.SoftPreferences.None, shown.Concat(references).ToList(), ReturnCount)
                {
                    // v1 runs its own TMDB and semantic pulls; ask the hybrid retriever for the catalogue arms only (WS-3 CR-2a).
                    Arms = Retrieval.RetrievalArms.Catalogue,
                },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ask Reel facet arm failed; discovery continues without it.");
            return null;
        }
    }

    /// <summary>
    /// The TMDB discovery arm — WS-3's <see cref="Retrieval.TmdbDiscoveryArm"/> for a request
    /// (R1-52), and the person-credit pull for a filmography. Returns the catalogue ids of what it
    /// found that the account could be shown: eligible catalogue titles as they stand, and new titles
    /// upserted so they can be hydrated and judged again before they reach the screen.
    ///
    /// <para><b>What the arm changes.</b> The old pull sent TMDB no language, no crowd floor and only
    /// the exclusions the model happened to echo, fetched one page by popularity, and searched the
    /// whole sentence as a title on every turn; 30 probes pulled 1,090 titles of which 22.8% were
    /// eligible. The arm sends the account's standing conditions WITH the fetch, pages up to three
    /// times until enough usable titles are in hand, searches by title only for a title-shaped
    /// message, and stops at its own 1.5 s deadline so a slow TMDB costs the turn the arm, never the
    /// answer (§4.7). The turn's own rejections ride along as the user's words only — the model's
    /// inferred exclusions never do.</para>
    ///
    /// <para><b>Cached per account and request</b> for <see cref="DiscoveryTtl"/>: a byte-identical
    /// repeat pulls nothing from TMDB, exactly as the old intent cache did. The account is on the key
    /// because the arm reads that account's conditions; the ids are re-checked by eligibility on every
    /// turn, so a cached list can never show a title the account has since seen.</para>
    ///
    /// <para>Returns empty on any failure ⇒ the facet and semantic arms carry the turn alone. A blip
    /// on ONE arm must not discard the others: before this guard a TMDB timeout or a 429 returned
    /// "search hit a snag" with zero results while every semantically-nearest unseen title was
    /// already in hand.</para>
    /// </summary>
    private async Task<List<Guid>> DiscoverCandidateIdsAsync(
        Guid accountId, SearchIntent intent, AskReelNegations negations, string message, string? region,
        CancellationToken ct)
    {
        if (intent.People is { Count: > 0 })
        {
            return await PersonCreditIdsAsync(intent, ct);
        }

        var key = ArmCacheKey(accountId, intent, negations, message, region, agent.ModelId);
        if (cache.TryGetValue<List<Guid>>(key, out var cached) && cached is { Count: > 0 })
        {
            return cached;
        }

        try
        {
            var result = await TmdbArm.DiscoverAsync(
                new Retrieval.TmdbDiscoveryRequest(
                    accountId,
                    intent.Genres,
                    intent.Keywords,
                    negations.RequestedGenres,
                    negations.RequestedKeywords,
                    Retrieval.MediaScopes.From(intent.MediaTypes),
                    intent.MinYear is null && intent.MaxYear is null
                        ? null
                        : new Retrieval.YearRange(
                            intent.MinYear is { } lo ? ClampYear(lo) : null,
                            intent.MaxYear is { } hi ? ClampYear(hi) : null),
                    intent.MaxRuntimeMinutes,
                    message,
                    region,
                    MaxCandidates),
                ct);
            if (result.TimedOut)
            {
                logger.LogInformation(
                    "Ask Reel TMDB arm: deadline reached after {Elapsed} ms; fusing what arrived ({Items} items, {Pages} page(s)).",
                    result.ElapsedMs, result.Items.Count, result.Pages);
            }

            var ids = result.EligibleItems
                .Select(i => i.CatalogueTitleId)
                .OfType<Guid>()
                .ToList();
            var provisional = result.ProvisionalItems.Select(i => i.Item).Take(MaxCandidates).ToList();
            if (provisional.Count > 0)
            {
                ids.AddRange(await UpsertAndCollectIdsAsync(provisional, ct));
            }

            ids = ids.Distinct().ToList();
            // A timed-out pull is not remembered: the next turn deserves the arm's full answer.
            if (ids.Count > 0 && !result.TimedOut)
            {
                cache.Set(key, ids, DiscoveryTtl);
            }

            return ids;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ask Reel TMDB arm: discovery failed; falling back to the catalogue arms only.");
            return [];
        }
    }

    /// <summary>
    /// A named person's credits, pulled once per (person, role, media, years) even under concurrent
    /// identical asks and cached for <see cref="DiscoveryTtl"/>: a filmography is a lookup, so the
    /// standing conditions are applied afterwards by the content filter, not sent with the fetch.
    /// </summary>
    private async Task<List<Guid>> PersonCreditIdsAsync(SearchIntent intent, CancellationToken ct)
    {
        var key = IntentKey(intent, region: null);
        if (cache.TryGetValue<List<Guid>>(CacheKey(key, agent.ModelId), out var cached) && cached is { Count: > 0 })
        {
            return cached;
        }

        try
        {
            // CancellationToken.None so a disconnecting waiter can't starve a sharer, and the pull
            // warms the cache regardless.
            var items = await coordinator.RunOnceAsync(
                $"askreel:credits:{key}",
                () => FetchPeopleCreditsAsync(intent.People!, intent, CancellationToken.None));
            if (items.Count == 0)
            {
                return [];
            }

            var ids = await UpsertAndCollectIdsAsync(items, ct);
            cache.Set(CacheKey(key, agent.ModelId), ids, DiscoveryTtl);
            return ids;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ask Reel: the person-credit pull failed; falling back to the catalogue arms only.");
            return [];
        }
    }

    /// <summary>
    /// The TMDB arm's cache key: everything the arm is asked — the request's reading, the user's own
    /// rejections, the message (it decides the title search), the region — plus the account, because
    /// the arm sends that account's standing conditions with the fetch.
    /// </summary>
    internal static string ArmCacheKey(
        Guid accountId, SearchIntent intent, AskReelNegations negations, string message, string? region, string modelId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"askreel:arm:{AskReelPromptVersion.Current}:{modelId}:{accountId}:{IntentKey(intent, region)}|" +
            $"-{string.Join(',', negations.RequestedGenres.OrderBy(g => g, StringComparer.Ordinal))}|" +
            $"-{string.Join(',', negations.RequestedKeywords.OrderBy(k => k, StringComparer.Ordinal))}|" +
            $"m={NormalizeQuery(message)}");

    /// <summary>
    /// The semantic discovery arm — pgvector cosine kNN over the account's own embedded UNSEEN
    /// catalogue against a concept embedding of the turn. The recall fix for vibe queries: TMDB
    /// genre/keyword/title search whiffs on "fantastically futuristic, tense", and a heavy watcher
    /// has already seen most of the little it does return, so discovery starves. Embedding the
    /// agent's concept text and pulling the nearest-by-meaning UNSEEN titles surfaces matches we
    /// already hold that no keyword names. Returns empty on any failure ⇒ the TMDB arm carries the
    /// turn alone (no regression).
    ///
    /// <para><b>The eligibility predicate below is not what makes this correct, and believing it was
    /// cost this surface real recall.</b> Postgres pushes <c>ORDER BY … &lt;=&gt; … LIMIT</c> into the
    /// HNSW index whatever order the LINQ is written in, and an HNSW scan post-filters its bounded
    /// candidate window — so with pgvector's default settings this asked for 40 eligible neighbours
    /// and got back only the eligible members of the 40 nearest titles OVERALL. Measured at 10 of a
    /// 20-row eligible set. What actually makes the pull whole is
    /// <c>hnsw.iterative_scan</c>, set on every connection by
    /// <c>HnswFilteredSearchInterceptor</c>; read that before touching either side of this query.
    /// The predicate stays because it is still the cheapest way to keep ineligible rows out.</para>
    /// </summary>
    private async Task<List<Guid>> SemanticNeighbourIdsAsync(
        Guid accountId, AgentReply agentReply, IReadOnlyList<float[]> referenceVectors, AskReelNegations negations,
        string rawQuery, ConceptVector? started, CancellationToken ct)
    {
        var concept = ConceptText(agentReply, rawQuery, negations);
        if (concept.Length == 0 && referenceVectors.Count == 0)
        {
            return [];
        }

        float[] queryVec;
        if (concept.Length > 0)
        {
            // The CONCEPT is cached, not the neighbour list (EmbedConceptAsync): the kNN underneath
            // is local, cheap and reads the account's own catalogue, which the enrichment sweep grows
            // continuously — so running it fresh is what keeps the pool current.
            var inFlight = started is not null && string.Equals(started.Text, concept, StringComparison.Ordinal)
                ? started
                : StartConceptVector(accountId, concept, ct)!;

            // §4.7: an uncached text takes 1.2–2.7 s to embed, so the arm waits at most
            // SemanticEmbedBudget from when the embedding STARTED and fusion proceeds without it.
            // The call is not abandoned — the ranking seed reads the same task.
            if (!inFlight.Vector.IsCompleted)
            {
                var remaining = SemanticEmbedBudget - inFlight.Started.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.WhenAny(inFlight.Vector, Task.Delay(remaining, ct));
                    ct.ThrowIfCancellationRequested();
                }

                if (!inFlight.Vector.IsCompleted)
                {
                    logger.LogInformation(
                        "Ask Reel semantic arm: the concept embedding is still in flight after {Budget} ms; fusing without kNN.",
                        (long)SemanticEmbedBudget.TotalMilliseconds);
                    return [];
                }
            }

            if (await inFlight.Vector is not { } embedded)
            {
                return [];
            }

            queryVec = embedded;
        }
        else
        {
            // No concept text at all but the user named references — anchor purely on them.
            queryVec = new float[referenceVectors[0].Length];
        }

        // Blend the reference-title embeddings in, so the kNN pull happens in the reference's
        // neighbourhood — this is what makes "like Blade Runner" DISCOVER Blade Runner's semantic
        // neighbours instead of merely re-ranking whatever the keywords happened to fetch.
        queryVec = BlendQueryVector(queryVec, referenceVectors);

        var vector = new Vector(queryVec);
        var eligibleIds = eligibility.EligibleTitles(accountId).Select(t => t.Id);
        return await db.TitleEmbeddings
            .Where(e => eligibleIds.Contains(e.TitleId))
            .OrderBy(e => e.Embedding.CosineDistance(vector))
            .Take(SemanticNeighbourCount)
            .Join(db.Titles, e => e.TitleId, t => t.Id, (e, t) => t.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Resolves the intent's reference titles ("like Blade Runner") to catalogue rows with
    /// embeddings. Local exact-name match first (cheap, and the reference is usually famous enough
    /// to be in the catalogue already); TMDB title search + the standard upsert path otherwise.
    /// Every step degrades to skipping the reference — anchoring is an enrichment, and a turn must
    /// never fail because a comparison could not be resolved.
    /// </summary>
    private async Task<(List<Title> Titles, List<float[]> Vectors)> ResolveReferenceTitlesAsync(
        SearchIntent intent, CancellationToken ct)
    {
        var names = intent.ReferenceTitles ?? [];
        if (names.Count == 0)
        {
            return ([], []);
        }

        var resolved = new List<Title>();
        foreach (var name in names.Take(3))
        {
            try
            {
                var title = await FindReferenceLocallyAsync(name, ct) ?? await ResolveReferenceViaTmdbAsync(name, ct);
                if (title is not null && resolved.All(t => t.Id != title.Id))
                {
                    resolved.Add(title);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Ask Reel: resolving reference title '{Name}' failed; anchoring without it.", name);
            }
        }

        if (resolved.Count == 0)
        {
            return ([], []);
        }

        await EnsureEmbeddedAsync(resolved, ct);
        var vectors = await LoadVectorsAsync(resolved.Select(t => t.Id).ToList(), ct);
        return (resolved, resolved.Where(t => vectors.ContainsKey(t.Id)).Select(t => vectors[t.Id]).ToList());
    }

    /// <summary>
    /// Exact-name catalogue lookup (case-insensitive, most popular first) — the typeahead-style
    /// resolution for a title the user names verbatim. Deliberately NOT fuzzy: a wrong anchor is
    /// worse than a missing one, and the TMDB fallback handles near-miss spellings.
    /// </summary>
    private async Task<Title?> FindReferenceLocallyAsync(string name, CancellationToken ct)
    {
        var lower = name.ToLowerInvariant();
        return await db.Titles
            .Where(t => t.Name.ToLower() == lower)
            .OrderByDescending(t => t.TmdbPopularity)
            .ThenBy(t => t.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// TMDB best-hit resolution for a reference the catalogue doesn't hold, through the SAME
    /// upsert path discovery uses so dedup and field semantics hold. Searches both media types:
    /// the reference is an anchor, not a result, so a UI "movies only" filter must not stop
    /// "movies like Severance" from anchoring on the show.
    /// </summary>
    private async Task<Title?> ResolveReferenceViaTmdbAsync(string name, CancellationToken ct)
    {
        var hits = (await tmdb.SearchTitlesAsync(true, name, 1, ct))
            .Concat(await tmdb.SearchTitlesAsync(false, name, 1, ct))
            .OrderByDescending(i => i.Popularity)
            .ToList();
        if (hits.Count == 0)
        {
            return null;
        }

        var best = hits[0];
        var ids = await UpsertAndCollectIdsAsync([best], ct);
        return ids.Count == 0
            ? null
            : await db.Titles.FirstOrDefaultAsync(t => t.Id == ids[0], ct);
    }

    /// <summary>A concept embedding in flight: the text, the call, and when it started.</summary>
    private sealed record ConceptVector(string Text, Task<float[]?> Vector, System.Diagnostics.Stopwatch Started);

    /// <summary>
    /// Starts embedding <paramref name="concept"/> — a network call that touches no database, so it can
    /// run underneath the database-bound arms. Null for an empty concept.
    /// </summary>
    private ConceptVector? StartConceptVector(Guid accountId, string concept, CancellationToken ct) =>
        concept.Length == 0
            ? null
            : new ConceptVector(concept, EmbedConceptAsync(accountId, concept, ct), System.Diagnostics.Stopwatch.StartNew());

    /// <summary>
    /// The concept's vector, from the cache when this process embedded the same text inside
    /// <see cref="InterpretTtl"/> (the repeat that used to re-buy a paid embedding on every turn), else
    /// from the provider. Null — never a throw — when the provider gives nothing usable: the arms
    /// degrade to the ones that need no vector.
    /// </summary>
    private async Task<float[]?> EmbedConceptAsync(Guid accountId, string concept, CancellationToken ct)
    {
        var key = ConceptVectorKey(accountId, concept, agent.ModelId);
        if (cache.TryGetValue<float[]>(key, out var remembered) && remembered is { Length: > 0 })
        {
            return remembered;
        }

        try
        {
            var embedded = await embeddings.EmbedAsync([concept], ct);
            if (embedded is [{ Length: > 0 } vector, ..])
            {
                cache.Set(key, vector, InterpretTtl);
                return vector;
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ask Reel: the concept embedding failed; the turn continues without a similarity signal.");
            return null;
        }
    }

    /// <summary>
    /// The reference-anchored ranking seed, or <c>null</c> when the provider gave nothing usable to
    /// anchor on.
    ///
    /// <para>The seed used to be a bare <c>[0]</c> index into the provider's answer, so an empty or
    /// truncated response threw and the turn reported a crash. A missing seed costs the turn its
    /// cosine ordering — real, and worth degrading for — but the pool has already been discovered
    /// and is still worth showing. Where the turn named comparison titles, their mean carries the
    /// seed on its own, exactly as the semantic arm already does.</para>
    /// </summary>
    private async Task<float[]?> RankingSeedVectorAsync(
        Guid accountId, string seedText, IReadOnlyList<float[]> referenceVectors, ConceptVector? started, CancellationToken ct)
    {
        float[]? seed = null;
        if (seedText.Length > 0)
        {
            // The same text the semantic arm embedded, as the same call: the seed and the concept are
            // one string (A-9), so the embedding started before discovery is awaited here rather than
            // bought twice — and when the arm's budget ran out on it, this is where it lands.
            seed = started is not null && string.Equals(started.Text, seedText, StringComparison.Ordinal)
                ? await started.Vector
                : await EmbedConceptAsync(accountId, seedText, ct);
        }

        if (seed is not null)
        {
            return BlendQueryVector(seed, referenceVectors);
        }

        if (seedText.Length > 0)
        {
            logger.LogWarning(
                "Ask Reel: the ranking seed came back empty; ranking this turn without a similarity signal.");
        }

        return referenceVectors.Count > 0
            ? BlendQueryVector(new float[referenceVectors[0].Length], referenceVectors)
            : null;
    }

    /// <summary>
    /// The reference-anchored query vector: <c>normalize(concept + 0.5 × mean(references))</c>.
    /// The concept keeps the majority weight — "like Blade Runner but MORE EMOTIONAL" must still
    /// honour the modifier — while the reference mean drags the query into the named titles'
    /// actual neighbourhood. Pure and internal so the math is pinned by unit tests. Returns the
    /// concept unchanged when no usable reference vector matches its dimensionality.
    /// </summary>
    internal static float[] BlendQueryVector(float[] concept, IReadOnlyList<float[]> referenceVectors)
    {
        var usable = referenceVectors.Where(v => v.Length == concept.Length).ToList();
        if (usable.Count == 0)
        {
            return concept;
        }

        var blended = new double[concept.Length];
        for (var i = 0; i < concept.Length; i++)
        {
            double mean = 0;
            foreach (var reference in usable)
            {
                mean += reference[i];
            }

            blended[i] = concept[i] + (0.5d * (mean / usable.Count));
        }

        var norm = Math.Sqrt(blended.Sum(x => x * x));
        return norm <= 0
            ? concept
            : blended.Select(x => (float)(x / norm)).ToArray();
    }

    /// <summary>
    /// True when the title IS one of the turn's resolved reference anchors. A reference is a
    /// place to search NEAR, never an answer: "like Blade Runner" returning Blade Runner is
    /// junk, and the exclusion is a pool filter — never a footprint record, because naming a
    /// film as a comparison is not evidence of having watched it.
    /// </summary>
    internal static bool IsReference(Title title, IReadOnlySet<Guid> referenceIds) =>
        referenceIds.Contains(title.Id);
}
