using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>
/// Writes the account's second-person taste narrative — the personalisation context every other
/// LLM call in the product then rides on.
///
/// <para>One call per account per run, and the run is skipped outright when the account's taste
/// fingerprint has not moved since the last narrative. That is the whole cost story: a weekly
/// cadence with an early exit, against a lens cache whose steady-state hit rate depends on this
/// same fingerprint being stable for weeks at a time.</para>
///
/// <para>A failure here is not a failure of anything downstream. Every surface falls back to the
/// compact taste line it used before narratives existed, so an unavailable provider degrades the
/// prose rather than breaking the feed.</para>
/// </summary>
public sealed class BuildTasteNarrativeJobHandler(
    IReelDbContext db,
    FeatureVectorBuilder featureBuilder,
    TasteNarrativeService narratives,
    ILogger<BuildTasteNarrativeJobHandler> logger,
    // Optional so a hand-built handler (a test) needs no breaker; DI always supplies it.
    Llm.IProviderHealth? health = null) : IJobHandler
{
    /// <summary>Set on the job's cursor by the regenerate action, which spends even when up to date.</summary>
    public const string ForceCursor = """{"Force":true}""";

    public JobKind Kind => JobKind.BuildTasteNarrative;

    public async Task ExecuteAsync(SyncJob job, CancellationToken ct)
    {
        var accountId = job.AccountId ?? throw new InvalidOperationException("BuildTasteNarrative requires an account.");
        if (health?.OpenBreakerFor(Domain.Sync.ApiProvider.OpenRouter, Domain.Sync.ApiUsagePurpose.TasteNarrative) is { } open)
        {
            // Paused, not failed — see PersonaliseTitlesJobHandler.
            job.ProgressMessage = $"taste profile paused ({open.ReasonCode})";
            await db.SaveChangesAsync(ct);
            return;
        }

        var force = job.CursorJson is { Length: > 0 } cursor
            && cursor.Contains("\"Force\":true", StringComparison.OrdinalIgnoreCase);

        job.ProgressMessage = "Reading your ratings";
        await db.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;
        var taste = await featureBuilder.BuildTasteStateAsync(accountId, now, ct);
        var result = await narratives.WriteAsync(accountId, taste, now, force, ct);

        job.ProgressMessage = result.Outcome switch
        {
            NarrativeOutcome.Written => $"Taste profile rewritten from {result.Narrative?.RatingsCount ?? 0} ratings",
            NarrativeOutcome.UpToDate => "Taste profile already current",
            NarrativeOutcome.Unavailable => "skipped — no writer configured",
            NarrativeOutcome.Ungrounded => "skipped — the draft cited something it was not given",
            _ => "skipped — the writer did not answer",
        };
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Taste narrative job for {AccountId} finished: {Outcome}.", accountId, result.Outcome);
    }
}
