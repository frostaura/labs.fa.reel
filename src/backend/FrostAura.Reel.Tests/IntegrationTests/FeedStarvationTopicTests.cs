using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The empty-pool diagnostic given a topic: which condition emptied THIS request. Every fixture tags
/// its titles with a keyword no other test uses, so the topic is exactly what the test seeded while
/// the account's wider pool (the shared catalogue) stays non-empty — the case a whole-feed diagnostic
/// cannot answer at all.
/// </summary>
[Collection("postgres")]
public class FeedStarvationTopicTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_topic_emptied_by_one_condition_names_that_condition()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = $"topic-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        await SeedTitleAsync(topic, t => t.Genres = ["comedy"]);
        await SeedTitleAsync(topic, t => t.Genres = ["comedy", "action"]);
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "comedy");

        var sentence = await ExplainAsync(accountId, topic);
        Assert.Equal($"Your excluded genres leaves no {topic} films you have not already seen. Relax it to see picks again.", sentence);

        // The whole-feed question has a different answer on the same account: the shared catalogue
        // still has eligible titles, so the feed is not empty and the plain diagnostic says nothing.
        await using var db = fixture.CreateContext(new AccountContext());
        Assert.Null(await new FeedStarvationDiagnostic(db, new EligibilityQueryBuilder(db)).ExplainEmptyPoolAsync(accountId, CancellationToken.None));
    }

    [Fact]
    public async Task A_topic_whose_titles_are_all_seen_never_offers_a_relaxation()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = $"topic-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        var seen = await SeedTitleAsync(topic, _ => { });
        var rated = await SeedTitleAsync(topic, _ => { });
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "comedy");
        await WatchAsync(accountId, seen);
        await RateAsync(accountId, rated);

        var sentence = await ExplainAsync(accountId, topic);
        Assert.Equal($"Every {topic} films title in the catalogue is one you have already seen, rated or dismissed.", sentence);
        Assert.DoesNotContain("Relax", sentence!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Low_vote_matches_do_not_count_and_are_not_blamed_on_the_user()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = $"topic-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        await SeedTitleAsync(topic, t => t.TmdbVoteCount = 3);

        Assert.Equal($"The catalogue has no {topic} films titles to offer.", await ExplainAsync(accountId, topic));

        // Counted with no vote floor, the same title is a live, eligible match: nothing to explain.
        Assert.Null(await ExplainAsync(accountId, topic, minVotes: 0));
    }

    [Fact]
    public async Task A_topic_with_eligible_titles_needs_no_explanation()
    {
        if (!fixture.Available)
        {
            return;
        }

        var topic = $"topic-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        await SeedTitleAsync(topic, _ => { });
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "comedy");

        Assert.Null(await ExplainAsync(accountId, topic));
    }

    private async Task<string?> ExplainAsync(Guid accountId, string topic, int minVotes = CrowdVotes.MinRelaxVotes)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var diagnostic = new FeedStarvationDiagnostic(db, new EligibilityQueryBuilder(db));
        var hard = HardConstraints.None with { Concepts = [new ConceptTerm(topic, [topic], [])] };
        return await diagnostic.ExplainEmptyPoolAsync(
            accountId, diagnostic.TopicFor(hard, minVotes), $"{topic} films", CancellationToken.None);
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"starve-{Guid.NewGuid():N}",
            Tier = AccountTier.Paid,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string topic, Action<Title> shape)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"starve-fixture {Guid.NewGuid():N}",
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = ["drama"],
            Keywords = [topic],
            Year = 2010,
            TmdbVoteAverage = 7.5m,
            TmdbVoteCount = 1_000,
            TmdbPopularity = 0m,
            CreatedAt = DateTime.UtcNow,
        };
        shape(title);
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
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

    private async Task WatchAsync(Guid accountId, Guid titleId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.WatchedTitles.Add(new WatchedTitle
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Plays = 1,
            LastWatchedAt = DateTime.UtcNow,
            IsFullyWatched = true,
            FirstSyncedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task RateAsync(Guid accountId, Guid titleId)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserRatings.Add(new UserRating
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            SubjectType = RatingSubjectType.Movie,
            Rating = 8,
            RatedAt = DateTime.UtcNow,
            FirstRatedAt = DateTime.UtcNow,
            SyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
