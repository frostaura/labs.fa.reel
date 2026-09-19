using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The keyless NL-search engine end-to-end on real Postgres: typo-tolerant concept queries
/// return the right titles, ranked by intent coverage; and content filters stay airtight —
/// a TMDB-keyword exclusion (the "remove lgbt themes" case) removes a title from search
/// even when nothing else about it matches the excluded word, without over-reaching onto
/// titles whose keyword merely prefixes the filter word.
/// </summary>
[Collection("postgres")]
public class LexicalSearchIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Misspelled_concept_query_finds_and_ranks_by_intent_coverage()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedTitleAsync("A Knight's Tale", ["adventure", "comedy"], ["medieval", "knight", "jousting"]);
        await SeedTitleAsync("Generic Comedy", ["comedy"], ["stand-up"]);
        await SeedTitleAsync("Unrelated Drama", ["drama"], ["divorce"]);

        var results = await SearchAsync(accountId, "something medevil and fun");

        Assert.True(results.Count >= 2);
        // Medieval+fun coverage must beat fun-only — regardless of popularity.
        Assert.Equal("A Knight's Tale", results[0].Title.Name);
        Assert.DoesNotContain(results, r => r.Title.Name == "Unrelated Drama");
    }

    // Was quarantined as a "local-only flake" until 2026-09-10; it was neither local nor flaky.
    // The old body seeded the TMDB keyword "lgbt" and the filter "lgbtq" and asserted the title
    // was removed — i.e. the REVERSE arm (filter.StartsWith(keyword)). That arm was deliberately
    // deleted from EligibilityQueryBuilder in 2767cf9 (2026-07-24) because it over-excluded: the
    // filter "warehouse" swallowed the unrelated keyword "ware". The test was never updated, so
    // it has been asserting a superseded contract ever since — red everywhere, CI included.
    // It now asserts the surviving contract (forward containment over TMDB's real phrase shape),
    // and the counterexample that killed the reverse arm is pinned below so it cannot come back.
    [Fact]
    public async Task Keyword_exclusion_filter_removes_titles_by_tmdb_keyword()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        // TMDB stores keywords as lowercase phrases ("lgbt themes", not "lgbt") — see
        // TmdbClient's keyword mapping, which trims and lowercases every entry.
        await SeedTitleAsync("Filtered Romance", ["romance"], ["lgbt themes", "love"]);
        await SeedTitleAsync("Kept Romance", ["romance"], ["love"]);

        await AddKeywordFilterAsync(accountId, "lgbt");

        var results = await SearchAsync(accountId, "romantic");

        Assert.Contains(results, r => r.Title.Name == "Kept Romance");
        Assert.DoesNotContain(results, r => r.Title.Name == "Filtered Romance");
    }

    /// <summary>
    /// The counterexample that retired the reverse (filter-contains-keyword) arm: a filter must
    /// never exclude a title on a keyword that merely happens to be a PREFIX of the filter word.
    /// Without this, "warehouse" removes everything tagged "ware". Pinned so the arm stays dead.
    /// </summary>
    [Fact]
    public async Task Keyword_exclusion_never_bites_on_a_shorter_unrelated_keyword()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        await SeedTitleAsync("Ware Romance", ["romance"], ["ware", "love"]);

        await AddKeywordFilterAsync(accountId, "warehouse");

        var results = await SearchAsync(accountId, "romantic");

        Assert.Contains(results, r => r.Title.Name == "Ware Romance");
    }

    private async Task AddKeywordFilterAsync(Guid accountId, string value)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.ContentFilters.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Kind = FilterKind.ExcludeKeyword,
            Value = value,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Discovery_never_shows_titles_with_any_interaction_footprint()
    {
        if (!fixture.Available)
        {
            return;
        }

        var accountId = await SeedAccountAsync();
        var ratedOnly = await SeedTitleAsync("Rated Long Ago", ["comedy"], []);
        var inProgress = await SeedTitleAsync("Half Watched Show", ["comedy"], []);
        var savedForLater = await SeedTitleAsync("Already On My List", ["comedy"], []);
        await SeedTitleAsync("Genuinely New", ["comedy"], []);

        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            // Rated without any logged play — still "seen" (founder rule 2026-06-12).
            db.UserRatings.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = ratedOnly,
                SubjectType = RatingSubjectType.Movie,
                Rating = 8,
                RatedAt = DateTime.UtcNow,
                SyncedAt = DateTime.UtcNow,
            });
            // Partially watched — belongs to continue-watching, never discovery.
            db.WatchedTitles.Add(new WatchedTitle
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = inProgress,
                Plays = 3,
                IsFullyWatched = false,
                FirstSyncedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            // Saved for later — a decision already made; lives on Saved, not in the picks
            // (founder addition 2026-06-19).
            db.UserTitleReactions.Add(new UserTitleReaction
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = savedForLater,
                Kind = ReactionKind.SaveForLater,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var results = await SearchAsync(accountId, "funny");

        Assert.Contains(results, r => r.Title.Name == "Genuinely New");
        Assert.DoesNotContain(results, r => r.Title.Name == "Rated Long Ago");
        Assert.DoesNotContain(results, r => r.Title.Name == "Half Watched Show");
        Assert.DoesNotContain(results, r => r.Title.Name == "Already On My List");
    }

    [Fact]
    public async Task Restricted_maturity_profile_never_sees_over_ceiling_titles()
    {
        if (!fixture.Available)
        {
            return;
        }

        // A kids-guarded profile (ceiling PG-13) searching a comedy concept must never surface an
        // R-rated match — maturity is a safety gate, not a preference, so it bites even when the
        // over-ceiling title is the strongest concept match.
        var accountId = await SeedAccountAsync(maturityCeiling: "PG-13");
        await SeedTitleAsync("Family Comedy", ["comedy"], ["feel-good"], certification: "PG");
        await SeedTitleAsync("Raunchy Comedy", ["comedy"], ["parody"], certification: "R");

        var results = await SearchAsync(accountId, "funny");

        Assert.Contains(results, r => r.Title.Name == "Family Comedy");
        Assert.DoesNotContain(results, r => r.Title.Name == "Raunchy Comedy");
    }

    private async Task<Guid> SeedAccountAsync(string? maturityCeiling = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"lexical-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        account.Settings.MaturityCeiling = maturityCeiling;
        db.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(string name, string[] genres, string[] keywords, string? certification = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var id = Guid.NewGuid();
        db.Add(new Title
        {
            Id = id,
            MediaType = MediaType.Movie,
            TraktId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = name.ToLowerInvariant().Replace(' ', '-'),
            Name = name,
            Genres = genres,
            Keywords = keywords,
            Certification = certification,
            // English by default: the eligibility builder excludes null-language titles under the
            // English-only default, which would otherwise mask what each assertion checks.
            Language = "en",
            TmdbPopularity = 50,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<List<LexicalSearchService.LexicalResult>> SearchAsync(Guid accountId, string query)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = fixture.CreateContext(scoped);
        var service = new LexicalSearchService(new EligibilityQueryBuilder(db));
        return await service.SearchAsync(accountId, query, 24, CancellationToken.None);
    }
}
