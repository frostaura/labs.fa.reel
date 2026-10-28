using System.Globalization;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The tap-through payload behind the title page's contribution bars, on real Postgres: person
/// features list ONLY taste-verified people (rated on ≥ 2 titles, same VerifiedPeople data the
/// why-sentences cite) with the plain mean of the user's own ratings of their titles; the decade
/// feature carries the honest "you average X for YYYYs titles" note, the plain mean too; and a
/// person below the verification bar never appears (HREV2-D-01). The account also rates titles
/// outside both pools, so its mean is not the pools' and a shrunken mean would print a different
/// number: with only the two pooled ratings the shrunken and plain means coincide and the test
/// could not tell them apart.
/// </summary>
[Collection("postgres")]
public class ContributionDetailsTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(new short[] { 3 })]
    [InlineData(new short[] { 10, 2, 4 })]
    public async Task Person_features_list_verified_people_and_the_decade_note_rides_along(short[] offPoolRatings)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var actorId = Guid.NewGuid();     // rated through 2 titles → verified
        var directorId = Guid.NewGuid();  // rated through 1 title → below the bar, must not appear
        var ratedA = Guid.NewGuid();
        var ratedB = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            db.Persons.Add(new Person { Id = actorId, Name = "Verified Actor", TmdbId = Random.Shared.Next(1, int.MaxValue) });
            db.Persons.Add(new Person { Id = directorId, Name = "Once Rated", TmdbId = Random.Shared.Next(1, int.MaxValue) });
            db.Add(NewTitle(ratedA, "Rated A", 1988));
            db.Add(NewTitle(ratedB, "Rated B", 1984));
            db.Add(NewTitle(targetId, "Target", 1986));
            foreach (var titleId in new[] { ratedA, ratedB, targetId })
            {
                db.TitleCredits.Add(new TitleCredit { Id = Guid.NewGuid(), TitleId = titleId, PersonId = actorId, Role = CreditRole.Actor, CastOrder = 0 });
            }

            db.TitleCredits.Add(new TitleCredit { Id = Guid.NewGuid(), TitleId = ratedA, PersonId = directorId, Role = CreditRole.Director });
            db.TitleCredits.Add(new TitleCredit { Id = Guid.NewGuid(), TitleId = targetId, PersonId = directorId, Role = CreditRole.Director });
            db.UserRatings.Add(NewRating(accountId, ratedA, 9));
            db.UserRatings.Add(NewRating(accountId, ratedB, 8));
            for (var i = 0; i < offPoolRatings.Length; i++)
            {
                // 1990s and uncredited: in neither the actor's pool nor the 1980s pool.
                var offPool = Guid.NewGuid();
                db.Add(NewTitle(offPool, $"Off pool {i}", 1992 + i));
                db.UserRatings.Add(NewRating(accountId, offPool, offPoolRatings[i]));
            }

            await db.SaveChangesAsync();
        }

        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var scopedDb = fixture.CreateContext(scoped);
        var taste = await new FeatureVectorBuilder(scopedDb).BuildTasteStateAsync(accountId, DateTime.UtcNow, CancellationToken.None);
        var target = await scopedDb.Titles.FindAsync(targetId);

        var details = await ContributionDetails.BuildAsync(scopedDb, taste, target!, CancellationToken.None);

        // The fixture must separate the two rules, or the asserts below pin nothing.
        Assert.NotEqual(8.5m, decimal.Round(TasteMath.ShrunkenMean(new short[] { 9, 8 }, taste.UserMean), 1, MidpointRounding.AwayFromZero));

        // castAffinity: the verified actor, with the plain mean of the two ratings: (9 + 8) / 2.
        var cast = Assert.Single(details, d => d.Feature == "castAffinity");
        var person = Assert.Single(cast.People!);
        Assert.Equal(actorId, person.PersonId);
        Assert.Equal("Verified Actor", person.Name);
        Assert.Equal(2, person.TitleCount);
        Assert.Equal(8.5m, person.YourAverage);

        // directorAffinity: one rating is below the verification bar — the feature is absent, not empty.
        Assert.DoesNotContain(details, d => d.Feature == "directorAffinity");

        // decadeAffinity: both rated titles are 1980s, the target is 1986 → an honest averaged
        // note. Invariant culture on the expectation too — the note is built invariant on purpose
        // (the dev machine's en_ZA locale would render "8,5"), and the test must not undo that.
        var decade = Assert.Single(details, d => d.Feature == "decadeAffinity");
        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"you average {8.5m:0.0} for 1980s titles"),
            decade.Note);
    }

    private static Title NewTitle(Guid id, string name, int year) => new()
    {
        Id = id,
        MediaType = MediaType.Movie,
        TraktId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = $"t-{id:N}",
        Name = name,
        Year = year,
        CreatedAt = DateTime.UtcNow,
    };

    private static UserRating NewRating(Guid accountId, Guid titleId, short rating) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TitleId = titleId,
        SubjectType = RatingSubjectType.Movie,
        Rating = rating,
        RatedAt = DateTime.UtcNow.AddDays(-10),
        SyncedAt = DateTime.UtcNow,
    };

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"batch-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }
}
