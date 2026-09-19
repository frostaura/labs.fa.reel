using System.Security.Cryptography;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Blend;

/// <summary>
/// Two-person Blend: share an 8-char code, get the titles BOTH of you would rate highly.
/// Joint eligibility is the intersection of both accounts' eligible sets (each computed
/// unscoped via <see cref="EligibilityQueryBuilder.EligibleTitleIdsUnscopedAsync"/> — used
/// strictly to EXCLUDE titles; no cross-tenant interaction data is ever surfaced), and the
/// joint score is the pessimistic min of the two personal predictions — a pick both models
/// vouch for, not one carried by a single enthusiast.
/// </summary>
public sealed class BlendService(IReelDbContext db, EligibilityQueryBuilder eligibility)
{
    public const int CodeLength = 8;

    /// <summary>Crockford base32 — no I, L, O, U, so codes survive being read aloud.</summary>
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const int MaxCodeAttempts = 5;

    public sealed record JointPick(Title Title, decimal ForYou, decimal ForThem, decimal Joint);

    /// <summary>
    /// Creates or regenerates the account's blend code. Regeneration deliberately invalidates
    /// any previously shared code — the old value is overwritten, so stale links go dead.
    /// </summary>
    public async Task<string> RegenerateCodeAsync(Guid accountId, CancellationToken ct)
    {
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var code = GenerateCode();
            var taken = await db.Accounts.AnyAsync(a => a.BlendCode == code && a.Id != accountId, ct);
            if (taken)
            {
                continue;
            }

            account.BlendCode = code;
            try
            {
                await db.SaveChangesAsync(ct);
                return code;
            }
            catch (DbUpdateException)
            {
                // Lost a race on the unique index. The account stays tracked with its pending
                // change; the next loop iteration assigns a fresh code and saves again.
            }
        }

        throw new InvalidOperationException("Could not allocate a unique blend code.");
    }

    /// <summary>Resolves a blend code to its owner; null when unknown. Case-insensitive.</summary>
    public Task<Account?> FindOwnerByCodeAsync(string code, CancellationToken ct)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return normalized.Length != CodeLength
            ? Task.FromResult<Account?>(null)
            : db.Accounts.FirstOrDefaultAsync(a => a.BlendCode == normalized, ct);
    }

    /// <summary>How many titles are eligible for BOTH accounts — the preview number.</summary>
    public async Task<int> EligibleBothCountAsync(Guid selfAccountId, Guid otherAccountId, CancellationToken ct)
    {
        var joint = await JointEligibleIdsAsync(selfAccountId, otherAccountId, ct);
        return joint.Count;
    }

    /// <summary>
    /// Top <paramref name="take"/> joint picks: eligible for both, scored by both accounts'
    /// Active artifacts (both predictions required), poster required, ordered by min(A, B).
    /// </summary>
    public async Task<IReadOnlyList<JointPick>> JointPicksAsync(
        Guid selfAccountId, Guid otherAccountId, int take, CancellationToken ct)
    {
        var joint = await JointEligibleIdsAsync(selfAccountId, otherAccountId, ct);
        if (joint.Count == 0)
        {
            return [];
        }

        var mine = await ActiveScoresAsync(selfAccountId, ct);
        var theirs = await ActiveScoresAsync(otherAccountId, ct);

        var scored = new List<(Guid TitleId, decimal ForYou, decimal ForThem, decimal Joint)>();
        foreach (var titleId in joint)
        {
            if (mine.TryGetValue(titleId, out var forYou) && theirs.TryGetValue(titleId, out var forThem))
            {
                scored.Add((titleId, forYou, forThem, Math.Min(forYou, forThem)));
            }
        }

        if (scored.Count == 0)
        {
            return [];
        }

        var scoredIds = scored.Select(s => s.TitleId).ToList();
        var titles = await db.Titles
            .Where(t => scoredIds.Contains(t.Id) && t.PosterPath != null && t.TmdbId != null)
            .ToDictionaryAsync(t => t.Id, ct);

        return
        [
            .. scored
                .Where(s => titles.ContainsKey(s.TitleId))
                .OrderByDescending(s => s.Joint)
                .Take(take)
                .Select(s => new JointPick(titles[s.TitleId], s.ForYou, s.ForThem, s.Joint)),
        ];
    }

    private async Task<HashSet<Guid>> JointEligibleIdsAsync(Guid selfAccountId, Guid otherAccountId, CancellationToken ct)
    {
        var mine = await eligibility.EligibleTitleIdsUnscopedAsync(selfAccountId, ct);
        var theirs = await eligibility.EligibleTitleIdsUnscopedAsync(otherAccountId, ct);
        mine.IntersectWith(theirs);
        return mine;
    }

    /// <summary>
    /// All of one account's persisted scores under its Active artifact, tenancy filters
    /// ignored (the session usually belongs to the OTHER participant). The artifact join
    /// re-states the two predicates the global filter would otherwise carry: Active status
    /// and feature-schema compatibility (shadow column "FeatureSchemaVersion" — see
    /// ReelDbContext.FeatureSchemaVersionProperty).
    /// </summary>
    private async Task<Dictionary<Guid, decimal>> ActiveScoresAsync(Guid accountId, CancellationToken ct)
    {
        return await db.TitleScores
            .IgnoreQueryFilters()
            .Where(s => s.AccountId == accountId)
            .Join(
                db.ModelArtifacts
                    .IgnoreQueryFilters()
                    .Where(a => a.AccountId == accountId
                        && a.Status == ArtifactStatus.Active
                        && EF.Property<int?>(a, "FeatureSchemaVersion") == FeatureSchema.Version),
                s => s.ModelArtifactId,
                a => a.Id,
                (s, a) => s)
            .ToDictionaryAsync(s => s.TitleId, s => s.PredictedRating, ct);
    }

    private static string GenerateCode()
    {
        var bytes = new byte[CodeLength];
        RandomNumberGenerator.Fill(bytes);
        return string.Create(CodeLength, bytes, static (chars, source) =>
        {
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = CrockfordAlphabet[source[i] % CrockfordAlphabet.Length];
            }
        });
    }
}
