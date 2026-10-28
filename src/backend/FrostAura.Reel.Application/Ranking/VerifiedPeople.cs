using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// The single source for "which credited people is the user's taste actually verified on" —
/// people in a title's credits whom the user has rated at least two titles of IN THAT CAPACITY. Shared
/// by the feed builder, the paginator and the title endpoint, so a why-sentence, a rail caption and the
/// tap-through breakdown can never disagree about who is driving a person-affinity feature.
///
/// <para><b>Two numbers, never confused (HREV2-D-01).</b> <c>Average</c> is the empirical-Bayes
/// shrunken mean the model and the discovery arm read; it is pulled toward the user's overall mean, so
/// it is NOT what the user gave this person's work — a person rated 7, 9, 7 shrinks to 7.2. Anything
/// printed as "you rate X N on average" quotes <c>YourAverage</c>: the plain mean of the user's own
/// ratings of the titles behind it, over <c>TitleCount</c> titles (<see cref="OwnRatings(FeatureVectorBuilder.TasteState, Guid, CreditRole)"/>).</para>
/// </summary>
public static class VerifiedPeople
{
    /// <summary>The minimum observation count for a person to count as taste-verified.</summary>
    public const int MinRatedTitles = 2;

    /// <summary>
    /// One taste-verified credited person on one title: <paramref name="Average"/> is the model's
    /// shrunken value (for ordering), <paramref name="YourAverage"/> the plain mean of the user's own
    /// ratings of <paramref name="TitleCount"/> titles (for printing).
    /// </summary>
    public sealed record Entry(
        Guid TitleId, Guid PersonId, string Name, CreditRole Role, decimal Average, decimal YourAverage, int TitleCount);

    /// <summary>
    /// Every taste-verified credited person for the given titles: at least <see cref="MinRatedTitles"/>
    /// of their titles in that capacity carry the user's own rating. An explicit person rating feeds
    /// the model but is not a rated title, so it alone verifies nothing that can be quoted.
    /// </summary>
    public static async Task<IReadOnlyList<Entry>> ForTitlesAsync(
        IReelDbContext db, FeatureVectorBuilder.TasteState taste, IReadOnlyList<Guid> titleIds, CancellationToken ct)
    {
        var credits = await db.TitleCredits
            .Where(c => titleIds.Contains(c.TitleId))
            .Join(db.Persons, c => c.PersonId, p => p.Id, (c, p) => new { c.TitleId, c.PersonId, c.Role, p.Name })
            .ToListAsync(ct);
        return credits
            .Where(c => IsVerified(taste, c.PersonId, c.Role)
                && OwnRatings(taste, c.PersonId, c.Role).Count >= MinRatedTitles)
            .Select(c =>
            {
                var own = OwnRatings(taste, c.PersonId, c.Role);
                return new Entry(
                    c.TitleId, c.PersonId, c.Name, c.Role,
                    Affinity(taste, c.PersonId, c.Role),
                    PlainMean(own),
                    own.Count);
            })
            .ToList();
    }

    /// <summary>One taste-verified person and one role they hold across the user's own rated titles.</summary>
    public sealed record AccountEntry(Guid PersonId, CreditRole Role, decimal Average, int TitleCount);

    /// <summary>
    /// Every (person, role) pair the user's taste is verified on, computed over the titles the user
    /// has actually rated rather than over a candidate list. This is what
    /// <c>PersonAffinityWriter</c> materialises into <c>PersonAffinities</c>, and it goes through the
    /// SAME <see cref="Affinity"/> and <see cref="PoolSize"/> helpers as the <c>Average</c> of
    /// <see cref="ForTitlesAsync"/> — so the affinity a stored row ranks discovery by is the one that
    /// picks which person a why-sentence names. Do not compute an affinity anywhere else. The number
    /// the sentence PRINTS is the user's plain average (<see cref="PlainMean"/>), never this one.
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

    /// <summary>
    /// The user's own rating of every title they rated that credits <paramref name="personId"/> in
    /// <paramref name="role"/>, one per title — the numbers a printed average is the plain mean of.
    /// </summary>
    public static IReadOnlyCollection<short> OwnRatings(FeatureVectorBuilder.TasteState taste, Guid personId, CreditRole role) =>
        taste.PersonRoleTitleRatings is { } pools && pools.TryGetValue((personId, role), out var titles)
            ? [.. titles.Values]
            : [];

    /// <summary>
    /// As <see cref="OwnRatings(FeatureVectorBuilder.TasteState, Guid, CreditRole)"/> across every
    /// capacity, each title once however many credits it gives the person.
    /// </summary>
    public static IReadOnlyCollection<short> OwnRatings(FeatureVectorBuilder.TasteState taste, Guid personId)
    {
        if (taste.PersonRoleTitleRatings is not { } pools)
        {
            return [];
        }

        var byTitle = new Dictionary<Guid, short>();
        foreach (var role in Enum.GetValues<CreditRole>())
        {
            if (pools.TryGetValue((personId, role), out var titles))
            {
                foreach (var (titleId, rating) in titles)
                {
                    byTitle[titleId] = rating;
                }
            }
        }

        return [.. byTitle.Values];
    }

    /// <summary>The plain mean of <paramref name="ratings"/>: what "you rate X N on average" claims. Zero for none.</summary>
    public static decimal PlainMean(IReadOnlyCollection<short> ratings) =>
        ratings.Count == 0 ? 0m : ratings.Sum(r => (decimal)r) / ratings.Count;

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
    /// directorAffinity clause names a director and never an actor. Chosen by the model's affinity,
    /// quoted at the user's own plain average.
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
                            .OrderByDescending(e => e.Average)
                            .ThenByDescending(e => e.TitleCount)
                            .ThenBy(e => e.Name, StringComparer.Ordinal)
                            .Select(e => new ExplanationTemplater.PersonAffinity(e.Name, e.YourAverage, e.TitleCount))
                            .First()));
}
