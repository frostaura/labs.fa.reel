using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Tests.IntegrationTests.SurfaceHarness;

/// <summary>
/// The surface harness re-checks every rendered why-line against <see cref="ExplanationTemplater.IsCitable"/>
/// (WS-5's IREV3S-01 rule: a clause cites only one of the card's two largest positive pushes, or one of at
/// least 0.15) using the card's stored contributions. The harness is JavaScript, so it carries the rule and
/// a phrase catalogue in <c>eval/surfaces/citable-clauses.json</c>. This class keeps that file honest:
/// the rule's constants, a rendered sample of every <c>Translate</c> phrasing and a set of IsCitable vectors
/// are GENERATED here from the templater itself, and the hand-written clause patterns must recognise every
/// sample as its own feature's clause and nothing else — so a new or reworded clause fails here before the
/// harness silently stops checking it. The node twin (<c>surfaces-score.test.mjs</c>) replays the same
/// samples and vectors through the JavaScript rule.
///
/// <para>Regenerate after changing <see cref="ExplanationTemplater"/>: run this class with
/// <c>REEL_REGENERATE_EVAL_SQL=1</c>, then review the diff.</para>
/// </summary>
public class CitableClauseCatalogueTests
{
    private static readonly string FilePath = Path.Combine("eval", "surfaces", "citable-clauses.json");

    private static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void The_catalogue_carries_the_rule_samples_and_vectors_the_templater_generates()
    {
        var path = Path.Combine(RepoRoot(), FilePath);
        var current = File.ReadAllText(path);
        var expected = Render(current);
        if (Environment.GetEnvironmentVariable("REEL_REGENERATE_EVAL_SQL") == "1" && expected != current)
        {
            File.WriteAllText(path, expected);
            return;
        }

        Assert.True(
            expected == current,
            $"{FilePath} is out of step with ExplanationTemplater — rerun {nameof(CitableClauseCatalogueTests)} with REEL_REGENERATE_EVAL_SQL=1 and review the diff.");
    }

    [Fact]
    public void Every_rendered_clause_is_recognised_as_its_own_feature_and_no_other()
    {
        var clauses = Clauses(File.ReadAllText(Path.Combine(RepoRoot(), FilePath)));
        var samples = Samples();
        Assert.True(samples.Count >= 25, $"only {samples.Count} samples rendered — a context stopped firing its clause");

        foreach (var (feature, text) in samples)
        {
            var matched = clauses.Where(c => c.Pattern.IsMatch(text)).ToList();
            if (feature is null)
            {
                // Anchor clauses are not contribution clauses: the harness must never charge them to one.
                Assert.True(matched.Count == 0, $"anchor-only line \"{text}\" is read as clause(s) {string.Join(", ", matched.Select(c => c.Id))}");
                continue;
            }

            var mine = Assert.Single(matched);
            Assert.True(mine.Covers(feature), $"\"{text}\" ({feature}) is read as clause '{mine.Id}', which does not cover {feature}");
        }

        // Every feature family the catalogue names fired at least once, so no pattern is untested.
        Assert.All(clauses, c => Assert.Contains(samples, s => s.Feature is { } f && c.Covers(f)));
    }

    [Fact]
    public void Every_rendered_sample_is_a_line_the_feed_guard_serves()
    {
        // The samples are the exact strings a card can carry. A sample the guard refuses (a "a 8.4"
        // article slip, say) would reach the user as a blank why-line, not as the sentence catalogued here.
        Assert.All(Samples(), s => Assert.True(FeedWhyGuard.IsServable(s.Text), s.Text));
    }

    [Fact]
    public void A_clause_left_out_of_the_catalogue_fails_rather_than_going_unchecked()
    {
        // The guard is the "single match" above: a phrasing no pattern reads is an empty match, which fails.
        var clauses = Clauses(File.ReadAllText(Path.Combine(RepoRoot(), FilePath)));
        Assert.DoesNotContain(clauses, c => c.Pattern.IsMatch("A brand new phrasing nobody catalogued."));
    }

    /// <summary><paramref name="json"/> with its generated keys (rule, samples, vectors) replaced by what the templater produces now.</summary>
    public static string Render(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        if (root["clauses"] is not JsonArray)
        {
            throw new InvalidOperationException($"{FilePath} has no hand-written 'clauses' array");
        }

        root["rule"] = new JsonObject
        {
            ["topPositiveCount"] = ExplanationTemplater.CitableTopPositiveCount,
            ["floor"] = Math.Round((double)ExplanationTemplater.CitableContributionFloor, 4),
            ["storedPrecision"] = 3,
        };
        root["samples"] = new JsonArray([.. Samples().Select(s => (JsonNode)new JsonObject { ["feature"] = s.Feature, ["text"] = s.Text })]);
        root["vectors"] = new JsonArray([.. Vectors().Select(v => (JsonNode)new JsonObject
        {
            ["contributions"] = new JsonArray([.. v.Select(c => (JsonNode)new JsonArray(c.Feature, Math.Round((double)c.Contribution, 3)))]),
            ["citable"] = new JsonArray([.. v.Select((_, i) => (JsonNode)ExplanationTemplater.IsCitable(v, i))]),
        })]);
        return root.ToJsonString(Write) + "\n";
    }

    private sealed record Clause(string Id, IReadOnlyList<string> Features, string? Prefix, Regex Pattern)
    {
        public bool Covers(string feature) =>
            Features.Contains(feature, StringComparer.Ordinal) || (Prefix is { } p && feature.StartsWith(p, StringComparison.Ordinal));
    }

    private static List<Clause> Clauses(string json) =>
        [.. JsonNode.Parse(json)!["clauses"]!.AsArray().Select(c => new Clause(
            c!["id"]!.GetValue<string>(),
            [.. (c["features"]?.AsArray() ?? []).Select(f => f!.GetValue<string>())],
            c["featurePrefix"]?.GetValue<string>(),
            new Regex(c["pattern"]!.GetValue<string>(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))))];

    /// <summary>
    /// Every phrasing every clause-bearing feature can render — alone and behind each kind of anchor clause
    /// — plus anchor-only lines (feature null). Rendered through the public BuildSentence, so these are the
    /// exact strings a card can carry.
    /// </summary>
    private static List<(string? Feature, string Text)> Samples()
    {
        var features = new[]
        {
            "castAffinity", "lovedCastCount", "directorAffinity", "writerAffinity", "userGenreAffinity", "genreDrift",
            "traktRating", "releaseAgeLog", "contrarianAdjustedRating", "simLovedCentroid", "simRecentCentroid",
            "simTopLovedMax", "simTopLovedMean", "genre:science-fiction", "tone:brooding",
        };
        var anchors = new (string? Name, string? Genre, string? Tone, IReadOnlyList<SharedEvidence>? Evidence)[]
        {
            (null, null, null, null),
            ("Heat", "crime", "brooding", null),
            ("Heat", null, "gritty", null),
            ("Heat", "crime", null, null),
            ("Heat", null, null, null),
            ("Heat", null, null, [new SharedEvidence("person", "Michael Mann", "director")]),
            ("Heat", null, null, [new SharedEvidence("person", "Al Pacino", "actor")]),
            ("Heat", null, null, [new SharedEvidence("keyword", "bank robbery")]),
            ("Heat", null, null, [new SharedEvidence("theme", "obsession")]),
        };

        var samples = new List<(string? Feature, string Text)>();
        foreach (var feature in features)
        {
            foreach (var (name, genre, tone, evidence) in anchors.Take(2))
            {
                for (var seed = 0; seed < 3; seed++)
                {
                    var text = ExplanationTemplater.BuildSentence(Context([(feature, 1.0f)], name, genre, tone, evidence), seed);
                    if (text.Length > 0 && !samples.Contains((feature, text)))
                    {
                        samples.Add((feature, text));
                    }
                }
            }
        }

        foreach (var (name, genre, tone, evidence) in anchors.Skip(1))
        {
            for (var seed = 0; seed < 3; seed++)
            {
                var text = ExplanationTemplater.BuildSentence(Context([], name, genre, tone, evidence), seed);
                if (text.Length > 0 && !samples.Contains((null, text)))
                {
                    samples.Add((null, text));
                }
            }
        }

        return samples;
    }

    private static ExplanationTemplater.Context Context(
        IReadOnlyList<(string Feature, float Contribution)> contributions, string? anchor, string? genre, string? tone, IReadOnlyList<SharedEvidence>? evidence)
    {
        var title = new Title
        {
            Id = Guid.Parse("6f1c2a3b-4d5e-4f60-8172-93a4b5c6d7e8"),
            Name = "Catalogue Probe",
            Genres = ["science-fiction", "crime"],
            TmdbVoteAverage = 8.5m,
            TmdbVoteCount = 5000,
            // Inside the templater's 120-day "fresh" window whenever the test runs.
            ReleasedAt = DateTime.UtcNow.AddDays(-10),
        };
        var people = new Dictionary<CreditRole, ExplanationTemplater.PersonAffinity>
        {
            // In the eights, so the article the templater writes before the number is exercised: "an 8.4".
            [CreditRole.Actor] = new("Rebecca Ferguson", 8.4m),
            [CreditRole.Director] = new("Denis Villeneuve", 8.2m),
            [CreditRole.Writer] = new("Eric Roth", 7.4m),
        };
        return new ExplanationTemplater.Context(
            title, contributions, people, new HashSet<string>(StringComparer.Ordinal) { "science-fiction", "crime" },
            anchor, genre, tone, evidence, AccountContrarianOffset: -0.5m);
    }

    /// <summary>Contribution lists that exercise every arm of IsCitable: the floor, the top-two rank, negatives, ties and order.</summary>
    private static List<(string Feature, float Contribution)[]> Vectors() =>
    [
        [("a", 1.0f), ("b", 0.7f), ("c", 0.09f), ("d", 0.04f), ("e", -0.3f)],
        [("a", -1.2f), ("b", 0.05f), ("c", 0.03f), ("d", 0.02f), ("e", 0.01f)],
        [("a", 0.9f), ("b", -0.8f), ("c", 0.16f), ("d", 0.15f), ("e", 0.149f)],
        [("a", 0.1f), ("b", 0.1f), ("c", 0.1f), ("d", -0.1f), ("e", 0.0f)],
        [("a", -0.5f), ("b", -0.4f), ("c", -0.3f), ("d", -0.2f), ("e", -0.1f)],
        [("a", 0.02f), ("b", 0.4f), ("c", -0.6f), ("d", 0.03f), ("e", 0.01f)],
        [("a", 0.0f), ("b", 0.0f), ("c", 0.12f), ("d", 0.11f), ("e", 0.13f)],
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "labs.fa.reel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("labs.fa.reel.slnx not found above the test assembly.");
    }
}
