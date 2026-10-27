using FrostAura.Reel.Application.Blend;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Tenancy;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;

namespace FrostAura.Reel.Api.Endpoints;

public static class BlendEndpoints
{
    /// <summary>
    /// A joint pick: the shared card fields plus both personal predictions. <c>Joint</c> is
    /// min(<c>ForYou</c>, <c>ForThem</c>) — the ordering key. All three at 1dp (<see cref="Shown"/>) at
    /// this edge, like every other rating the API serves.
    /// </summary>
    public record BlendCard(
        Guid TitleId, string MediaType, long? TmdbId, string Name, int? Year, int? RuntimeMinutes,
        string? PosterPath, string? BackdropPath, string[] Genres,
        decimal? CrowdRating, int? CrowdVotes, bool IsReleased,
        IReadOnlyList<FeedEndpoints.CardProvider> Providers,
        decimal ForYou, decimal ForThem, decimal Joint);

    private const int PickCount = 20;

    /// <summary>
    /// A Blend prediction as its badge reads it: the same number the feed and the title page show for
    /// that stored score (R1-04 — a stored 7.25 read "7.2" here and "7.3" on the feed).
    /// </summary>
    public static decimal Shown(decimal stored) => ShownRating.Of(stored);

    public static void MapBlendEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/blend").RequireAccount();

        group.MapPost("/code", async (IAccountContext accountContext, BlendService blend, CancellationToken ct) =>
        {
            var code = await blend.RegenerateCodeAsync(accountContext.AccountId!.Value, ct);
            return Results.Ok(new { code });
        });

        group.MapGet("/{code}/preview", async (
            string code, IAccountContext accountContext, BlendService blend, CancellationToken ct) =>
        {
            var owner = await blend.FindOwnerByCodeAsync(code, ct);
            if (owner is null)
            {
                return Results.NotFound();
            }

            var count = await blend.EligibleBothCountAsync(accountContext.AccountId!.Value, owner.Id, ct);
            return Results.Ok(new { ownerName = owner.DisplayName, eligibleBothCount = count });
        });

        group.MapPost("/{code}/picks", async (
            string code, IReelDbContext db, BlendService blend, HttpContext http, CancellationToken ct) =>
        {
            var account = await http.GetCurrentAccountAsync(db, ct);
            if (account is null)
            {
                return Results.Unauthorized();
            }

            var owner = await blend.FindOwnerByCodeAsync(code, ct);
            if (owner is null)
            {
                return Results.NotFound();
            }

            var picks = await blend.JointPicksAsync(account.Id, owner.Id, PickCount, ct);
            var nowUtc = DateTime.UtcNow;
            var providers = await FeedEndpoints.ProvidersForAsync(
                db, account.Region, picks.Select(p => p.Title.Id).ToList(), nowUtc, ct);

            var cards = picks.Select(pick =>
            {
                var title = pick.Title;
                var releaseDate = title.MediaType == MediaType.Movie ? title.ReleasedAt : title.FirstAiredAt;
                var isReleased = releaseDate is null || releaseDate <= nowUtc;
                var (crowd, crowdVotes) = CrowdRating.Resolve(title);
                return new BlendCard(
                    title.Id, title.MediaType.ToString(), title.TmdbId, title.Name, title.Year, title.RuntimeMinutes,
                    title.PosterPath, title.BackdropPath, title.Genres,
                    crowd, crowdVotes, isReleased,
                    providers.GetValueOrDefault(title.Id, []),
                    Shown(pick.ForYou), Shown(pick.ForThem), Shown(pick.Joint));
            }).ToList();

            return Results.Ok(new { ownerName = owner.DisplayName, items = cards });
        });
    }
}
