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
