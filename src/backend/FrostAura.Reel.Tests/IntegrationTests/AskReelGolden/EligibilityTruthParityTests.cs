using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FrostAura.Reel.Tests.IntegrationTests.AskReelGolden;

/// <summary>
/// The golden eval judges "is this card allowed on screen?" with its OWN statement of the eligibility
/// rule (<c>eval/askreel/truth/_eligible.sql</c>), written from the founder-locked wording rather than
/// generated from <see cref="EligibilityQueryBuilder"/>. Two statements of one rule are only useful while
/// they agree, so this pins them together on a fixture that trips every rule from both sides: if the
/// builder drifts, the eval's leak count would silently stop meaning anything, and if the truth drifts,
/// the eval would report leaks that are not there.
///
/// The fixture is varied across accounts (explicit language, no language rows, the "*" sentinel, an
/// include-genre allow-list, maturity ceilings) and media (movies and shows, including the FirstAiredAt
/// year arm), because a parity test on one configuration passes for the wrong reason as soon as one
/// rule's arm is vacuous for that configuration. The builder side is the persisted overload AND the
/// <see cref="FilterRuleSet"/> overload, each followed by the in-memory maturity gate every surface
/// applies, so all three statements of the rule are pinned together.
/// </summary>
[Collection("postgres")]
public class EligibilityTruthParityTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Truth_and_builder_agree_on_every_rule_with_explicit_conditions()
    {
        if (!fixture.Available)
        {
            return;
        }

        var account = await SeedAccountAsync(
            (FilterKind.ExcludeGenre, "horror"),
            (FilterKind.ExcludeKeyword, "ninja"),
            (FilterKind.Language, "en"),
            (FilterKind.MinCrowdRating, "6.0"),
            (FilterKind.MinYear, "2000"),
            (FilterKind.MaxYear, "2020"));

        var t = new Dictionary<string, Guid>
        {
            ["clean"] = await SeedTitleAsync(),
            ["excluded-genre"] = await SeedTitleAsync(genres: ["drama", "horror"]),
            ["keyword-phrase"] = await SeedTitleAsync(keywords: ["female ninja assassin"]),
            ["keyword-in-name"] = await SeedTitleAsync(name: "Way of the Ninja"),
            ["keyword-in-overview"] = await SeedTitleAsync(overview: "A retired NINJA returns."),
            ["french"] = await SeedTitleAsync(language: "fr"),
            ["no-language"] = await SeedTitleAsync(language: null),
            ["crowd-under-floor"] = await SeedTitleAsync(tmdb: 5.9m),
            ["crowd-from-trakt"] = await SeedTitleAsync(tmdb: 0m, trakt: 7.1m),
            ["crowd-missing"] = await SeedTitleAsync(tmdb: null, trakt: null),
            ["before-min-year"] = await SeedTitleAsync(year: 1999),
            ["after-max-year"] = await SeedTitleAsync(year: 2021),
            ["year-from-release-date"] = await SeedTitleAsync(year: null, releasedAt: new DateTime(2005, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            ["release-date-too-old"] = await SeedTitleAsync(year: null, releasedAt: new DateTime(1995, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            ["undated"] = await SeedTitleAsync(year: null),
            ["watched"] = await SeedTitleAsync(),
            ["rated"] = await SeedTitleAsync(),
            ["not-interested"] = await SeedTitleAsync(),
            ["not-interested-revoked"] = await SeedTitleAsync(),
            ["saved"] = await SeedTitleAsync(),
            ["dropped"] = await SeedTitleAsync(),
            ["someone-elses-watch"] = await SeedTitleAsync(),
        };

        await SeedWatchedAsync(account, t["watched"]);
        await SeedRatingAsync(account, t["rated"]);
        await ReactAsync(account, t["not-interested"], ReactionKind.NotInterested);
        await ReactAsync(account, t["not-interested-revoked"], ReactionKind.NotInterested, revoked: true);
        await ReactAsync(account, t["saved"], ReactionKind.SaveForLater);
        await ReactAsync(account, t["dropped"], ReactionKind.Dropped);
        // Another account's footprint is not this account's: tenancy, not a global "seen" flag.
        await SeedWatchedAsync(await SeedAccountAsync(), t["someone-elses-watch"]);

        var (builder, truth) = await BothAsync(account, t.Values);

        Assert.Equal(builder, truth);
        // Non-vacuous: exactly these survive, so a rule that silently stopped binding fails here too.
        Assert.Equal(
            new[] { "clean", "crowd-from-trakt", "year-from-release-date", "undated", "not-interested-revoked", "someone-elses-watch" }
                .Select(k => t[k]).ToHashSet(),
            truth);
    }

    [Fact]
    public async Task Truth_and_builder_agree_when_the_account_has_no_language_rows_and_when_it_opts_every_language_in()
    {
        if (!fixture.Available)
        {
            return;
        }

        var englishByDefault = await SeedAccountAsync();
        var everyLanguage = await SeedAccountAsync((FilterKind.Language, "*"));
        var english = await SeedTitleAsync(language: "en");
        var korean = await SeedTitleAsync(language: "ko");
        var unknown = await SeedTitleAsync(language: null);
        var mine = new[] { english, korean, unknown };

        var (defaultBuilder, defaultTruth) = await BothAsync(englishByDefault, mine);
        Assert.Equal(defaultBuilder, defaultTruth);
        Assert.Equal([english], defaultTruth);

        var (starBuilder, starTruth) = await BothAsync(everyLanguage, mine);
        Assert.Equal(starBuilder, starTruth);
        Assert.Equal(mine.ToHashSet(), starTruth);
    }

    [Fact]
    public async Task Truth_and_builder_agree_on_an_include_genre_allow_list_with_an_exclusion_on_top()
    {
        if (!fixture.Available)
        {
            return;
        }

        var account = await SeedAccountAsync(
            (FilterKind.IncludeGenre, "thriller"),
            (FilterKind.IncludeGenre, "crime"),
            (FilterKind.ExcludeGenre, "horror"));
        var thriller = await SeedTitleAsync(genres: ["thriller"]);
        var crimeDrama = await SeedTitleAsync(genres: ["crime", "drama"]);
        var drama = await SeedTitleAsync(genres: ["drama"]);
        var horrorThriller = await SeedTitleAsync(genres: ["thriller", "horror"]);

        var (builder, truth) = await BothAsync(account, [thriller, crimeDrama, drama, horrorThriller]);

        Assert.Equal(builder, truth);
        Assert.Equal(new[] { thriller, crimeDrama }.ToHashSet(), truth);
    }

    [Fact]
    public async Task Every_truth_query_runs_against_the_current_schema_and_returns_an_id_column()
    {
        if (!fixture.Available)
        {
            return;
        }

        // The truth queries read the catalogue directly, so a column another workstream renames would
        // break the eval silently at run time. Executing every file against the migrated schema turns
        // that into a red build, and the result must be exactly one uuid column named "Id" — the shape
        // the runner joins on. (That every query matches real titles is checked against the snapshot by
        // `askreel.mjs check --db`, which fails on an empty truth set.)
        var account = await SeedAccountAsync();
        var files = Directory.GetFiles(Path.Combine(RepoRoot(), "eval", "askreel", "truth"), "*.sql");
        Assert.True(files.Length >= 40, $"expected the golden truth set, found {files.Length} files");

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        foreach (var file in files)
        {
            var sql = Bind(await File.ReadAllTextAsync(file), account);
            await using var command = new NpgsqlCommand($"SELECT * FROM ({sql}) q LIMIT 0", connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(reader.FieldCount == 1, $"{Path.GetFileName(file)} returns {reader.FieldCount} columns");
            Assert.Equal("Id", reader.GetName(0));
            Assert.Equal(typeof(Guid), reader.GetFieldType(0));
        }
    }

    [Fact]
    public async Task Truth_queries_select_what_they_name_on_a_seeded_catalogue()
    {
        if (!fixture.Available)
        {
            return;
        }

        // Non-vacuous on a fixture: one title per arm (keyword phrase, title name, genre) must be IN its
        // truth set, and a near-miss must be OUT, so a predicate that silently matches nothing goes red.
        var account = await SeedAccountAsync();
        var zombieByKeyword = await SeedTitleAsync(keywords: ["zombie apocalypse"]);
        var zombieByName = await SeedTitleAsync(name: "Zombies of the Golden Parity");
        var crime = await SeedTitleAsync(genres: ["crime"]);
        var heist = await SeedTitleAsync(keywords: ["heist"]);
        var neither = await SeedTitleAsync(name: "golden-parity zombielike", genres: ["drama"]);

        var zombie = await TruthAsync("zombie", account);
        Assert.Contains(zombieByKeyword, zombie);
        Assert.Contains(zombieByName, zombie);
        Assert.DoesNotContain(neither, zombie);
        Assert.Contains(crime, await TruthAsync("genre-crime", account));
        Assert.DoesNotContain(neither, await TruthAsync("genre-crime", account));
        Assert.Contains(heist, await TruthAsync("heist", account));
    }

    private async Task<HashSet<Guid>> TruthAsync(string key, Guid accountId)
    {
        var sql = Bind(await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "eval", "askreel", "truth", $"{key}.sql")), accountId);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT q.\"Id\" FROM ({sql}) q", connection);
        var ids = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    [Fact]
    public async Task Truth_and_builder_agree_on_maturity_ceilings_including_unrated_titles()
    {
        if (!fixture.Available)
        {
            return;
        }

        var teen = await SeedAccountAsync(maturity: "PG-13");
        var adult = await SeedAccountAsync(maturity: "R");
        var unknownCeiling = await SeedAccountAsync(maturity: "not-a-rating");
        var pg = await SeedTitleAsync(certification: "PG");
        var tv14 = await SeedTitleAsync(certification: "tv-14", mediaType: MediaType.Show);
        var r = await SeedTitleAsync(certification: "R");
        var nc17 = await SeedTitleAsync(certification: "NC-17");
        var unrated = await SeedTitleAsync(certification: null);
        var odd = await SeedTitleAsync(certification: "Approved");
        var mine = new[] { pg, tv14, r, nc17, unrated, odd };

        var (teenBuilder, teenTruth) = await BothAsync(teen, mine);
        Assert.Equal(teenBuilder, teenTruth);
        Assert.Equal(new[] { pg, tv14 }.ToHashSet(), teenTruth);

        // An adult ceiling admits titles with no (or an unknown) certification; a kids' profile never does.
        var (adultBuilder, adultTruth) = await BothAsync(adult, mine);
        Assert.Equal(adultBuilder, adultTruth);
        Assert.Equal(new[] { pg, tv14, r, unrated, odd }.ToHashSet(), adultTruth);

        var (unknownBuilder, unknownTruth) = await BothAsync(unknownCeiling, mine);
        Assert.Equal(unknownBuilder, unknownTruth);
        Assert.Equal(mine.ToHashSet(), unknownTruth);
    }

    [Fact]
    public async Task Truth_and_builder_agree_on_shows_and_the_first_aired_year_arm()
    {
        if (!fixture.Available)
        {
            return;
        }

        var account = await SeedAccountAsync(
            (FilterKind.MinYear, "2000"),
            (FilterKind.MaxYear, "2020"),
            (FilterKind.ExcludeGenre, "reality"));
        var t = new Dictionary<string, Guid>
        {
            ["show-clean"] = await SeedTitleAsync(mediaType: MediaType.Show),
            ["show-aired-2005"] = await SeedTitleAsync(mediaType: MediaType.Show, year: null, firstAiredAt: new DateTime(2005, 1, 9, 0, 0, 0, DateTimeKind.Utc)),
            ["show-aired-1995"] = await SeedTitleAsync(mediaType: MediaType.Show, year: null, firstAiredAt: new DateTime(1995, 1, 9, 0, 0, 0, DateTimeKind.Utc)),
            ["show-aired-2025"] = await SeedTitleAsync(mediaType: MediaType.Show, year: null, firstAiredAt: new DateTime(2025, 1, 9, 0, 0, 0, DateTimeKind.Utc)),
            // ReleasedAt outranks FirstAiredAt: 2010 passes even though it first aired in 1990.
            ["released-beats-aired"] = await SeedTitleAsync(mediaType: MediaType.Show, year: null,
                releasedAt: new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc), firstAiredAt: new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            ["show-reality"] = await SeedTitleAsync(mediaType: MediaType.Show, genres: ["reality"]),
            ["show-watched"] = await SeedTitleAsync(mediaType: MediaType.Show),
            ["show-dropped"] = await SeedTitleAsync(mediaType: MediaType.Show),
        };
        await SeedWatchedAsync(account, t["show-watched"]);
        await ReactAsync(account, t["show-dropped"], ReactionKind.Dropped);

        var (builder, truth) = await BothAsync(account, t.Values);

        Assert.Equal(builder, truth);
        Assert.Equal(new[] { "show-clean", "show-aired-2005", "released-beats-aired" }.Select(k => t[k]).ToHashSet(), truth);
    }

    /// <summary>
    /// The eligible ids among <paramref name="titles"/> by every statement of the rule: the persisted
    /// builder overload and the <see cref="FilterRuleSet"/> overload (each through the in-memory maturity
    /// gate) — asserted equal here — and the eval's independent SQL truth, returned for the caller.
    /// </summary>
    private async Task<(HashSet<Guid> Builder, HashSet<Guid> Truth)> BothAsync(Guid accountId, IEnumerable<Guid> titles)
    {
        var mine = titles.ToList();
        await using var db = fixture.CreateContext(new AccountContext());
        var eligibility = new EligibilityQueryBuilder(db);
        var ceiling = await eligibility.MaturityCeilingAsync(accountId, CancellationToken.None);
        var persisted = await eligibility.EligibleTitles(accountId).Where(x => mine.Contains(x.Id)).ToListAsync();
        var builder = EligibilityQueryBuilder.WithinMaturity(persisted, ceiling).Select(x => x.Id).ToHashSet();

        var rules = FilterRuleSet.FromRows(await db.ContentFilters.Where(f => f.AccountId == accountId).ToListAsync());
        var draft = await eligibility.EligibleTitles(accountId, rules).Where(x => mine.Contains(x.Id)).ToListAsync();
        Assert.Equal(builder, EligibilityQueryBuilder.WithinMaturity(draft, ceiling).Select(x => x.Id).ToHashSet());

        var truthSql = Bind(await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "eval", "askreel", "truth", "_eligible.sql")), accountId);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT e.\"Id\" FROM ({truthSql}) e WHERE e.\"Id\" = ANY(@mine)", connection);
        command.Parameters.AddWithValue("mine", mine.ToArray());
        var truth = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            truth.Add(reader.GetGuid(0));
        }

        return (builder, truth);
    }

    /// <summary>The truth files take the account as the psql variable <c>:'account'</c>; bind it as a literal.</summary>
    private static string Bind(string sql, Guid accountId)
    {
        var lines = sql.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal));
        return string.Join('\n', lines).Replace(":'account'", $"'{accountId}'", StringComparison.Ordinal).TrimEnd().TrimEnd(';');
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "fa.reel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("fa.reel.slnx not found above the test assembly.");
    }

    private Task<Guid> SeedAccountAsync(params (FilterKind Kind, string Value)[] filters) => SeedAccountAsync(null, filters);

    private async Task<Guid> SeedAccountAsync(string? maturity, params (FilterKind Kind, string Value)[] filters)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"golden-parity-{Guid.NewGuid():N}",
            Tier = AccountTier.Paid,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        account.Settings.MaturityCeiling = maturity;
        db.Add(account);
        foreach (var (kind, value) in filters)
        {
            db.ContentFilters.Add(new ContentFilter
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                Kind = kind,
                Value = value,
                CreatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedTitleAsync(
        string? name = null,
        string[]? genres = null,
        string[]? keywords = null,
        string? overview = null,
        string? language = "en",
        decimal? tmdb = 7.0m,
        decimal? trakt = null,
        int? year = 2010,
        DateTime? releasedAt = null,
        MediaType mediaType = MediaType.Movie,
        string? certification = null,
        DateTime? firstAiredAt = null)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = mediaType,
            Certification = certification,
            FirstAiredAt = firstAiredAt,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = name ?? $"golden-parity {Guid.NewGuid():N}",
            Overview = overview,
            Language = language,
            Genres = genres ?? ["drama"],
            Keywords = keywords ?? [],
            TmdbVoteAverage = tmdb,
            TraktRating = trakt,
            Year = year,
            ReleasedAt = releasedAt,
            // Bottom of the popularity order so these fixtures never displace another test's top-N.
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
            FirstSyncedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedRatingAsync(Guid accountId, Guid titleId)
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
        });
        await db.SaveChangesAsync();
    }

    private async Task ReactAsync(Guid accountId, Guid titleId, ReactionKind kind, bool revoked = false)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        db.UserTitleReactions.Add(new UserTitleReaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TitleId = titleId,
            Kind = kind,
            CreatedAt = DateTime.UtcNow,
            RevokedAt = revoked ? DateTime.UtcNow : null,
        });
        await db.SaveChangesAsync();
    }
}
