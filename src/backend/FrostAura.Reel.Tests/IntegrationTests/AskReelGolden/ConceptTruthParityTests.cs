using System.Text.Json;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FrostAura.Reel.Tests.IntegrationTests.AskReelGolden;

/// <summary>
/// HREV4-RET-01: a golden turn's concept truth is the set the scorer holds its supply counts to, ±0. The
/// conflict-romcom scenario was held to every romance title (<c>genre-romance</c>, 4,675 at 50 votes), so
/// the app's correct romantic-comedy count (2,981: romance AND comedy, romance read with its TV stand-ins
/// and the soap alias) failed the supply gate and was filed as a WS-2 defect. This reads the scenario's own
/// query and concept key out of <c>eval/askreel/scenarios/open.json</c> and holds that truth file to the
/// app's reading of the same words (<see cref="HardConstraintFilter"/>: one concept per genre word,
/// AND-ed, each genre matched as its exclusion reads it), on fixtures that sit on every edge of the rule.
/// </summary>
[Collection("postgres")]
public class ConceptTruthParityTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_romcom_truth_is_romance_and_comedy_exactly_as_the_app_reads_romantic_comedies()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (query, concept, mustNot) = Scenario("conflict-romcom");
        Assert.Equal(["genre-comedy"], mustNot);

        var expected = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            // Both genres by their own tags, on either medium.
            ["film tagged romance and comedy"] = true,
            ["show tagged romance and comedy"] = true,
            // One genre only is never a romantic comedy.
            ["film tagged romance only"] = false,
            ["film tagged comedy only"] = false,
            ["show tagged romance only, keyword sitcom"] = false,
            // Romance's TV stand-ins: an exact label, a whole-word stem inside a label, the soap alias, an
            // overview phrase — each with comedy.
            ["comedy show, keyword love triangle"] = true,
            ["comedy show, keyword teenage romance"] = true,
            ["comedy show, genre soap"] = true,
            ["comedy show, overview falls in love"] = true,
            // The stand-ins' edges: not a whole word, a film (TMDB tags films with romance itself), and the
            // alias or label without comedy.
            ["comedy show, keyword bromance"] = false,
            ["comedy show, overview in lovely Paris"] = false,
            ["comedy film, keyword love triangle"] = false,
            ["comedy film, overview falls in love"] = false,
            ["soap show without comedy"] = false,
            ["show with no genres, keyword love triangle"] = false,
        };

        var seeded = new Dictionary<Guid, string>();
        async Task Seed(string name, MediaType media, string[] genres, string[]? keywords = null, string? overview = null) =>
            seeded[await SeedTitleAsync(media, genres, keywords ?? [], overview)] = name;

        await Seed("film tagged romance and comedy", MediaType.Movie, ["romance", "comedy"]);
        await Seed("show tagged romance and comedy", MediaType.Show, ["comedy", "romance"]);
        await Seed("film tagged romance only", MediaType.Movie, ["romance", "drama"]);
        await Seed("film tagged comedy only", MediaType.Movie, ["comedy"]);
        await Seed("show tagged romance only, keyword sitcom", MediaType.Show, ["romance"], ["sitcom"]);
        await Seed("comedy show, keyword love triangle", MediaType.Show, ["comedy"], ["love triangle"]);
        await Seed("comedy show, keyword teenage romance", MediaType.Show, ["comedy"], ["teenage romance"]);
        await Seed("comedy show, genre soap", MediaType.Show, ["comedy", "soap"]);
        await Seed("comedy show, overview falls in love", MediaType.Show, ["comedy"], null, "A chef falls In Love with her rival.");
        await Seed("comedy show, keyword bromance", MediaType.Show, ["comedy"], ["bromance"]);
        await Seed("comedy show, overview in lovely Paris", MediaType.Show, ["comedy"], null, "Two waiters in lovely Paris.");
        await Seed("comedy film, keyword love triangle", MediaType.Movie, ["comedy"], ["love triangle"]);
        await Seed("comedy film, overview falls in love", MediaType.Movie, ["comedy"], null, "A chef falls in love with her rival.");
        await Seed("soap show without comedy", MediaType.Show, ["soap", "drama"]);
        await Seed("show with no genres, keyword love triangle", MediaType.Show, [], ["love triangle"]);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), seeded.Values.Order(StringComparer.Ordinal));

        var ids = seeded.Keys.ToList();
        var hard = HardConstraints.None with { Concepts = [new ConceptTerm(query, [], [])] };
        await using var db = fixture.CreateContext(new AccountContext());
        var app = await HardConstraintFilter.Apply(db.Titles.Where(t => ids.Contains(t.Id)), db, hard)
            .Select(t => t.Id)
            .ToListAsync();
        var truth = await TruthAsync(concept, ids);

        string Names(IEnumerable<Guid> set) => string.Join(" | ", set.Select(id => seeded[id]).Order(StringComparer.Ordinal));
        var wanted = string.Join(" | ", expected.Where(e => e.Value).Select(e => e.Key).Order(StringComparer.Ordinal));
        Assert.Equal(wanted, Names(app));
        Assert.Equal(wanted, Names(truth));
    }

    /// <summary>
    /// Final golden eval, residual 2: the conflict-anime truth was the Trakt tag alone (2 titles), while
    /// the app reads "anime" as the founder's exclusion of it reads — the tag, animation in Japanese, or
    /// the stand-in keyword — and counted the 1,700 that rule holds out. The eval's own rules file reads
    /// anime the same way; the concept truth now does too, held here to the app's reading on every edge.
    /// </summary>
    [Fact]
    public async Task The_anime_truth_is_the_anime_genre_exactly_as_the_app_and_the_exclusion_read_it()
    {
        if (!fixture.Available)
        {
            return;
        }

        var (query, concept, _) = Scenario("conflict-anime");
        var expected = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["tagged anime"] = true,
            ["animation in Japanese"] = true,
            ["animated show in Japanese"] = true,
            ["keyword anime, English"] = true,
            ["animation in English"] = false,
            ["Japanese live action"] = false,
            ["keyword anime girl"] = false,
            ["no genres, Japanese"] = false,
        };

        var seeded = new Dictionary<Guid, string>();
        async Task Seed(string name, MediaType media, string[] genres, string language, string[]? keywords = null)
        {
            var id = await SeedTitleAsync(media, genres, keywords ?? [], null);
            await using var db = fixture.CreateContext(new AccountContext());
            (await db.Titles.SingleAsync(t => t.Id == id)).Language = language;
            await db.SaveChangesAsync();
            seeded[id] = name;
        }

        await Seed("tagged anime", MediaType.Show, ["anime"], "en");
        await Seed("animation in Japanese", MediaType.Movie, ["animation", "fantasy"], "ja");
        await Seed("animated show in Japanese", MediaType.Show, ["animation"], "ja");
        await Seed("keyword anime, English", MediaType.Movie, ["action"], "en", ["anime"]);
        await Seed("animation in English", MediaType.Movie, ["animation"], "en");
        await Seed("Japanese live action", MediaType.Movie, ["drama"], "ja");
        await Seed("keyword anime girl", MediaType.Movie, ["drama"], "en", ["anime girl"]);
        await Seed("no genres, Japanese", MediaType.Movie, [], "ja");
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), seeded.Values.Order(StringComparer.Ordinal));

        var ids = seeded.Keys.ToList();
        var hard = HardConstraints.None with { Concepts = [new ConceptTerm(query, [], [])] };
        await using var read = fixture.CreateContext(new AccountContext());
        var app = await HardConstraintFilter.Apply(read.Titles.Where(t => ids.Contains(t.Id)), read, hard).Select(t => t.Id).ToListAsync();
        var truth = await TruthAsync(concept, ids);

        string Names(IEnumerable<Guid> set) => string.Join(" | ", set.Select(id => seeded[id]).Order(StringComparer.Ordinal));
        var wanted = string.Join(" | ", expected.Where(e => e.Value).Select(e => e.Key).Order(StringComparer.Ordinal));
        Assert.Equal(wanted, Names(app));
        Assert.Equal(wanted, Names(truth));
    }

    /// <summary>The ids of <paramref name="ids"/> the truth file <c>eval/askreel/truth/&lt;key&gt;.sql</c> returns.</summary>
    private async Task<List<Guid>> TruthAsync(string key, IReadOnlyList<Guid> ids)
    {
        var body = string.Join('\n', File.ReadAllText(Path.Combine(RepoRoot(), "eval", "askreel", "truth", $"{key}.sql"))
                .Split('\n')
                .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)))
            .TrimEnd().TrimEnd(';');
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"WITH c AS ({body}) SELECT \"Id\" FROM c WHERE \"Id\" = ANY (@ids)", connection);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        var found = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            found.Add(reader.GetGuid(0));
        }

        return found;
    }

    /// <summary>The open scenario's single turn: what the user typed, the concept key it is scored under, its mustNot keys.</summary>
    private static (string Query, string Concept, string[] MustNot) Scenario(string id)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "eval", "askreel", "scenarios", "open.json")));
        var scenario = doc.RootElement.GetProperty("scenarios").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);
        var turn = Assert.Single(scenario.GetProperty("turns").EnumerateArray());
        var expect = turn.GetProperty("expect");
        return (
            turn.GetProperty("query").GetString()!,
            expect.GetProperty("concept").GetString()!,
            [.. expect.GetProperty("mustNot").EnumerateArray().Select(m => m.GetString()!)]);
    }

    private async Task<Guid> SeedTitleAsync(MediaType media, string[] genres, string[] keywords, string? overview)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var title = new Title
        {
            Id = Guid.NewGuid(),
            MediaType = media,
            TmdbId = Random.Shared.NextInt64(1, long.MaxValue),
            TraktSlug = string.Empty,
            Name = $"golden-concept {Guid.NewGuid():N}",
            Overview = overview,
            Language = "en",
            Year = 2012,
            Genres = [.. genres],
            Keywords = [.. keywords],
            TmdbVoteAverage = 7.0m,
            TmdbVoteCount = 500,
            // Bottom of the popularity order, so these fixtures never displace another test's top-N.
            TmdbPopularity = 0m,
            CreatedAt = DateTime.UtcNow,
        };
        db.Add(title);
        await db.SaveChangesAsync();
        return title.Id;
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
