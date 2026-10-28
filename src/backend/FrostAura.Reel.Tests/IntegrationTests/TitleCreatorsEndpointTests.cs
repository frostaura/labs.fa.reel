using System.Text.Json;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// HREV2-D-02 on the wire. A show's page named every episode director under "Directors" and never
/// the person the show is by; hydration now stores TMDB's <c>created_by</c> as
/// <see cref="CreditRole.Creator"/> credits in TMDB's order, and <c>GET /api/titles/{type}/{id}</c>
/// returns them as <c>creators</c> — never as cast, never folded into directors.
/// </summary>
[Collection("postgres")]
public class TitleCreatorsEndpointTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    /// <summary>
    /// Creators in TMDB's order whatever order the rows were written in, with and without the
    /// creator also directing an episode, and with one or two creators.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task A_show_returns_its_creators_in_tmdb_order(int creatorCount, bool firstCreatorDirectsAnEpisode)
    {
        if (!fixture.Available)
        {
            return;
        }

        var (_, client) = await api.CreateSignedInAccountAsync($"creators-{Guid.NewGuid():N}"[..24]);
        var show = NewTitle(MediaType.Show);
        var creators = Enumerable.Range(0, creatorCount).Select(i => NewPerson($"Creator {i}")).ToList();
        var episodeDirectors = Enumerable.Range(0, 3).Select(i => NewPerson($"Episode Director {i}")).ToList();
        var lead = NewPerson("Lead Actor");
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            db.Titles.Add(show);
            db.Persons.AddRange([.. creators, .. episodeDirectors, lead]);
            // Written in REVERSE order: the response must follow CastOrder, not insertion order.
            for (var i = creatorCount - 1; i >= 0; i--)
            {
                db.TitleCredits.Add(Credit(show, creators[i], CreditRole.Creator, i));
            }

            foreach (var director in episodeDirectors)
            {
                db.TitleCredits.Add(Credit(show, director, CreditRole.Director, null));
            }

            if (firstCreatorDirectsAnEpisode)
            {
                db.TitleCredits.Add(Credit(show, creators[0], CreditRole.Director, null));
            }

            db.TitleCredits.Add(Credit(show, lead, CreditRole.Actor, 0));
            await db.SaveChangesAsync();
        }

        using var body = await GetAsync(client, "show", show.TmdbId!.Value);
        var root = body.RootElement;

        Assert.Equal(
            creators.Select(c => c.Name),
            root.GetProperty("creators").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Equal(
            creators.Select(c => c.Id.ToString()),
            root.GetProperty("creators").EnumerateArray().Select(c => c.GetProperty("personId").GetString()));

        var cast = root.GetProperty("cast").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal([lead.Name], cast);

        var directors = root.GetProperty("directors").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToHashSet();
        Assert.Equal(firstCreatorDirectsAnEpisode, directors.Contains(creators[0].Name));
        Assert.DoesNotContain(creators.Skip(1).Select(c => c.Name), directors.Contains);
    }

    /// <summary>A film, and a show TMDB names no creator for, answer an empty array — never null.</summary>
    [Theory]
    [InlineData(MediaType.Movie, "movie")]
    [InlineData(MediaType.Show, "show")]
    public async Task A_title_without_creators_returns_an_empty_list(MediaType mediaType, string route)
    {
        if (!fixture.Available)
        {
            return;
        }

        var (_, client) = await api.CreateSignedInAccountAsync($"creators-{Guid.NewGuid():N}"[..24]);
        var title = NewTitle(mediaType);
        var director = NewPerson("Only Director");
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            db.Titles.Add(title);
            db.Persons.Add(director);
            db.TitleCredits.Add(Credit(title, director, CreditRole.Director, null));
            await db.SaveChangesAsync();
        }

        using var body = await GetAsync(client, route, title.TmdbId!.Value);
        var creators = body.RootElement.GetProperty("creators");
        Assert.Equal(JsonValueKind.Array, creators.ValueKind);
        Assert.Equal(0, creators.GetArrayLength());
        Assert.Single(body.RootElement.GetProperty("directors").EnumerateArray());
    }

    private static async Task<JsonDocument> GetAsync(HttpClient client, string route, long tmdbId)
    {
        using var response = await client.GetAsync($"/api/titles/{route}/{tmdbId}");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static Title NewTitle(MediaType mediaType) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = mediaType,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = $"creators-{Guid.NewGuid():N}",
        Name = $"Creators Fixture {Guid.NewGuid():N}",
        Year = 2008,
        Genres = ["drama"],
        Language = "en",
        PosterPath = "/p.jpg",
        CreatedAt = DateTime.UtcNow,
        LastMetadataRefreshAt = DateTime.UtcNow,
    };

    private static Person NewPerson(string name) => new()
    {
        Id = Guid.NewGuid(),
        TmdbId = Random.Shared.NextInt64(1_000_000, 9_000_000),
        Name = $"{name} {Guid.NewGuid():N}"[..40],
    };

    private static TitleCredit Credit(Title title, Person person, CreditRole role, int? order) => new()
    {
        Id = Guid.NewGuid(),
        TitleId = title.Id,
        PersonId = person.Id,
        Role = role,
        CastOrder = order,
    };
}
