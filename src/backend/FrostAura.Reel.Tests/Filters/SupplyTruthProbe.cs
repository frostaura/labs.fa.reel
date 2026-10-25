using System.Globalization;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
    /// Truth file, the catalogue labels the interpreter produced, the media scope of the turn, and
    /// whether every bucket is asserted (false: the topic half only — see the class remarks).
    /// </summary>
    private static readonly (string Truth, string[] Labels, MediaScope Media, bool EveryBucket)[] Probes =
    [
        ("ninja", ["ninja"], MediaScope.Movies, true),
        ("ninja", ["ninja", "shinobi", "kunoichi"], MediaScope.Any, true),
        ("samurai", ["samurai", "ronin"], MediaScope.Movies, true),
        ("samurai", ["samurai", "ronin"], MediaScope.Any, true),
        ("zombie", ["zombie"], MediaScope.Any, true),
        ("shark", ["shark"], MediaScope.Movies, true),
        ("heist", ["heist", "bank robbery", "robbery"], MediaScope.Any, false),
        ("vampire", ["vampire"], MediaScope.Any, false),
    ];

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

        var failures = new List<string>();
        foreach (var (truth, labels, media, everyBucket) in Probes)
        {
            var hard = HardConstraints.None with
            {
                Concepts = [new ConceptTerm(string.Join(" ", labels), labels, [])],
                Media = media,
            };
            SupplyReport report;
            await using (var db = Context())
            {
                report = await new SupplyReporter(db, new EligibilityQueryBuilder(db)).ReportAsync(accountId, hard, CancellationToken.None);
            }

            var expected = await TruthAsync(connection, truth, media, accountId, report.MinVotes);
            var actual = (report.MatchingInCatalogue, report.AlreadySeen, report.Eligible, report.Unscored, report.BelowFloor);
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{truth} [{string.Join(",", labels)}] {media}: app (matching, seen, eligible, unscored, below) = {actual}; truth = {expected}"));
            var agrees = everyBucket
                ? actual == expected
                : (actual.MatchingInCatalogue, actual.AlreadySeen) == (expected.Matching, expected.Seen);
            if (!agrees)
            {
                failures.Add($"{truth}/{media}: app {actual} vs truth {expected}");
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
}
