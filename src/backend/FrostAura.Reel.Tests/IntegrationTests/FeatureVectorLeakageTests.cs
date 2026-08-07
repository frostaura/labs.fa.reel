using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;
using Pgvector;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The leave-one-out contract, end to end on real Postgres. These are the regression tests for the
/// two leaks that invalidated the M2 edge proof: a training row seeing its own embedding in the
/// user's loved set (<c>simTopLovedMax = Cosine(a, a) = 1.0</c>, a perfect label proxy), and a
/// training row's own rating sitting inside the genre/decade/cast pools used to describe it.
/// Neither is reproducible at serving time — a scored candidate is unrated by definition — so both
/// were pure train/serve skew. Skips when REEL_TEST_CONNECTION_STRING is unset.
/// </summary>
[Collection("postgres")]
public class FeatureVectorLeakageTests(PostgresFixture fixture)
{
    private static int IndexOf(string scalar) => Array.IndexOf(FeatureSchema.Scalars, scalar);

    [Fact]
    public async Task SimTopLovedMax_is_strictly_below_one_for_a_title_the_user_loved()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        // A dozen loved titles, each with its own distinct embedding direction. Every one of them is
        // in the user's top-loved set, so every one of them is also a TRAINING row.
        var lovedIds = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            var id = await SeedTitleAsync($"Loved {i}", NearUnit(i, 0.3f));
            await SeedRatingAsync(accountId, id, rating: 9, DateTime.UtcNow.AddDays(-30 - i));
            lovedIds.Add(id);
        }

        var vectors = await BuildAsync(accountId, lovedIds);
        var max = IndexOf("simTopLovedMax");
        var mean = IndexOf("simTopLovedMean");
        var hasEmbedding = IndexOf("hasEmbedding");

        foreach (var id in lovedIds)
        {
            var vector = vectors[id];
            Assert.Equal(1f, vector[hasEmbedding]); // the feature is actually being exercised
            Assert.True(
                vector[max] < 1f,
                $"simTopLovedMax was {vector[max]} — a loved training row is still matching its own embedding (Cosine(a,a) = 1).");
            Assert.True(vector[mean] < 1f, $"simTopLovedMean was {vector[mean]} — the self-match is still in the average.");
        }
    }

    [Fact]
    public async Task A_single_loved_title_gets_no_centroid_similarity_from_itself()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Degenerate but important: with exactly ONE loved title, the loved centroid IS that title's
        // embedding. Cosine against itself is 1.0; leave-one-out has to yield "no centroid" (0),
        // not a perfect self-match.
        var accountId = await SeedAccountAsync();
        var onlyLovedId = await SeedTitleAsync("Sole Favourite", NearUnit(3, 0.1f));
        await SeedRatingAsync(accountId, onlyLovedId, rating: 10, DateTime.UtcNow.AddDays(-10));

        var vector = (await BuildAsync(accountId, [onlyLovedId]))[onlyLovedId];

        Assert.Equal(0f, vector[IndexOf("simLovedCentroid")]);
        Assert.Equal(0f, vector[IndexOf("simRecentCentroid")]);
        Assert.Equal(0f, vector[IndexOf("simTopLovedMax")]);
    }

    [Fact]
    public async Task An_unrated_candidate_still_sees_the_full_loved_set()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The mirror of the test above: leave-one-out must remove the title's OWN contribution and
        // nothing else. A candidate the user has never rated is entitled to the whole loved set —
        // otherwise the fix would have traded a leak for a blind serving path.
        var accountId = await SeedAccountAsync();
        for (var i = 0; i < 5; i++)
        {
            var lovedId = await SeedTitleAsync($"Anchor {i}", NearUnit(i, 0.2f));
            await SeedRatingAsync(accountId, lovedId, rating: 9, DateTime.UtcNow.AddDays(-20 - i));
        }

        var candidateId = await SeedTitleAsync("Unrated Candidate", NearUnit(0, 0.25f));
        var vector = (await BuildAsync(accountId, [candidateId]))[candidateId];

        Assert.True(vector[IndexOf("simTopLovedMax")] > 0.5f, "an unrated candidate must still be compared against the loved set");
        Assert.True(vector[IndexOf("simTopLovedMax")] < 1f);
        Assert.True(vector[IndexOf("simLovedCentroid")] > 0f);
    }

    [Fact]
    public async Task A_titles_own_rating_does_not_move_its_own_affinity_features()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The self-referential mean-encoding defect. Build the same title's vector twice against two
        // accounts with IDENTICAL histories except for that one title's own rating (10 vs 1). Every
        // pooled affinity — genre, drift, decade, cast/director/writer, contrarian — must be
        // bit-identical: the label may not describe itself.
        var subjectId = await SeedTitleAsync("Subject", NearUnit(0, 0.4f), traktRating: 6.0m, year: 1994);
        var personId = await SeedPersonAsync("Shared Star");
        await SeedCreditAsync(subjectId, personId, CreditRole.Actor, castOrder: 0);

        var companionIds = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var id = await SeedTitleAsync($"Companion {i}", NearUnit(i + 1, 0.2f), traktRating: 7.0m + (i * 0.1m), year: 1990 + i);
            await SeedCreditAsync(id, personId, CreditRole.Actor, castOrder: 1);
            companionIds.Add(id);
        }

        var high = await SeedAccountAsync();
        var low = await SeedAccountAsync();
        var ratedAt = DateTime.UtcNow.AddDays(-15);
        foreach (var accountId in new[] { high, low })
        {
            for (var i = 0; i < companionIds.Count; i++)
            {
                await SeedRatingAsync(accountId, companionIds[i], (short)(6 + (i % 4)), ratedAt.AddDays(-i));
            }
        }

        await SeedRatingAsync(high, subjectId, rating: 10, ratedAt);
        await SeedRatingAsync(low, subjectId, rating: 1, ratedAt);

        var fromHigh = (await BuildAsync(high, [subjectId]))[subjectId];
        var fromLow = (await BuildAsync(low, [subjectId]))[subjectId];

        string[] pooled =
        [
            "userGenreAffinity", "genreDrift", "decadeAffinity",
            "directorAffinity", "castAffinity", "writerAffinity", "lovedCastCount",
            "contrarianAdjustedRating",
        ];

        foreach (var scalar in pooled)
        {
            var i = IndexOf(scalar);
            Assert.True(
                Math.Abs(fromHigh[i] - fromLow[i]) < 1e-5f,
                $"{scalar} moved from {fromLow[i]} to {fromHigh[i]} when only the title's OWN rating changed — its label is still inside its own feature.");
        }
    }

    [Fact]
    public async Task Missing_crowd_data_is_flagged_and_imputed_neutrally()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Half the catalogue has no Trakt rating and a third has no runtime. Telling the trainer
        // "0 minutes, 0/10, zero popularity" invents a strong false negative; the presence flags plus
        // a mid-distribution fill-in are what "we don't know" is supposed to look like.
        var accountId = await SeedAccountAsync();
        var blank = await SeedBareTitleAsync("No Crowd Data", runtime: null, traktRating: null, popularity: null);
        var known = await SeedBareTitleAsync("Full Crowd Data", runtime: 128, traktRating: 8.1m, popularity: 44m);

        var vectors = await BuildAsync(accountId, [blank, known]);

        var missing = vectors[blank];
        Assert.Equal(0f, missing[IndexOf("hasRuntime")]);
        Assert.Equal(0f, missing[IndexOf("hasTraktRating")]);
        Assert.Equal(0f, missing[IndexOf("hasPopularity")]);
        Assert.Equal(FeatureSchema.NeutralRuntimeMinutes(MediaType.Movie), missing[IndexOf("runtimeMinutes")]);
        Assert.True(missing[IndexOf("traktRating")] > 0f, "a missing crowd rating must not read as 0/10");
        Assert.Equal(TasteMath.Log1p(FeatureSchema.NeutralPopularity(MediaType.Movie)), missing[IndexOf("tmdbPopularityLog")], 4);

        var present = vectors[known];
        Assert.Equal(1f, present[IndexOf("hasRuntime")]);
        Assert.Equal(1f, present[IndexOf("hasTraktRating")]);
        Assert.Equal(1f, present[IndexOf("hasPopularity")]);
        Assert.Equal(128f, present[IndexOf("runtimeMinutes")]);
        Assert.Equal(8.1f, present[IndexOf("traktRating")], 3);
    }

    private async Task<Dictionary<Guid, float[]>> BuildAsync(Guid accountId, IReadOnlyCollection<Guid> titleIds)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = fixture.CreateContext(scoped);
        var builder = new FeatureVectorBuilder(db);
        var now = DateTime.UtcNow;
        var taste = await builder.BuildTasteStateAsync(accountId, now, CancellationToken.None);
        return await builder.BuildAsync(taste, titleIds, now, CancellationToken.None);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"leakage-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string name, float[] embedding, decimal? traktRating = 7m, int? year = 2000)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = NewTitle(name, runtime: 100, traktRating, popularity: 12m, year);
        db.Add(title);
        db.TitleEmbeddings.Add(new TitleEmbedding
        {
            TitleId = title.Id,
            Embedding = new Vector(embedding),
            EmbeddingModel = "test",
            SourceTextHash = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task<Guid> SeedBareTitleAsync(string name, int? runtime, decimal? traktRating, decimal? popularity)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = NewTitle(name, runtime, traktRating, popularity, year: 2010);
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private static Title NewTitle(string name, int? runtime, decimal? traktRating, decimal? popularity, int? year) => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Movie,
        TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
        TraktSlug = string.Empty,
        Name = name,
        Language = "en",
        // No poster: every discovery lane filters on PosterPath != null, so these fixtures cannot
        // leak into the candidate pools of the other tests sharing this database.
        PosterPath = null,
        Genres = ["drama", "thriller"],
        Year = year,
        RuntimeMinutes = runtime,
        TraktRating = traktRating,
        TraktVotes = 500,
        TmdbPopularity = popularity,
        ReleasedAt = year is { } y ? new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
        CreatedAt = DateTime.UtcNow,
    };

    private async Task<Guid> SeedPersonAsync(string name)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var person = new Person
        {
            Id = Guid.NewGuid(),
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            Name = name,
        };
        db.Add(person);
        await db.SaveChangesAsync();
        return person.Id;
    }

    private async Task SeedCreditAsync(Guid titleId, Guid personId, CreditRole role, int? castOrder)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.TitleCredits.Add(new TitleCredit
        {
            Id = Guid.NewGuid(),
            TitleId = titleId,
            PersonId = personId,
            Role = role,
            CastOrder = castOrder,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedRatingAsync(Guid accountId, Guid titleId, short rating, DateTime ratedAt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = rating,
            RatedAt = ratedAt,
            SyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A normalized 1536-vector close to one axis — distinct but overlapping taste directions.</summary>
    private static float[] NearUnit(int axis, float nudge)
    {
        var v = new float[1536];
        v[axis % 1500] = 1f;
        v[(axis % 1500) + 1] = nudge;
        v[0] += 0.2f; // shared component so distinct titles are similar but never identical
        var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
        for (var i = 0; i < v.Length; i++)
        {
            v[i] /= norm;
        }

        return v;
    }
}
