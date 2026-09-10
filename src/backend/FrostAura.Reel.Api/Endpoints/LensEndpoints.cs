using FrostAura.Reel.Application.Lens;
using FrostAura.Reel.Application.Ml;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Lens;

namespace FrostAura.Reel.Api.Endpoints;

/// <summary>
/// "Through your lens" on one title, on demand — the moment the user has already clicked, which is
/// the highest-value place in the product to spend a generation.
///
/// <para>Synchronous with a hard ceiling rather than streamed: the modal already renders instantly
/// from the title payload, so what this adds is prose that either arrives inside five seconds or
/// does not arrive at all. A spinner that resolves to nothing is worse than copy that was always
/// going to be adequate, so the response ALWAYS carries a <c>source</c> and the client renders the
/// ladder — fresh, stale (flagged), or the templater sentence over the overview.</para>
/// </summary>
public static class LensEndpoints
{
    /// <summary>The lens as the client renders it. <c>source</c> is never omitted — honesty is the feature.</summary>
    public sealed record LensResponse(
        string Source,
        string? Lens,
        string? Hook,
        string? Caution,
        IReadOnlyList<LensAnchor> Anchors,
        string? Mood,
        string? WhenToWatch,
        DateTime? GeneratedAt);

    public static LensResponse ToResponse(ResolvedLens resolved) =>
        resolved.Note is { } note
            ? new LensResponse(
                resolved.Source, note.Lens, note.Hook, note.Caution, note.Anchors,
                note.Mood, note.WhenToWatch, resolved.GeneratedAt)
            : new LensResponse(LensSource.None, null, null, null, [], null, null, null);

    public static void MapLensEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/titles").RequireAccount();

        group.MapGet("/{mediaType}/{tmdbId:long}/lens", async (
            string mediaType, long tmdbId,
            IReelDbContext db, IAccountContext accountContext, LensService lens,
            FeatureVectorBuilder featureBuilder, TasteFactsBuilder facts, CancellationToken ct) =>
        {
            var title = await TitleEndpoints.ResolveTitleAsync(db, mediaType, tmdbId, ct);
            if (title is null)
            {
                return Results.NotFound();
            }

            var accountId = accountContext.AccountId!.Value;
            var now = DateTime.UtcNow;
            var taste = await featureBuilder.BuildTasteStateAsync(accountId, now, ct);
            var built = await facts.BuildAsync(accountId, taste, now, ct);

            // The ceiling is enforced here as well as inside the service: the service's own budget
            // bounds the provider call, and this bounds everything around it, so a slow database
            // cannot turn a five-second promise into a fifteen-second one.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LensService.OnDemandCeiling);
            try
            {
                var resolved = await lens.ResolveOrGenerateAsync(
                    accountId, title.Id, taste, built.ProfileHash, timeout.Token);
                return Results.Ok(ToResponse(resolved));
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Out of time. Whatever is already cached is still the honest answer; nothing is
                // an honest answer too, and the client's ladder handles both.
                var cached = await lens.ResolveAsync(accountId, title.Id, built.ProfileHash, ct);
                return Results.Ok(ToResponse(cached));
            }
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
