using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Infrastructure.Background;

/// <summary>
/// One-pass repair for the original-language gap: <see cref="Domain.Catalog.Title.Language"/> was
/// only ever set from Trakt (the user's library), so every TMDB-discovered title — i.e. the entire
/// recommendation pool — had a null language and slipped past the language allow-list. This fills
/// language from TMDB for any null-language title, in low-priority rate-gated batches, then idles.
/// Self-terminating: once no null-language rows remain the loop returns. Gate: Background:LanguageBackfill.
/// A title TMDB has no language for is stamped "" so it is not re-fetched forever.
/// </summary>
public sealed class TitleLanguageBackfillService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<TitleLanguageBackfillService> logger) : BackgroundService
{
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Background:LanguageBackfill", true))
        {
            logger.LogInformation("LanguageBackfill disabled via Background:LanguageBackfill=false.");
            return;
        }

        try
        {
            // Let the app finish starting before competing for the TMDB rate gate.
            await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);

            var filled = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ReelDbContext>();
                var tmdb = scope.ServiceProvider.GetRequiredService<ITmdbClient>();

                var batch = await db.Titles
                    .Where(t => t.TmdbId != null && t.Language == null)
                    .OrderBy(t => t.Id)
                    .Take(BatchSize)
                    .ToListAsync(stoppingToken);

                if (batch.Count == 0)
                {
                    if (filled > 0)
                    {
                        logger.LogInformation("Language backfill complete — filled {Count} title(s).", filled);
                    }
                    return;
                }

                foreach (var title in batch)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        var details = title.MediaType == MediaType.Movie
                            ? await tmdb.GetMovieAsync(title.TmdbId!.Value, stoppingToken)
                            : await tmdb.GetTvAsync(title.TmdbId!.Value, stoppingToken);

                        // Empty-string stamp (not left null) so a title TMDB can't language-tag is
                        // excluded from English-only AND not re-queried on the next pass.
                        title.Language = string.IsNullOrWhiteSpace(details?.OriginalLanguage)
                            ? string.Empty
                            : details!.OriginalLanguage!.ToLowerInvariant();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "Language backfill failed for tmdb {TmdbId}; stamping blank.", title.TmdbId);
                        title.Language = string.Empty;
                    }
                }

                filled += batch.Count;
                await db.SaveChangesAsync(stoppingToken);
                logger.LogInformation("Language backfill: {Filled} title(s) processed so far.", filled);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Language backfill aborted; will retry on next start.");
        }
    }
}
