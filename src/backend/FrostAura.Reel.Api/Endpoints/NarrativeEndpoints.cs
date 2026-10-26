using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>
/// The taste narrative as a product surface: read it, correct it, or ask for a rewrite.
///
/// <para>Editable is not a nicety here. This text is what the product believes about a person and
/// what every downstream model call is then told about them, so a wrong inference has to become a
/// correction rather than a standing bias nobody can reach. The edit wins everywhere and survives
/// every regeneration except the one the user explicitly asks for.</para>
/// </summary>
public static class NarrativeEndpoints
{
    /// <summary>
    /// The ceiling on a user's own rewrite. Generous — ten sentences is nowhere near it — but the
    /// field is injected into every rerank prompt in the product, so an unbounded one is a bill.
    /// </summary>
    public const int MaxEditChars = 4000;

    /// <summary>The trigger of a rewrite the read queued because the writer has moved on from the stored text.</summary>
    public const string OutdatedNarrativeReason = "narrative-outdated";

    /// <summary>The narrative as the client sees it. <c>text</c> is already the effective one.</summary>
    public sealed record NarrativeResponse(
        string? Text,
        bool IsEdited,
        int? Version,
        int RatingsCount,
        DateTime? GeneratedAt,
        bool IsStale,
        bool Regenerating,
        bool WriterAvailable);

    public sealed record NarrativeEdit(string? Text);

    public static void MapNarrativeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/taste/narrative").RequireAccount();

        group.MapGet("/", async (
            IReelDbContext db, IAccountContext accountContext, PersonalisationContext personalisation,
            FeatureVectorBuilder featureBuilder, TasteFactsBuilder factsBuilder, ILensWriter writer,
            IProviderHealth health, CancellationToken ct) =>
        {
            var accountId = accountContext.AccountId!.Value;
            var narrative = await personalisation.CurrentAsync(accountId, ct);
            var regenerating = await db.SyncJobs.AnyAsync(
                j => j.AccountId == accountId && j.Kind == JobKind.BuildTasteNarrative
                    && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);

            if (narrative is null)
            {
                return Results.Ok(new NarrativeResponse(
                    null, false, null, 0, null, false, regenerating, writer.IsAvailable));
            }

            // Staleness is computed, not stored: the account's taste moves continuously and the
            // narrative does not, so "is this still about you" can only be answered by recomputing
            // the fingerprint. It costs a handful of aggregates and no LLM spend.
            var taste = await featureBuilder.BuildTasteStateAsync(accountId, DateTime.UtcNow, ct);
            var built = await factsBuilder.BuildAsync(accountId, taste, DateTime.UtcNow, ct);

            // Stale when the fingerprint moved OR the account crossed a crowd band the fingerprint's
            // coarser bands miss — the narrative's crowd claim would otherwise sit beside a tile that
            // contradicts it (IREV3S-07).
            var crowdMoved = TasteNarrativeService.CrowdStanceMoved(narrative.FactsJson, built.Facts);
            var stale = !string.Equals(narrative.ProfileHash, built.ProfileHash, StringComparison.Ordinal) || crowdMoved;

            // Nothing else queues a rewrite for a narrative whose fingerprint has NOT moved but
            // which the writer would no longer stand behind: one written under an older prompt (a
            // v2 narrative read "-0.18" as "below the crowd"), or before a crowd-band crossing. The feed build only queues on a fingerprint move and the
            // weekly pass only on Sundays, so the text would stand for days. Queued here once — the
            // in-flight check is the partial unique index's own rule — and the user's edit is
            // carried forward, as on every non-forced rewrite.
            if (!regenerating
                && writer.IsAvailable
                && (narrative.PromptVersion != writer.NarrativePromptVersion || crowdMoved)
                && health.OpenBreakerFor(ApiProvider.OpenRouter, ApiUsagePurpose.TasteNarrative) is null)
            {
                db.SyncJobs.Add(new SyncJob
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    Kind = JobKind.BuildTasteNarrative,
                    Priority = 4,
                    EnqueuedAt = DateTime.UtcNow,
                    TriggerReason = OutdatedNarrativeReason,
                });
                regenerating = await db.SaveAbsorbingRebuildRaceAsync(ct) > 0 || regenerating;
            }

            return Results.Ok(new NarrativeResponse(
                narrative.Effective,
                narrative.EditedText is { Length: > 0 },
                narrative.Version,
                narrative.RatingsCount,
                narrative.GeneratedAt,
                stale,
                regenerating,
                writer.IsAvailable));
        });

        // The user's own words. Saved against the CURRENT version rather than minting a new one —
        // an edit is a correction to what the product said, not a new thing the product said.
        group.MapPut("/", async (
            NarrativeEdit body, IAccountContext accountContext, TasteNarrativeService narratives,
            CancellationToken ct) =>
        {
            if (body.Text is { Length: > MaxEditChars })
            {
                return Results.BadRequest(new { error = $"A taste profile is capped at {MaxEditChars} characters." });
            }

            var saved = await narratives.SaveEditAsync(
                accountContext.AccountId!.Value, body.Text, DateTime.UtcNow, ct);
            if (saved is null)
            {
                return Results.NotFound(new { error = "There is no taste profile to edit yet." });
            }

            return Results.Ok(new NarrativeResponse(
                saved.Effective,
                saved.EditedText is { Length: > 0 },
                saved.Version,
                saved.RatingsCount,
                saved.GeneratedAt,
                false,
                false,
                true));
        });

        // Regenerate. Queued rather than run inline: it is one LLM call against a fact sheet that
        // takes a few seconds to assemble, and the page already knows how to show "regenerating".
        // The Force cursor is what makes it spend even when the fingerprint has not moved — and
        // what clears the user's edit, because asking for a rewrite is asking to see what the
        // product would say without it.
        group.MapPost("/regenerate", async (
            IReelDbContext db, IAccountContext accountContext, ILensWriter writer, IProviderHealth health, CancellationToken ct) =>
        {
            if (!writer.IsAvailable)
            {
                return Results.BadRequest(new { error = "No writer is configured, so a profile cannot be written." });
            }

            // Refused up front while the provider breaker holds calls back: a queued job would
            // either pause (consuming the request silently) or fail on claim. Saying so now, with
            // the breaker's own code and retry time, is the honest answer to "regenerate".
            if (health.OpenBreakerFor(ApiProvider.OpenRouter, ApiUsagePurpose.TasteNarrative) is { } open)
            {
                return Results.Json(
                    new
                    {
                        error = $"The taste profile cannot be rewritten right now: {ProviderBreakerReasons.Describe(open.ReasonCode)}.",
                        reason = open.ReasonCode,
                        retryAt = open.RetryAt,
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var accountId = accountContext.AccountId!.Value;
            var inFlight = await db.SyncJobs.AnyAsync(
                j => j.AccountId == accountId && j.Kind == JobKind.BuildTasteNarrative
                    && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
            if (!inFlight)
            {
                db.SyncJobs.Add(new SyncJob
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    Kind = JobKind.BuildTasteNarrative,
                    Priority = 1,
                    EnqueuedAt = DateTime.UtcNow,
                    TriggerReason = "user-regenerate",
                    CursorJson = Application.Jobs.BuildTasteNarrativeJobHandler.ForceCursor,
                });
                await db.SaveAbsorbingRebuildRaceAsync(ct);
            }

            return Results.Accepted(value: new { queued = true });
        });
    }
}
