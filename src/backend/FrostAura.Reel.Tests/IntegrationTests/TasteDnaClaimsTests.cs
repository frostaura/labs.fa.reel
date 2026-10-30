using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Infrastructure.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Claims register, family <c>narrative-and-dna</c> (R1-45): the code fact behind each claim's
/// verdict, one test per claim, the verdict in the test's own summary (two more live in
/// <c>LensPipelineTests</c> and one in <c>TastePocketClaimsTests</c>). The first: the Taste DNA page,
/// when there is no model yet, says "Everything below is read straight from your ratings". Its verdict is FALSE, and
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
    // Nothing rated, four watched: the page's empty state says "your taste DNA is built from the
    // titles you've rated, and there are none so far" over an account with four titles and their
    // hours on record (that claim's verdict is FALSE for the same reason).
    [InlineData(0, 0, 4)]
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

    /// <summary>
    /// "every title you've ever rated counts — all N of them, with recent ones counting a little more"
    /// (Taste DNA header). The first half is TRUE and the second FALSE: every rating counts with no
    /// cutoff, eight years old included, but the page's genre and era affinities are shrunken means
    /// of the ratings with no time in them at all. Two accounts with the same ratings, one loving its
    /// recent genre and the other the same genre long ago, read identical affinities — recency
    /// moves only the pockets' weight (their own caption says so).
    /// </summary>
    [Fact]
    public async Task Every_rating_counts_whatever_its_age_and_the_dna_affinities_do_not_weight_recent_ones()
    {
        if (!fixture.Available)
        {
            return;
        }

        var loved = $"claimlove{Guid.NewGuid():N}";
        var tolerated = $"claimmeh{Guid.NewGuid():N}";
        var lovedTitles = await SeedTitlesAsync(loved, 12);
        var toleratedTitles = await SeedTitlesAsync(tolerated, 12);
        var recentLove = await api.SeedAccountAsync($"dna-recent-{Guid.NewGuid():N}");
        var oldLove = await api.SeedAccountAsync($"dna-old-{Guid.NewGuid():N}");

        // The same 24 ratings on both accounts; only WHEN each genre was rated is swapped.
        await RateAsync(recentLove, lovedTitles, 9, daysAgo: 2);
        await RateAsync(recentLove, toleratedTitles, 5, daysAgo: 3000);
        await RateAsync(oldLove, lovedTitles, 9, daysAgo: 3000);
        await RateAsync(oldLove, toleratedTitles, 5, daysAgo: 2);

        var recent = await DnaAsync(recentLove);
        var old = await DnaAsync(oldLove);

        // No cutoff: the eight-year-old ratings are counted on both accounts.
        Assert.Equal(24, recent.GetProperty("ratingsCount").GetInt32());
        Assert.Equal(24, old.GetProperty("ratingsCount").GetInt32());
        foreach (var genre in new[] { loved, tolerated })
        {
            Assert.Equal(Affinity(recent, genre), Affinity(old, genre));
        }

        Assert.True(Affinity(recent, loved) > Affinity(recent, tolerated));
        Assert.Equal(recent.GetProperty("eras").GetRawText(), old.GetProperty("eras").GetRawText());
    }

    /// <summary>
    /// "a dismissal only teaches me a genre or a tone when you say which" (DislikePanel). TRUE, end
    /// to end through the dismiss endpoint and the dislikes profile: the same dismissed title demotes
    /// its genre only under the genre reason and its tone only under the tone reason; no reason,
    /// "not for me" and "seen enough" teach neither (they steer the similarity centroids instead).
    /// </summary>
    [Theory]
    [InlineData(null, false, false)]
    [InlineData("not_for_me", false, false)]
    [InlineData("seen_enough", false, false)]
    [InlineData("genre", true, false)]
    [InlineData("tone", false, true)]
    public async Task A_dismissal_teaches_a_genre_or_a_tone_only_when_its_reason_says_which(
        string? reason, bool teachesGenre, bool teachesTone)
    {
        if (!fixture.Available)
        {
            return;
        }

        var genre = $"claimdismiss{Guid.NewGuid():N}";
        var (titleId, tmdbId) = (await SeedTitlesAsync(genre, 1))[0];
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            db.TitleAttributes.Add(new TitleAttributes
            {
                TitleId = titleId,
                Tone = "bleak",
                Status = AttributeExtractionStatus.Done,
                ExtractedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var accountId = await api.SeedAccountAsync($"dna-dismiss-{Guid.NewGuid():N}");
        using var client = await api.CreateSignedInClientAsync(accountId);
        using (var dismissed = await client.PostAsJsonAsync($"/api/titles/movie/{tmdbId}/reactions/not_interested", new { reason }))
        {
            Assert.True(dismissed.IsSuccessStatusCode, $"dismiss answered {(int)dismissed.StatusCode}");
        }

        using var response = await client.GetAsync("/api/taste/dislikes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var profile = json.RootElement.GetProperty("profile");

        Assert.Equal(1, profile.GetProperty("activeCount").GetInt32());
        Assert.Equal(teachesGenre, profile.GetProperty("genres").EnumerateArray().Any(g => g.GetProperty("genre").GetString() == genre));
        Assert.Equal(teachesTone, profile.GetProperty("tones").EnumerateArray().Any(t => t.GetProperty("tone").GetString() == "bleak"));
        Assert.Equal(teachesGenre ? 1 : 0, profile.GetProperty("genres").GetArrayLength());
        Assert.Equal(teachesTone ? 1 : 0, profile.GetProperty("tones").GetArrayLength());
    }

    /// <summary>
    /// "your taste has moved since" (TasteNarrativePanel, from the narrative API's <c>staleReason</c>
    /// <c>taste-moved</c>). TRUE: that reason is reported exactly when the account's taste fingerprint
    /// (or its crowd stance) differs from the one the profile was written at — and yes after a
    /// dismissal, which the fingerprint carries. The writer moving on is not the taste moving: since
    /// HREV6-DESK-01 a narrative written under an older prompt reads stale too, but with the reason
    /// <c>outdated-prompt</c>, which the panel words as "written by an earlier version of Reel"
    /// (lib/narrativeStale.ts) and never as a taste move. The narrative is seeded at the current prompt
    /// so that, until the test moves it, only the fingerprint can make it stale (HREV8-PLAT-02).
    /// </summary>
    [Theory]
    // The writer moves on first, then the taste: taste-moved wins over the older prompt.
    [InlineData(true)]
    // The taste moves under a narrative the current writer would still stand behind.
    [InlineData(false)]
    public async Task The_profile_says_your_taste_has_moved_exactly_when_its_taste_fingerprint_has(
        bool writerMovesOnFirst)
    {
        if (!fixture.Available)
        {
            return;
        }

        var genre = $"claimstale{Guid.NewGuid():N}";
        var titles = await SeedTitlesAsync(genre, 12);
        var accountId = await api.SeedAccountAsync($"dna-stale-{Guid.NewGuid():N}");
        await RateAsync(accountId, titles.Take(11).ToList(), 8, daysAgo: 10);
        using var client = await api.CreateSignedInClientAsync(accountId);

        var built = await BuildFactsAsync(accountId);
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            db.AccountTasteNarratives.Add(new AccountTasteNarrative
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Version = 1,
                ProfileHash = built.ProfileHash,
                ProfileFingerprint = built.ProfileFingerprint,
                Text = "You favour slow, grounded drama.",
                FactsJson = JsonSerializer.Serialize(built.Facts, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                RatingsCount = built.Facts.RatingsCount,
                ModelId = "test/writer",
                PromptVersion = OpenRouterLensWriter.CurrentNarrativePromptVersion,
                GeneratedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal((false, null), await StalenessAsync(client));

        if (writerMovesOnFirst)
        {
            // The writer moving on is not the taste moving: stale, but never "your taste has moved".
            await using (var db = fixture.CreateContext(new AccountContext()))
            {
                await db.AccountTasteNarratives.Where(n => n.AccountId == accountId)
                    .ExecuteUpdateAsync(u => u.SetProperty(
                        n => n.PromptVersion, OpenRouterLensWriter.CurrentNarrativePromptVersion - 1));
            }

            var (isStale, reason) = await StalenessAsync(client);
            Assert.True(isStale);
            Assert.NotEqual(TasteNarrativeService.StaleTasteMoved, reason);
            Assert.Equal(TasteNarrativeService.StaleOutdatedPrompt, reason);
        }

        // A dismissal is taste moving.
        using (var dismissed = await client.PostAsJsonAsync(
                   $"/api/titles/movie/{titles[11].TmdbId}/reactions/not_interested", new { reason = "genre" }))
        {
            Assert.True(dismissed.IsSuccessStatusCode, $"dismiss answered {(int)dismissed.StatusCode}");
        }

        Assert.Equal((true, TasteNarrativeService.StaleTasteMoved), await StalenessAsync(client));
    }

    /// <summary>
    /// "Turning your ratings into a description of what you actually like" (the narrative job's
    /// explainer card). FALSE: the profile's writer is handed the account's dismissals as well — and
    /// its stated preference tags and its pockets, which count unrated rewatches. One dismissal, with
    /// no reason and no rating changed, changes what the writer is told, and names the title.
    /// </summary>
    [Fact]
    public async Task The_taste_profile_is_written_from_dismissals_as_well_as_ratings()
    {
        if (!fixture.Available)
        {
            return;
        }

        var genre = $"claimwriter{Guid.NewGuid():N}";
        var titles = await SeedTitlesAsync(genre, 12);
        var accountId = await api.SeedAccountAsync($"dna-writer-{Guid.NewGuid():N}");
        await RateAsync(accountId, titles.Take(11).ToList(), 8, daysAgo: 10);
        using var client = await api.CreateSignedInClientAsync(accountId);

        var before = await BuildFactsAsync(accountId);
        using (var dismissed = await client.PostAsJsonAsync(
                   $"/api/titles/movie/{titles[11].TmdbId}/reactions/not_interested", new { reason = (string?)null }))
        {
            Assert.True(dismissed.IsSuccessStatusCode, $"dismiss answered {(int)dismissed.StatusCode}");
        }

        var after = await BuildFactsAsync(accountId);

        Assert.Equal(before.Facts.RatingsCount, after.Facts.RatingsCount);
        var dismissedName = $"{genre} 11";
        Assert.DoesNotContain(dismissedName, OpenRouterLensWriter.NarrativePrompt(before.Facts), StringComparison.Ordinal);
        Assert.Contains(dismissedName, OpenRouterLensWriter.NarrativePrompt(after.Facts), StringComparison.Ordinal);
    }

    private async Task<TasteFactsBuilder.Result> BuildFactsAsync(Guid accountId)
    {
        using var scope = api.Services.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<IAccountContext>().SetAccount(accountId);
        var taste = await services.GetRequiredService<FeatureVectorBuilder>().BuildTasteStateAsync(accountId, DateTime.UtcNow, default);
        return await services.GetRequiredService<TasteFactsBuilder>().BuildAsync(accountId, taste, DateTime.UtcNow, default);
    }

    /// <summary>The narrative read's <c>isStale</c> and <c>staleReason</c>, as the panel receives them.</summary>
    private static async Task<(bool IsStale, string? Reason)> StalenessAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/taste/narrative");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        return (root.GetProperty("isStale").GetBoolean(), root.GetProperty("staleReason").GetString());
    }

    private async Task<JsonElement> DnaAsync(Guid accountId)
    {
        using var client = await api.CreateSignedInClientAsync(accountId);
        using var response = await client.GetAsync("/api/taste/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static double Affinity(JsonElement dna, string genre) =>
        dna.GetProperty("topGenres").EnumerateArray()
            .Single(g => g.GetProperty("genre").GetString() == genre)
            .GetProperty("affinity").GetDouble();

    /// <summary>
    /// A synthetic language on every title the claim tests below seed. Eligibility defaults to
    /// English-only for an account with no language rows, so these titles stay out of every other
    /// class's eligible catalogue (and its counts) in the shared postgres collection.
    /// </summary>
    private readonly string _language = $"claimslang{Guid.NewGuid():N}";

    /// <summary>Movies of one synthetic genre, named "{genre} {i}", 100 minutes, one per year from 1990.</summary>
    private async Task<IReadOnlyList<(Guid Id, long TmdbId)>> SeedTitlesAsync(string genre, int count)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var seeded = new List<(Guid, long)>();
        for (var i = 0; i < count; i++)
        {
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TraktSlug = $"{genre}-{i}",
                TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
                Name = $"{genre} {i}",
                Year = 1990 + i,
                Genres = [genre],
                Language = _language,
                RuntimeMinutes = 100,
                CreatedAt = DateTime.UtcNow,
            };
            db.Titles.Add(title);
            seeded.Add((title.Id, title.TmdbId!.Value));
        }

        await db.SaveChangesAsync();
        return seeded;
    }

    private async Task RateAsync(Guid accountId, IReadOnlyList<(Guid Id, long TmdbId)> titles, short rating, int daysAgo)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        foreach (var (id, _) in titles)
        {
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = id,
                SubjectType = RatingSubjectType.Movie,
                Rating = rating,
                RatedAt = DateTime.UtcNow.AddDays(-daysAgo),
                SyncedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
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
