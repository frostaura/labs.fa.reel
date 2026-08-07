using System.Globalization;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// The tap-through behind the title page's contribution bars: WHO is driving a person-affinity
/// feature (verified, user-rated people only — the same <see cref="VerifiedPeople"/> data the
/// why-sentences cite, so bar and sentence can never disagree) and the honest note behind the
/// decade feature. Features without anything verifiable to show are simply absent — the endpoint
/// never fabricates a person or an average to make a bar expandable.
/// </summary>
public static class ContributionDetails
{
    /// <summary>How many people a single feature's tap-through lists, best average first.</summary>
    private const int MaxPeoplePerFeature = 5;

    /// <summary>One verified person behind a person-affinity bar. Averages are 1dp at the response edge.</summary>
    public sealed record PersonDetail(Guid PersonId, string Name, decimal YourAverage, int TitleCount);

    /// <summary>One expandable feature: person features carry People, era features carry Note.</summary>
    public sealed record Detail(string Feature, IReadOnlyList<PersonDetail>? People = null, string? Note = null);

    public static async Task<IReadOnlyList<Detail>> BuildAsync(
        IReelDbContext db, FeatureVectorBuilder.TasteState taste, Title title, CancellationToken ct)
    {
        var entries = await VerifiedPeople.ForTitlesAsync(db, taste, [title.Id], ct);
        var details = new List<Detail>();
        AddPeople("castAffinity", CreditRole.Actor);
        AddPeople("directorAffinity", CreditRole.Director);
        AddPeople("writerAffinity", CreditRole.Writer);

        // The decade note rides the taste-state the endpoint already has — "you average 7.8 for
        // 1980s titles". Omitted when the user has no ratings in the title's decade: shrinkage
        // toward the user mean would otherwise print a number that describes nothing they did.
        if (title.Year is { } year)
        {
            var decade = year / 10 * 10;
            if (taste.DecadeRatings.TryGetValue(decade, out var ratings) && ratings.Count > 0)
            {
                var average = Math.Round(TasteMath.ShrunkenMean(ratings, taste.UserMean), 1);
                details.Add(new Detail(
                    "decadeAffinity",
                    Note: string.Create(CultureInfo.InvariantCulture, $"you average {average:0.0} for {decade}s titles")));
            }
        }

        return details;

        void AddPeople(string feature, CreditRole role)
        {
            var people = entries
                .Where(e => e.Role == role)
                // A person can hold several same-role credit rows on one title — collapse to one.
                .GroupBy(e => e.PersonId)
                .Select(g => g.First())
                .OrderByDescending(e => e.Average)
                .ThenBy(e => e.Name, StringComparer.Ordinal)
                .Take(MaxPeoplePerFeature)
                .Select(e => new PersonDetail(e.PersonId, e.Name, Math.Round(e.Average, 1), e.TitleCount))
                .ToList();
            if (people.Count > 0)
            {
                details.Add(new Detail(feature, people));
            }
        }
    }
}
