using FrostAura.Reel.Application.Search.Retrieval.Concepts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Background;

/// <summary>
/// Keeps the keyword vocabulary's label embeddings current (<see cref="KeywordLabelRefresher"/>):
/// shortly after start, then once a day. Each run re-counts every label (free) and embeds at most
/// <see cref="EmbedsPerRun"/> labels that lack an embedding from the current model (batched 100 per
/// call, so a run is at most 20 small calls — about $0.0001 at text-embedding-3-small prices).
/// Once the backlog is embedded a run embeds only labels the day's ingest added.
///
/// <para>Gate: <c>Background:KeywordVocabulary</c> (default on). Off, the concept resolver still
/// resolves by containment and synonyms; only its nearest-label fallback has nothing to read. A
/// failed run is logged and retried on the next tick; it never stops the app.</para>
///
/// <para>Each pass reads the whole catalogue for every account, so it runs in a system scope
/// (<see cref="SystemScopes"/>) and keeps working when <c>Tenancy:UnscopedReads</c> is Throw.</para>
/// </summary>
public sealed class KeywordLabelRefreshService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<KeywordLabelRefreshService> logger) : BackgroundService
{
    /// <summary>The configuration key that turns the service off.</summary>
    public const string GateKey = "Background:KeywordVocabulary";

    /// <summary>The most labels one run embeds; a first run on a large catalogue spreads over days.</summary>
    public const int EmbedsPerRun = 2_000;

    private static readonly TimeSpan WarmDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue(GateKey, true))
        {
            logger.LogInformation("Keyword vocabulary refresh disabled via {Gate}=false.", GateKey);
            return;
        }

        try
        {
            // Warm the in-memory vocabulary first (one ~175 ms aggregate), so the first Ask turn
            // after a deploy does not pay for it.
            await Task.Delay(WarmDelay, stoppingToken);
            try
            {
                await WarmAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Keyword vocabulary warm-up failed; the first concept request will read it instead.");
            }

            await Task.Delay(StartDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshOnceAsync(stoppingToken);
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

    /// <summary>One warm-up pass: reads the vocabulary into the shared cache.</summary>
    internal async Task WarmAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateSystemScope();
        await scope.ServiceProvider.GetRequiredService<KeywordVocabulary>().LabelsAsync(ct);
    }

    /// <summary>One refresh pass: re-counts every label and embeds up to <see cref="EmbedsPerRun"/>.</summary>
    internal async Task<KeywordLabelRefresh> RefreshOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateSystemScope();
        return await scope.ServiceProvider.GetRequiredService<KeywordLabelRefresher>().RefreshAsync(EmbedsPerRun, ct);
    }
}
