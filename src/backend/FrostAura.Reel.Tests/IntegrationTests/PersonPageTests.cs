using System.Net;
using System.Text.Json;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The person page (XM-01, F-24): a lookup may show what you have seen, never what you ruled out —
/// the filmography holds out every content rule and says how many it held out — and each credit says
/// what the person did on the title, from the credits alone.
/// </summary>
[Collection("postgres")]
public class PersonPageTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Fact]
    public async Task The_filmography_holds_out_ruled_out_titles_and_names_each_credit()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (accountId, client) = await api.CreateSignedInAccountAsync($"person-page-{Guid.NewGuid():N}"[..24]);
        var person = new Person { Id = Guid.NewGuid(), TmdbId = Random.Shared.NextInt64(50_000_000, 60_000_000), Name = "Page Person" };
        var directed = NewTitle("Directed Drama", "drama");
        var acted = NewTitle("Acted Thriller", "thriller");
        var ruledOut = NewTitle("Ruled Out Horror", "horror");

        using (var scope = api.Services.CreateScope())
        {
            using var system = scope.ServiceProvider.GetRequiredService<IAccountContext>().EnterSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
            db.Add(person);
            db.Titles.AddRange(directed, acted, ruledOut);
            db.TitleCredits.AddRange(
                Credit(directed, person, CreditRole.Director),
                Credit(directed, person, CreditRole.Writer),
                Credit(acted, person, CreditRole.Actor, "Neil McCauley"),
                Credit(ruledOut, person, CreditRole.Actor, "The Ghost"));
            db.ContentFilters.Add(new ContentFilter
            {
                Id = Guid.NewGuid(), AccountId = accountId, Kind = FilterKind.ExcludeGenre, Value = "horror", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var response = await client.GetAsync($"/api/people/{person.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var films = root.GetProperty("filmography").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f);

        Assert.DoesNotContain("Ruled Out Horror", films.Keys);
        Assert.Equal(1, root.GetProperty("hiddenByConditions").GetInt32());
        Assert.Equal(["Director", "Writer"], films["Directed Drama"].GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(JsonValueKind.Null, films["Directed Drama"].GetProperty("character").ValueKind);
        Assert.Equal(["Actor"], films["Acted Thriller"].GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("Neil McCauley", films["Acted Thriller"].GetProperty("character").GetString());
    }

    private static Title NewTitle(string name, string genre)
    {
        var tmdbId = Random.Shared.NextInt64(50_000_000, 60_000_000);
        return new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = tmdbId,
            TraktSlug = $"person-page-{tmdbId}",
            Name = name,
            Year = 2001,
            Genres = [genre],
            Language = "en",
            TraktRating = 7.5m,
            TraktVotes = 5_000,
        };
    }

    private static TitleCredit Credit(Title title, Person person, CreditRole role, string? character = null) => new()
    {
        Id = Guid.NewGuid(), TitleId = title.Id, PersonId = person.Id, Role = role, CharacterName = character,
        CastOrder = role == CreditRole.Actor ? 1 : null,
    };
}
