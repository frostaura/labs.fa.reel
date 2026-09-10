using System.Globalization;
using FrostAura.Reel.Application.Configuration;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>One partition of the seed walk: one medium, one release year.</summary>
/// <param name="Movies">True for <c>/discover/movie</c>, false for <c>/discover/tv</c>.</param>
/// <param name="Year">The single release/first-air year the query is closed to at both ends.</param>
public readonly record struct CatalogSeedStep(bool Movies, int Year)
{
    /// <summary>The cursor's spelling of the medium — a readable jsonb value, not an ordinal.</summary>
    public string Media => Movies ? CatalogSeedPlan.MovieMedia : CatalogSeedPlan.ShowMedia;
}

/// <summary>
/// The catalogue bulk seed's plan: WHICH TMDB discover queries are walked, in what order, and where
/// the walk is allowed to stop. Pure — no database, no clock beyond the one it is handed, no I/O —
/// so the partitioning is assertable without a network or a Postgres.
///
/// <para>
/// <b>Why partition by year at all.</b> TMDB serves discover to page 500 and no further: 10,000
/// results per query, whatever <c>total_pages</c> claims. Movies with <c>vote_count ≥ 500</c> since
/// 1990 are an order of magnitude past that, so a single unpartitioned query cannot reach the tail
/// no matter how patiently it is paged — it simply stops, silently, at result 10,000 with the rest
/// of the corpus never offered. One query per (medium, year) puts every partition comfortably under
/// the wall: no year on record holds 10,000 movies above 500 votes, and the plan asserts the clamp
/// anyway rather than trusting that to stay true.
/// </para>
///
/// <para>
/// <b>Why <c>vote_count.desc</c> and not <c>popularity.desc</c>.</b> The walk spans days and
/// resumes from a cursor. Popularity is a trailing present-day metric that reshuffles daily, so a
/// resumed walk over it revisits rows it already has and skips rows it never saw, with no way to
/// tell which. Vote count only ever grows, and grows slowly at the depths this walk reaches, so a
/// page number means roughly the same thing tomorrow as it did today. It also front-loads exactly
/// the right thing: the most-voted titles in a year are the ones an account is most likely to
/// eventually rate.
/// </para>
///
/// <para>
/// <b>Why newest-year-first, alternating media.</b> The seed exists because only 25.5% of the
/// titles the founder loved in the last 180 days were in the catalogue the day before he rated
/// them. Recent supply closes more of that gap per page than 1991 does, and alternating movie/show
/// within each year means both media are usefully covered from the first run rather than shows
/// waiting behind every movie year on the plan.
/// </para>
///
/// <para>
/// <b>The floors are deliberately different per medium.</b> 500 votes for movies, 200 for shows:
/// TMDB's TV rows carry far fewer votes than its film rows for equivalent cultural reach, so one
/// shared floor either drowns the movie corpus in noise or amputates the show corpus. Neither
/// floor is a quality judgement — it is the line below which a row is unlikely to be a real,
/// findable title at all.
/// </para>
///
/// <para>
/// <b>What each floor actually buys, measured against TMDB on 2026-09-10</b> (one
/// <c>total_results</c> probe per partition, 1990–2026, summed):
/// <list type="table">
/// <item><term>movies ≥500 / shows ≥200</term><description>6,922 + 2,409 = <b>9,331</b> titles, 503 pages — the shipped default</description></item>
/// <item><term>movies ≥200 / shows ≥100</term><description>12,355 + 4,090 = 16,445 titles, 859 pages</description></item>
/// <item><term>movies ≥100 / shows ≥50</term><description>18,762 + 6,430 = 25,192 titles, 1,296 pages</description></item>
/// <item><term>movies ≥50 / shows ≥50</term><description>28,322 + 6,430 = <b>34,752</b> titles, 1,775 pages</description></item>
/// <item><term>movies ≥20 / shows ≥20</term><description>48,896 + 11,636 = 60,532 titles, 3,060 pages</description></item>
/// </list>
/// The defaults are therefore an order of magnitude smaller than the ~30–45k the brief expected,
/// and the corpus that DOES land in that band is movies ≥50 / shows ≥50. That is a supply decision
/// with a real trade — a lower floor buys more of the tail and more rows nobody will ever be shown,
/// and every one of them costs a hydration call and an extraction — so the floors are settings
/// rather than constants and the shipped default is the conservative one. No partition exceeds
/// TMDB's 10,000-result cap at any floor down to 20, so the year partitioning holds throughout.
/// </para>
/// </summary>
public static class CatalogSeedPlan
{
    public const string MovieMedia = "movie";

    public const string ShowMedia = "show";

    /// <summary>
    /// First release year the plan covers. 1990 is the founder's stated window; everything before
    /// it that matters is already reachable through the library, per-build discovery and the
    /// weekly growth sweep, all of which are unbounded by year.
    /// </summary>
    public const int FirstYear = 1990;

    /// <summary>TMDB's hard discover ceiling — 500 pages × 20 results. Not configurable; it is theirs.</summary>
    public const int MaxDiscoverPage = 500;

    /// <summary>Results TMDB puts on one discover page. Used only to turn totals into page counts.</summary>
    public const int ResultsPerPage = 20;

    /// <summary>Default movie vote floor. Override with <c>SEED_MOVIE_MIN_VOTES</c>.</summary>
    public const int MovieMinVoteCount = 500;

    /// <summary>Default show vote floor. Override with <c>SEED_SHOW_MIN_VOTES</c>.</summary>
    public const int ShowMinVoteCount = 200;

    public const string MovieMinVotesKey = "SEED_MOVIE_MIN_VOTES";

    public const string ShowMinVotesKey = "SEED_SHOW_MIN_VOTES";

    /// <summary>Discover pages one run may fetch, ≈20 titles each. <c>SEED_PAGES_PER_RUN</c>.</summary>
    public const string PagesPerRunKey = "SEED_PAGES_PER_RUN";

    public const int DefaultPagesPerRun = 400;

    /// <summary>
    /// Cumulative new titles the whole plan may add before it stops and waits for a human.
    /// <c>SEED_MAX_TITLES</c>. This is a guard, not a target: the plan's own estimate is well under
    /// it, so tripping it means TMDB returned far more than the arithmetic predicted and somebody
    /// should look before another 60,000 rows land.
    /// </summary>
    public const string MaxTitlesKey = "SEED_MAX_TITLES";

    public const int DefaultMaxTitles = 60_000;

    /// <summary>
    /// The ordered partitions: newest year first, movies then shows within each year, down to
    /// <see cref="FirstYear"/>. <paramref name="now"/> supplies the last year — the current one,
    /// partial and still filling, which is correct: a title released this month is exactly the kind
    /// of supply the recall gap is made of.
    /// </summary>
    public static IReadOnlyList<CatalogSeedStep> Build(DateTime now)
    {
        var steps = new List<CatalogSeedStep>((now.Year - FirstYear + 1) * 2);
        for (var year = now.Year; year >= FirstYear; year--)
        {
            steps.Add(new CatalogSeedStep(true, year));
            steps.Add(new CatalogSeedStep(false, year));
        }

        return steps;
    }

    /// <summary>The compiled-in default floor for a medium.</summary>
    public static int MinVoteCount(bool movies) => movies ? MovieMinVoteCount : ShowMinVoteCount;

    /// <summary>
    /// The EFFECTIVE floor for a medium — the setting where one is present, the default otherwise.
    /// <para>
    /// Configurable because the measurement above says the shipped defaults yield ~9,300 titles
    /// where the supply case wanted ~35,000, and the difference between those two corpora is one
    /// environment variable rather than a release. Floored at 1: a floor of zero would ask TMDB for
    /// every row it holds, which is a million movies and a walk that never ends.
    /// </para>
    /// </summary>
    public static int MinVoteCount(bool movies, IConfiguration? configuration) =>
        Math.Max(1, FeatureFlags.ReadInt(
            configuration,
            movies ? MovieMinVotesKey : ShowMinVotesKey,
            MinVoteCount(movies)));

    /// <summary>
    /// Where a cursor sits in a freshly-built plan, or -1 when the plan no longer contains it.
    /// <para>
    /// The -1 case is real rather than defensive: the plan is rebuilt from the clock on every run,
    /// so a job enqueued on 31 December and resumed on 1 January is resuming into a plan that has
    /// grown a new year at the FRONT. Resolving by (media, year) rather than by a stored index is
    /// what makes that harmless — the index would have silently pointed one partition off.
    /// </para>
    /// </summary>
    public static int IndexOf(IReadOnlyList<CatalogSeedStep> plan, string media, int year)
    {
        for (var i = 0; i < plan.Count; i++)
        {
            if (plan[i].Year == year && string.Equals(plan[i].Media, media, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The last page of a partition this walk will fetch: TMDB's reported <c>total_pages</c>,
    /// clamped to <see cref="MaxDiscoverPage"/> and floored at 1.
    /// <para>
    /// The clamp is the whole reason the walk terminates. TMDB reports <c>total_pages</c> for the
    /// full result set even where it will only SERVE 500 of them, so an unclamped walk of an
    /// oversized partition would page past 500 receiving errors or empty pages for as long as the
    /// budget allowed.
    /// </para>
    /// </summary>
    public static int LastPage(int totalPages) => Math.Clamp(totalPages, 1, MaxDiscoverPage);

    /// <summary>Pages a partition of <paramref name="totalResults"/> rows costs, after the clamp.</summary>
    public static int PagesFor(int totalResults) =>
        LastPage((int)Math.Ceiling(Math.Max(0, totalResults) / (double)ResultsPerPage));

    public static int PagesPerRun(IConfiguration? configuration) =>
        Math.Max(1, FeatureFlags.ReadInt(configuration, PagesPerRunKey, DefaultPagesPerRun));

    public static int MaxTitles(IConfiguration? configuration) =>
        Math.Max(0, FeatureFlags.ReadInt(configuration, MaxTitlesKey, DefaultMaxTitles));

    /// <summary>Invariant, because it is compared as an exact string in cursors and progress lines.</summary>
    public static string Describe(CatalogSeedStep step) =>
        string.Create(CultureInfo.InvariantCulture, $"{step.Media} {step.Year}");
}
