namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// A hedged model call: when the first call has not answered within a threshold, the same call is
/// sent once more, and whichever answers successfully first is used; the other is cancelled.
///
/// <para><b>Why (HREV2-ASK-04).</b> Ask's model calls have a long tail that has nothing to do with
/// the request: the integrator's people-ordinal turn waited 14.6 s on one judge batch whose siblings
/// answered in about 2 s, and a "romantic comedies" plan took 7.6 s where the plan's p50 is under
/// 2 s. A tail like that is a slow provider route, not a hard request, and a second identical call
/// almost always answers in the usual time. Waiting for the first call instead put the turn's p95
/// past the 15 s gate on one batch.</para>
///
/// <para><b>What it costs.</b> At most one extra call, and only for a call already past the
/// threshold — set above the stage's normal p95, so a healthy turn never hedges. Both calls are
/// recorded on the usage ledger by the adapter that makes them, so the cost report stays exact.
/// A failed answer is never preferred over a successful one: when the first to return failed, the
/// other is still awaited, and only when both fail is the first one's failure returned.</para>
/// </summary>
public static class AskReelHedge
{
    /// <summary>
    /// Runs <paramref name="call"/>; when it has not completed after <paramref name="after"/>, runs it
    /// once more and returns the first result <paramref name="succeeded"/> accepts. <paramref name="onHedge"/>
    /// is called, on the caller's flow, when the second call is sent. A non-positive
    /// <paramref name="after"/> never hedges.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> call,
        Func<T, bool> succeeded,
        TimeSpan after,
        Action? onHedge,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(succeeded);
        var firstCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var first = Start(call, firstCts);
        if (after <= TimeSpan.Zero)
        {
            return await Settle(first, firstCts);
        }

        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var delay = Task.Delay(after, waiting.Token);
            if (await Task.WhenAny(first, delay) == first || ct.IsCancellationRequested)
            {
                await waiting.CancelAsync();
                return await Settle(first, firstCts);
            }
        }

        onHedge?.Invoke();
        var secondCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var second = Start(call, secondCts);
        var winner = await Task.WhenAny(first, second);
        if (!Accepted(winner, succeeded))
        {
            // The first to come back failed: the other may still answer.
            winner = winner == first ? second : first;
            await Task.WhenAny(winner);
            if (!Accepted(winner, succeeded))
            {
                Release(second, secondCts);
                return await Settle(first, firstCts);
            }
        }

        var (loser, loserCts) = winner == first ? (second, secondCts) : (first, firstCts);
        await loserCts.CancelAsync();
        Release(loser, loserCts);
        Release(winner, winner == first ? firstCts : secondCts);
        return winner.Result;
    }

    /// <summary>
    /// Runs <paramref name="call"/>, and sends it once more at each offset of <paramref name="schedule"/>
    /// (from the start) while no answer <paramref name="succeeded"/> accepts is in — and AT ONCE, rather
    /// than at the next offset, when every call sent so far has come back unaccepted, so a fast failure
    /// is retried instead of waited out. At most one call per offset beyond the first. Returns the first
    /// accepted result; when every call failed, the first call's own outcome. Calls still out when it
    /// returns are cancelled. <paramref name="onHedge"/> is told the number of the call about to be sent.
    ///
    /// <para><b>Why a schedule (fix-golden-v2-2).</b> Two golden turns lost a whole judge batch to the
    /// 12 s judging deadline with the batch hedged at 4 s: both routes stayed slow for eight seconds,
    /// while a healthy batch answers in 0.5–2.5 s. A third send at 8 s still has four seconds inside
    /// the deadline, and costs nothing on a healthy turn, which never reaches it.</para>
    /// </summary>
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> call,
        Func<T, bool> succeeded,
        IReadOnlyList<TimeSpan> schedule,
        Action<int>? onHedge,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(succeeded);
        ArgumentNullException.ThrowIfNull(schedule);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sent = new List<(Task<T> Task, CancellationTokenSource Cts)>();
        var answered = new HashSet<Task<T>>();
        Task<T>? winner = null;
        void Send()
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sent.Add((Start(call, cts), cts));
        }

        Send();
        var rung = 0;
        try
        {
            while (true)
            {
                var pending = sent.Where(s => !answered.Contains(s.Task)).Select(s => (Task)s.Task).ToList();
                if (pending.Count == 0)
                {
                    if (rung >= schedule.Count)
                    {
                        // Every call failed: the first one's own outcome, as the single hedge returns it.
                        return await sent[0].Task;
                    }

                    rung++;
                    onHedge?.Invoke(sent.Count + 1);
                    Send();
                    continue;
                }

                using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task? timer = null;
                if (rung < schedule.Count)
                {
                    var wait = schedule[rung] - clock.Elapsed;
                    timer = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, timerCts.Token);
                    pending.Add(timer);
                }

                var done = await Task.WhenAny(pending);
                await timerCts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                if (done == timer)
                {
                    rung++;
                    onHedge?.Invoke(sent.Count + 1);
                    Send();
                    continue;
                }

                var task = (Task<T>)done;
                answered.Add(task);
                if (Accepted(task, succeeded))
                {
                    winner = task;
                    return task.Result;
                }
            }
        }
        finally
        {
            foreach (var (task, cts) in sent)
            {
                if (task != winner && !task.IsCompleted)
                {
                    await cts.CancelAsync();
                }

                Release(task, cts);
            }
        }
    }

    private static Task<T> Start<T>(Func<CancellationToken, Task<T>> call, CancellationTokenSource cts)
    {
        try
        {
            return call(cts.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Task.FromException<T>(ex);
        }
    }

    private static bool Accepted<T>(Task<T> task, Func<T, bool> succeeded) =>
        task.IsCompletedSuccessfully && succeeded(task.Result);

    /// <summary>Awaits <paramref name="task"/> and then releases its token source.</summary>
    private static async Task<T> Settle<T>(Task<T> task, CancellationTokenSource cts)
    {
        try
        {
            return await task;
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>Releases <paramref name="cts"/> once <paramref name="task"/> has stopped, observing any fault.</summary>
    private static void Release<T>(Task<T> task, CancellationTokenSource cts) =>
        _ = task.ContinueWith(
            t =>
            {
                _ = t.Exception;
                cts.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
