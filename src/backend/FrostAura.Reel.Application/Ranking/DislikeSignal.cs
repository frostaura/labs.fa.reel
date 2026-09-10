using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// The dislike profile and its deterministic serving-time demotion — the direct answer to the
/// founder's complaint that a Not-Interested / Dropped reaction only removed that ONE title while
/// its <see cref="ReactionReason"/> was never read by any ranking code. The profile aggregates the
/// last <see cref="WindowDays"/> days of live (unrevoked) dislikes into reason-specific signals:
/// a disliked-genre weight map (reason = Genre), a disliked-tone set (reason = Tone, closed-vocab
/// only), the embeddings of seen-enough titles (reason = SeenEnough, the strongest similarity
/// signal), and a mild recency-weighted centroid over every disliked title's embedding. Each
/// reaction decays with <c>exp(-ageDays/90)</c>, so a dislike from last week outweighs one from
/// five months ago.
///
/// <see cref="DemotionFor"/> turns a candidate's genres / tone / embedding into a 0–1.5
/// rating-point penalty that serving subtracts AFTER base + residual — it works immediately,
/// without retraining, on every surface that scores through <c>OnDemandScorer</c>, the feed build,
/// or the paginator. The schema-v3 features (<c>simNotInterestedCentroid</c> /
/// <c>dislikedGenreOverlap</c>) are the slower, learned half of the same signal and reuse this
/// class's pure math so the two can never drift apart.
/// </summary>
public sealed class DislikeSignal(IReelDbContext db, IMemoryCache cache)
{
    /// <summary>How far back a dislike still shapes the profile. Old reactions age out entirely.</summary>
    public const int WindowDays = 180;

    /// <summary>e-folding time of the recency decay: <c>w = exp(-ageDays / 90)</c>.</summary>
    public const double RecencyDecayDays = 90d;

    /// <summary>How many recent dislikes ride the Ask Reel prompts (top-N by recency).</summary>
    public const int PromptCount = 8;

    /// <summary>The hard ceiling on the total demotion — a nudge system, never a takeover.</summary>
    public const double TotalCap = 1.5;

    // Component scales and caps. Genre: 0.3 rating points per unit of matched recency-weighted
    // dislike (two fresh same-genre dislikes reach the 0.6 cap). Tone: a flat 0.3, recency-weighted.
    // Seen-enough: up to 0.9 for a near-identical plot, gated so unrelated titles pay nothing.
    // General centroid: a mild 0.3, gated high because the centroid of mixed dislikes is diffuse.
    private const double GenreScale = 0.3;
    private const double GenreCap = 0.6;
    private const double TonePenalty = 0.3;
    private const double SeenEnoughScale = 0.9;
    private const double SeenEnoughGate = 0.5;
    private const double GeneralScale = 0.3;
    private const double GeneralGate = 0.6;

    private static readonly TimeSpan ProfileTtl = TimeSpan.FromMinutes(10);

    /// <summary>One recent dislike, for the prompt line and diagnostics. Weight is the recency decay.</summary>
    public sealed record DislikedTitle(
        Guid TitleId, string Name, ReactionKind Kind, ReactionReason? Reason, DateTime CreatedAt, double Weight);

    /// <summary>
    /// The aggregated dislike profile for one account. Pure data — <see cref="DemotionFor"/> is a
    /// pure function over it, so every scale/cap/gate is unit-testable without a database.
    /// </summary>
    public sealed record Profile(
        IReadOnlyList<DislikedTitle> Recent,
        IReadOnlyDictionary<string, double> GenreWeights,
        IReadOnlyDictionary<string, double> ToneWeights,
        IReadOnlyList<(float[] Embedding, double Weight)> SeenEnoughEmbeddings,
        float[]? DislikeCentroid,
        /// <summary>
        /// The dislike-centroid version — a short deterministic fingerprint of WHICH titles the
        /// account has declined and why, ignoring the continuous recency decay. It is the part of
        /// this profile that belongs in a copy cache key: the lens's "what might bug you" line is
        /// grounded in these reactions, so a note written before a dismissal must not survive it,
        /// while the decay drifting by a percent overnight must not invalidate anything.
        /// </summary>
        string Version = "none")
    {
        /// <summary>
        /// The version of a profile with nothing in it — a real value, not a placeholder. The
        /// literal is repeated in the parameter default above because a primary-constructor
        /// default cannot reference a member of the type it is declaring.
        /// </summary>
        public const string EmptyVersion = "none";

        public static Profile Empty { get; } = new(
            [],
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(StringComparer.Ordinal),
            [],
            null);

        public bool IsEmpty => Recent.Count == 0;
    }

    /// <summary>
    /// The dislike-centroid version for a set of live reactions: SHA-256 over the ordered
    /// (title, kind, reason) triples, truncated to 12 hex characters. Ordered by title id so two
    /// reads of the same reactions in a different row order agree, and deliberately carrying no
    /// timestamp so the value is stable until the user actually changes their mind.
    /// </summary>
    internal static string ComputeVersion(IEnumerable<(Guid TitleId, ReactionKind Kind, ReactionReason? Reason)> reactions)
    {
        var ordered = reactions.OrderBy(r => r.TitleId).ToList();
        if (ordered.Count == 0)
        {
            return Profile.EmptyVersion;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var (titleId, kind, reason) in ordered)
        {
            sb.Append(titleId.ToString("N")).Append(':').Append(kind).Append(':').Append(reason?.ToString() ?? "-").Append('|');
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash)[..12];
    }

    /// <summary>
    /// The cached profile (~10 min TTL). Reaction create/revoke evicts via
    /// <see cref="Invalidate"/>, so the cache never outlives the user changing their mind.
    /// </summary>
    public async Task<Profile> GetProfileAsync(Guid accountId, CancellationToken ct) =>
        await cache.GetOrCreateAsync(CacheKey(accountId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ProfileTtl;
            return await BuildProfileAsync(accountId, ct);
        }) ?? await BuildProfileAsync(accountId, ct);

    /// <summary>Drops the cached profile so the next score sees the reaction that just landed.</summary>
    public void Invalidate(Guid accountId) => cache.Remove(CacheKey(accountId));

    /// <summary>
    /// Builds the profile from live (RevokedAt == null) NotInterested + Dropped reactions of the
    /// last <see cref="WindowDays"/> days, joined to their titles' genres, closed-vocab tone and
    /// plot embedding where present. A title reacted to twice (e.g. dropped then not-interested)
    /// counts once, by its newest reaction — double-counting would double its centroid pull.
    /// </summary>
    public async Task<Profile> BuildProfileAsync(Guid accountId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var since = now.AddDays(-WindowDays);
        var reactions = await db.UserTitleReactions
            .Where(r => r.AccountId == accountId && r.RevokedAt == null
                && (r.Kind == ReactionKind.NotInterested || r.Kind == ReactionKind.Dropped)
                && r.CreatedAt >= since)
            .Select(r => new { r.TitleId, r.Kind, r.Reason, r.CreatedAt })
            .ToListAsync(ct);
        if (reactions.Count == 0)
        {
            return Profile.Empty;
        }

        var newestPerTitle = reactions
            .GroupBy(r => r.TitleId)
            .Select(g => g.OrderByDescending(r => r.CreatedAt).First())
            .ToList();
        var titleIds = newestPerTitle.Select(r => r.TitleId).ToList();

        var titles = await db.Titles
            .Where(t => titleIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name, t.Genres })
            .ToDictionaryAsync(t => t.Id, ct);
        // Tone is only trusted when it passes the closed vocabulary — rows extracted before
        // enforcement carry polluted free-text labels (see AttributeVocabulary).
        var tones = await db.TitleAttributes
            .Where(a => titleIds.Contains(a.TitleId)
                && a.Status == AttributeExtractionStatus.Done && a.Tone != null)
            .Select(a => new { a.TitleId, a.Tone })
            .ToDictionaryAsync(a => a.TitleId, a => a.Tone, ct);
        var embeddings = (await db.TitleEmbeddings
                .Where(e => titleIds.Contains(e.TitleId))
                .Select(e => new { e.TitleId, e.Embedding })
                .ToListAsync(ct))
            .ToDictionary(e => e.TitleId, e => e.Embedding.ToArray());

        var recent = new List<DislikedTitle>(newestPerTitle.Count);
        var genreWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var toneWeights = new Dictionary<string, double>(StringComparer.Ordinal);
        var seenEnough = new List<(float[] Embedding, double Weight)>();
        double[]? centroidSum = null;
        var centroidWeight = 0d;

        foreach (var reaction in newestPerTitle)
        {
            if (!titles.TryGetValue(reaction.TitleId, out var title))
            {
                continue;
            }

            var weight = RecencyWeight(reaction.CreatedAt, now);
            recent.Add(new DislikedTitle(
                reaction.TitleId, title.Name, reaction.Kind, reaction.Reason, reaction.CreatedAt, weight));

            if (reaction.Reason == ReactionReason.Genre)
            {
                foreach (var genre in title.Genres)
                {
                    genreWeights[genre] = genreWeights.GetValueOrDefault(genre) + weight;
                }
            }

            if (reaction.Reason == ReactionReason.Tone
                && tones.TryGetValue(reaction.TitleId, out var tone)
                && AttributeVocabulary.IsClosedVocabTone(tone))
            {
                toneWeights[tone!] = Math.Max(toneWeights.GetValueOrDefault(tone!), weight);
            }

            if (embeddings.TryGetValue(reaction.TitleId, out var embedding))
            {
                if (reaction.Reason == ReactionReason.SeenEnough)
                {
                    seenEnough.Add((embedding, weight));
                }

                centroidSum ??= new double[embedding.Length];
                if (centroidSum.Length == embedding.Length)
                {
                    for (var i = 0; i < embedding.Length; i++)
                    {
                        centroidSum[i] += weight * embedding[i];
                    }

                    centroidWeight += weight;
                }
            }
        }

        float[]? centroid = null;
        if (centroidSum is not null && centroidWeight > 0d)
        {
            centroid = new float[centroidSum.Length];
            for (var i = 0; i < centroidSum.Length; i++)
            {
                centroid[i] = (float)(centroidSum[i] / centroidWeight);
            }
        }

        return new Profile(
            recent.OrderByDescending(d => d.CreatedAt).ToList(),
            genreWeights,
            toneWeights,
            seenEnough,
            centroid,
            ComputeVersion(newestPerTitle.Select(r => (r.TitleId, r.Kind, r.Reason))));
    }

    /// <summary>
    /// The 0–<see cref="TotalCap"/> rating-point demotion for one candidate. Pure: genres, tone and
    /// embedding are the candidate's, everything else comes from the profile. Null embedding ⇒ the
    /// two similarity components contribute nothing (never a penalty for missing data). Every
    /// component is recency-weighted — genre and seen-enough carry per-reaction weights, tone the
    /// max weight of its reactions, and the centroid is itself a recency-weighted mean.
    /// </summary>
    public static double DemotionFor(
        IReadOnlyList<string> genres, string? tone, float[]? embedding, Profile profile)
    {
        if (profile.IsEmpty)
        {
            return 0d;
        }

        var total = Math.Min(GenreCap, GenreOverlap(genres, profile.GenreWeights) * GenreScale);

        if (tone is not null
            && AttributeVocabulary.IsClosedVocabTone(tone)
            && profile.ToneWeights.TryGetValue(tone, out var toneWeight))
        {
            total += TonePenalty * toneWeight;
        }

        if (embedding is not null)
        {
            var seenEnoughBest = 0d;
            foreach (var (dislikedEmbedding, weight) in profile.SeenEnoughEmbeddings)
            {
                var cosine = Cosine(embedding, dislikedEmbedding);
                if (cosine > SeenEnoughGate)
                {
                    seenEnoughBest = Math.Max(seenEnoughBest, cosine * SeenEnoughScale * weight);
                }
            }

            total += seenEnoughBest;

            if (profile.DislikeCentroid is not null)
            {
                var centroidCosine = Cosine(embedding, profile.DislikeCentroid);
                if (centroidCosine > GeneralGate)
                {
                    total += centroidCosine * GeneralScale;
                }
            }
        }

        return Math.Min(TotalCap, total);
    }

    /// <summary>
    /// Batch demotions for a set of candidate titles — loads genres, closed-vocab tones and
    /// embeddings in three queries and folds them through <see cref="DemotionFor"/>. All-zeros for
    /// an empty profile without touching the database.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, double>> DemotionsForAsync(
        Profile profile, IReadOnlyCollection<Guid> titleIds, CancellationToken ct)
    {
        var ids = titleIds.Distinct().ToList();
        var result = ids.ToDictionary(id => id, _ => 0d);
        if (profile.IsEmpty || ids.Count == 0)
        {
            return result;
        }

        var genres = await db.Titles
            .Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.Genres })
            .ToDictionaryAsync(t => t.Id, t => t.Genres, ct);
        var tones = await db.TitleAttributes
            .Where(a => ids.Contains(a.TitleId)
                && a.Status == AttributeExtractionStatus.Done && a.Tone != null)
            .Select(a => new { a.TitleId, a.Tone })
            .ToDictionaryAsync(a => a.TitleId, a => a.Tone, ct);
        var embeddings = (await db.TitleEmbeddings
                .Where(e => ids.Contains(e.TitleId))
                .Select(e => new { e.TitleId, e.Embedding })
                .ToListAsync(ct))
            .ToDictionary(e => e.TitleId, e => e.Embedding.ToArray());

        foreach (var id in ids)
        {
            result[id] = DemotionFor(
                genres.GetValueOrDefault(id) ?? [],
                tones.GetValueOrDefault(id),
                embeddings.GetValueOrDefault(id),
                profile);
        }

        return result;
    }

    /// <summary>
    /// Subtracts the demotion from a predicted rating, floored so a penalty can drag a score down
    /// but never below 1 — and never RAISE one that already sat under 1. Zero demotion is an exact
    /// no-op, which is the no-regression guarantee for accounts with no dislikes.
    /// </summary>
    public static decimal ApplyDemotion(decimal predicted, double demotion)
    {
        if (demotion <= 0d)
        {
            return predicted;
        }

        return Math.Max(Math.Min(predicted, 1m), predicted - (decimal)demotion);
    }

    /// <summary>
    /// Sum of the profile's genre weights over the candidate's genres — the raw overlap the
    /// demotion scales and caps, and (unscaled) the schema-v3 <c>dislikedGenreOverlap</c> feature.
    /// Shared so the served penalty and the trained feature can never disagree about the math.
    /// </summary>
    public static double GenreOverlap(
        IReadOnlyList<string> genres, IReadOnlyDictionary<string, double> genreWeights)
    {
        if (genres.Count == 0 || genreWeights.Count == 0)
        {
            return 0d;
        }

        var total = 0d;
        foreach (var genre in genres)
        {
            total += genreWeights.GetValueOrDefault(genre);
        }

        return total;
    }

    /// <summary>The recency decay: <c>exp(-ageDays / 90)</c>, clamped so future timestamps read as fresh.</summary>
    public static double RecencyWeight(DateTime createdAt, DateTime asOf) =>
        Math.Exp(-Math.Max(0d, (asOf - createdAt).TotalDays) / RecencyDecayDays);

    /// <summary>
    /// The compact "Recently declined" line for the Ask Reel prompts — top
    /// <see cref="PromptCount"/> dislikes by recency, each with its reason. Null when there are
    /// none, so the taste summary stays untouched for accounts without dislikes.
    /// </summary>
    public static string? DeclinedLine(Profile profile)
    {
        if (profile.IsEmpty)
        {
            return null;
        }

        var parts = profile.Recent
            .Take(PromptCount)
            .Select(d => $"{d.Name} ({ReasonLabel(d)})");
        return "Recently declined, do not recommend anything too similar: " + string.Join(", ", parts);
    }

    private static string ReasonLabel(DislikedTitle disliked) => disliked.Reason switch
    {
        ReactionReason.Genre => "wrong genre",
        ReactionReason.SeenEnough => "seen enough of this kind",
        ReactionReason.Tone => "wrong tone",
        ReactionReason.NotForMe => "not for me",
        _ => disliked.Kind == ReactionKind.Dropped ? "dropped" : "not interested",
    };

    private static string CacheKey(Guid accountId) => $"dislikeprofile:{accountId}";

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return 0d;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na <= 0 || nb <= 0 ? 0d : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
