using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ml.ClosedLoop;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// v1's filmography turn and the catalogue upkeep a turn does on the way: upserting, embedding and hydrating
/// what discovery found. v1 only — v2 answers person requests from its retriever; deleted at cutover.
/// </summary>
public sealed partial class LiveSearchExpansionService
{
    /// <summary>
    /// Streams a person's unseen credits: chronological (newest first), the top slice scored for the
    /// predicted-rating badge — but never re-ordered or culled by LLM fit, because the user asked for
    /// the person's work, not a recommendation.
    ///
    /// <para><b>Unseen only (FD-3's default, HREV2-ASK-03).</b> The caller reads the credits through
    /// the account's full eligibility, so a seen, rated, saved or declined title never arrives here; a
    /// seen one that somehow did is dropped rather than badged, because a person query is a
    /// suggestion surface and the lock forbids seen titles on every one. The reply counts the seen
    /// credits (<see cref="PersonReply"/>) and names none of them.</para>
    /// </summary>
    private async Task<TurnOutcome> StreamFilmographyAsync(
        Guid accountId, string? assistantText, SearchIntent intent, List<Title> eligible,
        FeatureVectorBuilder.TasteState taste, DateTime now, AskStageTimings timings, Emit emit, CancellationToken ct)
    {
        var newestFirst = eligible
            .OrderByDescending(t => t.ReleasedAt ?? t.FirstAiredAt ?? DateTime.MinValue)
            .Take(PersonReturnCount)
            .ToList();
        var seenIds = await SeenTitleIdsAsync(accountId, newestFirst.Select(t => t.Id).ToList(), ct);
        var ordered = newestFirst.Where(t => !seenIds.Contains(t.Id)).ToList();
        foreach (var t in ordered)
        {
            await emit("candidate", Card(t, similarity: null, predicted: null, seen: false));
        }

        // Score the top slice for the badge (no re-rank — the order is the filmography).
        var sliceIds = ordered.Take(HydrateScoreSlice).Select(t => t.Id).ToList();
        IReadOnlyDictionary<Guid, decimal> scores;
        using (timings.Stage(AskStageTimings.Score))
        {
            await HydrateAsync(sliceIds, ct, timings);
            scores = await scorer.ScoreAsync(accountId, sliceIds, now, ct, taste);
        }

        foreach (var id in sliceIds)
        {
            await emit("candidate-scored", new { titleId = id, predictedRating = scores.TryGetValue(id, out var p) ? Ranking.ForYouBar.Shown(p) : (decimal?)null });
        }

        var results = ordered
            .Select(t => Card(t, similarity: null,
                predicted: scores.TryGetValue(t.Id, out var p) ? (decimal?)p : null,
                seen: false))
            .ToList();

        var who = intent.People is { Count: > 0 } ? intent.People[0] : null;
        var verb = who?.Role == PersonRole.Director ? "directed by" : who?.Role == PersonRole.Cast ? "starring" : "from";
        var reason = who is null
            ? $"{ordered.Count} titles in this filmography."
            : $"{ordered.Count} titles {verb} {who.Name}.";
        using (timings.Stage(AskStageTimings.Emit))
        {
            await emit("done", new { results, reason });
        }

        logger.LogInformation(
            "Ask Reel filmography: {Count} titles for {Person}. {StageTimings}",
            ordered.Count, who?.Name ?? "?", timings);

        // Chronological, not ranked — so the stored rank is the position in the filmography and
        // there is no blend to record. Replaying it re-establishes the same list in the same order.
        var stored = ordered
            .Select((t, index) => new TurnCard(
                t.Id, t.TmdbId, index, OutsideRequest: false,
                Similarity: null,
                scores.TryGetValue(t.Id, out var predicted) ? Math.Round(predicted, 2, MidpointRounding.AwayFromZero) : null,
                LlmFit: null, Blend: 0m, Why: null, ExposureWhySource.Generic))
            .ToList();
        return TurnOutcome.Answered(assistantText, reason, stored);
    }

    /// <summary>
    /// How many of a person's credits the account has seen (watched or rated): every catalogued title
    /// the supply's person predicate credits to them in the request's media — the count v2 and the
    /// none-found supply give — plus any title on TMDB's credit page the catalogue holds no credit row
    /// for yet. Not narrowed by the account's conditions: "you've seen N" is a fact about the person's
    /// work and the account's history, and the conditions only decide what may be offered.
    ///
    /// <para><b>Why (HREV2-ASK-03).</b> It was counted over the discovered titles alone — TMDB's forty
    /// newest credits — inside the conditions, so "Liam Neeson movies" said "You've seen 11" to an
    /// account that had seen 39 of his catalogued films, and v2 said 39 for the same question.</para>
    /// </summary>
    private async Task<int> SeenCreditCountAsync(
        Guid accountId, Retrieval.HardConstraints hard, IReadOnlyList<Guid> discovered, IReadOnlyList<MediaType> media, CancellationToken ct)
    {
        var seen = db.Titles.Where(t =>
            db.WatchedTitles.Any(w => w.AccountId == accountId && w.TitleId == t.Id)
            || db.UserRatings.Any(r => r.AccountId == accountId && r.TitleId == t.Id));
        var person = new Retrieval.HardConstraints([], hard.People, null, null, hard.Media);
        var credited = hard.People.Count > 0
            ? await Retrieval.HardConstraintFilter.Apply(seen, db, person).Select(t => t.Id).ToListAsync(ct)
            : [];
        var onPage = discovered.Count == 0
            ? []
            : (await seen.Where(t => discovered.Contains(t.Id)).ToListAsync(ct))
                .Where(t => MatchesMedia(t, media))
                .Select(t => t.Id);
        return credited.Union(onPage).Count();
    }

    /// <summary>
    /// The person's catalogued credits the account may be offered (<paramref name="eligible"/> is its
    /// eligibility) inside the request's hard constraints, dated newest first and capped — the ones
    /// past TMDB's page included.
    /// </summary>
    private async Task<List<Guid>> CataloguedCreditIdsAsync(IQueryable<Title> eligible, Retrieval.HardConstraints hard, CancellationToken ct) =>
        await Retrieval.HardConstraintFilter.Apply(eligible, db, hard)
            .OrderBy(t => t.ReleasedAt == null && t.FirstAiredAt == null)
            .ThenByDescending(t => t.ReleasedAt ?? t.FirstAiredAt)
            .ThenBy(t => t.Id)
            .Take(PersonReturnCount * 2)
            .Select(t => t.Id)
            .ToListAsync(ct);

    /// <summary>
    /// The person reply, counts only (FD-3): "You've seen 9 movies directed by Christopher Nolan; here
    /// are 3 you haven't." — the v2 composer's sentence, so both engines say the same thing.
    /// </summary>
    internal static string PersonReply(PersonQuery person, int unseen, int seen, IReadOnlyList<MediaType> media)
    {
        ArgumentNullException.ThrowIfNull(person);
        var credit = person.Role switch
        {
            PersonRole.Director => $"directed by {person.Name}",
            PersonRole.Cast => $"starring {person.Name}",
            _ => $"with {person.Name}",
        };
        var noun = (media.Distinct().ToList(), seen == 1) switch
        {
            ([MediaType.Movie], true) => "movie",
            ([MediaType.Movie], false) => "movies",
            ([MediaType.Show], true) => "show",
            ([MediaType.Show], false) => "shows",
            (_, true) => "title",
            _ => "titles",
        };
        var here = unseen == 1 ? "is one" : FormattableString.Invariant($"are {unseen}");
        return seen > 0
            ? FormattableString.Invariant($"You've seen {seen} {noun} {credit}; here {here} you haven't.")
            : $"Here {here} {credit} you haven't seen.";
    }

    /// <summary>The subset of the given title ids the account has watched or rated.</summary>
    private async Task<HashSet<Guid>> SeenTitleIdsAsync(Guid accountId, IReadOnlyList<Guid> titleIds, CancellationToken ct)
    {
        var watched = await db.WatchedTitles
            .Where(w => w.AccountId == accountId && titleIds.Contains(w.TitleId))
            .Select(w => w.TitleId).ToListAsync(ct);
        var rated = await db.UserRatings
            .Where(r => r.AccountId == accountId && titleIds.Contains(r.TitleId))
            .Select(r => r.TitleId).ToListAsync(ct);
        return watched.Concat(rated).ToHashSet();
    }

    /// <summary>Resolve each named person → their TMDB credits (role-sliced), unioned + deduped, newest first.</summary>
    private async Task<IReadOnlyList<TmdbListItem>> FetchPeopleCreditsAsync(IReadOnlyList<PersonQuery> people, SearchIntent intent, CancellationToken ct)
    {
        var wantMovies = intent.MediaTypes.Count == 0 || intent.MediaTypes.Contains(MediaType.Movie);
        var wantTv = intent.MediaTypes.Count == 0 || intent.MediaTypes.Contains(MediaType.Show);
        var after = YearStart(intent.MinYear);
        var before = YearEnd(intent.MaxYear);

        var all = new List<TmdbListItem>();
        foreach (var person in people.Take(2))
        {
            var resolved = await tmdb.SearchPersonAsync(person.Name, ct);
            if (resolved is null)
            {
                continue;
            }

            var role = person.Role switch
            {
                PersonRole.Director => PersonCreditRole.Director,
                PersonRole.Cast => PersonCreditRole.Cast,
                _ => PersonCreditRole.Any,
            };
            all.AddRange(await tmdb.GetPersonCreditsAsync(resolved.Id, role, ct));
        }

        return all
            .Where(i => i.IsMovie ? wantMovies : wantTv)
            .Where(i => after is null || i.ReleasedAt is null || i.ReleasedAt >= after)
            .Where(i => before is null || i.ReleasedAt is null || i.ReleasedAt <= before)
            .GroupBy(i => (i.IsMovie, i.Id))
            .Select(g => g.First())
            .OrderByDescending(i => i.ReleasedAt ?? DateTime.MinValue)
            .Take(MaxCandidates)
            .ToList();
    }

    private async Task<List<Guid>> UpsertAndCollectIdsAsync(IReadOnlyList<TmdbListItem> items, CancellationToken ct)
    {
        await candidateGenerator.UpsertCandidatesAsync(items, ct);
        var movieIds = items.Where(i => i.IsMovie).Select(i => i.Id).ToList();
        var tvIds = items.Where(i => !i.IsMovie).Select(i => i.Id).ToList();
        return await db.Titles
            .Where(t => t.TmdbId != null
                && ((t.MediaType == MediaType.Movie && movieIds.Contains(t.TmdbId.Value))
                    || (t.MediaType == MediaType.Show && tvIds.Contains(t.TmdbId.Value))))
            .Select(t => t.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Embeds the titles in <paramref name="eligible"/> that have no vector yet. When it has work to do,
    /// that work is timed as <see cref="AskStageTimings.EmbedTitles"/> on <paramref name="timings"/>;
    /// nothing is recorded when every title already had a vector.
    /// </summary>
    private async Task EnsureEmbeddedAsync(
        IReadOnlyList<Title> eligible, CancellationToken ct, AskStageTimings? timings = null)
    {
        var ids = eligible.Select(t => t.Id).ToList();
        var already = await db.TitleEmbeddings.Where(e => ids.Contains(e.TitleId)).Select(e => e.TitleId).ToListAsync(ct);
        var missing = eligible.Where(t => !already.Contains(t.Id)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        using var titlesStage = timings?.Stage(AskStageTimings.EmbedTitles);
        var texts = missing.Select(EmbeddingText.Build).ToList();

        // Embedding newly-discovered titles is an ENRICHMENT step, not the answer. It used to be
        // unguarded, so a single transient provider failure propagated to the turn's catch-all and
        // returned "search hit a snag" with zero results — discarding everything already
        // discovered, hydrated and scored. Degrade to whatever is already embedded instead: those
        // titles still rank, the un-embedded ones simply sit this turn out and get picked up next
        // time. Worst case is a thinner result set, never an empty one.
        float[][] vectors;
        try
        {
            vectors = await embeddings.EmbedAsync(texts, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ask Reel: embedding {Count} new titles failed; ranking on the existing vectors only.", missing.Count);
            return;
        }

        // The provider's answer is assumed row-aligned to the texts, and it is not contractually
        // guaranteed to be: a truncated or partially rate-limited response comes back SHORT, and
        // this loop indexed straight off the end of it — an IndexOutOfRangeException that the
        // turn's catch-all then reported as "search hit a snag" with zero results, having already
        // discovered and hydrated a full pool. Store what arrived; the rest sit this turn out.
        if (vectors.Length != missing.Count)
        {
            logger.LogWarning(
                "Ask Reel: the embedding provider answered with {Returned} vectors for {Requested} titles; "
                + "storing the ones that arrived and ranking on those.",
                vectors.Length, missing.Count);
        }

        var usable = Math.Min(missing.Count, vectors.Length);
        for (var i = 0; i < usable; i++)
        {
            if (vectors[i] is not { Length: > 0 })
            {
                continue;
            }

            db.TitleEmbeddings.Add(new TitleEmbedding
            {
                TitleId = missing[i].Id,
                Embedding = new Vector(vectors[i]),
                EmbeddingModel = EmbeddingText.Model,
                SourceTextHash = EmbeddingText.Hash(texts[i]),
                CreatedAt = DateTime.UtcNow,
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent search embedded some of these first (TitleEmbedding PK = TitleId).
            if (db is DbContext ctx)
            {
                foreach (var entry in ctx.ChangeTracker.Entries<TitleEmbedding>()
                             .Where(e => e.State == EntityState.Added).ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    /// <summary>
    /// Hydrates the slice's titles that have never been refreshed; returns how many it hydrated. The
    /// TMDB round trips are timed as <see cref="AskStageTimings.ScoreHydrate"/> on
    /// <paramref name="timings"/> when there are any.
    /// </summary>
    private async Task<int> HydrateAsync(IReadOnlyList<Guid> sliceIds, CancellationToken ct, AskStageTimings? timings = null)
    {
        var needs = await db.Titles
            .Where(t => sliceIds.Contains(t.Id) && t.LastMetadataRefreshAt == null && t.TmdbId != null)
            .ToListAsync(ct);
        if (needs.Count == 0)
        {
            return 0;
        }

        using var hydrateStage = timings?.Stage(AskStageTimings.ScoreHydrate);
        await hydrator.HydrateBatchAsync(needs, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Concurrent hydration conflict on {Count} live-search titles; continuing.", needs.Count);
        }

        return needs.Count;
    }

    private async Task<Dictionary<Guid, float[]>> LoadVectorsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var rows = await db.TitleEmbeddings.Where(e => ids.Contains(e.TitleId)).ToListAsync(ct);
        return rows.ToDictionary(e => e.TitleId, e => e.Embedding.ToArray());
    }

    /// <summary>The account's embedded preference-tag vectors — for the positive ranking boost.</summary>
    private async Task<List<float[]>> LoadPreferenceTagVectorsAsync(Guid accountId, CancellationToken ct)
    {
        var rows = await db.UserPreferenceTags
            .Where(t => t.AccountId == accountId && t.Embedding != null)
            .Select(t => t.Embedding!)
            .ToListAsync(ct);
        return rows.Select(v => v.ToArray()).ToList();
    }

    /// <summary>Max cosine of a title to any of the user's preference tags (0 when there are none).</summary>
    private static double TagAffinity(float[] titleVec, IReadOnlyList<float[]> tagVecs)
    {
        var best = 0d;
        foreach (var tag in tagVecs)
        {
            best = Math.Max(best, Cosine(titleVec, tag));
        }

        return best;
    }
}
