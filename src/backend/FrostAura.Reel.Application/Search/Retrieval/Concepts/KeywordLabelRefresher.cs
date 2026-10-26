using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace FrostAura.Reel.Application.Search.Retrieval.Concepts;

/// <summary>What one refresh did.</summary>
/// <param name="Counted">Labels whose title count was written.</param>
/// <param name="Embedded">Labels embedded this run.</param>
/// <param name="Calls">Embedding provider calls made (each carries up to <see cref="KeywordLabelRefresher.BatchSize"/> labels).</param>
/// <param name="Remaining">Labels still waiting for an embedding after this run.</param>
public sealed record KeywordLabelRefresh(int Counted, int Embedded, int Calls, int Remaining);

/// <summary>
/// Keeps <c>KeywordLabels</c> — the vocabulary's precomputed label embeddings — in step with the
/// catalogue: one statement re-counts every label from <c>Titles.Keywords</c> (free), then the
/// most-carried labels that have no embedding from the current model are embedded in batches, up to
/// a per-run cap so a first run on a large catalogue spreads its (small) cost instead of spending it
/// at once. Labels carried by fewer than <see cref="MinTitles"/> titles are never embedded: a
/// nearest-label match on a label that tags one title adds nothing a judge would keep.
/// </summary>
public sealed class KeywordLabelRefresher(IReelDbContext db, IEmbeddingProvider embeddings, ILogger<KeywordLabelRefresher> logger)
{
    /// <summary>Labels per provider call.</summary>
    public const int BatchSize = 100;

    /// <summary>Labels carried by fewer titles are counted but never embedded.</summary>
    public const int MinTitles = 2;

    /// <summary>
    /// Re-counts every label (labels no title carries any more drop to 0, so the fallback's count
    /// filter skips them without losing their embedding), then embeds up to <paramref name="maxEmbeds"/> labels that lack a current embedding.
    /// </summary>
    public async Task<KeywordLabelRefresh> RefreshAsync(int maxEmbeds, CancellationToken ct)
    {
        var counted = await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "KeywordLabels" ("Label", "TitleCount", "CountedAt")
            SELECT k, count(*)::int, now()
              FROM "Titles", unnest("Keywords") AS k
             WHERE k <> ''
             GROUP BY k
            ON CONFLICT ("Label") DO UPDATE
               SET "TitleCount" = EXCLUDED."TitleCount", "CountedAt" = EXCLUDED."CountedAt"
            """,
            ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "KeywordLabels" AS l SET "TitleCount" = 0, "CountedAt" = now()
             WHERE l."TitleCount" > 0
               AND NOT EXISTS (SELECT 1 FROM "Titles" t WHERE t."Keywords" @> ARRAY[l."Label"])
            """,
            ct);

        var embedded = 0;
        var calls = 0;
        if (embeddings.IsAvailable && maxEmbeds > 0)
        {
            var pending = await db.KeywordLabels
                .Where(l => l.TitleCount >= MinTitles && (l.Embedding == null || l.EmbeddingModel != EmbeddingText.Model))
                .OrderByDescending(l => l.TitleCount)
                .ThenBy(l => l.Label)
                .Take(maxEmbeds)
                .ToListAsync(ct);

            foreach (var batch in pending.Chunk(BatchSize))
            {
                var vectors = await embeddings.EmbedAsync([.. batch.Select(l => l.Label)], ct);
                calls++;
                if (vectors.Length != batch.Length)
                {
                    logger.LogWarning("Keyword labels: the provider returned {Got} vectors for {Sent} labels; batch skipped.", vectors.Length, batch.Length);
                    continue;
                }

                var now = DateTime.UtcNow;
                for (var i = 0; i < batch.Length; i++)
                {
                    if (vectors[i] is { Length: > 0 } vector)
                    {
                        batch[i].Embedding = new Vector(vector);
                        batch[i].EmbeddingModel = EmbeddingText.Model;
                        batch[i].EmbeddedAt = now;
                        embedded++;
                    }
                }

                await db.SaveChangesAsync(ct);
            }
        }

        var remaining = await db.KeywordLabels
            .CountAsync(l => l.TitleCount >= MinTitles && (l.Embedding == null || l.EmbeddingModel != EmbeddingText.Model), ct);
        logger.LogInformation(
            "Keyword labels: {Counted} counted, {Embedded} embedded in {Calls} call(s), {Remaining} waiting.",
            counted, embedded, calls, remaining);
        return new KeywordLabelRefresh(counted, embedded, calls, remaining);
    }
}
