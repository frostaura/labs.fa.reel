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
/// <b>The genre component answers to the user's own ratings (v5).</b> A <c>Reason = Genre</c>
/// dismissal used to add its recency weight to a genre and stop there, so on an account with a
/// large library the genre map simply saturated: the founder's 44 genre-reason dismissals put
/// drama, comedy, romance, action and science-fiction — the five genres he rates MOST, all at or
/// above his overall mean — at the full 0.6 cap, while unscripted television, which he had barely
/// reacted to, paid nothing, and the feed filled with reality shows. Two bounds now sit on that
/// component, both in <see cref="GenreDemotion"/>: each dismissal is damped by the user's rating
/// volume in that genre (<see cref="VolumeDamping"/>), and a genre their own shrunken affinity
/// SUPPORTS is held to <see cref="SupportedGenreCap"/> rather than <see cref="GenreCap"/>. The
/// per-title components — seen-enough, tone, centroid — are untouched: a dismissal that is about
/// THIS title should keep its full force, and that was never the failure.
///
/// <para><b>The centroid term was checked for the same failure and does not have it</b> (measured
/// on the live account, 2026-09-10, over its 174 embedded not-interested titles against 4,265
/// catalogue candidates). Its mean cosine is 0.505 and its maximum 0.713, so only 10% of candidates
/// clear the 0.6 gate at all and the worst penalty it can levy is 0.21, a third of the genre term's
/// ceiling. Crucially it does not track dismissal VOLUME: the centroid is a weighted MEAN, so 44
/// drama dismissals make it a more coherent drama centroid rather than a larger penalty, and the
/// per-genre gate rates it produces run horror 31% / thriller 27% / drama 11% / comedy 4% — highest
/// on the two genres the founder actually rates below his mean. It is discriminating within genres,
/// which is what it was built to do. Left alone.</para>
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
    /// <summary>
    /// How far back a dislike still shapes the profile. Old reactions age out entirely.
    ///
    /// <para><b>Reactions only.</b> This is the 180 people find when they go looking for one, and it
    /// is the answer to a different question from the one usually being asked. Ratings and watch
    /// history are NEVER windowed — see <see cref="Ml.FeatureVectorBuilder.BuildTasteStateAsync"/>,
    /// which reads every rating ever made — and neither are training labels. A Not-Interested or
    /// Dropped reaction is a statement about a moment ("not tonight"), so it expires; a rating is a
    /// verdict, so it does not.</para>
    /// </summary>
    public const int WindowDays = 180;

    /// <summary>e-folding time of the recency decay: <c>w = exp(-ageDays / 90)</c>.</summary>
    public const double RecencyDecayDays = 90d;

    /// <summary>How many recent dislikes ride the Ask Reel prompts (top-N by recency).</summary>
    public const int PromptCount = 8;

    /// <summary>The hard ceiling on the total demotion — a nudge system, never a takeover.</summary>
    public const double TotalCap = 1.5;

    // Component scales and caps. Genre: 0.3 rating points per unit of matched recency-weighted
    // dislike, VOLUME-NORMALISED and AFFINITY-CAPPED (see GenreDemotion). Tone: a flat 0.3,
    // recency-weighted. Seen-enough: up to 0.9 for a near-identical plot, gated so unrelated titles
    // pay nothing. General centroid: a mild 0.3, gated high because the centroid of mixed dislikes
    // is diffuse.
    private const double GenreScale = 0.3;
    private const double GenreCap = 0.6;
    private const double TonePenalty = 0.3;
    private const double SeenEnoughScale = 0.9;
    private const double SeenEnoughGate = 0.5;
    private const double GeneralScale = 0.3;
    private const double GeneralGate = 0.6;

    /// <summary>
    /// The ceiling on a genre-reason demotion for a genre the user's OWN ratings support — a genre
    /// whose shrunken affinity sits at or above their overall mean. A quarter of the unsupported
    /// cap, which is the point: it can still break a tie, and it can no longer decide a page.
    /// </summary>
    public const double SupportedGenreCap = 0.15;

    /// <summary>
    /// The rating count at which volume normalisation reaches its first e-fold — the "20" in
    /// <c>weight / (1 + log(1 + N/20))</c>. See <see cref="VolumeDamping"/>.
    /// </summary>
    public const double GenreVolumeScale = 20d;

    /// <summary>
    /// The shrinkage constant for the per-genre affinity the supported cap is decided on. The same
    /// k the feature layer uses (<c>TasteMath.ShrinkageK</c>), restated here rather than referenced
    /// because <c>Ranking</c> must not take a dependency on <c>Ml</c> in this direction; the two are
    /// pinned equal by test.
    /// </summary>
    public const double GenreAffinityShrinkageK = 3d;

    private static readonly TimeSpan ProfileTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// One genre, as the user's OWN ratings describe it: how many titles in it they have rated, and
    /// their shrunken mean rating for it. This is the evidence the genre demotion is measured
    /// against — a "wrong genre" dismissal is a claim about a genre, and a claim about a genre the
    /// user has rated 744 times has to answer to those 744 ratings.
    /// </summary>
    public readonly record struct GenreEvidence(int RatingCount, double Affinity);

    /// <summary>
    /// The account's per-genre rating evidence, plus their overall mean. <see cref="None"/> is the
    /// honest empty state — no ratings at all, so no genre is "supported" and the demotion falls
    /// back to its unsupported cap, which is exactly the pre-v5 behaviour for a brand new account.
    /// </summary>
    public sealed record GenreTaste(IReadOnlyDictionary<string, GenreEvidence> ByGenre, double UserMean)
    {
        public static GenreTaste None { get; } = new(
            new Dictionary<string, GenreEvidence>(StringComparer.OrdinalIgnoreCase), 0d);

        /// <summary>
        /// The user's evidence for one genre. A genre they have never rated returns zero ratings —
        /// NOT their mean affinity — because "no evidence" must never read as "supported".
        /// </summary>
        public GenreEvidence For(string genre) =>
            ByGenre.TryGetValue(genre, out var evidence) ? evidence : new GenreEvidence(0, 0d);

        /// <summary>
        /// True when the user's own ratings in this genre argue AGAINST demoting it: they have rated
        /// it at all, and their shrunken affinity for it sits at or above their overall mean.
        /// </summary>
        public bool Supports(string genre)
        {
            var evidence = For(genre);
            return evidence.RatingCount > 0 && evidence.Affinity >= UserMean;
        }
    }

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
        string Version = "none",
        /// <summary>
        /// The account's own per-genre rating evidence, which the genre-reason demotion is bounded
        /// by (see <see cref="GenreDemotion"/>). Defaults to <see cref="GenreTaste.None"/> so a
        /// profile built without it behaves exactly as it did before v5.
        /// </summary>
        GenreTaste? UserGenres = null)
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
            ComputeVersion(newestPerTitle.Select(r => (r.TitleId, r.Kind, r.Reason))),
            await BuildGenreTasteAsync(accountId, genreWeights.Keys, ct));
    }

    /// <summary>
    /// The account's own rating evidence for the genres their dismissals touch — count and shrunken
    /// affinity per genre, plus their overall mean. Loaded ONLY for the genres that actually carry a
    /// dislike weight, because those are the only ones <see cref="GenreDemotion"/> can consult, and
    /// an account with no genre-reason dismissals should not pay for a scan of its whole library.
    ///
    /// <para>The mean is over ALL movie/show-level ratings, not just the genres in question — it is
    /// the same "your overall mean" the affinity is shrunk toward and compared against, so shrinking
    /// toward a partial mean would make the comparison circular.</para>
    /// </summary>
    private async Task<GenreTaste> BuildGenreTasteAsync(
        Guid accountId, IEnumerable<string> genres, CancellationToken ct)
    {
        var wanted = genres.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return GenreTaste.None;
        }

        // One row per rated title (the unique index allows one movie/show-level rating per title,
        // but a defensive max keeps a duplicate from double-counting into the genre pools).
        var rated = await db.UserRatings
            .Where(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .Select(r => new { r.TitleId, r.Rating })
            .ToListAsync(ct);
        if (rated.Count == 0)
        {
            return GenreTaste.None;
        }

        var perTitle = rated
            .GroupBy(r => r.TitleId)
            .ToDictionary(g => g.Key, g => (double)g.Max(r => r.Rating));
        var userMean = perTitle.Values.Average();

        var ratedIds = perTitle.Keys.ToList();
        var titleGenres = await db.Titles
            .Where(t => ratedIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Genres })
            .ToListAsync(ct);

        var pools = new Dictionary<string, (int Count, double Sum)>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in titleGenres)
        {
            if (!perTitle.TryGetValue(title.Id, out var rating))
            {
                continue;
            }

            foreach (var genre in title.Genres)
            {
                if (!wanted.Contains(genre))
                {
                    continue;
                }

                var pool = pools.GetValueOrDefault(genre);
                pools[genre] = (pool.Count + 1, pool.Sum + rating);
            }
        }

        var byGenre = pools.ToDictionary(
            kv => kv.Key,
            kv => new GenreEvidence(kv.Value.Count, ShrunkenGenreAffinity(kv.Value.Sum, kv.Value.Count, userMean)),
            StringComparer.OrdinalIgnoreCase);

        return new GenreTaste(byGenre, userMean);
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

        var total = GenreDemotion(genres, profile);

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

    /// <summary>
    /// Volume damping for a genre-reason dismissal: <c>1 / (1 + log(1 + N / 20))</c>, where N is how
    /// many titles in that genre the user has rated. The shape, and why this one:
    ///
    /// <list type="bullet">
    /// <item>It is <b>1.0 at N = 0</b> — an account that has never rated the genre is not damped at
    /// all, so a new user's dismissals still steer their feed at full strength. This is the case the
    /// original design was built for and it is left exactly as it was.</item>
    /// <item>It is <b>logarithmic, not linear</b>: 1/1.69 at N = 20, 1/2.40 at N = 100, 1/3.31 at
    /// N = 300, 1/4.64 at N = 744. Volume never cancels a dismissal outright — it cannot reach zero
    /// — it only makes each one worth less against a larger body of evidence, which is the same
    /// arithmetic as the empirical-Bayes shrinkage every affinity in this system already uses.</item>
    /// <item>The <b>20</b> is the first e-fold: below about twenty ratings in a genre the user has
    /// not really told us anything about it, so the damping stays near 1 and their dismissals are
    /// taken at face value.</item>
    /// </list>
    ///
    /// <para><b>Why it exists.</b> On the founder's live account, 44 Genre-reason dismissals put
    /// drama at a recency-weighted dislike weight of 13.6 — against 744 drama ratings averaging
    /// 6.83, above his overall mean of 6.74. Comedy, romance, action and science-fiction were the
    /// same story. Every scripted title therefore carried the full 0.6 genre demotion while
    /// unscripted television, which he has barely reacted to, carried nothing at all — and the feed
    /// filled with reality shows. A "wrong genre" dismissal on a drama, from someone with 744 drama
    /// ratings, is evidence about that TITLE, not about drama.</para>
    /// </summary>
    public static double VolumeDamping(int genreRatingCount) =>
        1d / (1d + Math.Log(1d + (Math.Max(0, genreRatingCount) / GenreVolumeScale)));

    /// <summary>
    /// The genre-reason component of the demotion: per-genre, volume-damped, capped by what the
    /// user's OWN affinity in that genre supports, then summed and held under the global
    /// <see cref="GenreCap"/>.
    ///
    /// <para>The per-genre cap is the half that fixes the founder's page. Volume damping alone does
    /// not: 13.6 damped by 744 drama ratings is still 2.9, and 2.9 × 0.3 is still over the 0.6 cap.
    /// So the demotion for a genre the user's ratings SUPPORT — they have rated it, and their
    /// shrunken affinity for it is at or above their overall mean — is additionally held to
    /// <see cref="SupportedGenreCap"/>. It can nudge; it can no longer decide a page.</para>
    ///
    /// <para><b>The cap is per-genre, not per-title, and that is deliberate.</b> A title tagged
    /// [drama, horror] for a user who loves drama and dislikes horror pays the supported cap on
    /// drama and the full cap on horror. Taking the title-level minimum would let one loved genre
    /// launder a genuinely disliked one; taking the maximum would mean a loved genre never protects
    /// anything. Each genre answers for itself, and the sum is bounded as it always was.</para>
    ///
    /// <para><b>This deliberately differs from <see cref="GenreOverlap"/> now,</b> which is still
    /// the raw unbounded sum and still feeds the schema-v3 <c>dislikedGenreOverlap</c> FEATURE. The
    /// feature is raw evidence the model weights for itself — and since schema v5 it can condition
    /// that weight on <c>userGenreAffinity</c> and the genre one-hots, which is a better instrument
    /// than any constant here. This function is a hand-set serving rule, and a hand-set rule is
    /// exactly the thing that must not out-shout the user's own ratings.</para>
    /// </summary>
    public static double GenreDemotion(IReadOnlyList<string> genres, Profile profile)
    {
        if (genres.Count == 0 || profile.GenreWeights.Count == 0)
        {
            return 0d;
        }

        var taste = profile.UserGenres ?? GenreTaste.None;
        var total = 0d;
        foreach (var genre in genres)
        {
            var weight = profile.GenreWeights.GetValueOrDefault(genre);
            if (weight <= 0d)
            {
                continue;
            }

            var evidence = taste.For(genre);
            var damped = weight * VolumeDamping(evidence.RatingCount) * GenreScale;
            total += Math.Min(taste.Supports(genre) ? SupportedGenreCap : GenreCap, damped);
        }

        return Math.Min(GenreCap, total);
    }

    /// <summary>
    /// Shrunken per-genre affinity, the quantity <see cref="GenreTaste.Supports"/> compares against
    /// the user's mean: <c>(Σr + k·mean) / (n + k)</c>. The same empirical-Bayes form — and the same
    /// k — as the feature layer, so "his affinity for drama" means one thing across the system.
    /// </summary>
    public static double ShrunkenGenreAffinity(double ratingSum, int ratingCount, double userMean) =>
        ratingCount <= 0
            ? userMean
            : (ratingSum + (GenreAffinityShrinkageK * userMean)) / (ratingCount + GenreAffinityShrinkageK);

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
