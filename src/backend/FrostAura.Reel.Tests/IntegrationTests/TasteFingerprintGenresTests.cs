using System.Net;
using System.Text.Json;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// HREV4-DESK-03. The DNA page captions its genre fingerprint "How each genre you rate a lot scores
/// against your own average" on a diverging chart. Its rows were ranked by affinity × log(1 + count),
/// so a genre rated often and liked less than average could never make the list — on the founder's
/// account horror (363 ratings, the widest below-average gap) and mystery (339) were missing while
/// fantasy (270) was shown. These pin that the rows are chosen by how much the genre is rated, and
/// that the endpoint the page reads serves exactly that choice.
/// </summary>
[Collection("postgres")]
public class TasteFingerprintGenresTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    private static readonly DateTime RatedAt = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    // The founder's shape: eight loved genres, each rated less than one heavily rated, disliked one.
    [InlineData(40, (short)3, true)]
    // The same heavy genre at the mean: still a mainstay, still shown.
    [InlineData(40, (short)7, true)]
    // Rated less than all eight others: not "a genre you rate a lot", however it scores.
    [InlineData(11, (short)3, false)]
    // Below the evidence floor: never a bar, even when nothing else competes for the slot.
    [InlineData(9, (short)3, false)]
    public void The_fingerprint_picks_the_most_rated_genres_whatever_they_score(int heavyCount, short heavyRating, bool shown)
    {
        var ratings = new Dictionary<string, List<(short Rating, DateTime RatedAt)>>(StringComparer.Ordinal);
        for (var g = 0; g < TasteEndpoints.FingerprintGenreCount; g++)
        {
            ratings[$"loved-{g}"] = Ratings(12 + g, 9);
        }

        ratings["heavy"] = Ratings(heavyCount, heavyRating);

        var rows = TasteEndpoints.FingerprintGenres(ratings, userMean: 7.5m);

        Assert.Equal(TasteEndpoints.FingerprintGenreCount, rows.Count);
        Assert.Equal(shown, rows.Any(r => r.Genre == "heavy"));
        // Most-rated first, so the drift chart's first five are the five genres rated most.
        Assert.Equal(rows.OrderByDescending(r => r.Count).ThenBy(r => r.Genre, StringComparer.Ordinal).Select(r => r.Genre), rows.Select(r => r.Genre));
        Assert.All(rows, r => Assert.True(r.Count >= TasteEndpoints.FingerprintMinRatings));
        if (shown)
        {
            // The genre that loses the slot is the least-rated loved one, not the disliked one.
            Assert.DoesNotContain(rows, r => r.Genre == "loved-0");
        }
    }

    [Fact]
    public void Equal_counts_are_ordered_by_name_so_the_chart_does_not_reshuffle_between_loads()
    {
        var ratings = new Dictionary<string, List<(short Rating, DateTime RatedAt)>>(StringComparer.Ordinal)
        {
            ["thriller"] = Ratings(20, 5),
            ["comedy"] = Ratings(20, 9),
            ["drama"] = Ratings(30, 7),
        };

        var rows = TasteEndpoints.FingerprintGenres(ratings, userMean: 7m);

        Assert.Equal(["drama", "comedy", "thriller"], rows.Select(r => r.Genre));
    }

    [Fact]
    public async Task The_dna_endpoint_serves_a_heavily_rated_disliked_genre_on_the_fingerprint()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"dna-fingerprint-{Guid.NewGuid():N}");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var heavy = $"fp-horror-{suffix}";
        await SeedRatedGenreAsync(accountId, heavy, count: 40, rating: 4);
        for (var g = 0; g < TasteEndpoints.FingerprintGenreCount; g++)
        {
            await SeedRatedGenreAsync(accountId, $"fp-loved-{g}-{suffix}", count: 12, rating: 9);
        }

        using var client = await api.CreateSignedInClientAsync(accountId);
        using var response = await client.GetAsync("/api/taste/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var genres = json.RootElement.GetProperty("topGenres").EnumerateArray()
            .Select(g => (Genre: g.GetProperty("genre").GetString()!, Affinity: g.GetProperty("affinity").GetDouble(), Count: g.GetProperty("count").GetInt32()))
            .ToList();
        var userMean = json.RootElement.GetProperty("userMean").GetDouble();

        Assert.Equal(TasteEndpoints.FingerprintGenreCount, genres.Count);
        // Rated most, so first — and below the account's own average, which is the bar's whole point.
        Assert.Equal(heavy, genres[0].Genre);
        Assert.Equal(40, genres[0].Count);
        Assert.True(genres[0].Affinity < userMean, $"{genres[0].Affinity} should sit below the {userMean} mean");
    }

    private static List<(short Rating, DateTime RatedAt)> Ratings(int count, short rating) =>
        [.. Enumerable.Range(0, count).Select(i => (rating, RatedAt.AddDays(-i)))];

    private async Task SeedRatedGenreAsync(Guid accountId, string genre, int count, short rating)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        for (var i = 0; i < count; i++)
        {
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TraktSlug = $"{genre}-{i}-{Guid.NewGuid():N}",
                TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
                Name = $"{genre} {i}",
                Year = 1990 + (i % 30),
                Genres = [genre],
                RuntimeMinutes = 100,
                CreatedAt = RatedAt,
            };
            db.Titles.Add(title);
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = rating,
                RatedAt = RatedAt.AddDays(-i),
                FirstRatedAt = RatedAt.AddDays(-i),
                SyncedAt = RatedAt,
            });
        }

        await db.SaveChangesAsync();
    }
}
