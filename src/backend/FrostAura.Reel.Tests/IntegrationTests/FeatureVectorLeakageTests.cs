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

    [Fact]
    public async Task An_all_unknown_cast_is_no_longer_imputed_as_a_solidly_average_one()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The v5 credit fix, end to end. Two titles, identical but for WHO is in them: one crewed by
        // people the user rates well, one by people they have never encountered. Before v5 the
        // second scored director/cast/writer at the user's own mean behind hasCredits = 1, and those
        // slots turned up as top POSITIVE contributors on exactly the titles with nobody known in
        // them — "we know nothing" sold to the model as "solidly average for you".
        var accountId = await SeedAccountAsync();

        var knownDirector = await SeedPersonAsync("Known Director");
        var knownWriter = await SeedPersonAsync("Known Writer");
        var knownActor = await SeedPersonAsync("Known Actor");
        var strangers = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            strangers.Add(await SeedPersonAsync($"Stranger {i}"));
        }

        // Give the two known people a rating history the user actually has, via other titles.
        for (var i = 0; i < 4; i++)
        {
            var backCatalogue = await SeedBareTitleAsync($"Back catalogue {i}", 100, 7m, 10m);
            await SeedCreditAsync(backCatalogue, knownDirector, CreditRole.Director, null);
            await SeedCreditAsync(backCatalogue, knownWriter, CreditRole.Writer, null);
            await SeedCreditAsync(backCatalogue, knownActor, CreditRole.Actor, 0);
            await SeedRatingAsync(accountId, backCatalogue, rating: 9, DateTime.UtcNow.AddDays(-100 - i));
        }

        var known = await SeedBareTitleAsync("Known Crew", 100, 7m, 10m);
        await SeedCreditAsync(known, knownDirector, CreditRole.Director, null);
        await SeedCreditAsync(known, knownWriter, CreditRole.Writer, null);
        await SeedCreditAsync(known, knownActor, CreditRole.Actor, 0);

        var unknown = await SeedBareTitleAsync("Twelve Strangers", 100, 7m, 10m);
        foreach (var stranger in strangers)
        {
            await SeedCreditAsync(unknown, stranger, CreditRole.Actor, 0);
        }

        var vectors = await BuildAsync(accountId, [known, unknown]);
        var knownVector = vectors[known];
        var unknownVector = vectors[unknown];

        // hasCredits keeps its literal meaning on BOTH — which is precisely why it could never carry
        // the knowledge signal, and why the two new scalars exist.
        Assert.Equal(1f, knownVector[IndexOf("hasCredits")]);
        Assert.Equal(1f, unknownVector[IndexOf("hasCredits")]);

        // The knowledge signal, which is the part that now differs.
        Assert.Equal(1f, knownVector[IndexOf("knownPeopleFraction")], 4);
        Assert.Equal(0f, unknownVector[IndexOf("knownPeopleFraction")], 4);
        Assert.Equal(1f, knownVector[IndexOf("knownDirectorOrWriter")]);
        Assert.Equal(0f, unknownVector[IndexOf("knownDirectorOrWriter")]);

        // And the imputed affinities now sit BELOW the user's mean rather than on it. Every rating
        // this account has is a 9, so the mean is exactly 9 and the imputation is exactly 8.5 —
        // which makes the constant checkable rather than merely directional.
        const float userMean = 9f;
        var neutralLow = userMean - (float)FeatureSchema.NeutralLowPersonAffinityOffset;

        foreach (var scalar in new[] { "directorAffinity", "castAffinity", "writerAffinity" })
        {
            Assert.Equal(neutralLow, unknownVector[IndexOf(scalar)], 4);
            Assert.True(
                unknownVector[IndexOf(scalar)] < knownVector[IndexOf(scalar)],
                $"{scalar} on an all-unknown title ({unknownVector[IndexOf(scalar)]}) must not match a known one ({knownVector[IndexOf(scalar)]}).");
            Assert.True(
                unknownVector[IndexOf(scalar)] > 0f,
                $"{scalar} must still be a mid-scale imputation, never a false 0 — was {unknownVector[IndexOf(scalar)]}.");
        }
    }

    [Fact]
    public async Task A_role_with_no_credit_row_at_all_is_imputed_low_rather_than_at_the_mean()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The narrower case the first test surfaced while being written: a title with a well-known
        // CAST but no director or writer row at all. Those two slots used to read as the user's own
        // mean — a positive number asserted about people who are not merely unrated but not even
        // recorded — which is how directorAffinity turned up as a top contributor on unscripted
        // television, where director and writer credits genuinely do not exist.
        var accountId = await SeedAccountAsync();
        var actor = await SeedPersonAsync("Well Known Actor");
        for (var i = 0; i < 4; i++)
        {
            var backCatalogue = await SeedBareTitleAsync($"Cast history {i}", 100, 7m, 10m);
            await SeedCreditAsync(backCatalogue, actor, CreditRole.Actor, 0);
            await SeedRatingAsync(accountId, backCatalogue, rating: 8, DateTime.UtcNow.AddDays(-80 - i));
        }

        var castOnly = await SeedBareTitleAsync("Cast Only", 45, 7m, 30m);
        await SeedCreditAsync(castOnly, actor, CreditRole.Actor, 0);

        var vector = (await BuildAsync(accountId, [castOnly]))[castOnly];
        var neutralLow = 8f - (float)FeatureSchema.NeutralLowPersonAffinityOffset;

        // Every credited person IS known here — the fraction is 1 — but there is no above-the-line
        // credit, and the two facts are now stated separately instead of collapsed into hasCredits.
        Assert.Equal(1f, vector[IndexOf("hasCredits")]);
        Assert.Equal(1f, vector[IndexOf("knownPeopleFraction")], 4);
        Assert.Equal(0f, vector[IndexOf("knownDirectorOrWriter")]);
        Assert.Equal(neutralLow, vector[IndexOf("directorAffinity")], 4);
        Assert.Equal(neutralLow, vector[IndexOf("writerAffinity")], 4);
        Assert.True(vector[IndexOf("castAffinity")] > neutralLow, "the known cast must still read above the imputation");
    }

    [Fact]
    public async Task Known_people_fraction_obeys_leave_one_out()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The v5 scalars are pooled statistics like every other user-side feature, so they carry the
        // same obligation: a training row must not count ITSELF as the evidence that its people are
        // known. Here the actor's ONLY connection to the user is this very title — so after the
        // subtraction they are unknown, exactly as they would be at serving time where the candidate
        // is unrated by definition. Without the subtraction, knownPeopleFraction would read 1.0 on
        // every rated title and 0 on every candidate: a perfect train/serve tell.
        var accountId = await SeedAccountAsync();
        var soleActor = await SeedPersonAsync("Sole Appearance");
        var subject = await SeedBareTitleAsync("Only Credit", 100, 7m, 10m);
        await SeedCreditAsync(subject, soleActor, CreditRole.Actor, 0);

        // Unrelated ratings so the account has a mean and the pools are not empty.
        for (var i = 0; i < 5; i++)
        {
            var other = await SeedBareTitleAsync($"Other {i}", 100, 7m, 10m);
            await SeedRatingAsync(accountId, other, (short)(6 + (i % 3)), DateTime.UtcNow.AddDays(-50 - i));
        }

        var beforeRating = (await BuildAsync(accountId, [subject]))[subject];
        Assert.Equal(0f, beforeRating[IndexOf("knownPeopleFraction")], 4);

        await SeedRatingAsync(accountId, subject, rating: 10, DateTime.UtcNow.AddDays(-10));
        var afterRating = (await BuildAsync(accountId, [subject]))[subject];

        Assert.Equal(
            beforeRating[IndexOf("knownPeopleFraction")],
            afterRating[IndexOf("knownPeopleFraction")],
            4);
        Assert.Equal(
            beforeRating[IndexOf("knownDirectorOrWriter")],
            afterRating[IndexOf("knownDirectorOrWriter")]);
    }

    [Fact]
    public async Task Unscripted_titles_light_their_own_genre_slots_instead_of_none_at_all()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Before v5, `reality` / `soap` / `talk-show` / `news` / `game-show` had no slot in the genre
        // block, so a reality show lit ZERO of the 25 one-hots — indistinguishable from a title with
        // no genres at all. That is what let it sit at the top of a page decided by a genre demotion
        // it structurally could not attract.
        var accountId = await SeedAccountAsync();
        var reality = await SeedBareTitleAsync("Reality Show", 45, 7m, 30m, ["reality"]);
        var soap = await SeedBareTitleAsync("Soap", 45, 7m, 30m, ["soap"]);

        var vectors = await BuildAsync(accountId, [reality, soap]);
        var genreOffset = FeatureSchema.Scalars.Length;
        var all = FeatureSchema.AllNames();

        var realityVector = vectors[reality];
        Assert.Equal(1f, realityVector[genreOffset + Array.IndexOf(FeatureSchema.Genres, "reality")]);
        Assert.Equal(1f, realityVector[IndexOf("isUnscripted")]);

        // A soap gets its own slot but is NOT unscripted — it is scripted, it merely shares the
        // scheduling economics. One vocabulary, and it is UnscriptedFormats' vocabulary.
        var soapVector = vectors[soap];
        Assert.Equal(1f, soapVector[genreOffset + Array.IndexOf(FeatureSchema.Genres, "soap")]);
        Assert.Equal(0f, soapVector[IndexOf("isUnscripted")]);

        // Exactly one genre one-hot each — no stray slot lit by the new block.
        foreach (var (vector, slug) in new[] { (realityVector, "reality"), (soapVector, "soap") })
        {
            var lit = Enumerable.Range(0, FeatureSchema.Genres.Length)
                .Where(g => vector[genreOffset + g] > 0f)
                .Select(g => all[genreOffset + g])
                .ToList();
            Assert.Equal([$"genre:{slug}"], lit);
        }
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

    private async Task<Guid> SeedBareTitleAsync(
        string name, int? runtime, decimal? traktRating, decimal? popularity, string[]? genres = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = NewTitle(name, runtime, traktRating, popularity, year: 2010, genres);
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private static Title NewTitle(
        string name, int? runtime, decimal? traktRating, decimal? popularity, int? year, string[]? genres = null) => new()
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
            Genres = genres ?? ["drama", "thriller"],
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
