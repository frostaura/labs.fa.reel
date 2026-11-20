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
            // HREV2-RET-04: a title with no genres cannot prove it is not horror, so it fails closed.
            ["no-genres"] = await SeedTitleAsync(genres: []),
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
        // With no genre exclusion a title with no genres is eligible: only an ExcludeGenre fails it closed.
        var untagged = await SeedTitleAsync(genres: []);
        var mine = new[] { english, korean, unknown, untagged };

        var (defaultBuilder, defaultTruth) = await BothAsync(englishByDefault, mine);
        Assert.Equal(defaultBuilder, defaultTruth);
        Assert.Equal(new[] { english, untagged }.ToHashSet(), defaultTruth);

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

    [Fact]
    public async Task Truth_and_builder_agree_on_genre_stand_ins_and_aliases_on_both_media()
    {
        if (!fixture.Available)
        {
            return;
        }

        // IREV3-SYS-02: an ExcludeGenre also removes a title by an alias genre (Trakt's children / suspense)
        // or by a stand-in keyword on a medium whose TMDB taxonomy has no id for the genre. Before the truth
        // modelled this, every row below marked "out" was truth-eligible while the API excluded it.
        var account = await SeedAccountAsync(
            (FilterKind.ExcludeGenre, "anime"),
            (FilterKind.ExcludeGenre, "romance"),
            (FilterKind.ExcludeGenre, "history"),
            (FilterKind.ExcludeGenre, "thriller"),
            (FilterKind.ExcludeGenre, "family"),
            (FilterKind.ExcludeGenre, "musical"));
        var t = new Dictionary<string, Guid>
        {
            // Out: anime has an id on neither medium, so its exact label removes films and shows alike.
            ["movie-anime"] = await SeedTitleAsync(genres: ["animation"], keywords: ["anime"]),
            ["show-anime"] = await SeedTitleAsync(mediaType: MediaType.Show, genres: ["animation"], keywords: ["anime"]),
            // Out: a whole-word romance stem inside a TV label; a period label stands in for history on TV.
            ["show-teenage-romance"] = await SeedTitleAsync(mediaType: MediaType.Show, keywords: ["teenage romance"]),
            ["show-romantic"] = await SeedTitleAsync(mediaType: MediaType.Show, keywords: ["romantic"]),
            ["show-period-drama"] = await SeedTitleAsync(mediaType: MediaType.Show, keywords: ["period drama"]),
            // Out: Trakt's alias genres.
            ["show-suspense"] = await SeedTitleAsync(mediaType: MediaType.Show, genres: ["suspense"]),
            ["show-children"] = await SeedTitleAsync(mediaType: MediaType.Show, genres: ["children"]),
            // Out: musical has no id anywhere, exact label.
            ["movie-jukebox-musical"] = await SeedTitleAsync(keywords: ["jukebox musical"]),
            // In: films have a romance and a history id, so a keyword never stands in for them there.
            ["movie-teenage-romance"] = await SeedTitleAsync(keywords: ["teenage romance"]),
            ["movie-period-drama"] = await SeedTitleAsync(keywords: ["period drama"]),
            // In: a stem only inside another word, and an exact anime label only as a substring.
            ["show-bromance"] = await SeedTitleAsync(mediaType: MediaType.Show, keywords: ["bromance"]),
            ["show-anime-inspired"] = await SeedTitleAsync(mediaType: MediaType.Show, keywords: ["anime inspired"]),
            ["show-clean"] = await SeedTitleAsync(mediaType: MediaType.Show, keywords: ["heist"]),
            // HREV1-RET-09. Out: TV's Soap genre is romance; a romance phrase in a show's overview stands in.
            ["show-soap"] = await SeedTitleAsync(mediaType: MediaType.Show, genres: ["drama", "soap"]),
            ["show-overview-romance"] = await SeedTitleAsync(mediaType: MediaType.Show, keywords: ["hometown"], overview: "She deals with an old ROMANCE."),
            ["show-overview-newline"] = await SeedTitleAsync(mediaType: MediaType.Show, keywords: ["hometown"], overview: "Two doctors.\nIn love, at last."),
            // In: the phrase inside another word, and the same overview on a film (TMDB tags films itself).
            ["show-overview-lovely"] = await SeedTitleAsync(mediaType: MediaType.Show, keywords: ["hometown"], overview: "A lovely bromance; a crushed car."),
            ["movie-overview-romance"] = await SeedTitleAsync(keywords: ["hometown"], overview: "She deals with an old romance."),
        };

        var (builder, truth) = await BothAsync(account, t.Values);

        Assert.Equal(builder, truth);
        Assert.Equal(
            new[]
            {
                "movie-teenage-romance", "movie-period-drama", "show-bromance", "show-anime-inspired", "show-clean",
                "show-overview-lovely", "movie-overview-romance",
            }.Select(k => t[k]).ToHashSet(),
            truth);
    }

    [Fact]
    public async Task Truth_and_builder_agree_on_the_anime_composite_on_both_media()
    {
        if (!fixture.Available)
        {
            return;
        }

        // WS-3 CR-3 (H-7): anime is a composite exclusion — animation whose original language is Japanese
        // (case-insensitive), on films and shows alike. Every language is opted in so the language rule
        // cannot hold a Japanese title out for the composite; the keyword stand-in still applies on its own.
        var account = await SeedAccountAsync((FilterKind.ExcludeGenre, "anime"), (FilterKind.Language, "*"));
        var t = new Dictionary<string, Guid>
        {
            ["movie-japanese-animation"] = await SeedTitleAsync(genres: ["animation", "action"], language: "ja"),
            ["show-japanese-animation"] = await SeedTitleAsync(mediaType: MediaType.Show, genres: ["animation"], language: "JA"),
            ["movie-anime-keyword"] = await SeedTitleAsync(genres: ["animation"], keywords: ["anime"], language: "en"),
            // In: animation in another language, a Japanese title that is not animation, and no language at all.
            ["movie-english-animation"] = await SeedTitleAsync(genres: ["animation"], language: "en"),
            ["movie-japanese-drama"] = await SeedTitleAsync(genres: ["drama"], language: "ja"),
            ["show-animation-no-language"] = await SeedTitleAsync(mediaType: MediaType.Show, genres: ["animation"], language: null),
        };

        var (builder, truth) = await BothAsync(account, t.Values);

        Assert.Equal(builder, truth);
        Assert.Equal(
            new[] { "movie-english-animation", "movie-japanese-drama", "show-animation-no-language" }.Select(k => t[k]).ToHashSet(),
            truth);
    }

    /// <summary>
    /// The conditions editor's "These rules leave N of M titles" and Ask Reel's conditions() tool read
    /// WS-3's <see cref="FilterCostMeter"/>, a third statement of the rule (one grouped statement over the
    /// whole catalogue). Its eligible count must equal the SQL truth's, unrated titles included: a title
    /// with NO crowd rating breaks a crowd floor that exists ("no score fails a floor that exists", FD-7's
    /// default) in the builder, the serving path and the truth, and — since WS-3's R1-06 — in the meter's
    /// bitmask too. Before it, SQL's NOT(NULL &gt;= floor) left the breach bit clear and the meter counted
    /// every unrated title as clearing the floor (eval snapshot: 3,706 vs a truth of 3,507). This test
    /// ledgered that gap until the fix landed; it now holds the meter to the truth. The fixture varies the
    /// one field that mattered — no rating at all, and a rating of 0 (also no rating) — beside a rated title.
    /// </summary>
    [Fact]
    public async Task The_cost_meter_counts_what_the_truth_counts_unrated_titles_included()
    {
        if (!fixture.Available)
        {
            return;
        }

        var account = await SeedAccountAsync((FilterKind.MinCrowdRating, "6.0"), (FilterKind.Language, "*"));
        await SeedTitleAsync(tmdb: null, trakt: null); // unrated: breaks the floor on every statement, the meter's included
        await SeedTitleAsync(tmdb: 0m, trakt: 0m);     // "0" is no rating too
        await SeedTitleAsync(tmdb: 7.5m);

        await using var db = fixture.CreateContext(new AccountContext());
        var scoped = new AccountContext();
        scoped.SetAccount(account);
        var rules = FilterRuleSet.FromRows(await db.ContentFilters.Where(f => f.AccountId == account).ToListAsync());
        var meter = await new FilterCostMeter(db, new EligibilityQueryBuilder(db), scoped, new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())).MeasureAsync(account, rules);

        var truthEligible = await ScalarAsync($"SELECT count(*) FROM ({Bind(await ReadTruthAsync("truth", "_eligible.sql"), account)}) e");
        var unratedCrowdOnly = await ScalarAsync(
            $"SELECT count(*) FROM (SELECT r.\"Id\" FROM ({Bind(await ReadTruthAsync("supply", "rules.sql"), account)}) r "
            + "GROUP BY r.\"Id\" HAVING count(*) = 1 AND bool_and(r.\"Kind\" = 'MinCrowdRating')) solo "
            + "JOIN \"Titles\" t ON t.\"Id\" = solo.\"Id\" "
            + "WHERE coalesce(nullif(t.\"TmdbVoteAverage\", 0), nullif(t.\"TraktRating\", 0)) IS NULL");

        Assert.True(unratedCrowdOnly >= 2, "the fixture's unrated titles did not reach the crowd-only knockout");
        Assert.Equal(truthEligible, meter.Eligible);
    }

    private async Task<int> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Task<string> ReadTruthAsync(string folder, string file) =>
        File.ReadAllTextAsync(Path.Combine(RepoRoot(), "eval", "askreel", folder, file));

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
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "labs.fa.reel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("labs.fa.reel.slnx not found above the test assembly.");
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
