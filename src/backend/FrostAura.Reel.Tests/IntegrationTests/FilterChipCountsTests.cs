using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The genre-chip counts on real Postgres (H-7): chips come from the catalogue, every count is what
/// an exclusion of that chip removes, aliases fold into their target (soap, a genre of its own, keeps a
/// chip too), anime is animation in Japanese,
/// and a chip under the bar is listed but not offered. Counts are catalogue-wide, so each assertion
/// is a DELTA over what this test seeded rather than an absolute.
/// </summary>
[Collection("postgres")]
public class FilterChipCountsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Chips_count_what_an_exclusion_removes_by_medium()
    {
        if (!fixture.Available)
        {
            return;
        }

        var before = await CountAsync();

        await SeedAsync(MediaType.Show, ["reality"]);
        await SeedAsync(MediaType.Show, ["reality"]);
        await SeedAsync(MediaType.Movie, ["animation", "action"], language: "ja");
        await SeedAsync(MediaType.Movie, ["animation"], language: "en");
        await SeedAsync(MediaType.Show, ["suspense"]);
        await SeedAsync(MediaType.Show, ["drama"], keywords: ["office romance"]);
        await SeedAsync(MediaType.Movie, ["drama"], keywords: ["office romance"]);
        await SeedAsync(MediaType.Show, ["drama", "soap"]);
        await SeedAsync(MediaType.Show, ["drama"], overview: "She deals with an old romance.");
        await SeedAsync(MediaType.Movie, ["drama"], overview: "She deals with an old romance.");

        var after = await CountAsync();

        Assert.Equal(2, Shows(after, "reality") - Shows(before, "reality"));
        Assert.Equal(0, Movies(after, "reality") - Movies(before, "reality"));
        // Anime: the Japanese animated film only — not the English one.
        Assert.Equal(1, Movies(after, "anime") - Movies(before, "anime"));
        Assert.True(Chip(after, "anime")!.Composite);
        Assert.Equal(2, Movies(after, "animation") - Movies(before, "animation"));
        // Trakt's suspense slug counts under thriller and is never a chip of its own.
        Assert.Equal(1, Shows(after, "thriller") - Shows(before, "thriller"));
        Assert.Null(Chip(after, "suspense"));
        // The romance stand-ins reach the shows (TV has no romance genre) — the keyword, the soap alias
        // and the overview phrase (HREV1-RET-09) — never the films, which TMDB tags itself.
        Assert.Equal(3, Shows(after, "romance") - Shows(before, "romance"));
        Assert.Equal(0, Movies(after, "romance") - Movies(before, "romance"));
        // Soap folds into romance AND keeps its own chip: "no soaps" is a condition of its own.
        Assert.Equal(1, Shows(after, "soap") - Shows(before, "soap"));

        Assert.All(after.Chips, c => Assert.Equal(c.Total >= FilterChipCounts.MinTitles, c.Offered));
        Assert.Equal(after.Chips.OrderByDescending(c => c.Offered).ThenByDescending(c => c.Total).Select(c => c.Slug), after.Chips.Select(c => c.Slug));
    }

    private static FilterChip? Chip(FilterChipReport report, string slug) => report.Chips.SingleOrDefault(c => c.Slug == slug);

    private static int Movies(FilterChipReport report, string slug) => Chip(report, slug)?.Movies ?? 0;

    private static int Shows(FilterChipReport report, string slug) => Chip(report, slug)?.Shows ?? 0;

    private async Task<FilterChipReport> CountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await new FilterChipCounts(db, new MemoryCache(new MemoryCacheOptions())).CountAsync(default);
    }

    private async Task SeedAsync(MediaType media, string[] genres, string language = "en", string[]? keywords = null, string? overview = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.Add(new Title
        {
            Id = Guid.NewGuid(),
            MediaType = media,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"chip-fixture {Guid.NewGuid():N}",
            Language = language,
            Genres = genres,
            Keywords = keywords ?? [],
            Overview = overview,
            TmdbPopularity = 0m,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
