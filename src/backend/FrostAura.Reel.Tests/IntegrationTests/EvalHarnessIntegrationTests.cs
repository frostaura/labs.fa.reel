using System.Text.Json;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Pipeline;
using FrostAura.Reel.Application.Ranking.Interleaving;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Library;
using FrostAura.Reel.Domain.Ml;
using FrostAura.Reel.Domain.Tenancy;
using FrostAura.Reel.Infrastructure.Ml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Tests.IntegrationTests;

/// <summary>
/// The whole eval harness, end to end on real Postgres — the thing twelve ledger rows were
/// produced by and no test ever ran. Three claims are proved here that no unit test can:
/// <list type="bullet">
/// <item>the ledger row is <b>complete</b> — NDCG, a bootstrap CI, calibration, Spearman's SE and
/// both holdout counts all land in columns, not just in a log line;</item>
/// <item>the evaluated pool is the <b>servable</b> pool — a holdout title the account's own
/// content rules exclude is dropped from both the model's ranking and the baseline's, and the
/// ledger records how many were dropped;</item>
/// <item>the iteration counter <b>stops inflating</b> — adding ratings and re-running the same
/// config keeps the iteration where it was, which is what "iteration 12 of 3" was really
/// counting.</item>
/// </list>
/// Skips when REEL_TEST_CONNECTION_STRING is unset.
/// </summary>
[Collection("postgres")]
public class EvalHarnessIntegrationTests(PostgresFixture fixture)
{
    private const string Hyperparams = """{"Trees":40,"Leaves":8,"MinExamplesPerLeaf":5,"LearningRate":0.2}""";
    private static readonly DateTime Origin = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Titles are globally unique on (MediaType, TmdbId), so each seeded account needs its
    /// own block of ids — the collection runs both facts against one database.</summary>
    private static int _tmdbBlock;

    [Fact]
    public async Task The_harness_writes_a_complete_ledger_row_over_the_servable_pool()
    {
        if (!fixture.Available)
        {
            return; // no Postgres available — covered in CI's backend-integration job
        }

        var seed = await SeedAsync();
        var outcome = await RunAsync(seed.AccountId);

        Assert.NotNull(outcome);
        var run = outcome!.Run;
        var result = outcome.Result;

        // ── The row is complete ─────────────────────────────────────────────────────────
        Assert.Equal(TrainingRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.Iteration);
        Assert.Equal(EvalHarness.ConfigHash(Hyperparams), run.ConfigHash);
        Assert.NotNull(result.NdcgAt10Model);
        Assert.NotNull(result.NdcgAt10Baseline);
        Assert.NotNull(result.NdcgLiftCiLow);
        Assert.NotNull(result.NdcgLiftCiHigh);
        Assert.NotNull(result.SpearmanSe);
        Assert.NotNull(result.CalibrationBias);
        Assert.NotNull(result.Ece);
        Assert.NotNull(result.OriginsJson);
        Assert.True(result.NdcgLiftCiHigh >= result.NdcgLiftCiLow);
        Assert.InRange(result.NdcgAt10Model!.Value, 0m, 1m);
        Assert.InRange(result.NdcgAt10Baseline!.Value, 0m, 1m);

        // The split model returned by the harness is the one that produced these numbers.
        Assert.NotEmpty(outcome.SplitModel.ArtifactBytes);

        // ── The threshold is frozen at the account's top quintile, floored at 8 ─────────
        Assert.Equal(8m, run.PositiveThreshold);
        Assert.False(result.LowSample);
        Assert.True(result.HoldoutPositiveCount >= EvalHarness.MinHoldoutPositives);

        // ── The evaluated pool is the servable pool ────────────────────────────────────
        Assert.NotNull(result.EligibleHoldoutCount);
        Assert.NotNull(result.UnfilteredHoldoutCount);
        Assert.True(
            result.UnfilteredHoldoutCount > result.EligibleHoldoutCount,
            $"the pre-2000 holdout titles should have been dropped: {result.UnfilteredHoldoutCount} unfiltered "
            + $"vs {result.EligibleHoldoutCount} eligible");
        Assert.Equal(result.EligibleHoldoutCount, run.HoldoutRowCount);

        // Nothing the account's own rules exclude may appear in the ranking that was measured.
        var detail = JsonSerializer.Deserialize<JsonElement>(result.DetailJson);
        var rankedIds = detail.GetProperty("topRanked").EnumerateArray()
            .Select(e => e.GetProperty("titleId").GetGuid())
            .ToHashSet();
        Assert.NotEmpty(rankedIds);
        Assert.Empty(rankedIds.Intersect(seed.IneligibleTitleIds));

        // ── Five rolling origins, each with its own numbers ────────────────────────────
        var origins = JsonSerializer.Deserialize<JsonElement>(result.OriginsJson!);
        Assert.Equal(EvalHarness.SplitOrigins.Length, origins.GetArrayLength());
        foreach (var origin in origins.EnumerateArray())
        {
            Assert.InRange(origin.GetProperty("ndcgModel").GetDecimal(), 0m, 1m);
            Assert.InRange(origin.GetProperty("ndcgBaseline").GetDecimal(), 0m, 1m);
            Assert.True(origin.GetProperty("eligibleHoldout").GetInt32() >= EvalHarness.NdcgDepth);
        }

        // ── The gate is the persisted arithmetic, not a separate opinion ───────────────
        var meanRelativeLift = origins.EnumerateArray()
            .Select(o => o.GetProperty("ndcgRelativeLift").GetDecimal())
            .Average();
        Assert.Equal(
            EvalHarness.GatePassed(result.NdcgLiftCiLow!.Value, meanRelativeLift),
            result.PassedGate);
        Assert.Equal(EvalHarness.LegacyGatePassed(result.RelativeImprovement), result.PassedLegacyGate);

        // ── The caveats ship with the row ──────────────────────────────────────────────
        var caveats = detail.GetProperty("caveats").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Contains(caveats, c => c.Contains(FeatureSchema.VectorLength.ToString(), StringComparison.Ordinal));
        Assert.Contains(caveats, c => c.Contains("DISPLAY ONLY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task New_ratings_alone_no_longer_burn_an_iteration()
    {
        if (!fixture.Available)
        {
            return;
        }

        // This is the defect that produced "iteration 12 of 3": the ConfigHash included coverage
        // statistics, whose title count grows with every new rating, so each nightly drift retrain
        // minted a fresh hash and incremented the counter the kill criterion reads.
        var seed = await SeedAsync();
        var first = await RunAsync(seed.AccountId);
        Assert.NotNull(first);
        Assert.Equal(1, first!.Run.Iteration);

        await AddRatingsAsync(seed.AccountId, count: 20, startingAt: Origin.AddDays(2_000));

        var second = await RunAsync(seed.AccountId);
        Assert.NotNull(second);
        Assert.Equal(1, second!.Run.Iteration);
        Assert.Equal(first.Run.ConfigHash, second.Run.ConfigHash);

        // A genuine config change is still an iteration.
        var third = await RunAsync(seed.AccountId, """{"Trees":80,"Leaves":8,"MinExamplesPerLeaf":5,"LearningRate":0.1}""");
        Assert.NotNull(third);
        Assert.Equal(2, third!.Run.Iteration);
    }

    [Fact]
    public async Task The_ledger_carries_five_baselines_and_an_input_hash_that_pins_the_ndcg()
    {
        if (!fixture.Available)
        {
            return;
        }

        var seed = await SeedAsync();
        var first = await RunAsync(seed.AccountId);
        var second = await RunAsync(seed.AccountId);
        Assert.NotNull(first);
        Assert.NotNull(second);

        // Equal inputs, equal hash, identical NDCG — the instrument is reproducible.
        Assert.Matches("^[0-9a-f]{64}$", first!.Result.FeatureSetHash);
        Assert.Equal(first.Result.FeatureSetHash, second!.Result.FeatureSetHash);
        Assert.Equal(first.Result.NdcgAt10Model, second.Result.NdcgAt10Model);
        Assert.Equal(first.Result.BaselinesJson, second.Result.BaselinesJson);

        // Every baseline is read at the primary origin, with the as-of caveat.
        var ledger = JsonSerializer.Deserialize<JsonElement>(first.Result.BaselinesJson!);
        Assert.Equal(EvalHarness.PrimaryOrigin, ledger.GetProperty("origin").GetDouble());
        Assert.Equal(first.Result.NdcgAt10Model, ledger.GetProperty("model").GetDecimal());
        Assert.Contains("present-day", ledger.GetProperty("caveat").GetString(), StringComparison.Ordinal);
        var baselines = ledger.GetProperty("baselines").EnumerateArray().ToDictionary(b => b.GetProperty("name").GetString()!);
        Assert.Equal(["popularity", "traktRating", "shrunkCrowd", "genreAffinity", "random"], baselines.Keys);
        foreach (var (name, baseline) in baselines)
        {
            Assert.InRange(baseline.GetProperty("ndcg").GetDecimal(), 0m, 1m);
            if (name != "random")
            {
                Assert.True(baseline.GetProperty("ciHigh").GetDecimal() >= baseline.GetProperty("ciLow").GetDecimal());
            }
        }

        // The challenger's pre-registered report: fitted on the 60–70% origins only, read out of
        // sample at the primary origin with paired CIs against FastTree and popularity.
        var challenger = ledger.GetProperty("challenger");
        Assert.Equal(TransparentBlendPolicy.PreRegistration, challenger.GetProperty("preRegistration").GetString());
        Assert.All(challenger.GetProperty("fitOrigins").EnumerateArray(), o => Assert.True(o.GetDouble() <= 0.70d + 1e-9));
        Assert.Contains(challenger.GetProperty("weights").GetProperty("affinity").GetDouble(), TransparentBlendPolicy.WeightGrid);
        var primary = challenger.GetProperty("primary");
        foreach (var versus in new[] { "versusModel", "versusPopularity" })
        {
            var read = primary.GetProperty(versus);
            Assert.True(read.GetProperty("ciHigh").GetDecimal() >= read.GetProperty("ciLow").GetDecimal());
            Assert.Contains(read.GetProperty("reading").GetString(), new[] { "better", "worse", "no measurable difference" });
        }

        Assert.Equal(first.Result.NdcgAt10Model, primary.GetProperty("model").GetDecimal());
        Assert.Equal(
            EvalHarness.ChallengerWeights(first.Result.BaselinesJson),
            (challenger.GetProperty("weights").GetProperty("affinity").GetDouble(), challenger.GetProperty("weights").GetProperty("similarity").GetDouble()));

        // The popularity entry is the gate's own baseline, not a second definition of it.
        Assert.Equal(first.Result.NdcgAt10Baseline, baselines["popularity"].GetProperty("ndcg").GetDecimal());

        // A title-side change moves the hash: the hash fingerprints features, not just config.
        await using (var db = fixture.CreateContext(new AccountContext()))
        {
            var rated = await db.UserRatings.Where(r => r.AccountId == seed.AccountId).Select(r => r.TitleId).ToListAsync();
            await db.Titles.Where(t => rated.Contains(t.Id)).ExecuteUpdateAsync(u => u.SetProperty(t => t.TraktVotes, t => t.TraktVotes + 5_000));
        }

        var third = await RunAsync(seed.AccountId);
        Assert.NotEqual(first.Result.FeatureSetHash, third!.Result.FeatureSetHash);
    }

    // ── Harness construction ────────────────────────────────────────────────────────────

    private async Task<EvalHarness.HarnessOutcome?> RunAsync(Guid accountId, string? hyperparams = null)
    {
        var scoped = new AccountContext();
        scoped.SetAccount(accountId);
        await using var db = fixture.CreateContext(scoped);
        var harness = new EvalHarness(
            db,
            new FeatureVectorBuilder(db),
            new FastTreeModelEngine(),
            new EligibilityQueryBuilder(db),
            new SilentEventHub(),
            NullLogger<EvalHarness>.Instance);
        return await harness.RunAsync(accountId, hyperparams ?? Hyperparams, CancellationToken.None);
    }

    // ── Seed ────────────────────────────────────────────────────────────────────────────

    private sealed record Seed(Guid AccountId, HashSet<Guid> IneligibleTitleIds);

    /// <summary>
    /// 240 rated titles over roughly five years, with the rating distribution's shape (a mode at
    /// 6–7 and a real tail of loves) so the frozen top-quintile threshold lands on 8 and the
    /// holdout carries more than the ten positives the harness insists on. Every fifth title in the
    /// newest third is released in 1995 against a MinYear=2000 rule, so the eligibility filter has
    /// something real to drop — and the ledger has something real to report.
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var account = new Account
        {
            Id = Guid.NewGuid(),
            TraktUserSlug = $"eval-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            PipelineStageChangedAt = DateTime.UtcNow,
        };
        db.Add(account);

        db.Add(new ContentFilter
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Kind = FilterKind.MinYear,
            Value = "2000",
            CreatedAt = DateTime.UtcNow,
        });

        var rng = new Random(7);
        var tmdbBase = 900_000_000L + (Interlocked.Increment(ref _tmdbBlock) * 10_000L);
        var ineligible = new HashSet<Guid>();
        // A fixed rating ladder whose 80th percentile is exactly 8 — mirroring the live account,
        // where p80 lands on 8 and has for all twelve iterations.
        short[] ladder = [4, 5, 6, 6, 7, 7, 7, 8, 9, 10];

        for (var i = 0; i < 240; i++)
        {
            var preMillennium = i >= 160 && i % 5 == 0;
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TraktSlug = $"eval-title-{i}-{Guid.NewGuid():N}",
                TmdbId = tmdbBase + i,
                Name = $"Eval Title {i}",
                Year = preMillennium ? 1995 : 2005 + (i % 15),
                Language = "en",
                TmdbPopularity = (decimal)Math.Round(rng.NextDouble() * 100d, 2),
                TraktVotes = rng.Next(50, 5_000),
                RuntimeMinutes = 90 + (i % 45),
                Genres = [i % 2 == 0 ? "thriller" : "drama"],
                Keywords = [],
                Subgenres = [],
                CreatedAt = DateTime.UtcNow,
            };
            db.Add(title);
            if (preMillennium)
            {
                ineligible.Add(title.Id);
            }

            db.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = ladder[(i * 7) % ladder.Length],
                RatedAt = Origin.AddDays(i * 7),
                FirstRatedAt = Origin.AddDays(i * 7),
                SyncedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return new Seed(account.Id, ineligible);
    }

    private async Task AddRatingsAsync(Guid accountId, int count, DateTime startingAt)
    {
        await using var db = fixture.CreateContext(new AccountContext());
        var tmdbBase = 950_000_000L + (Interlocked.Increment(ref _tmdbBlock) * 10_000L);
        for (var i = 0; i < count; i++)
        {
            var title = new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TraktSlug = $"eval-extra-{i}-{Guid.NewGuid():N}",
                TmdbId = tmdbBase + i,
                Name = $"Eval Extra {i}",
                Year = 2015,
                Language = "en",
                TmdbPopularity = 10m + i,
                TraktVotes = 100 + i,
                RuntimeMinutes = 100,
                Genres = ["thriller"],
                Keywords = [],
                Subgenres = [],
                CreatedAt = DateTime.UtcNow,
            };
            db.Add(title);
            db.Add(new UserRating
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TitleId = title.Id,
                SubjectType = RatingSubjectType.Movie,
                Rating = (short)(6 + (i % 5)),
                RatedAt = startingAt.AddDays(i),
                FirstRatedAt = startingAt.AddDays(i),
                SyncedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
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
