using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// The single source for "which credited people is the user's taste actually verified on" —
/// people in a title's credits whom the user has rated at least two titles of IN THAT CAPACITY (or
/// rated explicitly), with their empirical-Bayes shrunken average for that capacity. Shared by the feed builder, the
/// paginator and the title endpoint, so a why-sentence, a rail caption and the tap-through
/// breakdown can never disagree about who is driving a person-affinity feature.
/// </summary>
public static class VerifiedPeople
{
    /// <summary>The minimum observation count for a person to count as taste-verified.</summary>
    public const int MinRatedTitles = 2;

    /// <summary>One taste-verified credited person on one title.</summary>
    public sealed record Entry(Guid TitleId, Guid PersonId, string Name, CreditRole Role, decimal Average, int TitleCount);

    /// <summary>
    /// Every taste-verified credited person for the given titles. <c>TitleCount</c> is the size of
    /// the observation pool behind the average — for a person the user rated explicitly this is
    /// the pseudo-observation count the model actually uses, not a count of distinct titles.
    /// </summary>
    public static async Task<IReadOnlyList<Entry>> ForTitlesAsync(
        IReelDbContext db, FeatureVectorBuilder.TasteState taste, IReadOnlyList<Guid> titleIds, CancellationToken ct)
    {
        var credits = await db.TitleCredits
            .Where(c => titleIds.Contains(c.TitleId))
            .Join(db.Persons, c => c.PersonId, p => p.Id, (c, p) => new { c.TitleId, c.PersonId, c.Role, p.Name })
            .ToListAsync(ct);
        return credits
            .Where(c => IsVerified(taste, c.PersonId, c.Role))
            .Select(c => new Entry(
                c.TitleId, c.PersonId, c.Name, c.Role,
                Affinity(taste, c.PersonId, c.Role),
                PoolSize(taste, c.PersonId, c.Role)))
            .ToList();
    }

    /// <summary>One taste-verified person and one role they hold across the user's own rated titles.</summary>
    public sealed record AccountEntry(Guid PersonId, CreditRole Role, decimal Average, int TitleCount);

    /// <summary>
    /// Every (person, role) pair the user's taste is verified on, computed over the titles the user
    /// has actually rated rather than over a candidate list. This is what
    /// <c>PersonAffinityWriter</c> materialises into <c>PersonAffinities</c>, and it goes through the
    /// SAME <see cref="Affinity"/> and <see cref="PoolSize"/> helpers as
    /// <see cref="ForTitlesAsync"/> — so the number a stored row carries is the number a why-sentence
    /// quotes, by construction rather than by coincidence. That agreement is the whole reason this
    /// class exists; do not compute an affinity anywhere else.
    ///
    /// <para>
    /// <b>The pool is per role.</b> It used to be role-agnostic — one average per person across
    /// every credit — so Christopher Nolan's row as an ACTOR (a cameo) carried the average of the 22
    /// films he directed, and a "you rate him 8.3" cast clause quoted his directing. Each row now
    /// averages only the rated titles the person was credited on in that capacity
    /// (<c>TasteState.PersonRoleRatings</c>), so a director-only person has no cast affinity at all.
    /// The model's credit FEATURES still read the role-blind pool; aligning them is a feature-schema
    /// change and a retrain, and is deliberately not made here.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<AccountEntry>> ForAccountAsync(
        IReelDbContext db, FeatureVectorBuilder.TasteState taste, Guid accountId, CancellationToken ct)
    {
        // Movie/show-level ratings only — the same label-bearing events TasteState was built from,
        // so the pools this joins against and the credits it walks describe one set of titles.
        var ratedTitleIds = await db.UserRatings
            .Where(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .Select(r => r.TitleId)
            .Distinct()
            .ToListAsync(ct);
        if (ratedTitleIds.Count == 0)
        {
            return [];
        }

        var pairs = await db.TitleCredits
            .Where(c => ratedTitleIds.Contains(c.TitleId))
            .Select(c => new { c.PersonId, c.Role })
            .Distinct()
            .ToListAsync(ct);

        return pairs
            .Where(c => IsVerified(taste, c.PersonId, c.Role))
            .Select(c => new AccountEntry(
                c.PersonId, c.Role, Affinity(taste, c.PersonId, c.Role), PoolSize(taste, c.PersonId, c.Role)))
            .ToList();
    }

    /// <summary>The user's ratings behind (person, role), or null when there are none.</summary>
    public static IReadOnlyList<short>? PoolFor(FeatureVectorBuilder.TasteState taste, Guid personId, CreditRole role) =>
        taste.PersonRoleRatings is { } pools && pools.TryGetValue((personId, role), out var pool) ? pool : null;

    private static bool IsVerified(FeatureVectorBuilder.TasteState taste, Guid personId, CreditRole role) =>
        PoolFor(taste, personId, role) is { Count: >= MinRatedTitles };

    private static decimal Affinity(FeatureVectorBuilder.TasteState taste, Guid personId, CreditRole role) =>
        TasteMath.ShrunkenMean(PoolFor(taste, personId, role)!, taste.UserMean);

    private static int PoolSize(FeatureVectorBuilder.TasteState taste, Guid personId, CreditRole role) =>
        PoolFor(taste, personId, role)!.Count;

    /// <summary>
    /// Per-title, per-role best-affinity person — the exact shape
    /// <see cref="ExplanationTemplater.Context.VerifiedPersonByRole"/> consumes, so a
    /// directorAffinity clause names a director and never an actor.
    /// </summary>
    public static Dictionary<Guid, IReadOnlyDictionary<CreditRole, ExplanationTemplater.PersonAffinity>> BestByRole(
        IReadOnlyList<Entry> entries) =>
        entries
            .GroupBy(e => e.TitleId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<CreditRole, ExplanationTemplater.PersonAffinity>)g
                    .GroupBy(e => e.Role)
                    .ToDictionary(
                        rg => rg.Key,
                        rg => rg
                            .Select(e => new ExplanationTemplater.PersonAffinity(e.Name, e.Average))
                            .OrderByDescending(p => p.Affinity)
                            .First()));
}
