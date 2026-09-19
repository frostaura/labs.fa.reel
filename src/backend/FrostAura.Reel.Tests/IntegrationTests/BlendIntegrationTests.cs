using System.Text.Json;
using FrostAura.Reel.Application.Blend;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// Blend on real Postgres, deliberately run through a context PINNED to account A — the way an
/// HTTP request arrives — so the unscoped eligibility/score reads must actually cross the
/// tenancy filters rather than ride an already-open background context. Skips when
/// REEL_TEST_CONNECTION_STRING is unset.
/// </summary>
[Collection("postgres")]
public class BlendIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Joint_picks_honor_both_exclusion_sets_and_order_by_min_prediction()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Shared five-title catalogue:
        //   watchedByA  — must vanish because A watched it (A's exclusion set)
        //   watchedByB  — must vanish because B watched it (B's exclusion set)
        //   horror      — must vanish because B excludes the genre (B's content filters)
        //   strongJoint — 9.0 for A, 8.0 for B → joint 8.0
        //   weakJoint   — 8.5 for A, 7.0 for B → joint 7.0 (higher for A, weaker jointly)
        var accountA = await SeedAccountAsync();
        var accountB = await SeedAccountAsync();
        var watchedByA = await SeedTitleAsync("drama");
        var watchedByB = await SeedTitleAsync("drama");
        var horror = await SeedTitleAsync("horror");
        var strongJoint = await SeedTitleAsync("drama");
        var weakJoint = await SeedTitleAsync("drama");
        var pool = new[] { watchedByA, watchedByB, horror, strongJoint, weakJoint };

        await SeedWatchedAsync(accountA, watchedByA);
        await SeedWatchedAsync(accountB, watchedByB);
        await SeedGenreExclusionAsync(accountB, "horror");

        // Every pool title scores HIGH for both accounts — the excluded ones highest of all, so
        // any exclusion leak would immediately surface at the top of the picks.
        var artifactA = await SeedActiveArtifactAsync(accountA);
        var artifactB = await SeedActiveArtifactAsync(accountB);
        await SeedScoreAsync(accountA, artifactA, watchedByA, 9.9m);
        await SeedScoreAsync(accountB, artifactB, watchedByA, 9.9m);
        await SeedScoreAsync(accountA, artifactA, watchedByB, 9.8m);
        await SeedScoreAsync(accountB, artifactB, watchedByB, 9.8m);
        await SeedScoreAsync(accountA, artifactA, horror, 9.5m);
        await SeedScoreAsync(accountB, artifactB, horror, 9.5m);
        await SeedScoreAsync(accountA, artifactA, strongJoint, 9.0m);
        await SeedScoreAsync(accountB, artifactB, strongJoint, 8.0m);
        await SeedScoreAsync(accountA, artifactA, weakJoint, 8.5m);
        await SeedScoreAsync(accountB, artifactB, weakJoint, 7.0m);

        // Session pinned to A, exactly like the HTTP path that serves /api/blend.
        var sessionContext = new AccountContext();
        sessionContext.SetAccount(accountA);
        await using var db = fixture.CreateContext(sessionContext);
        var eligibility = new EligibilityQueryBuilder(db);
        var blend = new BlendService(db, eligibility);

        // The intersection must honor BOTH accounts' exclusion sets even though the session
        // only carries A: A's watch removes watchedByA, B's watch removes watchedByB, and B's
        // genre filter removes horror.
        var mine = await eligibility.EligibleTitleIdsUnscopedAsync(accountA);
        var theirs = await eligibility.EligibleTitleIdsUnscopedAsync(accountB);
        mine.IntersectWith(theirs);
        Assert.Equal(
            new[] { strongJoint, weakJoint }.ToHashSet(),
            mine.Where(pool.Contains).ToHashSet());

        // Only the seeded scores exist for these two fresh accounts, so the picks are exactly
        // the two clean titles — ordered by min(A, B), not by either single account's score
        // (A alone would put weakJoint's 8.5 above strongJoint's... no: 9.0 > 8.5, so order by
        // B's side proves the min: strongJoint min 8.0 > weakJoint min 7.0).
        var picks = await blend.JointPicksAsync(accountA, accountB, 20, CancellationToken.None);
        Assert.Equal([strongJoint, weakJoint], picks.Select(p => p.Title.Id).ToList());

        var top = picks[0];
        Assert.Equal(9.0m, top.ForYou);
        Assert.Equal(8.0m, top.ForThem);
        Assert.Equal(8.0m, top.Joint);
        Assert.Equal(7.0m, picks[1].Joint);
    }

    [Fact]
    public async Task Codes_resolve_owners_and_regeneration_invalidates_the_old_code()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await using var db = fixture.CreateContext(new AccountContext());
        var blend = new BlendService(db, new EligibilityQueryBuilder(db));

        var firstCode = await blend.RegenerateCodeAsync(accountId, CancellationToken.None);
        Assert.Equal(BlendService.CodeLength, firstCode.Length);
        Assert.Equal(accountId, (await blend.FindOwnerByCodeAsync(firstCode, CancellationToken.None))?.Id);

        // Case-insensitive lookup — codes get read aloud and typed back lowercased.
        Assert.Equal(
            accountId,
            (await blend.FindOwnerByCodeAsync(firstCode.ToLowerInvariant(), CancellationToken.None))?.Id);

        // Unknown code (right shape, never issued): null — the endpoint maps this to 404.
        Assert.Null(await blend.FindOwnerByCodeAsync("~~~~~~~~", CancellationToken.None));

        var secondCode = await blend.RegenerateCodeAsync(accountId, CancellationToken.None);
        Assert.NotEqual(firstCode, secondCode);
        Assert.Null(await blend.FindOwnerByCodeAsync(firstCode, CancellationToken.None));
        Assert.Equal(accountId, (await blend.FindOwnerByCodeAsync(secondCode, CancellationToken.None))?.Id);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"blend-{Guid.NewGuid():N}",
            DisplayName = "Blend Fixture",
            Tier = AccountTier.Paid,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string genre)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"blend-fixture {Guid.NewGuid():N}",
            // Eligibility defaults to English-only when no language filter is configured.
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = [genre],
            // Bottom of the popularity order so these fixtures never displace another
            // integration test's top-N assertions on the shared catalogue.
            TmdbPopularity = 0m,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task SeedWatchedAsync(Guid accountId, Guid titleId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.WatchedTitles.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Plays = 1,
            IsFullyWatched = true,
            FirstSyncedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedGenreExclusionAsync(Guid accountId, string genre)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = FilterKind.ExcludeGenre,
            Value = genre,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedActiveArtifactAsync(Guid accountId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var artifact = new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            ArtifactBytes = [1],
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
            // The schema-compat shadow column projects 'version' out of this jsonb; without a
            // current version the artifact is invisible to every Active-artifact read.
            FeatureSchemaJson = JsonSerializer.Serialize(new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
        };
        db.Add(artifact);
        await db.SaveChangesAsync();
        return artifact.Id;
    }

    private async Task SeedScoreAsync(Guid accountId, Guid artifactId, Guid titleId, decimal predicted)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.TitleScores.Add(new TitleScore
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            ModelArtifactId = artifactId,
            PredictedRating = predicted,
            ScoredAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
