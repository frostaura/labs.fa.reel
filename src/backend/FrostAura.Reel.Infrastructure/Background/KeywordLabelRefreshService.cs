using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Background;

/// <summary>
/// Keeps the keyword vocabulary's label embeddings current (<see cref="KeywordLabelRefresher"/>):
/// shortly after start, then once a day. Each run re-counts every label (free) and embeds at most
/// <c>Retrieval:VocabularyEmbedsPerRun</c> labels that lack an embedding from the current model
/// (default 2,000; batched 100 per call, so a run is at most 20 small calls — about $0.0001 at
/// text-embedding-3-small prices). Once the backlog is embedded a run embeds only labels the day's
/// ingest added.
///
/// <para>Gate: <c>Background:KeywordVocabulary</c> (default on). Off, the concept resolver still
/// resolves by containment and synonyms; only its nearest-label fallback has nothing to read. A
/// failed run is logged and retried on the next tick; it never stops the app.</para>
/// </summary>
public sealed class KeywordLabelRefreshService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<KeywordLabelRefreshService> logger) : BackgroundService
{
    /// <summary>The configuration key that turns the service off.</summary>
    public const string GateKey = "Background:KeywordVocabulary";

    /// <summary>The configuration key for the per-run embedding cap.</summary>
    public const string EmbedsPerRunKey = "Retrieval:VocabularyEmbedsPerRun";

    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue(GateKey, true))
        {
            logger.LogInformation("Keyword vocabulary refresh disabled via {Gate}=false.", GateKey);
            return;
        }

        var perRun = Math.Max(0, configuration.GetValue(EmbedsPerRunKey, 2_000));
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var refresher = scope.ServiceProvider.GetRequiredService<KeywordLabelRefresher>();
                    await refresher.RefreshAsync(perRun, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Keyword vocabulary refresh failed; retrying on the next tick.");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
