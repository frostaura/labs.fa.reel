using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// Closes every <c>SearchTurn</c> the previous process left <see cref="SearchTurnStatus.Pending"/>,
/// once, at boot, before the API serves its first request.
///
/// <para><b>The hole it fills.</b> A turn is written Pending BEFORE the stream opens and stamped
/// terminal after <c>done</c>, and both writers — the persist step and the catch around the stream —
/// run inside the request pipeline. Kill the process mid-stream and neither ever runs: the row stays
/// Pending, <c>CompletedAt</c> null, <c>FailureCode</c> null, with no code path left in the system
/// that would ever reach it. Because <c>/ask</c> refuses a new turn while one is Pending on the
/// conversation, the thread is then wedged for good — every retry, every "Try again" and every new
/// question answers <c>409 turn_in_progress</c>, and <c>GET /turns/{id}</c> answers <c>202</c>
/// forever. Reproduced on a copy database: one SIGKILL 20 seconds into a turn, and the conversation
/// never accepted another question.</para>
///
/// <para><b>Why boot is the right moment, and why it is not the only guard.</b> Boot is the one
/// instant at which "Pending" is unambiguous: this process has just started, so it owns no streams,
/// so every Pending row in the table belongs to a process that is gone. No clock is consulted and no
/// live turn can be caught, which is why this arm may state the stronger
/// <see cref="SearchTurnFailure.ServerRestarted"/> as a fact. It cannot be the whole fix, though —
/// a turn wedged by something short of a process death is never seen by a boot that does not happen
/// — so the endpoint carries the clock-based <see cref="SearchTurnFailure.TurnTimedOut"/> guard as
/// well. Two arms, two codes, and the pair is what makes the wedge unreachable.</para>
///
/// <para><b>Account-agnostic, deliberately.</b> Every <c>IAccountScoped</c> entity carries a global
/// query filter bound to the request's account, and this runs with no request and no account. The
/// filter happens to be permissive when the context holds no account, but that is a property of one
/// expression in <c>ReelDbContext</c> that a future tightening would silently reverse — and the
/// failure mode would be a sweep that looks like it ran and closed nothing. <c>IgnoreQueryFilters</c>
/// states the intent where it cannot be undone by accident.</para>
///
/// <para>A single set-based <c>UPDATE</c>: no change tracking, nothing else in the context flushed,
/// and an empty table costs one statement and returns zero.</para>
/// </summary>
public static class PendingTurnReconciliation
{
    /// <summary>
    /// Marks every Pending turn Failed with <see cref="SearchTurnFailure.ServerRestarted"/> and
    /// <paramref name="utcNow"/> as its <c>CompletedAt</c>. Returns how many rows were closed.
    ///
    /// <para>Exposed as a plain method — not only as a startup hook — because the integration
    /// harness strips <c>IHostedService</c> wholesale and because a reconciliation nothing can call
    /// directly is a reconciliation nothing can test.</para>
    /// </summary>
    public static Task<int> ReconcileAsync(IReelDbContext db, DateTime utcNow, CancellationToken ct = default) =>
        db.SearchTurns
            .IgnoreQueryFilters()
            .Where(t => t.Status == SearchTurnStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(t => t.Status, SearchTurnStatus.Failed)
                    .SetProperty(t => t.FailureCode, SearchTurnFailure.ServerRestarted)
                    .SetProperty(t => t.CompletedAt, (DateTime?)utcNow),
                ct);

    /// <summary>
    /// The startup step, shaped on <c>DatabaseInitializer</c>: its own scope, its own logger, called
    /// from <c>Program</c> beside the migration step and BEFORE <c>app.Run()</c> — so it completes
    /// while the server is still not listening, and no request can race a half-swept table.
    ///
    /// <para>The count is logged at Information even when it is zero. Zero is the normal day and is
    /// also the evidence that the sweep ran at all; a line that only appears on the bad day cannot
    /// distinguish "nothing was stuck" from "the step was dropped from Program.cs".</para>
    /// </summary>
    public static async Task RunAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("PendingTurnReconciliation");
        var db = scope.ServiceProvider.GetRequiredService<IReelDbContext>();

        var closed = await ReconcileAsync(db, DateTime.UtcNow, ct);
        logger.LogInformation(
            "Ask Reel startup reconciliation closed {Count} turn(s) left Pending by a previous process "
            + "(FailureCode={FailureCode}).",
            closed,
            SearchTurnFailure.ServerRestarted);
    }
}
