using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
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
public class FeatureVectorLeakageTests(PostgresFixture fixture) : EmbeddedCatalogTestBase(fixture)
{
    /// <summary>
    /// A synthetic LANGUAGE stamped on every title this class seeds.
    ///
    /// <para>It is the ELIGIBILITY half of this class's isolation, and only that half: eligibility
    /// defaults to English-only when an account configures no <c>Language</c> filter, so a
    /// non-English row cannot enter another account's pool through any predicate. The other half —
    /// the shared HNSW index, which no predicate can reach because the interference happens inside
    /// it — belongs to <see cref="EmbeddedCatalogTestBase"/>, which removes the embedding rows this
    /// test added when it ends. Schema v6 roughly tripled this class's embedded fixture count,
    /// which is what made an already-marginal <c>LiveSearchExpansionTests</c> assertion tip over;
    /// the base class is the fix, and the leftover <c>Title</c> rows it deliberately tolerates are
    /// exactly what this scope keeps harmless.</para>
    /// </summary>
    private readonly string _scope = $"fx-{Guid.NewGuid():N}"[..8];

    private static int IndexOf(string scalar) => Array.IndexOf(FeatureSchema.Scalars, scalar);

    [Fact]
    public async Task SimTopLovedMax_is_strictly_below_one_for_a_title_the_user_loved()
    {
        if (!Fixture.Available)
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
        if (!Fixture.Available)
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
        if (!Fixture.Available)
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
        if (!Fixture.Available)
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
        if (!Fixture.Available)
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
        if (!Fixture.Available)
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
        if (!Fixture.Available)
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
        if (!Fixture.Available)
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
        if (!Fixture.Available)
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

    // ── Schema v6: the watch history ────────────────────────────────────────────────────

    [Fact]
    public async Task A_watched_unrated_title_is_never_compared_against_a_centroid_it_is_still_inside()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // simWatchedCentroid's leave-one-out, proved the only way it can be: two accounts with the
        // SAME watch history except that one of them has not watched the title under test. The pool
        // minus a member must equal a pool that never held it, or a member is being described by a
        // centroid it is still inside — the embedding-side twin of the simTopLovedMax leak above.
        var withMember = await SeedAccountAsync();
        var without = await SeedAccountAsync();

        var watched = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            watched.Add(await SeedTitleAsync($"Watched {i}", NearUnit(200 + i, 0.4f)));
        }

        // Both accounts need a rating history, or the taste state has no mean to work from — and
        // these titles must be rated by NEITHER account, or they leave the watched-unrated pool.
        foreach (var accountId in new[] { withMember, without })
        {
            for (var i = 0; i < 5; i++)
            {
                var other = await SeedBareTitleAsync($"Rated {accountId:N}-{i}", 100, 7m, 10m);
                await SeedRatingAsync(accountId, other, (short)(6 + (i % 3)), DateTime.UtcNow.AddDays(-60 - i));
            }
        }

        var subject = watched[0];
        foreach (var id in watched)
        {
            await SeedWatchedAsync(withMember, id, plays: 1, DateTime.UtcNow.AddDays(-40));
            if (id != subject)
            {
                await SeedWatchedAsync(without, id, plays: 1, DateTime.UtcNow.AddDays(-40));
            }
        }

        var sim = IndexOf("simWatchedCentroid");
        var has = IndexOf("hasWatchedCentroid");

        var member = (await BuildAsync(withMember, [subject]))[subject];
        var stranger = (await BuildAsync(without, [subject]))[subject];

        Assert.Equal(1f, member[has]);
        Assert.Equal(1f, stranger[has]);
        Assert.True(
            member[sim] < 1f,
            $"simWatchedCentroid was {member[sim]} — a pool member is still matching its own embedding.");
        Assert.True(member[sim] > 0f, "the feature has to be doing something, or the test proves nothing");
        Assert.Equal(stranger[sim], member[sim], 4);
    }

    [Fact]
    public async Task An_account_with_nothing_watched_and_unrated_gets_a_flag_of_zero_not_a_similarity_of_zero()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // The v2 lesson, re-stated for v6: "the pool is empty" and "the cosine happens to be 0" must
        // not be spelled the same way. Every title this account has watched, it also rated — so the
        // watched-unrated pool is empty by construction and the flag, not the value, says so.
        var accountId = await SeedAccountAsync();
        var candidate = await SeedTitleAsync("Candidate", NearUnit(300, 0.3f));
        for (var i = 0; i < 5; i++)
        {
            var rated = await SeedTitleAsync($"Rated and watched {i}", NearUnit(310 + i, 0.3f));
            await SeedRatingAsync(accountId, rated, rating: 8, DateTime.UtcNow.AddDays(-30 - i));
            await SeedWatchedAsync(accountId, rated, plays: 1, DateTime.UtcNow.AddDays(-31 - i));
        }

        var vector = (await BuildAsync(accountId, [candidate]))[candidate];

        Assert.Equal(0f, vector[IndexOf("hasWatchedCentroid")]);
        Assert.Equal(0f, vector[IndexOf("simWatchedCentroid")]);
        Assert.Equal(0f, vector[IndexOf("watchedGenreShare")]);

        // …while the REWATCH pool is a different pool and takes rated titles, so its flag is silent
        // here only because nothing was watched twice.
        Assert.Equal(0f, vector[IndexOf("hasRewatchCentroid")]);
    }

    [Fact]
    public async Task A_rewatched_training_row_is_taken_back_out_of_the_plays_weighted_centroid()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // The rewatch pool is the one v6 pool a TRAINING row is routinely a member of — it takes
        // rated titles, because a title watched three times and rated 9 is evidence twice over. So
        // this is where the leave-one-out actually fires in training, and the subtraction has to
        // carry the member's OWN min(plays, 3) weight rather than an average of everyone's.
        var withMember = await SeedAccountAsync();
        var without = await SeedAccountAsync();

        var subject = await SeedTitleAsync("Rewatched and rated", NearUnit(400, 0.35f));
        var others = new List<(Guid Id, int Plays)>();
        for (var i = 0; i < 6; i++)
        {
            others.Add((await SeedTitleAsync($"Rewatched {i}", NearUnit(410 + i, 0.35f)), 2 + (i % 3)));
        }

        foreach (var accountId in new[] { withMember, without })
        {
            for (var i = 0; i < 5; i++)
            {
                var filler = await SeedBareTitleAsync($"Filler {accountId:N}-{i}", 100, 7m, 10m);
                await SeedRatingAsync(accountId, filler, (short)(6 + (i % 3)), DateTime.UtcNow.AddDays(-70 - i));
            }

            foreach (var (id, plays) in others)
            {
                await SeedWatchedAsync(accountId, id, plays, DateTime.UtcNow.AddDays(-25));
            }
        }

        // The subject is rated by BOTH accounts — so it is a training row for both — but rewatched by
        // only one. Watched once is not a rewatch, so it stays out of the pool for `without`.
        await SeedRatingAsync(withMember, subject, rating: 9, DateTime.UtcNow.AddDays(-20));
        await SeedRatingAsync(without, subject, rating: 9, DateTime.UtcNow.AddDays(-20));
        await SeedWatchedAsync(withMember, subject, plays: 3, DateTime.UtcNow.AddDays(-20));
        await SeedWatchedAsync(without, subject, plays: 1, DateTime.UtcNow.AddDays(-20));

        var sim = IndexOf("simRewatchCentroid");
        var member = (await BuildAsync(withMember, [subject]))[subject];
        var stranger = (await BuildAsync(without, [subject]))[subject];

        Assert.Equal(1f, member[IndexOf("hasRewatchCentroid")]);
        Assert.True(
            member[sim] < 1f,
            $"simRewatchCentroid was {member[sim]} — a rewatched training row is matching itself.");
        Assert.True(member[sim] > 0f);
        Assert.Equal(stranger[sim], member[sim], 4);
    }

    [Fact]
    public async Task The_rewatch_pool_takes_a_title_rated_below_the_loved_bar()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // "Rated or not" means rated ANYTHING. A title watched three times and rated 6 is a rewatch,
        // and it is arguably the most interesting kind — a comfort watch the user would never call a
        // favourite. This is a regression test for a real bug found while writing this bump: the
        // rewatch centroid drew its embeddings from the LOVED dictionary, so every rewatch rated
        // below 8 fell out of the pool without a trace.
        var accountId = await SeedAccountAsync();
        var comfort = await SeedTitleAsync("Comfort Watch", NearUnit(600, 0.3f));
        var loved = await SeedTitleAsync("Loved Rewatch", NearUnit(610, 0.3f));

        await SeedRatingAsync(accountId, comfort, rating: 6, DateTime.UtcNow.AddDays(-30));
        await SeedRatingAsync(accountId, loved, rating: 9, DateTime.UtcNow.AddDays(-30));
        await SeedWatchedAsync(accountId, comfort, plays: 3, DateTime.UtcNow.AddDays(-15));
        await SeedWatchedAsync(accountId, loved, plays: 2, DateTime.UtcNow.AddDays(-15));

        var taste = await TasteAsync(accountId, DateTime.UtcNow);

        Assert.Contains(comfort, taste.Self.RewatchWeights.Keys);
        Assert.Contains(loved, taste.Self.RewatchWeights.Keys);

        // …and the weight is min(plays, 3), so the comfort watch outweighs the loved one here. The
        // pool is behaviour, not opinion; the rating decides nothing inside it.
        Assert.Equal(3d, taste.Self.RewatchWeights[comfort]);
        Assert.Equal(2d, taste.Self.RewatchWeights[loved]);
        Assert.Equal(5d, taste.Self.RewatchWeightTotal);
    }

    [Fact]
    public async Task Watched_and_unrated_is_evaluated_as_of_the_cut_and_not_as_of_today()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // THE leakage rule of this bump, and the one that is invisible if you test at asOf = now.
        // A title watched in January and rated in June is watched-AND-UNRATED for a vector built in
        // March; using today's ratings to decide would remove it from the March pool because of
        // something that had not happened yet. That is future information, and it would delete every
        // holdout title from the pool exactly when the holdout is what is being measured.
        var accountId = await SeedAccountAsync();
        var subject = await SeedTitleAsync("Watched then rated", NearUnit(500, 0.3f));

        for (var i = 0; i < 5; i++)
        {
            var filler = await SeedBareTitleAsync($"Filler {i}", 100, 7m, 10m);
            await SeedRatingAsync(accountId, filler, (short)(6 + (i % 3)), DateTime.UtcNow.AddDays(-200 - i));
        }

        await SeedWatchedAsync(accountId, subject, plays: 1, DateTime.UtcNow.AddDays(-100));
        await SeedRatingAsync(accountId, subject, rating: 9, DateTime.UtcNow.AddDays(-10));

        var duringTheGap = await TasteAsync(accountId, DateTime.UtcNow.AddDays(-50));
        var today = await TasteAsync(accountId, DateTime.UtcNow);

        Assert.Contains(subject, duringTheGap.Self.WatchedUnratedTitleIds);
        Assert.DoesNotContain(subject, today.Self.WatchedUnratedTitleIds);

        // …and the complement holds in both directions, which is what makes the two pools a partition
        // rather than two independent queries that can disagree.
        Assert.DoesNotContain(subject, duringTheGap.Self.Ratings.Keys);
        Assert.Contains(subject, today.Self.Ratings.Keys);

        // A watch that had not happened yet is not a member either — the pool is bounded on both
        // sides by asOf, not just on the rating side.
        var beforeTheWatch = await TasteAsync(accountId, DateTime.UtcNow.AddDays(-150));
        Assert.DoesNotContain(subject, beforeTheWatch.Self.WatchedUnratedTitleIds);
    }

    [Fact]
    public async Task Watched_genre_share_measures_reach_into_the_watched_pool()
    {
        if (!Fixture.Available)
        {
            return;
        }

        // The genre half of the same signal, and the one that keeps working for the ~8 titles in
        // every hundred that carry no embedding at all. Six of ten watched-unrated titles are drama,
        // two are western, two are documentary — so a drama candidate reaches 0.6 and a western one
        // 0.2, and the spread between them is the whole feature.
        var accountId = await SeedAccountAsync();
        for (var i = 0; i < 5; i++)
        {
            var filler = await SeedBareTitleAsync($"Share filler {i}", 100, 7m, 10m, ["comedy"]);
            await SeedRatingAsync(accountId, filler, (short)(6 + (i % 3)), DateTime.UtcNow.AddDays(-90 - i));
        }

        var pool = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            var genres = i < 6 ? new[] { "drama" } : i < 8 ? ["western"] : new[] { "documentary" };
            var id = await SeedBareTitleAsync($"Watched genre {i}", 100, 7m, 10m, genres);
            await SeedWatchedAsync(accountId, id, plays: 1, DateTime.UtcNow.AddDays(-45));
            pool.Add(id);
        }

        var drama = await SeedBareTitleAsync("Drama candidate", 100, 7m, 10m, ["drama"]);
        var western = await SeedBareTitleAsync("Western candidate", 100, 7m, 10m, ["western"]);
        var holiday = await SeedBareTitleAsync("Holiday candidate", 100, 7m, 10m, ["holiday"]);

        var share = IndexOf("watchedGenreShare");
        var vectors = await BuildAsync(accountId, [drama, western, holiday, pool[0]]);

        Assert.Equal(0.6f, vectors[drama][share], 4);
        Assert.Equal(0.2f, vectors[western][share], 4);
        // Nothing in the pool shares the genre: low, not missing. hasEmbedding is what says "we know
        // nothing about this title"; this scalar says "nothing you watched looks like it".
        Assert.Equal(0f, vectors[holiday][share], 4);

        // A pool MEMBER never counts itself: 5 of the other 9, not 6 of 10. The training path cannot
        // reach this branch (a rated row is not a pool member by construction), but the title-detail
        // path scores watched-and-unrated titles directly, and it would otherwise inflate every one
        // of them by 1/|pool|.
        Assert.Equal(5f / 9f, vectors[pool[0]][share], 4);
    }

    private async Task<Dictionary<Guid, float[]>> BuildAsync(Guid accountId, IReadOnlyCollection<Guid> titleIds)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = Fixture.CreateContext(scoped);
        var builder = new FeatureVectorBuilder(db);
        var now = DateTime.UtcNow;
        var taste = await builder.BuildTasteStateAsync(accountId, now, CancellationToken.None);
        return await builder.BuildAsync(taste, titleIds, now, CancellationToken.None);
    }

    private async Task<FeatureVectorBuilder.TasteState> TasteAsync(Guid accountId, DateTime asOf)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = Fixture.CreateContext(scoped);
        return await new FeatureVectorBuilder(db).BuildTasteStateAsync(accountId, asOf, CancellationToken.None);
    }

    private async Task SeedWatchedAsync(Guid accountId, Guid titleId, int plays, DateTime lastWatchedAt)
    {
        await using var db = Fixture.CreateContext(new AccountContext());
        db.WatchedTitles.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Plays = plays,
            LastWatchedAt = lastWatchedAt,
            IsFullyWatched = true,
            FirstSyncedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = Fixture.CreateContext(new AccountContext());
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
        await using var db = Fixture.CreateContext(new AccountContext());
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
        await using var db = Fixture.CreateContext(new AccountContext());
        var title = NewTitle(name, runtime, traktRating, popularity, year: 2010, genres);
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private Title NewTitle(
        string name, int? runtime, decimal? traktRating, decimal? popularity, int? year, string[]? genres = null) => new()
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = name,
            // The cleanup handle — see _scope. Nothing in FeatureVectorBuilder reads Language, so
            // this is invisible to every assertion in this file and visible to DisposeAsync alone.
            Language = _scope,
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
        await using var db = Fixture.CreateContext(new AccountContext());
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
        await using var db = Fixture.CreateContext(new AccountContext());
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
        await using var db = Fixture.CreateContext(new AccountContext());
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
