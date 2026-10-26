using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using FrostAura.Reel.Domain.Ports;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Search.AskReel.Engine;

/// <summary>
/// The v2 judge (§4.2 step 3, §4.5): the top <see cref="MaxJudged"/> titles in retrieval order,
/// split into batches of at most <see cref="BatchSize"/> (the first one smaller, so the first card
/// waits for the fewest titles), at most <see cref="MaxConcurrent"/> in flight, each batch shuffled
/// by a seed of its own so the model's position bias cannot track retrieval rank.
///
/// <para><b>Every title gets exactly one verdict, and a batch's verdicts arrive together.</b> A
/// failed batch, a batch past the deadline and a title the model skipped all come back as
/// <see cref="JudgeVerdict.Unjudged"/> — <see cref="ResultSource.Fallback"/> sources, never shown as
/// model output, never cached. Callers group by <see cref="Batches"/> to emit a batch the moment its
/// last verdict lands.</para>
///
/// <para><b>Cached per request in its conversation</b>, keyed by the model's salt (model, effort,
/// prompt version), the account, the title and the normalised request text — a verdict is reused
/// only where the same thing was asked of the same model.</para>
/// </summary>
public sealed class RequestJudge(IRequestJudgeModel model, IMemoryCache cache, ILogger<RequestJudge> logger) : IRequestJudge
{
    public const int MaxJudged = 16;

    public const int BatchSize = 6;

    public const int FirstBatchSize = 4;

    public const int MaxConcurrent = 3;

    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(25);

    public static readonly TimeSpan VerdictTtl = TimeSpan.FromHours(6);

    internal TimeSpan StageDeadline { get; init; } = Deadline;

    /// <summary>The batches <paramref name="titles"/> are judged in — membership only, retrieval order.</summary>
    public static IReadOnlyList<IReadOnlyList<Guid>> Batches(IReadOnlyList<Guid> titles)
    {
        var judged = titles.Take(MaxJudged).ToList();
        var batches = new List<IReadOnlyList<Guid>>();
        var offset = 0;
        while (offset < judged.Count)
        {
            var size = batches.Count == 0 ? FirstBatchSize : BatchSize;
            batches.Add(judged.Skip(offset).Take(size).ToList());
            offset += size;
        }

        return batches;
    }

    public async IAsyncEnumerable<JudgeVerdict> JudgeAsync(JudgeInput input, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var titles = input.Titles.Take(MaxJudged).ToList();
        if (titles.Count == 0)
        {
            yield break;
        }

        var byId = titles.ToDictionary(t => t.TitleId);
        var batches = Batches(titles.Select(t => t.TitleId).ToList())
            .Select(ids => ids.Select(id => byId[id]).ToList())
            .ToList();
        var request = Normalise(input.Request);
        var channel = Channel.CreateUnbounded<IReadOnlyList<JudgeVerdict>>();
        var deadlineAt = DateTime.UtcNow + StageDeadline;

        using var gate = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
        using var siblings = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = batches
            .Select((batch, index) => RunAsync(index, batch, input, request, gate, deadlineAt, channel.Writer, siblings.Token))
            .ToList();
        var all = Task.WhenAll(running).ContinueWith(
            _ => channel.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            await foreach (var verdicts in channel.Reader.ReadAllAsync(ct))
            {
                foreach (var verdict in verdicts)
                {
                    yield return verdict;
                }
            }
        }
        finally
        {
            // Batches still out when the reader leaves (the turn was cancelled, or the caller stopped
            // reading) are cancelled and awaited, so nothing outlives the gate it holds.
            await siblings.CancelAsync();
            try
            {
                await Task.WhenAll(running);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogDebug(ex, "Ask Reel v2 judge: batches abandoned with the turn.");
            }

            await all;
        }
    }

    private async Task RunAsync(
        int index,
        IReadOnlyList<JudgeTitle> batch,
        JudgeInput input,
        string request,
        SemaphoreSlim gate,
        DateTime deadlineAt,
        ChannelWriter<IReadOnlyList<JudgeVerdict>> writer,
        CancellationToken ct)
    {
        var verdicts = new Dictionary<Guid, JudgeVerdict>();
        var uncached = new List<JudgeTitle>();
        foreach (var title in batch)
        {
            if (cache.TryGetValue<JudgeVerdict>(Key(input.AccountId, title.TitleId, request), out var hit) && hit is { Judged: true })
            {
                verdicts[title.TitleId] = hit;
            }
            else
            {
                uncached.Add(title);
            }
        }

        try
        {
            if (uncached.Count > 0)
            {
                await gate.WaitAsync(ct);
                try
                {
                    var remaining = deadlineAt - DateTime.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        bounded.CancelAfter(remaining);
                        var shuffled = Shuffle(uncached, request);
                        var result = await model.JudgeBatchAsync(input with { Titles = shuffled }, bounded.Token);
                        foreach (var verdict in result.Verdicts.Where(v => v.Judged && uncached.Any(u => u.TitleId == v.TitleId)))
                        {
                            verdicts[verdict.TitleId] = verdict;
                            cache.Set(Key(input.AccountId, verdict.TitleId, request), verdict, VerdictTtl);
                        }

                        if (result.Failed)
                        {
                            logger.LogWarning(
                                "Ask Reel v2 judge batch {Batch} failed ({Code}); its {Count} uncached titles are unjudged.",
                                index, result.Meta.FailureCode ?? "-", uncached.Count);
                        }
                    }
                    else
                    {
                        logger.LogWarning(
                            "Ask Reel v2 judge batch {Batch} skipped: the judging deadline passed; {Count} titles unjudged.",
                            index, uncached.Count);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Ask Reel v2 judge batch {Batch} ran past the judging deadline; its titles are unjudged.", index);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Ask Reel v2 judge batch {Batch} threw; its titles are unjudged.", index);
        }

        // Retrieval order inside the batch, whatever order the model answered in.
        writer.TryWrite(batch.Select(t => verdicts.GetValueOrDefault(t.TitleId) ?? JudgeVerdict.Unjudged(t.TitleId)).ToList());
    }

    private string Key(Guid accountId, Guid titleId, string request) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"askjudge:{model.CacheSalt}:{accountId:N}:{titleId:N}:{request}");

    internal static string Normalise(string request) =>
        string.Join(' ', request.ToLowerInvariant().Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// A stable order seeded on the request and the SET of ids (sorted), never on the order they
    /// arrived in — so the order the model sees cannot carry retrieval rank (§4.5).
    /// </summary>
    internal static IReadOnlyList<JudgeTitle> Shuffle(IReadOnlyList<JudgeTitle> titles, string request)
    {
        if (titles.Count < 2)
        {
            return titles;
        }

        var seed = new StringBuilder(request);
        foreach (var id in titles.Select(t => t.TitleId).Order())
        {
            seed.Append('|').Append(id.ToString("N"));
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString()));
        return titles
            .OrderBy(t => BitConverter.ToUInt64(SHA256.HashData([.. digest, .. t.TitleId.ToByteArray()]), 0))
            .ThenBy(t => t.TitleId)
            .ToList();
    }
}
