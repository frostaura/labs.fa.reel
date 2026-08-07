using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
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
            .Where(c => taste.PersonRatings.TryGetValue(c.PersonId, out var r) && r.Count >= MinRatedTitles)
            .Select(c => new Entry(
                c.TitleId, c.PersonId, c.Name, c.Role,
                TasteMath.ShrunkenMean(taste.PersonRatings[c.PersonId], taste.UserMean),
                taste.PersonRatings[c.PersonId].Count))
            .ToList();
    }

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
