using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Infrastructure.Ml;
using FrostAura.Reel.Infrastructure.Persistence;
using FrostAura.Reel.Tests.IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FrostAura.Reel.Tests.Ml;

/// <summary>
/// The offline A/B for WS-5 task 12 (show rating rollups, N-8). It runs the real
/// <see cref="EvalHarness"/> twice over one account on a CLONE of the eval founder data, with
/// <see cref="FeatureVectorBuilder.IncludeShowRollups"/> on and off, and writes both arms to a JSON
/// artifact. Same data, same code, same seeds: the only difference between the arms is the rollup,
/// and equal feature-set hashes give equal NDCG, so a re-run reproduces the file byte for byte.
///
/// <para>Opt-in, never part of a normal run: it does nothing unless
/// <c>REEL_ABLATION_CONNECTION_STRING</c> is set. Run it with <c>scripts/ml/show-rollup-ablation.sh</c>,
/// which clones the source database, points this class at the clone and drops the clone afterwards.
/// The harness writes its TrainingRun and EvaluationResult rows into the clone, so the class refuses
/// the live Postgres and the eval stack's own databases.</para>
/// </summary>
public class ShowRollupAblationRun
{
    private const string ConnectionVariable = "REEL_ABLATION_CONNECTION_STRING";
    private static readonly string[] RefusedDatabases = ["reel", "reel_eval", "reel_eval_template"];

    [Fact]
    [Trait("Category", "OfflineEval")]
    public async Task Measure_the_show_rollup_against_the_same_data_without_it()
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

        var database = new NpgsqlConnectionStringBuilder(connection).Database ?? string.Empty;
        if (RefusedDatabases.Contains(database, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to write evaluation rows into '{database}': point the run at a clone (scripts/ml/show-rollup-ablation.sh).");
        }

        await using (var migrate = CreateContext(connection, new AccountContext()))
        {
            await migrate.Database.MigrateAsync();
        }

        var accountId = await AccountAsync(connection);
        var with = await RunArmAsync(connection, accountId, includeRollups: true);
        var without = await RunArmAsync(connection, accountId, includeRollups: false);
        Assert.NotNull(with);
        Assert.NotNull(without);

        var artifact = new JsonObject
        {
            ["purpose"] = "Offline A/B of WS-5 task 12 (ShowRatingRollup in the taste pools): the real EvalHarness on one account, rollups on vs off, same data and code.",
            ["command"] = "scripts/ml/show-rollup-ablation.sh [source-db] [out.json]",
            ["source"] = Environment.GetEnvironmentVariable("REEL_ABLATION_SOURCE") ?? database,
            ["commit"] = Environment.GetEnvironmentVariable("REEL_ABLATION_COMMIT"),
            ["account"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(accountId.ToString())))[..12],
            ["hyperparams"] = JsonNode.Parse(TrainJobHandler.DefaultHyperparams),
            ["primaryOrigin"] = EvalHarness.PrimaryOrigin,
            ["withRollups"] = Arm(with!.Result),
            ["withoutRollups"] = Arm(without!.Result),
            ["rollupMinusNone"] = new JsonObject
            {
                ["ndcgAt10Model"] = Delta(with.Result.NdcgAt10Model, without.Result.NdcgAt10Model),
                ["meanOriginNdcgModel"] = Delta(MeanOriginNdcg(with.Result), MeanOriginNdcg(without.Result)),
                ["ece"] = Delta(with.Result.Ece, without.Result.Ece),
                ["mae"] = Delta(with.Result.Mae, without.Result.Mae),
                ["calibrationBias"] = Delta(with.Result.CalibrationBias, without.Result.CalibrationBias),
                ["spearmanRho"] = Delta(with.Result.SpearmanRho, without.Result.SpearmanRho),
            },
        };

        var path = Environment.GetEnvironmentVariable("REEL_ABLATION_OUT") is { Length: > 0 } outPath
            ? outPath
            : Path.Combine(RepoRoot(), "eval", "model", "show-rollup-ablation.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            artifact.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
    }

    private static JsonObject Arm(Domain.Ml.EvaluationResult r) => new()
    {
        ["featureSetHash"] = r.FeatureSetHash,
        ["ndcgAt10Model"] = r.NdcgAt10Model,
        ["ndcgAt10Baseline"] = r.NdcgAt10Baseline,
        // The primary origin alone is one 250-title holdout; the mean over every origin is the same
        // statistic the gate's lift is averaged over, and the two can disagree in sign.
        ["meanOriginNdcgModel"] = MeanOriginNdcg(r),
        ["ndcgLiftCiLow"] = r.NdcgLiftCiLow,
        ["ndcgLiftCiHigh"] = r.NdcgLiftCiHigh,
        ["passedGate"] = r.PassedGate,
        ["ece"] = r.Ece,
        ["mae"] = r.Mae,
        ["rmse"] = r.Rmse,
        ["calibrationBias"] = r.CalibrationBias,
        ["spearmanRho"] = r.SpearmanRho,
        ["holdoutPositives"] = r.HoldoutPositiveCount,
        ["eligibleHoldout"] = r.EligibleHoldoutCount,
        ["unfilteredHoldout"] = r.UnfilteredHoldoutCount,
        ["origins"] = r.OriginsJson is { } origins ? JsonNode.Parse(origins) : null,
    };

    private static decimal? MeanOriginNdcg(Domain.Ml.EvaluationResult r)
    {
        if (r.OriginsJson is not { } json)
        {
            return null;
        }

        var values = JsonNode.Parse(json)!.AsArray().Select(o => o!["ndcgModel"]!.GetValue<decimal>()).ToList();
        return values.Count == 0 ? null : Math.Round(values.Average(), 6);
    }

    private static decimal? Delta(decimal? with, decimal? without) =>
        with is { } a && without is { } b ? a - b : null;

    private static async Task<EvalHarness.HarnessOutcome?> RunArmAsync(string connection, Guid accountId, bool includeRollups)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = CreateContext(connection, scoped);
        var harness = new EvalHarness(
            db,
            new FeatureVectorBuilder(db) { IncludeShowRollups = includeRollups },
            new FastTreeModelEngine(),
            new EligibilityQueryBuilder(db),
            new SilentEventHub(),
            NullLogger<EvalHarness>.Instance);
        return await harness.RunAsync(accountId, TrainJobHandler.DefaultHyperparams, CancellationToken.None);
    }

    /// <summary>REEL_ABLATION_ACCOUNT, else the account with the most movie and show ratings.</summary>
    private static async Task<Guid> AccountAsync(string connection)
    {
        if (Guid.TryParse(Environment.GetEnvironmentVariable("REEL_ABLATION_ACCOUNT"), out var chosen))
        {
            return chosen;
        }

        await using var db = CreateContext(connection, new AccountContext());
        return await db.UserRatings
            .Where(r => r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show)
            .GroupBy(r => r.AccountId)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstAsync();
    }

    private static ReelDbContext CreateContext(string connection, IAccountContext accountContext) =>
        new(
            new DbContextOptionsBuilder<ReelDbContext>()
                .UseNpgsql(connection, npgsql => npgsql.UseVector())
                .Options,
            accountContext);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "fa.reel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("fa.reel.slnx not found above the test assembly.");
    }

    private sealed class SilentEventHub : IPipelineEventHub
    {
        public void Publish(Guid accountId, string type, IReadOnlyDictionary<string, object?> data) { }

        public async IAsyncEnumerable<PipelineSseEvent> SubscribeAsync(
            Guid accountId, long? lastEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
