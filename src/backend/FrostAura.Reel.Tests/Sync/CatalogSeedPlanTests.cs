using FrostAura.Reel.Application.Jobs;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Tests.Sync;

/// <summary>
/// The seed's partitioning, asserted without a network or a database. Every one of these is a
/// property the walk's correctness rests on: get the partitioning wrong and the job still runs,
/// still logs progress and still finishes — it just seeds a fraction of the corpus it claims to.
/// </summary>
public class CatalogSeedPlanTests
{
    private static readonly DateTime Now = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Plan_covers_every_year_from_1990_to_now_for_both_media()
    {
        var plan = CatalogSeedPlan.Build(Now);
        var years = Enumerable.Range(CatalogSeedPlan.FirstYear, Now.Year - CatalogSeedPlan.FirstYear + 1).ToList();

        Assert.Equal(years.Count * 2, plan.Count);
        Assert.Equal(years.OrderBy(y => y), plan.Where(s => s.Movies).Select(s => s.Year).OrderBy(y => y));
        Assert.Equal(years.OrderBy(y => y), plan.Where(s => !s.Movies).Select(s => s.Year).OrderBy(y => y));

        // No partition is walked twice — a duplicate would spend a whole year's pages for nothing.
        Assert.Equal(plan.Count, plan.Distinct().Count());
    }

    /// <summary>
    /// Newest year first, movies then shows within each year. Both halves matter: recency is where
    /// the recall gap is densest, and alternating media is what stops every show year queuing behind
    /// thirty-seven movie years.
    /// </summary>
    [Fact]
    public void Plan_walks_newest_first_and_alternates_media_within_a_year()
    {
        var plan = CatalogSeedPlan.Build(Now);

        Assert.Equal(new CatalogSeedStep(true, 2026), plan[0]);
        Assert.Equal(new CatalogSeedStep(false, 2026), plan[1]);
        Assert.Equal(new CatalogSeedStep(true, 2025), plan[2]);
        Assert.Equal(new CatalogSeedStep(false, CatalogSeedPlan.FirstYear), plan[^1]);

        var movieYears = plan.Where(s => s.Movies).Select(s => s.Year).ToList();
        Assert.Equal(movieYears.OrderByDescending(y => y), movieYears);
    }

    [Fact]
    public void The_current_partial_year_is_included()
    {
        var plan = CatalogSeedPlan.Build(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        Assert.Contains(new CatalogSeedStep(true, 2026), plan);
    }

    /// <summary>
    /// Resolution is by (media, year), never by a stored index — a plan built after new year's day
    /// grows a partition at the FRONT, and an index would silently point one partition off.
    /// </summary>
    [Fact]
    public void IndexOf_resolves_a_partition_by_media_and_year_across_a_year_boundary()
    {
        var before = CatalogSeedPlan.Build(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        var after = CatalogSeedPlan.Build(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var indexBefore = CatalogSeedPlan.IndexOf(before, CatalogSeedPlan.MovieMedia, 2020);
        var indexAfter = CatalogSeedPlan.IndexOf(after, CatalogSeedPlan.MovieMedia, 2020);

        Assert.Equal(before[indexBefore], after[indexAfter]);
        Assert.NotEqual(indexBefore, indexAfter);
    }

    [Fact]
    public void IndexOf_reports_minus_one_for_a_partition_the_plan_no_longer_holds()
    {
        var plan = CatalogSeedPlan.Build(Now);
        Assert.Equal(-1, CatalogSeedPlan.IndexOf(plan, CatalogSeedPlan.MovieMedia, 1975));
        Assert.Equal(-1, CatalogSeedPlan.IndexOf(plan, "audiobook", 2020));
    }

    /// <summary>
    /// TMDB serves discover to page 500 and no further, whatever <c>total_pages</c> claims. The
    /// clamp is what stops the walk paging into a wall for as long as its budget allows.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(37, 37)]
    [InlineData(500, 500)]
    [InlineData(501, 500)]
    [InlineData(9_999, 500)]
    public void LastPage_clamps_to_TMDBs_500_page_wall_and_never_below_one(int totalPages, int expected) =>
        Assert.Equal(expected, CatalogSeedPlan.LastPage(totalPages));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(20, 1)]
    [InlineData(21, 2)]
    [InlineData(9_999, 500)]
    [InlineData(50_000, 500)]
    public void PagesFor_converts_a_result_count_into_clamped_pages(int totalResults, int expected) =>
        Assert.Equal(expected, CatalogSeedPlan.PagesFor(totalResults));

    /// <summary>
    /// The floors differ per medium on purpose: TMDB's TV rows carry far fewer votes than its film
    /// rows for equivalent reach, so one shared floor either floods the movie corpus or amputates
    /// the show corpus.
    /// </summary>
    [Fact]
    public void The_vote_floor_is_lower_for_shows_than_for_movies()
    {
        Assert.Equal(500, CatalogSeedPlan.MinVoteCount(movies: true));
        Assert.Equal(200, CatalogSeedPlan.MinVoteCount(movies: false));
        Assert.True(CatalogSeedPlan.MinVoteCount(false) < CatalogSeedPlan.MinVoteCount(true));
    }

    /// <summary>
    /// The floors are settings, not constants, and this is the only knob that changes how BIG the
    /// seeded corpus is. Measured against TMDB on 2026-09-10 the shipped defaults yield ~9,300
    /// titles and 50/50 yields ~34,800, so the difference between the corpus that was specified and
    /// the corpus that was expected is these two variables.
    /// </summary>
    [Fact]
    public void The_vote_floors_are_overridable_per_medium()
    {
        var overridden = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CatalogSeedPlan.MovieMinVotesKey] = "50",
                [CatalogSeedPlan.ShowMinVotesKey] = "50",
            })
            .Build();

        Assert.Equal(50, CatalogSeedPlan.MinVoteCount(true, overridden));
        Assert.Equal(50, CatalogSeedPlan.MinVoteCount(false, overridden));

        var absent = new ConfigurationBuilder().Build();
        Assert.Equal(CatalogSeedPlan.MovieMinVoteCount, CatalogSeedPlan.MinVoteCount(true, absent));
        Assert.Equal(CatalogSeedPlan.ShowMinVoteCount, CatalogSeedPlan.MinVoteCount(false, absent));

        // A floor of zero would ask TMDB for its entire million-row corpus; a typo must not do that.
        var zero = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [CatalogSeedPlan.MovieMinVotesKey] = "0" })
            .Build();
        Assert.Equal(1, CatalogSeedPlan.MinVoteCount(true, zero));
    }

    [Fact]
    public void Budgets_come_from_configuration_and_fall_back_when_it_is_absent_or_nonsense()
    {
        Assert.Equal(CatalogSeedPlan.DefaultPagesPerRun, CatalogSeedPlan.PagesPerRun(Config(null, null)));
        Assert.Equal(CatalogSeedPlan.DefaultMaxTitles, CatalogSeedPlan.MaxTitles(Config(null, null)));

        Assert.Equal(25, CatalogSeedPlan.PagesPerRun(Config("25", null)));
        Assert.Equal(9_000, CatalogSeedPlan.MaxTitles(Config(null, "9000")));

        // A typo must degrade to the documented default, never to zero — a page budget of nothing is
        // a job that claims a worker slot on every run and walks no pages at all.
        Assert.Equal(CatalogSeedPlan.DefaultPagesPerRun, CatalogSeedPlan.PagesPerRun(Config("four hundred", null)));
        Assert.Equal(1, CatalogSeedPlan.PagesPerRun(Config("0", null)));
        Assert.Equal(1, CatalogSeedPlan.PagesPerRun(Config("-5", null)));
    }

    private static IConfiguration Config(string? pages, string? maxTitles) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CatalogSeedPlan.PagesPerRunKey] = pages,
                [CatalogSeedPlan.MaxTitlesKey] = maxTitles,
            })
            .Build();
}
