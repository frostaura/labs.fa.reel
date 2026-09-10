using System.Text.Json;
using FrostAura.Reel.Api.Endpoints;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The rate grid's query, on real Postgres — the only place its content-filter predicates and its
/// uncertainty ordering can be trusted, since both are SQL.
///
/// The bug this replaces: <c>rate-suggestions</c> returned the top 30 catalogue titles by TMDB
/// popularity that the user had not rated. No content filters, so it could offer titles from the
/// eight genres the founder has excluded; no uncertainty targeting; and no preference at all for
/// the ~1,300 titles already WATCHED and never scored — the highest-information, zero-recall-burden
/// labels in the database, invisible to the one screen built to collect labels.
///
/// Every assertion is scoped to the ids this test seeds, so a shared catalogue left by other
/// integration tests can neither satisfy nor break it.
/// </summary>
[Collection("postgres")]
public class RateSuggestionsTests(PostgresFixture fixture)
{
    /// <summary>
    /// A synthetic genre stamped on every title this instance seeds, with a matching IncludeGenre
    /// allowlist row on its account. The catalogue is GLOBAL and shared by every class in the
    /// postgres collection, so without this the rate grid's 30 slots are contested by whatever the
    /// sibling classes happened to seed. xUnit builds one instance per test, so the scope is unique
    /// per test and the assertions are about this test's rows and nothing else.
    /// </summary>
    private readonly string _scope = $"ratescope{Guid.NewGuid():N}";

    [Fact]
    public async Task Watched_but_unrated_titles_come_first_and_the_popularity_pool_only_fills_behind_them()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        // Watched, never rated — the whole point of the screen. Deliberately LESS popular than the
        // unwatched row below, so ranking them first cannot be an accident of the old popularity
        // order. (All of them sit at the bottom of the global order; see SeedTitleAsync.)
        var watchedA = await SeedTitleAsync(t => t.TmdbPopularity = 0.01m);
        var watchedB = await SeedTitleAsync(t => t.TmdbPopularity = 0.01m);
        // Never watched, the most popular row in this test's scope: under the old query rows like
        // this one were the entire answer.
        var popular = await SeedTitleAsync(t => { t.TmdbPopularity = 0.9m; t.TmdbVoteCount = 50_000; });
        // Watched AND rated — already told us, must not be asked again.
        var alreadyRated = await SeedTitleAsync(t => t.TmdbPopularity = 0.7m);

        await SeedWatchedAsync(accountId, watchedA, DateTime.UtcNow.AddDays(-2));
        await SeedWatchedAsync(accountId, watchedB, DateTime.UtcNow.AddDays(-9));
        await SeedWatchedAsync(accountId, alreadyRated, DateTime.UtcNow.AddDays(-1));
        await SeedRatingAsync(accountId, alreadyRated, 8);

        var mine = new[] { watchedA, watchedB, popular, alreadyRated };
        var suggestions = await SuggestAsync(accountId, mine);

        Assert.DoesNotContain(alreadyRated, suggestions.Select(s => s.TitleId));

        // Both watched rows precede the popular one, and each is labelled with the half it came
        // from — the page headlines a memory differently from a guess.
        var watchedIds = suggestions
            .Where(s => s.Source == TitleEndpoints.RateSuggestionSources.Watched)
            .Select(s => s.TitleId)
            .ToList();
        Assert.Equal(new HashSet<Guid> { watchedA, watchedB }, watchedIds.ToHashSet());
        Assert.Equal(
            new[] { TitleEndpoints.RateSuggestionSources.Popular },
            suggestions.Where(s => s.TitleId == popular).Select(s => s.Source).ToArray());
        Assert.True(
            suggestions.FindIndex(s => s.TitleId == popular) > suggestions.FindIndex(s => s.TitleId == watchedB),
            "the popularity pool fills behind the watched half, never in front of it");
    }

    [Fact]
    public async Task Watched_rows_are_ordered_by_how_close_the_prediction_sits_to_the_users_own_mean()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        // Two ratings of 6 and 8 ⇒ a mean of 7. A prediction AT the mean is the model declining to
        // commit, so it is the most informative thing to ask about; a confident 9.5 is the least.
        var anchorLow = await SeedTitleAsync(_ => { });
        var anchorHigh = await SeedTitleAsync(_ => { });
        await SeedRatingAsync(accountId, anchorLow, 6);
        await SeedRatingAsync(accountId, anchorHigh, 8);

        var atTheMean = await SeedTitleAsync(_ => { });
        var nearby = await SeedTitleAsync(_ => { });
        var confident = await SeedTitleAsync(_ => { });
        // Never scored: no prediction is no evidence of uncertainty, so it sorts LAST, not first.
        var unscored = await SeedTitleAsync(_ => { });

        var watchedAt = DateTime.UtcNow.AddDays(-3);
        foreach (var id in new[] { atTheMean, nearby, confident, unscored })
        {
            await SeedWatchedAsync(accountId, id, watchedAt);
        }

        await SeedActiveArtifactAndScoresAsync(accountId, new()
        {
            [atTheMean] = 7.0m,
            [nearby] = 7.6m,
            [confident] = 9.5m,
        });

        var mine = new[] { atTheMean, nearby, confident, unscored, anchorLow, anchorHigh };
        var suggestions = await SuggestAsync(accountId, mine);

        Assert.Equal(
            new[] { atTheMean, nearby, confident, unscored },
            suggestions.Select(s => s.TitleId).ToArray());
    }

    [Fact]
    public async Task With_nothing_rated_yet_the_watched_half_falls_back_to_most_recently_watched()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var oldest = await SeedTitleAsync(_ => { });
        var middle = await SeedTitleAsync(_ => { });
        var newest = await SeedTitleAsync(_ => { });

        await SeedWatchedAsync(accountId, oldest, DateTime.UtcNow.AddDays(-40));
        await SeedWatchedAsync(accountId, middle, DateTime.UtcNow.AddDays(-10));
        await SeedWatchedAsync(accountId, newest, DateTime.UtcNow.AddDays(-1));

        var suggestions = await SuggestAsync(accountId, [oldest, middle, newest]);

        Assert.Equal(new[] { newest, middle, oldest }, suggestions.Select(s => s.TitleId).ToArray());
    }

    [Fact]
    public async Task Both_halves_respect_the_accounts_content_filters()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        var watchedHorror = await SeedTitleAsync(t => t.Genres = ["horror"]);
        var watchedDrama = await SeedTitleAsync(t => t.Genres = ["drama"]);
        var popularHorror = await SeedTitleAsync(t =>
        {
            t.Genres = ["horror"];
            t.TmdbPopularity = 0.9m;
            t.TmdbVoteCount = 50_000;
        });
        var popularDrama = await SeedTitleAsync(t =>
        {
            t.Genres = ["drama"];
            t.TmdbPopularity = 0.8m;
            t.TmdbVoteCount = 50_000;
        });

        await SeedWatchedAsync(accountId, watchedHorror, DateTime.UtcNow.AddDays(-2));
        await SeedWatchedAsync(accountId, watchedDrama, DateTime.UtcNow.AddDays(-3));

        var mine = new[] { watchedHorror, watchedDrama, popularHorror, popularDrama };

        // No rule ⇒ all four are on offer.
        Assert.Equal(mine.ToHashSet(), (await SuggestAsync(accountId, mine)).Select(s => s.TitleId).ToHashSet());

        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "horror");

        var filtered = (await SuggestAsync(accountId, mine)).Select(s => s.TitleId).ToHashSet();
        Assert.Equal(new HashSet<Guid> { watchedDrama, popularDrama }, filtered);
    }

    [Fact]
    public async Task A_live_not_interested_reaction_still_excludes_a_title_and_revoking_it_restores_it()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var declined = await SeedTitleAsync(_ => { });
        await SeedWatchedAsync(accountId, declined, DateTime.UtcNow.AddDays(-2));
        var reactionId = await SeedReactionAsync(accountId, declined, ReactionKind.NotInterested);

        Assert.Empty(await SuggestAsync(accountId, [declined]));

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var reaction = await db.UserTitleReactions.SingleAsync(r => r.Id == reactionId);
            reaction.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        Assert.Single(await SuggestAsync(accountId, [declined]));
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs the query and narrows the answer to the ids this test seeded.</summary>
    private async Task<List<TitleEndpoints.RateSuggestion>> SuggestAsync(Guid accountId, IReadOnlyList<Guid> mine)
    {
        await using var db = fixture.CreateContext(Pinned(accountId));
        var all = await TitleEndpoints.BuildRateSuggestionsAsync(
            db, new EligibilityQueryBuilder(db), accountId, CancellationToken.None);
        return all.Where(s => mine.Contains(s.TitleId)).ToList();
    }

    private static AccountContext Pinned(Guid accountId)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        return context;
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"rate-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Accounts.Add(account);
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = FilterKind.IncludeGenre,
            Value = _scope,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(Action<Title> configure)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"rate-fixture {Guid.NewGuid():N}",
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = ["drama"],
            // Above the popularity fallback's vote gate by default, so a test that means to
            // exercise that half does not have to say so.
            TmdbVoteCount = 5_000,
            // Everything this class seeds sits at the very bottom of the GLOBAL popularity order
            // on purpose: CandidateRetrieverTests asserts on the catalogue's popularity head.
            TmdbPopularity = 0.01m,
            CreatedAt = DateTime.UtcNow,
        };
        configure(title);
        // Appended AFTER configure so a test that sets its own genres still lands in scope.
        title.Genres = [.. title.Genres, _scope];
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task SeedWatchedAsync(Guid accountId, Guid titleId, DateTime lastWatchedAt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.WatchedTitles.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Plays = 1,
            LastWatchedAt = lastWatchedAt,
            IsFullyWatched = true,
            FirstSyncedAt = lastWatchedAt,
            UpdatedAt = lastWatchedAt,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedRatingAsync(Guid accountId, Guid titleId, short rating)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var now = DateTime.UtcNow;
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = rating,
            RatedAt = now,
            FirstRatedAt = now,
            SyncedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedReactionAsync(Guid accountId, Guid titleId, ReactionKind kind)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var reaction = new UserTitleReaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Kind = kind,
            CreatedAt = DateTime.UtcNow,
        };
        db.UserTitleReactions.Add(reaction);
        await db.SaveChangesAsync();
        return reaction.Id;
    }

    private async Task SeedFilterAsync(Guid accountId, FilterKind kind, string value)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = kind,
            Value = value,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedActiveArtifactAndScoresAsync(Guid accountId, Dictionary<Guid, decimal> scores)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var artifact = new ModelArtifact
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Version = 1,
            Algo = "FastTree",
            ArtifactBytes = [1],
            TrainingRunId = Guid.NewGuid(),
            Status = ArtifactStatus.Active,
            TrainedAt = DateTime.UtcNow,
            // The schema-compat shadow column projects 'version' out of this jsonb; without a
            // current version the artifact is invisible to every Active-artifact read. Nothing
            // here ever scores — the predictions are seeded directly — so the bytes are a stub.
            FeatureSchemaJson = JsonSerializer.Serialize(
                new { version = FeatureSchema.Version, names = FeatureSchema.AllNames() }),
        };
        db.ModelArtifacts.Add(artifact);
        foreach (var (titleId, predicted) in scores)
        {
            db.TitleScores.Add(new TitleScore
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = titleId,
                ModelArtifactId = artifact.Id,
                PredictedRating = predicted,
                ScoredAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }
}
