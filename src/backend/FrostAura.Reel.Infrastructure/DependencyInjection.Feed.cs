using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Background;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FrostAura.Reel.Infrastructure;

public static partial class DependencyInjection
{
    /// <summary>The model engine, scoring, ranking, the feed build, interleaving and blend.</summary>
    private static IServiceCollection AddReelFeed(this IServiceCollection services, IConfiguration configuration)
    {
        // ── ML engine ──────────────────────────────────────────────────────────────────────
        services.AddSingleton<IModelEngine, Ml.FastTreeModelEngine>();
        services.AddScoped<Application.Ml.FeatureVectorBuilder>();
        services.AddScoped<Application.Ml.OnlineResidualModel>();
        services.AddScoped<Application.Ml.RatingCalibrationService>();
        services.AddScoped<Application.Ml.RetrainGate>();
        services.AddScoped<Application.Ml.ModelLearnedQuery>();
        services.AddScoped<Application.Ml.ResidualReplay>();
        services.AddScoped<Application.Ranking.DislikeSignal>();
        services.AddScoped<Application.Ml.OnDemandScorer>();
        services.AddScoped<Application.Ml.EvalHarness>();
        services.AddScoped<Application.Ml.PersonAffinityWriter>();
        services.AddScoped<Application.Ml.ClosedLoop.RetroLoopQuery>();
        services.AddScoped<Application.Ml.ClosedLoop.ExposureLog>();
        services.AddScoped<Application.Experiments.InterleaveSummaryQuery>();
        services.AddScoped<Application.Ml.ClosedLoop.AccuracyQuery>();
        services.AddScoped<Application.Ml.ClosedLoop.RatingVerdictQuery>();
        services.AddScoped<Application.Ml.TasteFactsBuilder>();
        services.AddScoped<Application.Ml.PersonalisationContext>();

        // ── Serving pipeline ─────────────────────────────────────────────────────────────
        services.AddScoped<Application.Ranking.CandidateGenerator>();
        // The day's taste partition. Scoped so it shares the request/job DbContext; the partition
        // itself is cached day-granular in IMemoryCache, which is a singleton, so a build and the
        // rails it triggers all split their ANN budget across the same pockets.
        services.AddScoped<Application.Ranking.TasteClusterProvider>();
        services.AddScoped<Application.Ranking.CandidateRetriever>();
        // Offline diagnostic, reached only through the founder-only /api/dev surface. Scoped like
        // everything else it composes; it writes nothing.
        services.AddScoped<Application.Ranking.RetrievalRecallProbe>();
        services.AddScoped<Application.Ranking.FeedPaginator>();
        services.AddScoped<Application.Ranking.FeedRefreshCoordinator>();

        // ── Job handlers and schedulers ────────────────────────────────────────────────────
        services.AddScoped<IJobHandler, TrainJobHandler>();
        services.AddScoped<IJobHandler, EvaluateJobHandler>();
        // The interleaving posture, resolved from the environment once per process. Singleton so
        // every build in a process agrees on what the experiment is; off unless INTERLEAVE_ENABLED
        // says otherwise, in which case nothing downstream behaves differently at all.
        services.AddSingleton(Application.Ranking.Interleaving.InterleaveOptions.From(configuration));
        services.AddScoped<IJobHandler, BuildFeedJobHandler>();
        services.AddHostedService<NightlyReconcileService>();

        // ── Blend ──────────────────────────────────────────────────────────────────────────
        services.AddScoped<Application.Blend.BlendService>();

        return services;
    }
}
