using System.Globalization;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit.Abstractions;

namespace FrostAura.Reel.Tests.Filters;

/// <summary>
/// The founder-ninja acceptance (program §5.2 WS-3): a <see cref="SupplyReport"/> on a real account's
/// data must match an independent SQL statement of the same question to ±0, the unscored count
/// included. The independent side is the golden eval's own truth files —
/// <c>eval/askreel/truth/&lt;topic&gt;.sql</c> for "is this title about the topic" and
/// <c>_eligible.sql</c> for "may the account be shown it" — plus the badge's vote rule and the active
/// model's predictions written out in SQL here, so nothing on the truth side is the reporter's code.
///
/// <para><b>Opt-in only, never in CI.</b> It does nothing unless <c>REEL_SUPPLY_TRUTH_PROBE=1</c>, and
/// then needs <c>REEL_PROBE_CONNECTION_STRING</c> (a SLOT copy of an account's data — never the live
/// database) and optionally <c>REEL_PROBE_ACCOUNT_ID</c> (default: the account with the most
/// ratings). It reads, it writes nothing, and it calls no provider. Run it with
/// <c>--filter FullyQualifiedName~SupplyTruthProbe --logger "console;verbosity=detailed"</c>.</para>
///
/// <para>Each probe states the labels the interpreter produces for the founder's words (the turn
/// records them in <c>SearchTurns.IntentJson</c>). Where those labels and a truth file's pattern name
/// the same topic, the counts must agree exactly; a gap is then a predicate defect, never noise.</para>
///
/// <para>Some probes are held to the TOPIC half only (matching and seen). Their eligible count
/// differs because <c>_eligible.sql</c> does not yet state the genre stand-ins
/// (<see cref="GenreStandIns"/>) that <see cref="EligibilityQueryBuilder"/> applies — vampire shows
/// carrying a romance keyword, or "Dancer in the Dark" carrying "musical", are excluded for an
/// account that excludes those genres. That is the eligibility truth lagging the builder, not a
/// supply defect, and it is the eval owner's to restate; the gap is printed, not asserted.</para>
/// </summary>
public class SupplyTruthProbe(ITestOutputHelper output)
{
    /// <summary>
    /// Truth file, the user's words, the catalogue labels the interpreter produced, the media scope of
    /// the turn, and whether every bucket is asserted (false: the topic half only — see the class
    /// remarks). The words are what the founder typed, not the labels joined: the name arm reads only
    /// the user's own words, so "samurai ronin" would have said "ronin" for him (HREV3-RET-A).
    /// </summary>
    private static readonly (string Truth, string Words, string[] Labels, MediaScope Media, bool EveryBucket)[] Probes =
    [
        ("ninja", "ninja movies", ["ninja"], MediaScope.Movies, true),
        ("ninja", "ninja", ["ninja", "shinobi", "kunoichi"], MediaScope.Any, true),
        ("samurai", "samurai films", ["samurai", "ronin"], MediaScope.Movies, true),
        ("samurai", "samurai", ["samurai", "ronin"], MediaScope.Any, true),
        ("zombie", "zombie", ["zombie"], MediaScope.Any, true),
        ("shark", "shark movies", ["shark"], MediaScope.Movies, true),
        ("heist", "heist", ["heist", "bank robbery", "robbery"], MediaScope.Any, false),
        ("vampire", "vampire", ["vampire"], MediaScope.Any, false),
        ("alien", "alien", ["alien", "extraterrestrial", "alien invasion"], MediaScope.Any, false),
    ];

    /// <summary>
    /// The golden eval's facet scenarios as the founder typed them (<c>eval/askreel/scenarios/open.json</c>),
    /// resolved and compared to their truth files on the topic half — printed, never asserted, so the
    /// eval owner sees where a truth file and the predicate read a topic differently. On only with
    /// <c>REEL_SUPPLY_TRUTH_SURVEY=1</c>.
    /// </summary>
    private static readonly (string Truth, string Words, MediaScope Media)[] Survey =
    [
        ("heist", "heist movies", MediaScope.Movies), ("samurai", "samurai films", MediaScope.Movies),
        ("space", "space movies", MediaScope.Movies), ("survival-at-sea", "survival at sea", MediaScope.Any),
        ("superhero", "superhero movies", MediaScope.Movies), ("spy", "spy series", MediaScope.Shows),
        ("cyberpunk", "cyberpunk films", MediaScope.Movies), ("ai-robot", "films about artificial intelligence", MediaScope.Movies),
        ("boxing", "boxing movies", MediaScope.Movies), ("noir", "neo-noir films", MediaScope.Movies),
        ("mafia", "mafia movies", MediaScope.Movies), ("dinosaur", "dinosaur movies", MediaScope.Movies),
        ("martial-arts", "martial arts movies", MediaScope.Movies), ("submarine", "submarine movies", MediaScope.Movies),
        ("con-artist", "con artist movies", MediaScope.Movies), ("witch", "witch", MediaScope.Any),
        ("werewolf", "werewolf series", MediaScope.Shows), ("chess", "movies about chess", MediaScope.Movies),
        ("conspiracy", "conspiracy thriller series", MediaScope.Shows), ("cult", "movies about cults", MediaScope.Movies),
        ("pandemic", "pandemic shows", MediaScope.Shows), ("cartel", "drug cartel movies", MediaScope.Movies),
        ("treasure-hunt", "treasure hunt adventures", MediaScope.Any), ("slasher", "slasher movies", MediaScope.Movies),
        ("found-footage", "found footage", MediaScope.Any), ("disaster", "natural disaster movies", MediaScope.Movies),
        ("alien", "alien invasion movies", MediaScope.Movies), ("mountaineering", "mountain climbing movies", MediaScope.Movies),
        ("serial-killer", "serial killer series", MediaScope.Shows), ("kaiju", "kaiju movies", MediaScope.Movies),
        ("dragon", "dragon movies", MediaScope.Movies), ("buddy-cop", "buddy cop movies", MediaScope.Movies),
        ("time-travel", "time travel movies", MediaScope.Movies), ("time-loop", "time loop movies", MediaScope.Movies),
        ("hitman", "hitman movies", MediaScope.Movies), ("zombie", "zombie movies", MediaScope.Movies),
        ("shark", "shark movies", MediaScope.Movies), ("ninja", "ninja shows", MediaScope.Shows),
    ];

    [Fact]
    public async Task Golden_facet_topics_against_their_truth_files_survey()
    {
        if (Environment.GetEnvironmentVariable("REEL_SUPPLY_TRUTH_SURVEY") != "1")
        {
            return;
        }

        var connection = Environment.GetEnvironmentVariable("REEL_PROBE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("REEL_PROBE_CONNECTION_STRING is required (a slot copy, never live).");
        var cache = new MemoryCache(new MemoryCacheOptions());
        Guid accountId;
        await using (var first = Context(connection))
        {
            accountId = await first.UserRatings.GroupBy(r => r.AccountId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstAsync();
        }

        foreach (var (truth, words, media) in Survey)
        {
            await using var db = Context(connection);
            var resolver = new ConceptResolver(
                new KeywordVocabulary(db, cache),
                new ConceptEmbeddings(new NoEmbeddings(), cache, NullLogger<ConceptEmbeddings>.Instance),
                db,
                NullLogger<ConceptResolver>.Instance);
            var hard = HardConstraints.None with { Concepts = [new ConceptTerm(words, [], [])], Media = media };
            var report = await new SupplyReporter(db, new EligibilityQueryBuilder(db), resolver).ReportAsync(accountId, hard, CancellationToken.None);
            var expected = await TruthAsync(connection, truth, media, accountId, report.MinVotes);
            var gap = (report.MatchingInCatalogue, report.AlreadySeen) == (expected.Matching, expected.Seen) ? "  " : "≠ ";
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{gap}{truth,-16} \"{words}\" {media}: app (matching, seen) = ({report.MatchingInCatalogue}, {report.AlreadySeen}); truth = ({expected.Matching}, {expected.Seen})"));
            if (gap == "  ")
            {
                continue;
            }

            // The titles each side counts that the other does not (all votes), so a gap can be read.
            var resolved = await resolver.ResolveAsync(hard, CancellationToken.None);
            var app = (await HardConstraintFilter.Apply(db.Titles, db, resolved).Select(t => t.Id).ToListAsync()).ToHashSet();
            var topic = Bind(await File.ReadAllTextAsync(TruthFile(truth)), accountId);
            var mediaClause = media switch
            {
                MediaScope.Movies => "AND t.\"MediaType\" = 'Movie'",
                MediaScope.Shows => "AND t.\"MediaType\" = 'Show'",
                _ => string.Empty,
            };
            // The truth file is this repository's own SQL, read from disk: nothing user-supplied is spliced.
            var truthSql = $"SELECT t.\"Id\" AS \"Value\" FROM \"Titles\" t WHERE t.\"Id\" IN ({topic}) {mediaClause}";
            var truthIds = (await db.Database.SqlQueryRaw<Guid>(truthSql).ToListAsync()).ToHashSet();
            async Task<string> NamesAsync(IEnumerable<Guid> ids)
            {
                var list = ids.Take(12).ToList();
                var names = await db.Titles.Where(t => list.Contains(t.Id)).Select(t => t.Name).ToListAsync();
                return string.Join(" · ", names.Order(StringComparer.Ordinal));
            }

            output.WriteLine($"    app only ({app.Except(truthIds).Count()}): {await NamesAsync(app.Except(truthIds))}");
            output.WriteLine($"    truth only ({truthIds.Except(app).Count()}): {await NamesAsync(truthIds.Except(app))}");
        }
    }

    private static ReelDbContext Context(string connection) => new(
        new DbContextOptionsBuilder<ReelDbContext>().UseNpgsql(connection, npgsql => npgsql.UseVector()).Options,
        new AccountContext());

    /// <summary>
    /// Each probe twice: as the planner's unresolved labels, and RESOLVED the way the live reporter
    /// resolves them (curated synonyms and the vocabulary's exact labels; no embedding fallback, so it
    /// calls no provider). The resolved reading is the one a reply quotes since the reporter resolves its
    /// own constraints, and it is the reading that once counted Ronin (1998) as samurai.
    /// </summary>
    [Fact]
    public async Task Founder_supply_matches_the_independent_sql_truth_to_zero()
    {
        if (Environment.GetEnvironmentVariable("REEL_SUPPLY_TRUTH_PROBE") != "1")
        {
            return;
        }

        var connection = Environment.GetEnvironmentVariable("REEL_PROBE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("REEL_PROBE_CONNECTION_STRING is required (a slot copy, never live).");

        ReelDbContext Context() => new(
            new DbContextOptionsBuilder<ReelDbContext>().UseNpgsql(connection, npgsql => npgsql.UseVector()).Options,
            new AccountContext());

        Guid accountId;
        await using (var db = Context())
        {
            accountId = Guid.TryParse(Environment.GetEnvironmentVariable("REEL_PROBE_ACCOUNT_ID"), out var given)
                ? given
                : await db.UserRatings.GroupBy(r => r.AccountId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstAsync();
        }

        var cache = new MemoryCache(new MemoryCacheOptions());
        var failures = new List<string>();
        foreach (var (truth, words, labels, media, everyBucket) in Probes)
        {
            foreach (var resolve in new[] { false, true })
            {
                var hard = HardConstraints.None with
                {
                    Concepts = [new ConceptTerm(words, labels, [])],
                    Media = media,
                };
                SupplyReport report;
                await using (var db = Context())
                {
                    var resolver = resolve
                        ? new ConceptResolver(
                            new KeywordVocabulary(db, cache),
                            new ConceptEmbeddings(new NoEmbeddings(), cache, NullLogger<ConceptEmbeddings>.Instance),
                            db,
                            NullLogger<ConceptResolver>.Instance)
                        : null;
                    report = await new SupplyReporter(db, new EligibilityQueryBuilder(db), resolver).ReportAsync(accountId, hard, CancellationToken.None);
                }

                var expected = await TruthAsync(connection, truth, media, accountId, report.MinVotes);
                var actual = (report.MatchingInCatalogue, report.AlreadySeen, report.Eligible, report.Unscored, report.BelowFloor);
                var reading = resolve ? "resolved" : "planned";
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{truth} \"{words}\" [{string.Join(",", labels)}] {media} {reading}: app (matching, seen, eligible, unscored, below) = {actual}; truth = {expected}"));
                var agrees = everyBucket
                    ? actual == expected
                    : (actual.MatchingInCatalogue, actual.AlreadySeen) == (expected.Matching, expected.Seen);
                if (!agrees)
                {
                    failures.Add($"{truth}/{media}/{reading}: app {actual} vs truth {expected}");
                }
            }
        }

        // The same labels with none of the user's words (IntentConstraints.FromIntent without them: the v2
        // state rebuilt from a v1 reading, a v1 topic no word of the user's anchored). The name arm then
        // reads the labels that are not curated synonyms — the concept's own name — so it is held to the
        // same truth: "samurai ronin" once said "ronin" and counted Ronin (1998) here (HREV3-RET-A).
        foreach (var (truth, _, labels, media, everyBucket) in Probes)
        {
            var standIn = IntentConstraints.FromIntent(
                new SearchIntent([], labels, [], string.Empty, null, null), media switch
                {
                    MediaScope.Movies => [MediaType.Movie],
                    MediaScope.Shows => [MediaType.Show],
                    _ => null,
                });
            await using var db = Context();
            var resolver = new ConceptResolver(
                new KeywordVocabulary(db, cache),
                new ConceptEmbeddings(new NoEmbeddings(), cache, NullLogger<ConceptEmbeddings>.Instance),
                db,
                NullLogger<ConceptResolver>.Instance);
            var report = await new SupplyReporter(db, new EligibilityQueryBuilder(db), resolver).ReportAsync(accountId, standIn, CancellationToken.None);
            var expected = await TruthAsync(connection, truth, media, accountId, report.MinVotes);
            var actual = (report.MatchingInCatalogue, report.AlreadySeen, report.Eligible, report.Unscored, report.BelowFloor);
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{truth} (no user words) [{string.Join(",", labels)}] {media} stand-in: app (matching, seen, eligible, unscored, below) = {actual}; truth = {expected}"));
            var agrees = everyBucket
                ? actual == expected
                : (actual.MatchingInCatalogue, actual.AlreadySeen) == (expected.Matching, expected.Seen);
            if (!agrees)
            {
                failures.Add($"{truth}/{media}/stand-in: app {actual} vs truth {expected}");
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>
    /// The buckets counted from the truth files alone: topical by the truth pattern, at or above the
    /// vote floor by the badge rule (Trakt when it has the floor's votes, else TMDB, else Trakt), seen
    /// as a watch or a rating, eligible by <c>_eligible.sql</c>, and split by the ACTIVE artifact's
    /// predictions at badge precision against the account's For-you bar.
    /// </summary>
    private static async Task<(int Matching, int Seen, int Eligible, int Unscored, int Below)> TruthAsync(
        string connectionString, string truth, MediaScope media, Guid accountId, int minVotes)
    {
        var topic = Bind(await File.ReadAllTextAsync(TruthFile(truth)), accountId);
        var eligible = Bind(await File.ReadAllTextAsync(TruthFile("_eligible")), accountId);
        var mediaClause = media switch
        {
            MediaScope.Movies => "AND t.\"MediaType\" = 'Movie'",
            MediaScope.Shows => "AND t.\"MediaType\" = 'Show'",
            _ => string.Empty,
        };
        var sql = $"""
            WITH topic AS ({topic}),
                 eligible AS ({eligible}),
                 bar AS (SELECT CASE WHEN v > 0 THEN least(v, 10) END AS floor
                           FROM (SELECT (SELECT CASE WHEN f."Value" ~ '^\s*-?[0-9]+(\.[0-9]+)?\s*$' THEN f."Value"::numeric END
                                           FROM "ContentFilters" f
                                          WHERE f."AccountId" = '{accountId}' AND f."Kind" = 'MinPredictedRating'
                                          ORDER BY f."CreatedAt", f."Id" LIMIT 1) AS v) b),
                 active AS (SELECT a."Id" FROM "ModelArtifacts" a
                             WHERE a."AccountId" = '{accountId}' AND a."Status" = 'Active'
                             ORDER BY a."Version" DESC LIMIT 1),
                 pop AS (
                     SELECT t."Id"
                       FROM "Titles" t
                      WHERE t."Id" IN (SELECT "Id" FROM topic) {mediaClause}
                        AND (CASE WHEN t."TraktRating" > 0 AND t."TraktVotes" >= 50 THEN t."TraktVotes"
                                  WHEN t."TmdbVoteAverage" > 0 AND t."TmdbVoteCount" > 0 THEN t."TmdbVoteCount"
                                  WHEN t."TraktRating" > 0 AND t."TraktVotes" > 0 THEN t."TraktVotes"
                                  ELSE 0 END) >= {minVotes.ToString(CultureInfo.InvariantCulture)}),
                 scored AS (
                     SELECT s."TitleId", min(s."PredictedRating") AS predicted
                       FROM "TitleScores" s
                      WHERE s."AccountId" = '{accountId}' AND s."ModelArtifactId" = (SELECT "Id" FROM active)
                      GROUP BY s."TitleId")
            SELECT count(*)::int,
                   count(*) FILTER (WHERE EXISTS (SELECT 1 FROM "WatchedTitles" w WHERE w."AccountId" = '{accountId}' AND w."TitleId" = p."Id")
                                       OR EXISTS (SELECT 1 FROM "UserRatings" r WHERE r."AccountId" = '{accountId}' AND r."TitleId" = p."Id"))::int,
                   count(*) FILTER (WHERE p."Id" IN (SELECT "Id" FROM eligible))::int,
                   count(*) FILTER (WHERE p."Id" IN (SELECT "Id" FROM eligible) AND p."Id" NOT IN (SELECT "TitleId" FROM scored))::int,
                   count(*) FILTER (WHERE p."Id" IN (SELECT "Id" FROM eligible)
                                      AND EXISTS (SELECT 1 FROM scored s, bar
                                                   WHERE s."TitleId" = p."Id" AND bar.floor IS NOT NULL
                                                     AND round(s.predicted, 1) < bar.floor))::int
              FROM pop p
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
    }

    private static string TruthFile(string key) => Path.Combine(RepoRoot(), "eval", "askreel", "truth", $"{key}.sql");

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

    /// <summary>No embedding provider: the probe resolves without the nearest-label fallback and calls nothing.</summary>
    private sealed class NoEmbeddings : IEmbeddingProvider
    {
        public bool IsAvailable => false;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<float[]>());
    }
}
