using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// Assembles the <see cref="TasteFacts"/> the taste narrative is written from — and, because the
/// same object is persisted beside the text, the evidence a later reader checks that text against.
///
/// <para>Everything here already existed and was already being computed; none of it had a reader
/// that could speak. The genre affinities are the shrunken means the model itself scores on (not
/// the rating COUNTS the old twenty-token summary ordered by, which described what the account had
/// watched most rather than what it rated highest); the drift is the same recency-weighted
/// estimate the feature vector carries; the people come from <see cref="VerifiedPeople"/>'s ≥2
/// rated-titles bar, the identical rule the why-line templater is held to; the texture is the mean
/// of the seven extracted scalars over the titles the account actually loved.</para>
///
/// <para><b>Excluded genres are subtracted.</b> A genre the eligibility layer refuses to show is
/// not a favourite, and the old summary would happily open with one — asking the model to write
/// warmly about a taste the surface forbids.</para>
/// </summary>
public sealed class TasteFactsBuilder(IReelDbContext db, DislikeSignal dislikes)
{
    /// <summary>The affinity list length. Matches <see cref="TasteProfileHash.TopN"/> — the hash reads the same list.</summary>
    public const int TopGenres = TasteProfileHash.TopN;

    /// <summary>Loved anchors named to the writer. More than this and the prose starts listing.</summary>
    public const int LovedAnchors = 8;

    /// <summary>Verified people named to the writer, best affinity first.</summary>
    public const int TopPeople = 6;

    /// <summary>A genre needs this many ratings before a drift figure means anything.</summary>
    private const int MinDriftObservations = 4;

    /// <summary>Drift smaller than this is formula noise, not a change of taste.</summary>
    private const decimal MinDriftDelta = 0.3m;

    /// <summary>"Loved" for the texture aggregate — the same "loved, not tolerated" bar the eval harness uses.</summary>
    private const short LovedRatingFloor = 8;

    /// <summary>The facts, plus the fingerprint they hash to and the loved ids that fed it.</summary>
    public sealed record Result(
        TasteFacts Facts, string ProfileHash, string ProfileFingerprint, IReadOnlyList<Guid> TopLovedTitleIds);

    public async Task<Result> BuildAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, DateTime asOf, CancellationToken ct)
    {
        var excludedGenres = (await db.ContentFilters
                .Where(f => f.AccountId == accountId && f.Kind == FilterKind.ExcludeGenre)
                .Select(f => f.Value)
                .ToListAsync(ct))
            .Select(v => v.Trim().ToLowerInvariant())
            .Where(v => v.Length > 0)
            .ToHashSet();

        var genreStats = taste.GenreRatings
            .Where(kv => !excludedGenres.Contains(kv.Key))
            .Select(kv => new
            {
                Genre = kv.Key,
                Affinity = TasteMath.ShrunkenMean(kv.Value.Select(r => r.Rating).ToList(), taste.UserMean),
                Recent = TasteMath.RecencyWeightedMean(kv.Value, asOf, taste.UserMean),
                kv.Value.Count,
            })
            // Count then name as tie-breakers: shrunken means collide readily on small pools, and a
            // fact list that reshuffles between runs churns the profile hash for nothing.
            .OrderByDescending(g => g.Affinity)
            .ThenByDescending(g => g.Count)
            .ThenBy(g => g.Genre, StringComparer.Ordinal)
            .ToList();

        var topGenres = genreStats
            .Take(TopGenres)
            .Select(g => new GenreFact(g.Genre, Math.Round(g.Affinity, 2), g.Count))
            .ToList();

        var drifting = genreStats
            .Where(g => g.Count >= MinDriftObservations && Math.Abs(g.Recent - g.Affinity) >= MinDriftDelta)
            .OrderByDescending(g => g.Recent - g.Affinity)
            .Take(3)
            .Select(g => new DriftFact(g.Genre, Math.Round(g.Recent - g.Affinity, 2)))
            .ToList();

        var lovedRank = taste.TopLovedTitleIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);
        var lovedIds = taste.TopLovedTitleIds.Take(LovedAnchors).ToList();
        var lovedRatings = await db.UserRatings
            .Where(r => r.AccountId == accountId && lovedIds.Contains(r.TitleId)
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .Select(r => new { r.TitleId, r.Rating })
            .ToDictionaryAsync(r => r.TitleId, r => r.Rating, ct);
        var loved = (await db.Titles
                .Where(t => lovedIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Name, t.Year })
                .ToListAsync(ct))
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .OrderBy(t => lovedRank[t.Id])
            .Select(t => new LovedFact(t.Name, t.Year, lovedRatings.GetValueOrDefault(t.Id)))
            .ToList();

        var dislikeProfile = await dislikes.GetProfileAsync(accountId, ct);
        var people = await VerifiedPeopleAsync(taste, ct);
        var texture = await TextureAsync(accountId, ct);

        var contrarian = new ContrarianFact(
            Math.Round(taste.OverallContrarianOffset, 2),
            topGenres
                .Where(g => taste.GenreContrarianOffset.ContainsKey(g.Genre))
                .Select(g => new GenreOffset(g.Genre, Math.Round(taste.GenreContrarianOffset[g.Genre], 2)))
                .ToList());

        var preferenceTags = await db.UserPreferenceTags
            .Where(t => t.AccountId == accountId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => t.Text)
            .ToListAsync(ct);

        var ratingsCount = await db.UserRatings
            .CountAsync(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show), ct);

        var facts = new TasteFacts(
            Math.Round(taste.UserMean, 2),
            ratingsCount,
            Math.Round(taste.MovieRatedShare, 2),
            topGenres,
            drifting,
            loved,
            DislikeSignal.DeclinedLine(dislikeProfile),
            people,
            texture,
            contrarian,
            preferenceTags);

        // The account's last stored fingerprint is the hysteresis anchor — see TasteProfileHash.
        // Read here, once, so every caller (the feed build, the endpoints, both jobs) computes the
        // SAME hash. Two callers anchoring differently would key the same taste two ways and halve
        // the cache hit rate silently.
        var previous = await db.AccountTasteNarratives
            .Where(n => n.AccountId == accountId)
            .OrderByDescending(n => n.Version)
            .Select(n => n.ProfileFingerprint)
            .FirstOrDefaultAsync(ct);

        var fingerprint = TasteProfileHash.Compute(
            facts, taste.TopLovedTitleIds, dislikeProfile.Version, previous);

        return new Result(facts, fingerprint.Hash, fingerprint.Components, taste.TopLovedTitleIds);
    }

    /// <summary>
    /// The account's taste-verified people, best affinity first. Same ≥2-rated-titles bar as
    /// <see cref="VerifiedPeople"/> — a person seen once is a coincidence, and the narrative must
    /// not present one as a preference. The role is the one the person most often holds in this
    /// account's rated library, so a director is described as a director.
    /// </summary>
    private async Task<List<PersonFact>> VerifiedPeopleAsync(FeatureVectorBuilder.TasteState taste, CancellationToken ct)
    {
        var eligible = taste.PersonRatings
            .Where(kv => kv.Value.Count >= VerifiedPeople.MinRatedTitles)
            .Select(kv => new
            {
                PersonId = kv.Key,
                Average = TasteMath.ShrunkenMean(kv.Value, taste.UserMean),
                kv.Value.Count,
            })
            .OrderByDescending(p => p.Average)
            .ThenByDescending(p => p.Count)
            .Take(TopPeople * 3)
            .ToList();
        if (eligible.Count == 0)
        {
            return [];
        }

        var ids = eligible.Select(p => p.PersonId).ToList();
        var names = await db.Persons
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var roles = (await db.TitleCredits
                .Where(c => ids.Contains(c.PersonId))
                .Select(c => new { c.PersonId, c.Role })
                .ToListAsync(ct))
            .GroupBy(c => c.PersonId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(c => c.Role)
                    .OrderByDescending(rg => rg.Count())
                    .ThenBy(rg => rg.Key)
                    .First().Key);

        return eligible
            .Where(p => names.ContainsKey(p.PersonId) && !string.IsNullOrWhiteSpace(names[p.PersonId]))
            .Select(p => new PersonFact(
                names[p.PersonId],
                roles.TryGetValue(p.PersonId, out var role) ? role.ToString() : nameof(CreditRole.Actor),
                Math.Round(p.Average, 2),
                p.Count))
            .Take(TopPeople)
            .ToList();
    }

    /// <summary>
    /// The mean of the seven extracted scalars over the titles this account rated ≥8 — what the
    /// person's taste feels like, rather than what any one film does. Null when nothing loved has
    /// been enriched yet, which is honest: an average over zero titles is not a neutral 0.5, it is
    /// the absence of an answer, and the extractor's own rule against fabricated scalars applies
    /// with equal force to an aggregate of them.
    /// </summary>
    private async Task<TextureFact?> TextureAsync(Guid accountId, CancellationToken ct)
    {
        var rows = await db.UserRatings
            .Where(r => r.AccountId == accountId && r.Rating >= LovedRatingFloor
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .Join(
                db.TitleAttributes.Where(a => a.Status == AttributeExtractionStatus.Done),
                r => r.TitleId,
                a => a.TitleId,
                (_, a) => new
                {
                    a.Darkness,
                    a.Pacing,
                    a.Complexity,
                    a.EmotionalIntensity,
                    a.Humor,
                    a.Optimism,
                    a.EnsembleVsSolo,
                })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return null;
        }

        return new TextureFact(
            Math.Round(rows.Average(r => r.Darkness), 2),
            Math.Round(rows.Average(r => r.Pacing), 2),
            Math.Round(rows.Average(r => r.Complexity), 2),
            Math.Round(rows.Average(r => r.EmotionalIntensity), 2),
            Math.Round(rows.Average(r => r.Humor), 2),
            Math.Round(rows.Average(r => r.Optimism), 2),
            Math.Round(rows.Average(r => r.EnsembleVsSolo), 2),
            rows.Count);
    }
}
