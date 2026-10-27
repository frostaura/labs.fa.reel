using System.Globalization;
using System.Net;
using System.Text.Json;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// HREV2-D-01: every "you rate X N on average" the app prints is the plain mean of the user's own
/// ratings of X's titles, over the count it names. It printed the model's shrunken value instead,
/// pulled toward the account's mean: "You rate their work 6.4 on average across 3 titles" for titles
/// rated 7, 5 and 6; "You rate Andrew Niccol 7.2" for 7, 9 and 7. The fixtures are the reviewer's
/// own shapes, each against an account mean far enough away that shrinkage moves the number, on both
/// sides of it: the person page, the why-line person (and its breakdown), the narrative's person
/// facts, and the decade note.
/// </summary>
[Collection("postgres")]
public class PersonAverageClaimsTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Theory]
    [InlineData(CreditRole.Actor, new short[] { 7, 5, 6 }, (short)9)] // Dacascos: 6.0 against a high mean
    [InlineData(CreditRole.Director, new short[] { 7, 9, 7 }, (short)4)] // Niccol: 7.67 against a low mean
    [InlineData(CreditRole.Writer, new short[] { 9, 6 }, (short)8)] // Blomkamp's shape: 7.5, two titles
    public async Task Every_printed_person_average_is_the_plain_mean_of_your_own_ratings(
        CreditRole role, short[] personRatings, short otherRating)
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"person-avg-{Guid.NewGuid():N}"[..24]);
        var person = new Person { Id = Guid.NewGuid(), TmdbId = Random.Shared.NextInt64(60_000_000, 70_000_000), Name = $"Averaged {role}" };
        var candidate = NewTitle("Unrated candidate", 2015);
        var plain = personRatings.Average(r => (decimal)r);

        using (var scope = api.Services.CreateScope())
        {
            using var system = scope.ServiceProvider.GetRequiredService<IAccountContext>().EnterSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
            db.Add(person);
            db.Titles.Add(candidate);
            db.TitleCredits.Add(Credit(candidate, person, role));
            for (var i = 0; i < personRatings.Length; i++)
            {
                var title = NewTitle($"{person.Name} {i}", 2010 + i);
                db.Titles.Add(title);
                db.TitleCredits.Add(Credit(title, person, role));
                db.UserRatings.Add(Rating(accountId, title, personRatings[i]));
            }

            // The rest of the library sits far from the person's titles, so a shrunken value moves.
            for (var i = 0; i < 12; i++)
            {
                var other = NewTitle($"Other {i}", 1995 + i);
                db.Titles.Add(other);
                db.UserRatings.Add(Rating(accountId, other, otherRating));
            }

            await db.SaveChangesAsync();
        }

        // The model's value really is a different number on this fixture — otherwise the test proves nothing.
        var taste = await TasteAsync(accountId);
        Assert.NotEqual(Round1(plain), Round1(TasteMath.ShrunkenMean(personRatings, taste.UserMean)));

        // Person page: "You rate their work X on average across N titles".
        using (var response = await client.GetAsync($"/api/people/{person.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(Math.Round(plain, 2), doc.RootElement.GetProperty("derivedAffinity").GetDecimal());
            Assert.Equal(personRatings.Length, doc.RootElement.GetProperty("ratedTitleCount").GetInt32());
        }

        using var read = Scoped(accountId);
        var db2 = read.ServiceProvider.GetRequiredService<ReelDbContext>();

        // The why-line person and the breakdown under it.
        var entries = await VerifiedPeople.ForTitlesAsync(db2, taste, [candidate.Id], default);
        var printed = VerifiedPeople.BestByRole(entries)[candidate.Id][role];
        Assert.Equal(plain, printed.Average);
        Assert.Equal(personRatings.Length, printed.TitleCount);
        var detail = (await ContributionDetails.BuildAsync(db2, taste, candidate, default))
            .Single(d => d.People is not null).People!.Single();
        Assert.Equal(Round1(plain), detail.YourAverage);
        Assert.Equal(personRatings.Length, detail.TitleCount);

        // The narrative's facts: the same number, and each title counted once.
        var facts = await new TasteFactsBuilder(
                db2,
                new DislikeSignal(db2, new MemoryCache(new MemoryCacheOptions())),
                new FeatureVectorBuilder(db2),
                new MemoryCache(new MemoryCacheOptions()))
            .BuildAsync(accountId, taste, DateTime.UtcNow, default);
        var fact = Assert.Single(facts.Facts.VerifiedPeople, p => p.Name == person.Name);
        Assert.Equal(Math.Round(plain, 2), fact.Average);
        Assert.Equal(personRatings.Length, fact.TitleCount);
    }

    [Fact]
    public async Task The_decade_note_prints_the_plain_mean_of_that_decade()
    {
        if (!fixture.Available)
        {
            return;
        }

        // One 1930s title rated 9 against a library rated 5: "you average 9.0 for 1930s titles",
        // not the 5.9 a shrunken mean would print.
        var (accountId, _) = await api.CreateSignedInAccountAsync($"decade-avg-{Guid.NewGuid():N}"[..24]);
        var old = NewTitle("Rated thirties", 1934);
        var candidate = NewTitle("Unrated thirties", 1938);
        using (var scope = api.Services.CreateScope())
        {
            using var system = scope.ServiceProvider.GetRequiredService<IAccountContext>().EnterSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
            db.Titles.AddRange(old, candidate);
            db.UserRatings.Add(Rating(accountId, old, 9));
            for (var i = 0; i < 10; i++)
            {
                var other = NewTitle($"Decade other {i}", 2001 + i);
                db.Titles.Add(other);
                db.UserRatings.Add(Rating(accountId, other, 5));
            }

            await db.SaveChangesAsync();
        }

        var taste = await TasteAsync(accountId);
        using var read = Scoped(accountId);
        var details = await ContributionDetails.BuildAsync(read.ServiceProvider.GetRequiredService<ReelDbContext>(), taste, candidate, default);
        Assert.Equal("you average 9.0 for 1930s titles", Assert.Single(details, d => d.Feature == "decadeAffinity").Note);
    }

    private static decimal Round1(decimal value) => ContributionDetails.OneDecimal(value);

    private async Task<FeatureVectorBuilder.TasteState> TasteAsync(Guid accountId)
    {
        using var scope = Scoped(accountId);
        var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
        return await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
    }

    private IServiceScope Scoped(Guid accountId)
    {
        var scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAccountContext>().SetAccount(accountId);
        return scope;
    }

    private static UserRating Rating(Guid accountId, Title title, short rating) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TitleId = title.Id,
        SubjectType = RatingSubjectType.Movie,
        Rating = rating,
        RatedAt = DateTime.UtcNow.AddDays(-3),
        FirstRatedAt = DateTime.UtcNow.AddDays(-3),
        SyncedAt = DateTime.UtcNow,
    };

    private static Title NewTitle(string name, int year)
    {
        var tmdbId = Random.Shared.NextInt64(60_000_000, 70_000_000);
        return new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = tmdbId,
            TraktSlug = string.Create(CultureInfo.InvariantCulture, $"person-avg-{tmdbId}"),
            Name = name,
            Year = year,
            Genres = ["drama"],
            Language = "en",
            TraktRating = 7.0m,
            TraktVotes = 5_000,
        };
    }

    private static TitleCredit Credit(Title title, Person person, CreditRole role) => new()
    {
        Id = Guid.NewGuid(),
        TitleId = title.Id,
        PersonId = person.Id,
        Role = role,
        CastOrder = role == CreditRole.Actor ? 0 : null,
    };
}
