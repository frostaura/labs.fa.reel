using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The one taste line every LLM prompt rides on, driven by a hand-built taste state so the
/// assertions are about the summary's own rules rather than about the feature builder.
///
/// <para>
/// Three regressions are pinned, each of which shipped in all three hand-copied versions of this
/// method: genres ordered by rating COUNT rather than affinity (so "watched a lot of" outranked
/// "loves"); no subtraction of the account's ExcludeGenre rules (so the prompt could tell the model
/// the user favours a genre the eligibility layer refuses to show); and an unordered Where/Take(4)
/// over the loved titles (so "loves …" named an arbitrary four).
/// </para>
/// </summary>
[Collection("postgres")]
public class TasteSummaryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Genres_are_ordered_by_affinity_not_by_how_many_were_rated()
    {
        if (!fixture.Available)
        {
            return; // no Postgres available — covered in CI's backend-integration job
        }

        var accountId = await SeedAccountAsync();
        // Documentary is the most-WATCHED and the least-liked; horror the least-watched and the
        // most-liked. Ordering by count puts them in exactly the wrong order.
        var taste = Taste(
            userMean: 6m,
            genres: new()
            {
                ["documentary"] = Ratings(4, 12),
                ["drama"] = Ratings(7, 6),
                ["horror"] = Ratings(10, 4),
            });

        var summary = await TasteSummary.BuildAsync(Db(), accountId, taste, CancellationToken.None);

        Assert.Equal("favours horror, drama, documentary", summary);
    }

    [Fact]
    public async Task A_banned_genre_never_appears_however_highly_it_is_rated()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await ExcludeGenreAsync(accountId, "comedy");
        var taste = Taste(
            userMean: 6m,
            genres: new()
            {
                ["comedy"] = Ratings(10, 20),   // the top of the taste map, and banned outright
                ["thriller"] = Ratings(8, 5),
            });

        var summary = await TasteSummary.BuildAsync(Db(), accountId, taste, CancellationToken.None);

        Assert.DoesNotContain("comedy", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("thriller", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_loved_titles_follow_the_taste_ranking_rather_than_an_arbitrary_four()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        // Six loved titles, named for their rank; the taste state's order is deliberately not the
        // order they were inserted in, which is the order an unordered Take tends to return.
        var ids = new List<Guid>();
        for (var i = 1; i <= 6; i++)
        {
            ids.Add(await SeedTitleAsync($"Loved {i}"));
        }

        var ranked = new List<Guid> { ids[4], ids[1], ids[5], ids[0], ids[3], ids[2] };
        var taste = Taste(6m, new() { ["drama"] = Ratings(8, 3) }, ranked);

        var summary = await TasteSummary.BuildAsync(Db(), accountId, taste, CancellationToken.None);

        Assert.Contains("loves Loved 5, Loved 2, Loved 6, Loved 1", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Loved 4", summary, StringComparison.Ordinal); // the fifth-ranked is out
    }

    [Fact]
    public async Task An_account_with_nothing_to_say_says_so()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        var summary = await TasteSummary.BuildAsync(Db(), accountId, Taste(6m, []), CancellationToken.None);

        Assert.Equal("no strong signal yet", summary);
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────

    private FrostAura.Reel.Application.Persistence.IReelDbContext Db() => fixture.CreateContext(new AccountContext());

    private static List<(short Rating, DateTime RatedAt)> Ratings(short rating, int count) =>
        [.. Enumerable.Range(0, count).Select(i => (rating, DateTime.UtcNow.AddDays(-i)))];

    private static FeatureVectorBuilder.TasteState Taste(
        decimal userMean,
        Dictionary<string, List<(short Rating, DateTime RatedAt)>> genres,
        IReadOnlyList<Guid>? topLoved = null) =>
        new(
            userMean,
            0m,
            genres,
            new Dictionary<string, decimal>(),
            new Dictionary<int, List<short>>(),
            new Dictionary<Guid, List<short>>(),
            topLoved ?? [],
            null,
            null,
            new Dictionary<Guid, float[]>(),
            FeatureVectorBuilder.SelfExclusion.Empty,
            [],
            new Dictionary<string, double>(),
            null,
            0.5m,
            null,
            null,
            [],
            new Dictionary<string, int>());

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"summary-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string name)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = name,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
    }

    private async Task ExcludeGenreAsync(Guid accountId, string genre)
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
}
