using System.Net;
using System.Text.Json;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Claims register, family <c>narrative-and-dna</c> (R1-45): the Taste DNA page, when there is no
/// model yet, says "Everything below is read straight from your ratings". Its verdict is FALSE, and
/// this pins the code fact that makes it so: the headline stats directly below that line — hours
/// watched, titles, show completion — are read from the watch history, and move with it while the
/// ratings stand still (and the reverse). Below them sit the dismissals panel (read from what the
/// user dismissed) and the taste narrative (model-written prose), neither of which is a rating.
///
/// <para>What the sentence is for — "none of it waits for the model" — is true: nothing on the page
/// needs the trained model. The copy owes "read from your ratings and watch history", not this test.
/// When the copy changes, this test stays: it is the fact the copy must agree with.</para>
/// </summary>
[Collection("postgres")]
public class TasteDnaClaimsTests(PostgresFixture fixture, ReelApiFactory api) : IClassFixture<ReelApiFactory>
{
    [Theory]
    // Rated AND watched, plus three watched and never rated: four titles, one rating.
    [InlineData(1, 0, 3)]
    // Rated but never watched (rated on Trakt from memory): five ratings, no titles at all.
    [InlineData(0, 5, 0)]
    // Both kinds at once, unequal.
    [InlineData(2, 1, 6)]
    public async Task The_dna_headline_stats_are_read_from_watch_history_not_from_ratings(
        int ratedAndWatched, int ratedOnly, int watchedOnly)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await api.SeedAccountAsync($"dna-claims-{Guid.NewGuid():N}");
        await SeedHistoryAsync(accountId, ratedAndWatched, ratedOnly, watchedOnly);
        using var client = await api.CreateSignedInClientAsync(accountId);

        using var response = await client.GetAsync("/api/taste/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(ratedAndWatched + ratedOnly, root.GetProperty("ratingsCount").GetInt32());
        Assert.Equal(ratedAndWatched + watchedOnly, root.GetProperty("titlesWatched").GetInt32());
        // 100-minute movies, one play each: the hours are the watched titles' runtime, rated or not.
        Assert.Equal(Math.Round((ratedAndWatched + watchedOnly) * 100 / 60d), root.GetProperty("hoursWatched").GetDouble());
    }

    private async Task SeedHistoryAsync(Guid accountId, int ratedAndWatched, int ratedOnly, int watchedOnly)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        var total = ratedAndWatched + ratedOnly + watchedOnly;
        for (var i = 0; i < total; i++)
        {
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TraktSlug = $"dna-claims-{accountId:N}-{i}",
                TmdbId = Random.Shared.Next(1, int.MaxValue),
                Name = $"DNA {i}",
                Year = 2000 + i,
                Genres = ["drama"],
                RuntimeMinutes = 100,
                CreatedAt = now,
            };
            db.Titles.Add(title);

            var rated = i < ratedAndWatched + ratedOnly;
            var watched = i < ratedAndWatched || i >= ratedAndWatched + ratedOnly;
            if (rated)
            {
                db.UserRatings.Add(new UserRating
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = title.Id,
                    SubjectType = RatingSubjectType.Movie,
                    Rating = 8,
                    RatedAt = now.AddDays(-i),
                });
            }

            if (watched)
            {
                db.WatchedTitles.Add(new WatchedTitle
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    TitleId = title.Id,
                    Plays = 1,
                    IsFullyWatched = true,
                    LastWatchedAt = now.AddDays(-i),
                    FirstSyncedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        await db.SaveChangesAsync();
    }
}
