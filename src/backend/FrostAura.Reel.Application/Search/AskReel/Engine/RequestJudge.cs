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
///
/// <para><b>Judged ahead, never twice.</b> <see cref="PrejudgeAsync"/> judges a first batch before
/// the turn's plan is back (HREV1-ASK-05) and registers each title as in flight under the same key.
/// A batch that reaches a title still in flight waits for that verdict instead of sending the title
/// again, and judges afresh only what the early call failed to rule on.</para>
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

    /// <summary>
    /// Judges the first batch of <paramref name="input"/> (<see cref="FirstBatchSize"/> titles) ahead
    /// of the turn's own <see cref="JudgeAsync"/>, which then takes these verdicts from the cache —
    /// or, while this call is still out, waits for it. Each title is registered as in flight before
    /// the model is called. Returns how long the model call took; null when nothing was sent (every
    /// title was already judged or in flight) or the call failed. Never throws but on cancellation.
    /// </summary>
    public async Task<double?> PrejudgeAsync(JudgeInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var request = Normalise(input.Request);
        var batch = input.Titles.Take(FirstBatchSize)
            .Where(t => !cache.TryGetValue(InFlightKey(input.AccountId, t.TitleId, request), out _)
                        && !(cache.TryGetValue<JudgeVerdict>(Key(input.AccountId, t.TitleId, request), out var hit) && hit is { Judged: true }))
            .ToList();
        if (batch.Count == 0)
        {
            return null;
        }

        var ruled = new TaskCompletionSource<IReadOnlyDictionary<Guid, JudgeVerdict>>(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var title in batch)
        {
            _ = cache.Set(InFlightKey(input.AccountId, title.TitleId, request), ruled.Task, StageDeadline);
        }

        var verdicts = new Dictionary<Guid, JudgeVerdict>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(StageDeadline);
            var result = await model.JudgeBatchAsync(input with { Titles = Shuffle(batch, request) }, bounded.Token);
            foreach (var verdict in result.Verdicts.Where(v => v.Judged && batch.Any(t => t.TitleId == v.TitleId)))
            {
                verdicts[verdict.TitleId] = verdict;
                cache.Set(Key(input.AccountId, verdict.TitleId, request), verdict, VerdictTtl);
            }

            if (result.Failed)
            {
                logger.LogWarning("Ask Reel v2 judge: the early first batch failed ({Code}); the turn judges it itself.", result.Meta.FailureCode ?? "-");
                return null;
            }

            return clock.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Ask Reel v2 judge: the early first batch threw; the turn judges it itself.");
            return null;
        }
        finally
        {
            // Verdicts first, then the in-flight marks go: a batch that finds no mark finds the verdict.
            ruled.TrySetResult(verdicts);
            foreach (var title in batch)
            {
                cache.Remove(InFlightKey(input.AccountId, title.TitleId, request));
            }
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
        var inFlight = new List<(JudgeTitle Title, Task<IReadOnlyDictionary<Guid, JudgeVerdict>> Ruling)>();
        foreach (var title in batch)
        {
            // The in-flight mark is read BEFORE the verdict: PrejudgeAsync caches its verdicts and only
            // then drops the marks, so a title with no mark that was judged early has its verdict here.
            if (cache.TryGetValue<Task<IReadOnlyDictionary<Guid, JudgeVerdict>>>(InFlightKey(input.AccountId, title.TitleId, request), out var ruling)
                && ruling is not null)
            {
                inFlight.Add((title, ruling));
            }
            else if (cache.TryGetValue<JudgeVerdict>(Key(input.AccountId, title.TitleId, request), out var hit) && hit is { Judged: true })
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
            // The titles nobody judged yet are sent while the early ones are awaited; both are always
            // awaited, so no call outlives this batch (and the gate it holds).
            var sending = SendAsync(index, uncached, input, request, gate, deadlineAt, ct);
            var waiting = EarlyAsync(index, inFlight, deadlineAt, ct);
            await Task.WhenAll(sending, waiting);
            var (early, missed) = await waiting;
            foreach (var (id, verdict) in early.Concat(await sending))
            {
                verdicts[id] = verdict;
            }

            // What the early call failed to rule on is judged here, as it would have been without it.
            foreach (var (id, verdict) in await SendAsync(index, missed, input, request, gate, deadlineAt, ct))
            {
                verdicts[id] = verdict;
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

    /// <summary>
    /// The verdicts an early call (<see cref="PrejudgeAsync"/>) returned for <paramref name="inFlight"/>,
    /// waited for up to the judging deadline, and the titles it did not rule on.
    /// </summary>
    private async Task<(Dictionary<Guid, JudgeVerdict> Early, List<JudgeTitle> Missed)> EarlyAsync(
        int index,
        IReadOnlyList<(JudgeTitle Title, Task<IReadOnlyDictionary<Guid, JudgeVerdict>> Ruling)> inFlight,
        DateTime deadlineAt,
        CancellationToken ct)
    {
        var early = new Dictionary<Guid, JudgeVerdict>();
        var missed = new List<JudgeTitle>();
        foreach (var (title, ruling) in inFlight)
        {
            IReadOnlyDictionary<Guid, JudgeVerdict>? ruled = null;
            var remaining = deadlineAt - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    ruled = await ruling.WaitAsync(remaining, ct);
                }
                catch (TimeoutException)
                {
                    logger.LogWarning("Ask Reel v2 judge batch {Batch} waited past the judging deadline for an early verdict.", index);
                }
            }

            if (ruled is not null && ruled.TryGetValue(title.TitleId, out var verdict))
            {
                early[title.TitleId] = verdict;
            }
            else
            {
                missed.Add(title);
            }
        }

        return (early, missed);
    }

    /// <summary>
    /// One model call for <paramref name="titles"/>, holding a slot of <paramref name="gate"/>: the
    /// verdicts that came back judged, each cached. Empty for no titles, a failed call or a passed
    /// deadline (the caller leaves those titles unjudged).
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, JudgeVerdict>> SendAsync(
        int index, IReadOnlyList<JudgeTitle> titles, JudgeInput input, string request, SemaphoreSlim gate, DateTime deadlineAt, CancellationToken ct)
    {
        var verdicts = new Dictionary<Guid, JudgeVerdict>();
        if (titles.Count == 0)
        {
            return verdicts;
        }

        await gate.WaitAsync(ct);
        try
        {
            var remaining = deadlineAt - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                logger.LogWarning(
                    "Ask Reel v2 judge batch {Batch} skipped: the judging deadline passed; {Count} titles unjudged.",
                    index, titles.Count);
                return verdicts;
            }

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(remaining);
            var result = await model.JudgeBatchAsync(input with { Titles = Shuffle(titles, request) }, bounded.Token);
            foreach (var verdict in result.Verdicts.Where(v => v.Judged && titles.Any(u => u.TitleId == v.TitleId)))
            {
                verdicts[verdict.TitleId] = verdict;
                cache.Set(Key(input.AccountId, verdict.TitleId, request), verdict, VerdictTtl);
            }

            if (result.Failed)
            {
                logger.LogWarning(
                    "Ask Reel v2 judge batch {Batch} failed ({Code}); its {Count} uncached titles are unjudged.",
                    index, result.Meta.FailureCode ?? "-", titles.Count);
            }

            return verdicts;
        }
        finally
        {
            gate.Release();
        }
    }

    private string InFlightKey(Guid accountId, Guid titleId, string request) => "inflight:" + Key(accountId, titleId, request);

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
