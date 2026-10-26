using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Llm;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Sync;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>
/// "Through your lens" on one title — read, never written, on the request path.
///
/// <para><b>Off the read path (WS-1 task 7, F-6).</b> This GET used to generate inline behind a
/// five-second ceiling, so opening an uncached title held a request open on a provider and three
/// opens could pay three times. It now answers from what is stored — fresh, stale (flagged), or
/// nothing — in a couple of queries, and when the note is not current it queues the lens pass for
/// the title (<see cref="LensService.RequestAsync"/>) and says so with <c>pending</c>. The client
/// renders the ladder exactly as before; the next read after the pass shows the note. The response
/// ALWAYS carries a <c>source</c> — honesty is the feature.</para>
/// </summary>
public static class LensEndpoints
{
    /// <summary>
    /// The lens as the client renders it. <c>source</c> is never omitted. <c>pending</c> is true
    /// when a pass that writes this title is queued — the copy shown now may be replaced shortly.
    /// </summary>
    public sealed record LensResponse(
        string Source,
        string? Lens,
        string? Hook,
        string? Caution,
        IReadOnlyList<LensAnchor> Anchors,
        string? Mood,
        string? WhenToWatch,
        DateTime? GeneratedAt,
        bool Pending = false);

    public static LensResponse ToResponse(ResolvedLens resolved, bool pending = false) =>
        resolved.Note is { } note
            ? new LensResponse(
                resolved.Source, note.Lens, LensGuards.ServableHook(note), note.Caution, note.Anchors,
                note.Mood, note.WhenToWatch, resolved.GeneratedAt, pending)
            : new LensResponse(LensSource.None, null, null, null, [], null, null, null, pending);

    public static void MapLensEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/titles").RequireAccount();

        group.MapGet("/{mediaType}/{tmdbId:long}/lens", async (
            string mediaType, long tmdbId,
            IReelDbContext db, IAccountContext accountContext, LensService lens,
            TasteFactsBuilder facts, IProviderHealth health, CancellationToken ct) =>
        {
            var title = await TitleEndpoints.ResolveTitleAsync(db, mediaType, tmdbId, ct);
            if (title is null)
            {
                return Results.NotFound();
            }

            var accountId = accountContext.AccountId!.Value;
            // The render paths' cached fingerprint, not a full fact-sheet build: this path writes
            // nothing, so the hash is all it needs.
            var profileHash = await facts.CurrentHashAsync(accountId, DateTime.UtcNow, ct);
            var resolved = await lens.ResolveAsync(accountId, title.Id, profileHash, ct);

            // Behind an open provider breaker a queued pass would only pause; saying "pending"
            // would promise copy that is not coming.
            var pending = resolved.Source != LensSource.Fresh
                && health.OpenBreakerFor(ApiProvider.OpenRouter, ApiUsagePurpose.Lens) is null
                && await lens.RequestAsync(accountId, title.Id, profileHash, resolved, ct) == LensService.RequestOutcome.Queued;

            return Results.Ok(ToResponse(resolved, pending));
        });

        // "This spoiled it for me." Withdraws every note this account holds for the title so the
        // surface falls back immediately. The rows are kept as Rejected rather than deleted — the
        // refusal is the record, and it is what stops a later pass writing the same thing again.
        group.MapPost("/{mediaType}/{tmdbId:long}/lens/spoiled", async (
            string mediaType, long tmdbId,
            IReelDbContext db, IAccountContext accountContext, LensService lens, CancellationToken ct) =>
        {
            var title = await TitleEndpoints.ResolveTitleAsync(db, mediaType, tmdbId, ct);
            if (title is null)
            {
                return Results.NotFound();
            }

            var withdrawn = await lens.ReportSpoiledAsync(accountContext.AccountId!.Value, title.Id, ct);
            return Results.Ok(new { withdrawn });
        });
    }
}
