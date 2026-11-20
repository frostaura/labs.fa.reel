using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Ml;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Tests.IntegrationTests;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The founder-snapshot acceptance probe for HREV2-RET-02. It trains a serving model in memory exactly
/// as <see cref="TrainJobHandler"/> does (the current <see cref="TrainingRecipe"/>),
/// scores the titles the account is actually served (every unrated title it has a stored score for)
/// and checks two things per release decade the account has rated at least
/// <see cref="MinRatedPerDecade"/> titles from:
/// <list type="number">
/// <item><b>Level.</b> The decade's mean prediction is within <see cref="MaxDecadeGap"/> of the mean
/// rating the account gives that decade. The defect: 2020s predictions averaged about 2 against a
/// 6.31 average rating.</item>
/// <item><b>No cliff.</b> Moving the decade's pool value by ±<see cref="PoolNudge"/> — about what one
/// new rating does to a pool of 800 — moves that mean by less than <see cref="MaxCliff"/>. The
/// leave-one-out model moved it by up to 5.7 points.</item>
/// </list>
/// The leave-one-out arm is trained and reported beside it for contrast, never asserted. The stored
/// Active artifact is scored too, through the serving builder (which follows that artifact's
/// <see cref="TrainingRecipe"/>), and held to the level check: it is what the account sees until the
/// recipe retrain replaces it. It is also scored on every title the account has RATED, as the title
/// page scores them, and held to <see cref="MaxRatedMae"/> against those ratings: those are its own
/// training rows, so a served description that matches how it was trained reproduces them closely
/// (v101 with leave-one-out rows: MAE 0.02), and one that does not is off by points (out-of-fold
/// rows: MAE 2.33, Boss Level rated 7 shown 2.0).
///
/// <para>Opt-in: nothing runs unless <c>REEL_DECADE_PROBE_CONNECTION_STRING</c> is set. It only reads
/// (the model lives in memory), refuses the live Postgres, and costs no paid calls. Writes its
/// numbers to <c>REEL_DECADE_PROBE_OUT</c>, default <c>eval/model/decade-calibration-probe.json</c>.</para>
/// </summary>
public class DecadeCalibrationProbe
{
    private const string ConnectionVariable = "REEL_DECADE_PROBE_CONNECTION_STRING";

    public const int MinRatedPerDecade = 50;
    public const decimal MaxDecadeGap = 1.0m;
    public const float PoolNudge = 0.004f;
    public const double MaxCliff = 0.5d;
    public const double MaxRatedMae = 1.0d;

    [Fact]
    [Trait("Category", "OfflineEval")]
    public async Task Every_well_rated_decade_is_predicted_near_its_own_average_and_off_any_cliff()
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connection))
        {
            return;
        }

        if (PostgresFixtureGate.LiveTargetRefusal(connection) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        var accountContext = new AccountContext();
        await using var db = new ReelDbContext(
            new DbContextOptionsBuilder<ReelDbContext>().UseNpgsql(connection, npgsql => npgsql.UseVector()).Options,
            accountContext);
        var accountId = await db.UserRatings
            .Where(r => r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show)
            .GroupBy(r => r.AccountId)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstAsync();
        accountContext.SetAccount(accountId);

        var labels = await db.UserRatings
            .Where(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show))
            .GroupBy(r => r.TitleId)
            .Select(g => new { TitleId = g.Key, Rating = g.Max(r => r.Rating) })
            .ToListAsync();
        var ratedIds = labels.Select(l => l.TitleId).ToHashSet();
        var served = (await db.TitleScores.Where(s => s.AccountId == accountId).Select(s => s.TitleId).Distinct().ToListAsync())
            .Where(id => !ratedIds.Contains(id))
            .ToList();
        var years = await db.Titles
            .Where(t => ratedIds.Contains(t.Id) || served.Contains(t.Id))
            .Select(t => new { t.Id, t.Year })
            .ToDictionaryAsync(t => t.Id, t => t.Year);

        var ratedByDecade = labels
            .Where(l => years.GetValueOrDefault(l.TitleId) is not null)
            .GroupBy(l => years[l.TitleId]!.Value / 10 * 10)
            .Where(g => g.Count() >= MinRatedPerDecade)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Mean: g.Average(l => (decimal)l.Rating)));

        var now = DateTime.UtcNow;
        var arms = new JsonObject();
        List<string> failures = [];
        var decadeSlot = Array.IndexOf(FeatureSchema.Scalars, "decadeAffinity");

        var active = await db.ModelArtifacts
            .Where(a => a.AccountId == accountId && a.Status == Domain.Ml.ArtifactStatus.Active)
            .OrderByDescending(a => a.Version)
            .FirstOrDefaultAsync();
        if (active is not null)
        {
            var serving = new FeatureVectorBuilder(db);
            var engine = new FastTreeModelEngine();
            var taste = await serving.BuildTasteStateAsync(accountId, now, CancellationToken.None);
            var vectors = await serving.BuildAsync(taste, served, now, CancellationToken.None);
            var ratedVectors = await serving.BuildAsync(taste, [.. ratedIds], now, CancellationToken.None);
            arms["activeArtifact"] = new JsonObject
            {
                ["version"] = active.Version,
                ["recipe"] = TrainingRecipe.ReadFrom(active.FeatureSchemaJson) ?? "leave-one-out, no rollups (unstamped)",
                ["decades"] = Decades(engine, active.ArtifactBytes, vectors, assertCliff: false, label: $"active v{active.Version}"),
                ["ratedTitles"] = Rated(engine, active.ArtifactBytes, ratedVectors, $"active v{active.Version}"),
            };
        }
        foreach (var outOfFold in new[] { true, false })
        {
            var builder = new FeatureVectorBuilder(db) { OutOfFoldPools = outOfFold }.ForTraining();
            var engine = new FastTreeModelEngine();
            var taste = await builder.BuildTasteStateAsync(accountId, now, CancellationToken.None);
            var trainVectors = await builder.BuildAsync(taste, [.. ratedIds], now, CancellationToken.None);
            var rows = labels.Where(l => trainVectors.ContainsKey(l.TitleId)).ToList();
            var model = engine.Train(
                new TrainingMatrix([.. rows.Select(l => trainVectors[l.TitleId])], [.. rows.Select(l => (float)l.Rating)], FeatureSchema.AllNames()),
                TrainJobHandler.DefaultHyperparams);

            var vectors = await builder.BuildAsync(taste, served, now, CancellationToken.None);
            arms[outOfFold ? "outOfFold" : "leaveOneOut"] = new JsonObject
            {
                ["trainingRows"] = rows.Count,
                ["decades"] = outOfFold
                    ? Decades(engine, model.ArtifactBytes, vectors, assertCliff: true, label: "retrained")
                    : Decades(engine, model.ArtifactBytes, vectors, assertCliff: null, label: "leave-one-out"),
            };
        }

        // Every rated title scored as the title page scores it, against its own rating; overall and
        // per well-rated decade. Asserted: these are the artifact's training rows.
        JsonObject Rated(FastTreeModelEngine engine, byte[] artifact, Dictionary<Guid, float[]> vectors, string label)
        {
            var rows = labels.Where(l => vectors.ContainsKey(l.TitleId)).ToList();
            var predicted = engine.Score(artifact, [.. rows.Select(l => vectors[l.TitleId])]);
            var errors = rows.Select((l, i) => (l.TitleId, Error: Math.Abs(predicted[i] - l.Rating))).ToList();

            JsonObject Summary(IReadOnlyCollection<(Guid TitleId, float Error)> set) => new()
            {
                ["titles"] = set.Count,
                ["mae"] = set.Count == 0 ? 0 : Math.Round(set.Average(e => (double)e.Error), 3),
                ["offByMoreThan3"] = set.Count(e => e.Error > 3f),
            };

            var mae = errors.Count == 0 ? 0d : errors.Average(e => (double)e.Error);
            if (mae > MaxRatedMae)
            {
                failures.Add($"{label}: MAE {mae:F2} over the {errors.Count} titles it was trained on");
            }

            var result = Summary(errors);
            var byDecade = new JsonObject();
            foreach (var decade in ratedByDecade.Keys.Order())
            {
                byDecade[decade.ToString(System.Globalization.CultureInfo.InvariantCulture)] = Summary(
                    [.. errors.Where(e => years.GetValueOrDefault(e.TitleId) is { } y && y / 10 * 10 == decade)]);
            }

            result["decades"] = byDecade;
            return result;
        }

        // assertCliff: true = level and cliff are asserted, false = level only, null = report only.
        JsonObject Decades(FastTreeModelEngine engine, byte[] artifact, Dictionary<Guid, float[]> vectors, bool? assertCliff, string label)
        {
            var decades = new JsonObject();
            foreach (var (decade, rated) in ratedByDecade.OrderBy(kv => kv.Key))
            {
                var members = vectors.Keys.Where(id => years.GetValueOrDefault(id) is { } y && y / 10 * 10 == decade).ToList();
                if (members.Count == 0)
                {
                    continue;
                }

                double MeanAt(float nudge) => engine.Score(
                    artifact,
                    [.. members.Select(id =>
                    {
                        var v = (float[])vectors[id].Clone();
                        v[decadeSlot] += nudge;
                        return v;
                    })]).Average(s => (double)s);

                var mean = MeanAt(0f);
                var cliff = Math.Max(Math.Abs(MeanAt(PoolNudge) - mean), Math.Abs(MeanAt(-PoolNudge) - mean));
                var gap = (decimal)mean - rated.Mean;
                decades[decade.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                {
                    ["ratedTitles"] = rated.Count,
                    ["meanRating"] = Math.Round(rated.Mean, 3),
                    ["servedTitles"] = members.Count,
                    ["meanPrediction"] = Math.Round(mean, 3),
                    ["gap"] = Math.Round(gap, 3),
                    ["cliff"] = Math.Round(cliff, 3),
                };

                if (assertCliff is not null && Math.Abs(gap) > MaxDecadeGap)
                {
                    failures.Add($"{label}, {decade}s: mean prediction {mean:F2} vs mean rating {rated.Mean:F2}");
                }

                if (assertCliff is true && cliff >= MaxCliff)
                {
                    failures.Add($"{label}, {decade}s: ±{PoolNudge} on the decade pool moves the mean by {cliff:F2}");
                }
            }

            return decades;
        }

        var report = new JsonObject
        {
            ["purpose"] = "HREV2-RET-02 acceptance probe: per release decade with >= 50 ratings, the served titles' mean base prediction minus the account's mean rating, and its move under a +/-0.004 nudge of the decade pool; and the Active artifact on every rated title, served as the title page serves it, against the rating. Base model only (no online head, calibration or demotion).",
            ["source"] = new Npgsql.NpgsqlConnectionStringBuilder(connection).Database,
            ["recipe"] = TrainingRecipe.Current,
            ["thresholds"] = new JsonObject { ["maxGap"] = MaxDecadeGap, ["maxCliff"] = MaxCliff, ["nudge"] = PoolNudge, ["maxRatedMae"] = MaxRatedMae },
            ["arms"] = arms,
        };
        var path = Environment.GetEnvironmentVariable("REEL_DECADE_PROBE_OUT") is { Length: > 0 } outPath
            ? outPath
            : Path.Combine(RepoRoot(), "eval", "model", "decade-calibration-probe.json");
        await File.WriteAllTextAsync(
            path,
            report.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");

        Assert.True(ratedByDecade.Count > 0, "no decade with enough ratings; the probe proves nothing");
        Assert.True(failures.Count == 0, string.Join("; ", failures));
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
}
