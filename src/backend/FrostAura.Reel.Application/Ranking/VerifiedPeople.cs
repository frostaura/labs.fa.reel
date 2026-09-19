using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// The single source for "which credited people is the user's taste actually verified on" —
/// people in a title's credits whom the user has rated at least two titles of (or rated
/// explicitly), with their empirical-Bayes shrunken average. Shared by the feed builder, the
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
            .Where(c => IsVerified(taste, c.PersonId))
            .Select(c => new Entry(
                c.TitleId, c.PersonId, c.Name, c.Role,
                Affinity(taste, c.PersonId),
                PoolSize(taste, c.PersonId)))
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
    /// <b>The pool is role-agnostic and that is deliberate.</b> <c>TasteState.PersonRatings</c> is
    /// keyed by person, not by (person, role), so an actor-turned-director carries ONE average
    /// across every credit — exactly what <see cref="FeatureVectorBuilder"/>'s credit features and
    /// the why-sentences already use. The role on the row says which capacity that person was
    /// credited in on the user's rated titles, not which capacity the average was measured in.
    /// A per-role average would be a second, disagreeing number for the same person.
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
            .Where(c => IsVerified(taste, c.PersonId))
            .Select(c => new AccountEntry(c.PersonId, c.Role, Affinity(taste, c.PersonId), PoolSize(taste, c.PersonId)))
            .ToList();
    }

    private static bool IsVerified(FeatureVectorBuilder.TasteState taste, Guid personId) =>
        taste.PersonRatings.TryGetValue(personId, out var pool) && pool.Count >= MinRatedTitles;

    private static decimal Affinity(FeatureVectorBuilder.TasteState taste, Guid personId) =>
        TasteMath.ShrunkenMean(taste.PersonRatings[personId], taste.UserMean);

    private static int PoolSize(FeatureVectorBuilder.TasteState taste, Guid personId) =>
        taste.PersonRatings[personId].Count;

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
