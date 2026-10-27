using System.Data.Common;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The filter cost meter on real Postgres — the only place it can be trusted, because the draft
/// predicate is a second phrasing of the eligibility rule and a translation fault in it is invisible
/// to an in-memory test.
///
/// Three things are pinned here and nothing else will catch them:
/// 1. the marginals are the real subtractive counts, per VALUE rather than per kind;
/// 2. the draft path and the persisted path agree title-for-title on the same rules — this is the
///    anti-drift guard the builder's doc-comment points at, and the reason a second phrasing is
///    allowed to exist at all;
/// 3. the meter refuses to answer without an account scope, and one account's rules never see
///    another account's interaction footprint.
///
/// Every assertion is scoped to the ids this test seeded, so a shared catalogue left behind by other
/// integration tests can neither satisfy nor break it — except <c>Total</c>, which is catalogue-wide
/// by definition and is therefore asserted as a lower bound rather than an equality.
/// </summary>
[Collection("postgres")]
public class FilterCostMeterTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Marginals_are_the_titles_each_single_rule_alone_is_keeping_out()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();

        // A catalogue shaped so each of the three rules owns a distinct, countable slice.
        // Baseline survivors: English, 2010, crowd 8.0, drama — nothing excludes them.
        var keepA = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Year = 2010; t.TmdbVoteAverage = 8.0m; });
        var keepB = await SeedTitleAsync(t => { t.Genres = ["thriller"]; t.Year = 2015; t.TmdbVoteAverage = 7.4m; });

        // Held out ONLY by the comedy exclusion.
        var comedy1 = await SeedTitleAsync(t => { t.Genres = ["comedy"]; t.Year = 2012; t.TmdbVoteAverage = 8.2m; });
        var comedy2 = await SeedTitleAsync(t => { t.Genres = ["comedy", "drama"]; t.Year = 2011; t.TmdbVoteAverage = 7.9m; });
        var comedy3 = await SeedTitleAsync(t => { t.Genres = ["comedy"]; t.Year = 2018; t.TmdbVoteAverage = 9.1m; });

        // Held out ONLY by the crowd floor.
        var weak = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Year = 2013; t.TmdbVoteAverage = 5.1m; });

        // Held out ONLY by the language allow-list.
        var french = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Year = 2016; t.TmdbVoteAverage = 8.4m; t.Language = "fr"; });
        var korean = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Year = 2017; t.TmdbVoteAverage = 8.6m; t.Language = "ko"; });

        // Held out by TWO rules at once — the reason marginals do not sum, and the reason this test
        // asserts each marginal separately instead of a single "your filters cost you N".
        var frenchComedy = await SeedTitleAsync(t => { t.Genres = ["comedy"]; t.Year = 2014; t.TmdbVoteAverage = 8.8m; t.Language = "fr"; });

        var mine = new[] { keepA, keepB, comedy1, comedy2, comedy3, weak, french, korean, frenchComedy };

        var rules = FilterRuleSet.Create(
            excludeGenres: ["comedy"],
            includeGenres: null,
            excludeKeywords: null,
            languages: ["en"],
            minCrowdRating: 7.0m,
            minYear: 2000,
            maxYear: null);

        Assert.Equal(
            new[] { keepA, keepB }.ToHashSet(),
            (await EligibleAsync(accountId, rules, mine)).ToHashSet());

        // Relaxing comedy returns the three English comedies — NOT the French one, which the language
        // rule is still holding.
        Assert.Equal(
            new[] { keepA, keepB, comedy1, comedy2, comedy3 }.ToHashSet(),
            (await EligibleAsync(accountId, rules.Without(new FilterRule(FilterKind.ExcludeGenre, "comedy")), mine)).ToHashSet());

        // Relaxing the language rule returns both foreign dramas AND the French comedy is still held
        // by the genre rule — so this marginal is 2, not 3.
        Assert.Equal(
            new[] { keepA, keepB, french, korean }.ToHashSet(),
            (await EligibleAsync(accountId, rules.Without(new FilterRule(FilterKind.Language, "en")), mine)).ToHashSet());

        var report = await MeasureAsync(accountId, rules);

        // Total is the whole catalogue — the denominator in "421 of 5,319" — so it is at least what
        // this test seeded and usually much more on a shared database.
        Assert.True(report.Total >= mine.Length, $"catalogue total {report.Total} < seeded {mine.Length}");

        // Marginals are catalogue-wide, so they are asserted as "at least the slice this test owns":
        // another test's fixtures can only ever add to them, never subtract.
        Assert.True(Marginal(report, FilterKind.ExcludeGenre, "comedy") >= 3);
        Assert.True(Marginal(report, FilterKind.MinCrowdRating, "7.0") >= 1);
        Assert.True(Marginal(report, FilterKind.Language, "en") >= 2);

        // Every rule that is ON is reported, addressed by VALUE — the editor renders a badge per chip,
        // so a per-kind report would have nowhere to put the number.
        Assert.Equal(
            new[] { "ExcludeGenre", "Language", "MinCrowdRating", "MinYear" },
            report.ByRule.Select(r => r.Kind).ToArray());

        // Suggestions are the top three by gain, biggest first, and never include a rule that returns
        // nothing — offering a one-tap relaxation worth zero titles is worse than offering none.
        Assert.True(report.Suggestions.Count <= FilterCostMeter.SuggestionCount);
        Assert.All(report.Suggestions, s => Assert.True(s.Gain > 0));
        Assert.Equal(report.Suggestions.OrderByDescending(s => s.Gain).ToArray(), report.Suggestions.ToArray());
        // Deliberately NOT asserting WHICH rule leads: marginals are catalogue-wide, and on a shared
        // integration database another test's fixtures can legitimately make the crowd floor the most
        // expensive rule. The orderings that are properties of the meter are asserted above.
        Assert.All(report.Suggestions, s => Assert.Contains(report.ByRule, r => r.Kind == s.Kind && r.Value == s.Value && r.Marginal == s.Gain));
    }

    [Fact]
    public async Task Draft_rules_match_the_persisted_rules_title_for_title()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The whole justification for a second phrasing of the content predicate. Same rules, saved
        // one way and drafted the other, must select the identical set — including the awkward edges:
        // an unrated title fails a crowd floor, an undated title passes a year window, a null-language
        // title reads as not-English, and a keyword matches name, overview or TMDB keyword alike.
        var accountId = await SeedAccountAsync();

        var plain = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Year = 2005; t.TmdbVoteAverage = 7.7m; });
        var excluded = await SeedTitleAsync(t => { t.Genres = ["documentary"]; t.Year = 2005; t.TmdbVoteAverage = 8.9m; });
        var unrated = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Year = 2005; });
        var traktOnly = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Year = 2005; t.TraktRating = 8.5m; t.TraktVotes = 30; });
        var undated = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.TmdbVoteAverage = 7.7m; });
        var tooOld = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Year = 1980; t.TmdbVoteAverage = 9.5m; });
        var tooNew = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Year = 2024; t.TmdbVoteAverage = 9.5m; });
        var noLanguage = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Year = 2005; t.TmdbVoteAverage = 8.1m; t.Language = null; });
        var keywordInName = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Year = 2005; t.TmdbVoteAverage = 8.1m; t.Name = $"A Zombie Story {Guid.NewGuid():N}"; });
        var keywordInOverview = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Year = 2005; t.TmdbVoteAverage = 8.1m; t.Overview = "Quiet drama about a ZOMBIE outbreak."; });
        var keywordInKeywords = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Year = 2005; t.TmdbVoteAverage = 8.1m; t.Keywords = ["zombie apocalypse"]; });
        var included = await SeedTitleAsync(t => { t.Genres = ["drama", "horror"]; t.Year = 2005; t.TmdbVoteAverage = 8.1m; });

        var mine = new[]
        {
            plain, excluded, unrated, traktOnly, undated, tooOld, tooNew, noLanguage,
            keywordInName, keywordInOverview, keywordInKeywords, included,
        };

        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "documentary");
        await SeedFilterAsync(accountId, FilterKind.ExcludeKeyword, "zombie");
        await SeedFilterAsync(accountId, FilterKind.Language, "en");
        await SeedFilterAsync(accountId, FilterKind.MinCrowdRating, ContentFilterRules.FormatRating(7.0m));
        await SeedFilterAsync(accountId, FilterKind.MinYear, ContentFilterRules.FormatYear(2000));
        await SeedFilterAsync(accountId, FilterKind.MaxYear, ContentFilterRules.FormatYear(2020));

        HashSet<Guid> persisted;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            persisted = (await new EligibilityQueryBuilder(db)
                .EligibleTitles(accountId)
                .Where(t => mine.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync()).ToHashSet();
        }

        FilterRuleSet drafted;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            drafted = FilterRuleSet.FromRows(await db.ContentFilters.Where(f => f.AccountId == accountId).ToListAsync());
        }

        var draft = (await EligibleAsync(accountId, drafted, mine)).ToHashSet();

        Assert.Equal(persisted, draft);
        // Sanity: the fixture must actually exercise the edges, or "both agree" is a vacuous truth.
        Assert.Contains(plain, draft);
        Assert.Contains(traktOnly, draft);
        Assert.Contains(undated, draft);
        Assert.Contains(included, draft);
        Assert.DoesNotContain(excluded, draft);
        Assert.DoesNotContain(unrated, draft);
        Assert.DoesNotContain(tooOld, draft);
        Assert.DoesNotContain(tooNew, draft);
        Assert.DoesNotContain(noLanguage, draft);
        Assert.DoesNotContain(keywordInName, draft);
        Assert.DoesNotContain(keywordInOverview, draft);
        Assert.DoesNotContain(keywordInKeywords, draft);
    }

    [Fact]
    public async Task Draft_and_persisted_rules_agree_on_genre_stand_ins_and_aliases()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The TV taxonomy-gap arm exists twice (persisted rows, draft rules) like every other arm, so
        // it is pinned the same way: same rules, both phrasings, identical sets — with a fixture that
        // exercises every branch (show vs film, word vs substring, alias genre, a slug with no row).
        var accountId = await SeedAccountAsync();
        var romanceShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["office romance"]; });
        var romanceFilm = await SeedTitleAsync(t => { t.Genres = ["thriller"]; t.Keywords = ["office romance"]; });
        var bromanceShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["bromance"]; });
        var horrorShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Keywords = ["folk horror"]; });
        var animeFilm = await SeedTitleAsync(t => { t.Keywords = ["anime"]; });
        var childrenShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Genres = ["children"]; });
        var mine = new[] { romanceShow, romanceFilm, bromanceShow, horrorShow, animeFilm, childrenShow };

        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "romance");
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "anime");
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "family");

        HashSet<Guid> persisted;
        FilterRuleSet drafted;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            persisted = (await new EligibilityQueryBuilder(db)
                .EligibleTitles(accountId)
                .Where(t => mine.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync()).ToHashSet();
            drafted = FilterRuleSet.FromRows(await db.ContentFilters.Where(f => f.AccountId == accountId).ToListAsync());
        }

        var draft = (await EligibleAsync(accountId, drafted, mine)).ToHashSet();
        Assert.Equal(persisted, draft);
        Assert.Equal(new HashSet<Guid> { romanceFilm, bromanceShow, horrorShow }, draft);

        // And the meter costs the stand-ins as part of the rule they belong to: relaxing "romance"
        // returns the romance-keyword show, so its marginal is not zero.
        await using var scoped = fixture.CreateContext(new AccountContext());
        var relaxed = (await new EligibilityQueryBuilder(scoped)
            .EligibleTitles(accountId, drafted.Without(new FilterRule(FilterKind.ExcludeGenre, "romance")))
            .Where(t => mine.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync()).ToHashSet();
        Assert.Contains(romanceShow, relaxed);
        Assert.DoesNotContain(animeFilm, relaxed);
    }

    [Fact]
    public async Task The_meter_reports_the_pool_under_the_shrunk_crowd_floor_without_serving_it()
    {
        if (!fixture.Available)
        {
            return;
        }

        // FD-7: the badge shows the vote-shrunk score; the floor keeps the raw average until the
        // founder confirms. Both directions of the change must be visible: a thin high sample drops
        // out under a floor above the prior, and a thin low sample comes IN under a floor below it.
        var accountId = await SeedAccountAsync();
        var thinHigh = await SeedTitleAsync(t => { t.TmdbVoteAverage = 7.5m; t.TmdbVoteCount = 10; });
        var solidHigh = await SeedTitleAsync(t => { t.TmdbVoteAverage = 7.5m; t.TmdbVoteCount = 9000; });
        var thinLow = await SeedTitleAsync(t => { t.TmdbVoteAverage = 5.0m; t.TmdbVoteCount = 2; });
        var traktLed = await SeedTitleAsync(t =>
        {
            t.TmdbVoteAverage = 7.6m;
            t.TmdbVoteCount = 3000;
            t.TraktRating = 6.4m;
            t.TraktVotes = 5000;
        });
        var unknownVotes = await SeedTitleAsync(t => { t.TmdbVoteAverage = 9.0m; t.TmdbVoteCount = 0; });
        var mine = new[] { thinHigh, solidHigh, thinLow, traktLed, unknownVotes };

        var seven = FilterRuleSet.Create(null, null, null, null, 7.0m, null, null);
        Assert.Equal(
            new HashSet<Guid> { thinHigh, solidHigh, traktLed, unknownVotes },
            (await EligibleAsync(accountId, seven, mine)).ToHashSet());
        Assert.Equal(
            new HashSet<Guid> { solidHigh },
            (await EligibleShrunkAsync(accountId, seven, mine)).ToHashSet());

        var six = FilterRuleSet.Create(null, null, null, null, 6.0m, null, null);
        Assert.DoesNotContain(thinLow, await EligibleAsync(accountId, six, mine));
        Assert.Contains(thinLow, await EligibleShrunkAsync(accountId, six, mine));

        // The meter carries the figure only when there is a floor to re-read.
        Assert.NotNull((await MeasureAsync(accountId, seven)).EligibleUnderShrunkFloor);
        Assert.Null((await MeasureAsync(accountId, FilterRuleSet.Create(["comedy"], null, null, null, null, null, null))).EligibleUnderShrunkFloor);
    }

    [Fact]
    public async Task An_unscoped_context_is_refused_rather_than_answered_for_nobody()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext(new AccountContext());
        var meter = new FilterCostMeter(
            db, new EligibilityQueryBuilder(db), new AccountContext(), new MemoryCache(new MemoryCacheOptions()));

        // Unscoped means the global query filters pass everything, so an answer would be a real
        // number belonging to no one — the exact failure mode that must be loud, not silent.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => meter.MeasureAsync(FilterRuleSet.Create(["comedy"], null, null, ["en"], null, null, null)));
    }

    [Fact]
    public async Task One_accounts_footprint_never_moves_another_accounts_count()
    {
        if (!fixture.Available)
        {
            return;
        }

        var mine = await SeedAccountAsync();
        var theirs = await SeedAccountAsync();

        var shared = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Year = 2012; t.TmdbVoteAverage = 8.3m; });
        var ids = new[] { shared };
        var rules = FilterRuleSet.Create(null, null, null, ["en"], null, 2000, null);

        Assert.Contains(shared, await EligibleAsync(mine, rules, ids));
        Assert.Contains(shared, await EligibleAsync(theirs, rules, ids));

        // The OTHER account watches it. Eligibility is per-account by construction, so this must move
        // exactly one of the two counts.
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            db.Add(new WatchedTitle
            {
                Id = Guid.NewGuid(),
                AccountId = theirs,
                TitleId = shared,
                Plays = 1,
                LastWatchedAt = DateTime.UtcNow,
                IsFullyWatched = true,
                FirstSyncedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        Assert.Contains(shared, await EligibleAsync(mine, rules, ids));
        Assert.DoesNotContain(shared, await EligibleAsync(theirs, rules, ids));

        // And through the meter itself: two accounts, one draft, two different eligible counts —
        // proving the cache key is per-account and not just per-draft.
        // Both accounts are freshly seeded and carry the identical draft, so the ONLY difference
        // between their eligible counts is that one watched title — exactly one, catalogue-wide.
        var minesReport = await MeasureAsync(mine, rules);
        var theirsReport = await MeasureAsync(theirs, rules);
        Assert.Equal(minesReport.Total, theirsReport.Total);
        Assert.Equal(minesReport.Eligible, theirsReport.Eligible + 1);
    }

    [Fact]
    public async Task The_cost_of_every_rule_comes_from_one_statement()
    {
        if (!fixture.Available)
        {
            return;
        }

        // H-6: the meter ran 1 total + 1 eligible + 1 count per rule + 1 shrunk count — 13 sequential
        // scans for the founder's rules, 7–53 s per draft. One draft is now ONE statement, however
        // many rules it carries.
        var accountId = await SeedAccountAsync();
        var rules = FilterRuleSet.Create(
            ["comedy", "romance", "anime", "history"], null, ["zombie", "gore"], ["en"], 6.0m, 2000, 2024);
        Assert.True(rules.Rules().Count >= 9);

        var counter = new CommandCounter();
        var context = new AccountContext();
        context.SetAccount(accountId);
        await using var db = new ReelDbContext(
            new DbContextOptionsBuilder<ReelDbContext>()
                .UseNpgsql(fixture.ConnectionString!, npgsql => npgsql.UseVector())
                .AddInterceptors(counter)
                .Options,
            context);
        var meter = new FilterCostMeter(db, new EligibilityQueryBuilder(db), context, new MemoryCache(new MemoryCacheOptions()));

        var report = await meter.MeasureAsync(rules);

        Assert.Equal(1, counter.Executed);
        Assert.Equal(rules.Rules().Count, report.ByRule.Count);
        Assert.NotNull(report.EligibleUnderShrunkFloor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Single_pass_marginals_equal_the_subtractive_counts(bool twoAllowListedGenres)
    {
        if (!fixture.Available)
        {
            return;
        }

        // The single pass is a second phrasing of "count, then re-count with one rule relaxed". Both
        // are run here over the WHOLE catalogue for a rule set that touches every kind — stand-ins,
        // aliases, the anime composite, an allow-list of one genre (relaxing it widens to everything)
        // and of two (relaxing one can only narrow, so the gain is 0) — and must agree exactly.
        var accountId = await SeedAccountAsync();
        var watchedComedy = await SeedTitleAsync(t => { t.Genres = ["comedy"]; t.Year = 2012; t.TmdbVoteAverage = 8.2m; });
        await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Genres = ["drama"]; t.Keywords = ["office romance"]; t.Year = 2015; t.TmdbVoteAverage = 7.5m; });
        await SeedTitleAsync(t => { t.Genres = ["animation", "drama"]; t.Language = "ja"; t.Year = 2016; t.TmdbVoteAverage = 8.0m; });
        await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Year = 1995; t.TmdbVoteAverage = 8.0m; });
        await SeedTitleAsync(t => { t.Genres = ["thriller"]; t.Year = 2014; t.TmdbVoteAverage = 5.0m; t.TmdbVoteCount = 3; });
        await SeedTitleAsync(t => { t.Genres = ["crime"]; t.Year = 2019; t.TmdbVoteAverage = 7.9m; t.Language = "ko"; });
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            db.Add(new WatchedTitle
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = watchedComedy,
                Plays = 1,
                LastWatchedAt = DateTime.UtcNow,
                IsFullyWatched = true,
                FirstSyncedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var rules = FilterRuleSet.Create(
            ["comedy", "romance", "anime", "thriller"],
            twoAllowListedGenres ? ["drama", "crime"] : ["drama"],
            // One keyword runs ungated; three share the AnyKeyword gate (R1-20), so both phrasings of
            // the keyword bits are held to the subtractive counts over the whole catalogue.
            twoAllowListedGenres ? ["zombie", "shark", "c++"] : ["zombie"],
            ["en"],
            6.0m,
            2000,
            2023);

        // One context, one REPEATABLE READ snapshot for the meter and every subtractive count: the
        // catalogue is shared with test classes running in parallel, and a title inserted between
        // the two readings would make two correct counts disagree.
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var check = fixture.CreateContext(scoped);
        await using var snapshot = await check.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var builder = new EligibilityQueryBuilder(check);
        var report = await new FilterCostMeter(check, builder, scoped, new MemoryCache(new MemoryCacheOptions())).MeasureAsync(rules);
        var eligible = await builder.EligibleTitles(accountId, rules).CountAsync();
        Assert.Equal(await check.Titles.CountAsync(), report.Total);
        Assert.Equal(eligible, report.Eligible);
        Assert.Equal(await builder.EligibleTitles(accountId, rules, CrowdFloorMeasure.Shrunk).CountAsync(), report.EligibleUnderShrunkFloor);
        foreach (var rule in rules.Rules())
        {
            var without = await builder.EligibleTitles(accountId, rules.Without(rule)).CountAsync();
            Assert.Equal(
                (rule.Kind, rule.Value, Math.Max(0, without - eligible)),
                (rule.Kind, rule.Value, Marginal(report, rule.Kind, rule.Value)));
        }
    }

    /// <summary>
    /// R1-06: a title with no crowd rating from either source FAILS the floor on the serving path, so
    /// it breaks the floor in the meter too. In SQL the raw reading is NULL for such a title, and
    /// NOT (NULL &gt;= floor) is NULL, so the breach bit stayed clear and the meter counted every
    /// unrated title as eligible — 3,706 against a SQL truth of 3,507 on the founder's rules. Each
    /// way a title can be unrated is its own case: no TMDB average, a zero TMDB average (TMDB's "no
    /// votes"), and a zero Trakt rating behind either.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, null)]
    [InlineData(null, 0.0)]
    [InlineData(0.0, 0.0)]
    public async Task An_unrated_title_breaks_the_crowd_floor_in_the_meter_as_it_does_in_eligibility(double? tmdb, double? trakt)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var genre = $"unrated{Guid.NewGuid():N}"[..16];
        var rated = await SeedTitleAsync(t => { t.Genres = [genre]; t.Year = 2015; t.TmdbVoteAverage = 7.5m; t.TmdbVoteCount = 900; });
        var traktOnly = await SeedTitleAsync(t => { t.Genres = [genre]; t.Year = 2015; t.TmdbVoteAverage = null; t.TraktRating = 7.1m; });
        var unrated = await SeedTitleAsync(t =>
        {
            t.Genres = [genre];
            t.Year = 2016;
            t.TmdbVoteAverage = (decimal?)tmdb;
            t.TraktRating = (decimal?)trakt;
        });

        // Allow-listing a genre only these three carry keeps the counts to this test's own titles.
        var rules = FilterRuleSet.Create(null, [genre], null, null, 6.0m, null, null);
        Assert.Equal(new HashSet<Guid> { rated, traktOnly }, (await EligibleAsync(accountId, rules, [rated, traktOnly, unrated])).ToHashSet());

        var report = await MeasureAsync(accountId, rules);
        Assert.Equal(2, report.Eligible);
        Assert.Equal(1, Marginal(report, FilterKind.MinCrowdRating, "6.0"));
        Assert.Equal((await EligibleShrunkAsync(accountId, rules, [rated, traktOnly, unrated])).Count, report.EligibleUnderShrunkFloor);
    }

    /// <summary>
    /// A keyword exclusion matches its value LITERALLY and regardless of case in the name and the
    /// overview, on the persisted path, the draft path and the meter alike. The draft path and the
    /// meter read a pattern rather than lower(…) (R1-20, for speed), so a value carrying a pattern
    /// character — "sci.fi", "c++", "(500)" — is the case that would tell the two phrasings apart.
    /// </summary>
    [Theory]
    [InlineData("sci.fi", "A loud SCI.FI romp", "A loud sci-fi romp")]
    [InlineData("c++", "Coding in C++ at midnight", "Coding in C at midnight")]
    [InlineData("(500)", "Days (500) of summer", "Days 500 of summer")]
    [InlineData("zombie", "The ZOMBIE apocalypse", "The zomb ie apocalypse")]
    public async Task Keyword_exclusions_match_literally_and_ignore_case_on_every_path(string keyword, string matches, string doesNot)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var genre = $"literal{Guid.NewGuid():N}"[..16];
        var inOverview = await SeedTitleAsync(t => { t.Genres = [genre]; t.Year = 2015; t.TmdbVoteAverage = 7.5m; t.Overview = matches; });
        var inName = await SeedTitleAsync(t => { t.Genres = [genre]; t.Year = 2015; t.TmdbVoteAverage = 7.5m; t.Name = $"{matches} {Guid.NewGuid():N}"; });
        var lookalike = await SeedTitleAsync(t => { t.Genres = [genre]; t.Year = 2015; t.TmdbVoteAverage = 7.5m; t.Overview = doesNot; });
        var inKeywords = await SeedTitleAsync(t => { t.Genres = [genre]; t.Year = 2015; t.TmdbVoteAverage = 7.5m; t.Keywords = ["night", $"old {keyword} lore"]; });

        // Split across two labels: the labels are searched joined, and a match must never span two.
        var acrossLabels = await SeedTitleAsync(t => { t.Genres = [genre]; t.Year = 2015; t.TmdbVoteAverage = 7.5m; t.Keywords = [keyword[..2], keyword[2..]]; });
        var mine = new[] { inOverview, inName, lookalike, inKeywords, acrossLabels };
        await SeedFilterAsync(accountId, FilterKind.IncludeGenre, genre);
        await SeedFilterAsync(accountId, FilterKind.ExcludeKeyword, keyword);
        await SeedFilterAsync(accountId, FilterKind.Language, "*");

        HashSet<Guid> persisted;
        FilterRuleSet drafted;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            persisted = (await new EligibilityQueryBuilder(db).EligibleTitles(accountId).Where(t => mine.Contains(t.Id)).Select(t => t.Id).ToListAsync()).ToHashSet();
            drafted = FilterRuleSet.FromRows(await db.ContentFilters.Where(f => f.AccountId == accountId).ToListAsync());
        }

        Assert.Equal(new HashSet<Guid> { lookalike, acrossLabels }, persisted);
        Assert.Equal(persisted, (await EligibleAsync(accountId, drafted, mine)).ToHashSet());
        var report = await MeasureAsync(accountId, drafted);
        Assert.Equal(2, report.Eligible);
        Assert.Equal(3, Marginal(report, FilterKind.ExcludeKeyword, keyword));
    }

    [Fact]
    public async Task Excluding_anime_removes_japanese_animation_on_both_paths()
    {
        if (!fixture.Available)
        {
            return;
        }

        // H-7: neither TMDB taxonomy has an anime genre and 2 titles carry Trakt's slug, so "no anime"
        // removed nothing. Anime is animation in Japanese; animation in any other language is not.
        var accountId = await SeedAccountAsync();
        var anime = await SeedTitleAsync(t => { t.Genres = ["animation", "action"]; t.Language = "ja"; });
        var animeShow = await SeedTitleAsync(t => { t.MediaType = MediaType.Show; t.Genres = ["animation"]; t.Language = "JA"; });
        var cartoon = await SeedTitleAsync(t => { t.Genres = ["animation"]; t.Language = "en"; });
        var japaneseDrama = await SeedTitleAsync(t => { t.Genres = ["drama"]; t.Language = "ja"; });
        var mine = new[] { anime, animeShow, cartoon, japaneseDrama };
        await SeedFilterAsync(accountId, FilterKind.ExcludeGenre, "anime");
        await SeedFilterAsync(accountId, FilterKind.Language, "*");

        HashSet<Guid> persisted;
        FilterRuleSet drafted;
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            persisted = (await new EligibilityQueryBuilder(db)
                .EligibleTitles(accountId)
                .Where(t => mine.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync()).ToHashSet();
            drafted = FilterRuleSet.FromRows(await db.ContentFilters.Where(f => f.AccountId == accountId).ToListAsync());
        }

        Assert.Equal(new HashSet<Guid> { cartoon, japaneseDrama }, persisted);
        Assert.Equal(persisted, (await EligibleAsync(accountId, drafted, mine)).ToHashSet());
        Assert.True(Marginal(await MeasureAsync(accountId, drafted), FilterKind.ExcludeGenre, "anime") >= 2);
    }

    /// <summary>
    /// R1-20: two or more keyword exclusions share one gate in the meter's pass, and only the titles
    /// it admits run each keyword's own pattern. Every keyword bit must come out exactly as it did
    /// ungated, whichever field carries the match (name, overview or label), with a regex
    /// metacharacter in the mix, and with a title that carries two keywords (its marginal for each is
    /// 0, because relaxing one still leaves it held by the other). The unique allow-listed genre
    /// scopes every marginal to this test's titles. Checked against the subtractive counts too.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Gated_keyword_exclusions_keep_every_keyword_bit(int keywordCount)
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var genre = $"gated{Guid.NewGuid():N}"[..16];
        string[] keywords = ["gore", "vampire", "clown", "c++"];
        void Base(Title t)
        {
            t.Genres = [genre];
            t.Year = 2015;
            t.TmdbVoteAverage = 7.5m;
        }

        var inName = await SeedTitleAsync(t => { Base(t); t.Name = $"GORE fest {Guid.NewGuid():N}"; });
        var inOverview = await SeedTitleAsync(t => { Base(t); t.Overview = "A lonely Vampire in Lisbon"; });
        var inLabels = await SeedTitleAsync(t => { Base(t); t.Keywords = ["night", "evil clown"]; });
        var metachar = await SeedTitleAsync(t => { Base(t); t.Overview = "Coding in C++ at midnight"; });
        var two = await SeedTitleAsync(t => { Base(t); t.Name = $"Vampire {Guid.NewGuid():N}"; t.Overview = "Wall-to-wall gore."; });
        var clean = await SeedTitleAsync(t => { Base(t); t.Overview = "Coding in C at midnight, gor e, vamp ire"; });
        var mine = new[] { inName, inOverview, inLabels, metachar, two, clean };
        var used = keywords[..keywordCount];
        var rules = FilterRuleSet.Create(null, [genre], used, ["*"], null, null, null);

        var holders = new Dictionary<string, Guid[]>
        {
            ["gore"] = [inName, two],
            ["vampire"] = [inOverview, two],
            ["clown"] = [inLabels],
            ["c++"] = [metachar],
        };
        var expectedEligible = mine.Where(id => !used.Any(k => holders[k].Contains(id))).ToHashSet();
        Assert.Equal(expectedEligible, (await EligibleAsync(accountId, rules, mine)).ToHashSet());

        var report = await MeasureAsync(accountId, rules);
        Assert.Equal(expectedEligible.Count, report.Eligible);
        foreach (var keyword in used)
        {
            // Held out by this keyword and no other kept keyword: "two" counts only when its other
            // keyword is not in the rule set.
            var alone = holders[keyword].Count(id => !used.Any(other => other != keyword && holders[other].Contains(id)));
            Assert.Equal((keyword, alone), (keyword, Marginal(report, FilterKind.ExcludeKeyword, keyword)));
            var relaxed = await EligibleAsync(accountId, rules.Without(new FilterRule(FilterKind.ExcludeKeyword, keyword)), mine);
            Assert.Equal((keyword, relaxed.Count - expectedEligible.Count), (keyword, Marginal(report, FilterKind.ExcludeKeyword, keyword)));
        }
    }

    private static int Marginal(FilterCostReport report, FilterKind kind, string value) =>
        report.ByRule.Single(r => r.Kind == kind.ToString() && r.Value == value).Marginal;

    private async Task<FilterCostReport> MeasureAsync(Guid accountId, FilterRuleSet rules)
    {
        var context = new AccountContext();
        context.SetAccount(accountId);
        await using var db = fixture.CreateContext(context);
        var meter = new FilterCostMeter(
            db, new EligibilityQueryBuilder(db), context, new MemoryCache(new MemoryCacheOptions()));
        return await meter.MeasureAsync(rules);
    }

    /// <summary>Eligible ids among <paramref name="mine"/> under a draft, the crowd floor read on the shrunk score.</summary>
    private async Task<IReadOnlyList<Guid>> EligibleShrunkAsync(Guid accountId, FilterRuleSet rules, IReadOnlyCollection<Guid> mine)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await new EligibilityQueryBuilder(db)
            .EligibleTitles(accountId, rules, CrowdFloorMeasure.Shrunk)
            .Where(t => mine.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync();
    }

    /// <summary>Eligible ids among <paramref name="mine"/> under a DRAFT rule set.</summary>
    private async Task<IReadOnlyList<Guid>> EligibleAsync(Guid accountId, FilterRuleSet rules, IReadOnlyCollection<Guid> mine)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        return await new EligibilityQueryBuilder(db)
            .EligibleTitles(accountId, rules)
            .Where(t => mine.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync();
    }

    private async Task<Guid> SeedAccountAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"cost-{Guid.NewGuid():N}",
            Tier = AccountTier.Paid,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(Action<Title> shape)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = MediaType.Movie,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"cost-fixture {Guid.NewGuid():N}",
            Language = "en",
            PosterPath = "/p.jpg",
            Genres = ["drama"],
            // Bottom of the popularity order, so these fixtures never displace another integration
            // test's top-N assertions on the shared catalogue.
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

    /// <summary>Counts the statements a context sends, so a test can pin "one pass" literally.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int executed;

        public int Executed => executed;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref executed);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref executed);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
